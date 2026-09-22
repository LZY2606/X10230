using ChronoCore.CasePack;
using ChronoCore.Domain;
using ChronoCore.Storage;

namespace ChronoWeb.Services;

/// <summary>
/// 启动播种：离线案例包在一次会话里完成导入 → 推导 → 复核剧本。
/// 数据库已存在且含项目时不重置（保留审阅者的会话结果）。
/// </summary>
public sealed class CaseSeeder
{
    public string DbPath { get; }
    public ProjectRepository Repo { get; }
    public ProjectWorkflowService Workflow { get; }

    public CaseSeeder(IWebHostEnvironment env)
    {
        DbPath = Path.Combine(env.ContentRootPath, "App_Data", "chrono.sqlite");
        Repo = new ProjectRepository(DbPath);
        Workflow = new ProjectWorkflowService(Repo);
    }

    public ProjectRecord EnsureSeeded()
    {
        if (Repo.HasProjects() && Repo.LoadProject(CasePackBuilder.ProjectId) is { } existing)
            return existing;

        var pack = CasePackBuilder.BuildInitial().WithScript();
        var record = new ProjectRecord
        {
            Id = pack.Id, Name = pack.Name, CreatedAt = pack.CreatedAt,
            Materials = pack.Materials, Decisions = pack.Decisions,
            ParameterSets = pack.ParameterSets, Branches = pack.Branches
        };
        Repo.SaveProject(record);

        // step-00 导入 + 基线推导
        Workflow.ImportMaterials(pack.Id, pack.Materials);
        Workflow.DeriveAndCommit(pack.Id, "main", "model.derived",
            new { step = "step-00-import", note = "初始基线：六类材料与排除决定均保留" }, "params-v1");

        // step-02 参数修订（储层偏移 410→380，只影响海相锚点）
        var revised = Workflow.Require(pack.Id);
        var resV2 = revised.Materials.First(m => m.Id == "mat-res-01").ShallowCopy();
        resV2.DeltaR = Measurement.ParseOffset("380 yr");
        resV2.DeltaRSigma = Measurement.ParseOffset("25 yr");
        resV2.Id = "mat-res-02";
        resV2.SourceRef = "lab/marine_reservoir_curve_2026.csv";
        resV2.SourceLine = 8;
        resV2.ReservoirName = "区域 E 海相碳库（湾内）v2026";
        Workflow.ImportMaterials(pack.Id, new[] { resV2 });
        Workflow.RecordDecision(pack.Id, "main", new Decision
        {
            Id = "dec-res-v2", Kind = DecisionKind.ParameterRevision, Status = DecisionStatus.Accepted,
            TargetMaterialId = "mat-res-01", ParameterSetId = "params-v2",
            Reason = "采用区域海洋碳库新曲线：410±30→380±25 yr（params-v2）",
            PayloadJson = "{\"deltaRYr\":380,\"sigmaYr\":25,\"newReservoirId\":\"mat-res-02\"}"
        }, new { step = "step-02-param-revision", from = "mat-res-01", to = "mat-res-02" });

        // step-03 局部源数据改动：重测 mat-date-06（仅 L3 重算）
        var redate = Workflow.Require(pack.Id).Materials.First(m => m.Id == "mat-date-06").ShallowCopy();
        redate.Id = "mat-date-06r";
        redate.AgeRaw = Measurement.ParseAge("13490 yr BP", TimeScale.Bp1950);
        redate.OneSigmaRaw = Measurement.ParseAge("120 yr", TimeScale.Bp1950);
        redate.SourceRef = "lab/radiocarbon_table_v3_remeasured.tsv";
        redate.SourceLine = 14;
        Workflow.ImportMaterials(pack.Id, new[] { redate });
        Workflow.DeriveAndCommit(pack.Id, "main", "source.local-change",
            new { step = "step-03-local-source-change", replaces = "mat-date-06", replacement = "mat-date-06r" },
            "params-v2", scopeMaterialId: "mat-date-06r");

        // step-04 互相冲突的人工决定（恢复 mat-date-13 与维持排除并存）
        Workflow.RecordDecision(pack.Id, "main", new Decision
        {
            Id = "dec-reinstate-13", Kind = DecisionKind.Reinstate, Status = DecisionStatus.Accepted,
            TargetMaterialId = "mat-date-13", ParameterSetId = "params-v2",
            Reason = "复核认为现代根屑污染证据不足，要求恢复 mat-date-13"
        }, new { step = "step-04-conflict-decision", claim = "reinstate" });
        Workflow.RecordDecision(pack.Id, "main", new Decision
        {
            Id = "dec-keep-excluded-13", Kind = DecisionKind.Exclude, Status = DecisionStatus.Accepted,
            TargetMaterialId = "mat-date-13", ParameterSetId = "params-v2",
            Reason = "原排除方坚持维持排除：全沉积物有机质污染初判仍成立"
        }, new { step = "step-04-conflict-decision", claim = "keep-excluded" });

        // step-05 冻结主干
        Workflow.Freeze(pack.Id, "main");

        // 冻结点上的新锚点试验
        var trialAnchor = new Material
        {
            Id = "mat-date-trial-01", Kind = MaterialKind.RadiometricDate,
            SourceRef = "lab/radiocarbon_trial_new_core.tsv", SourceFragment = "radiocarbon,trial-01", SourceLine = 3,
            ImportedAt = DateTime.UtcNow, DepthDatum = DepthDatum.Mbsf,
            DepthRaw = Measurement.ParseDepth("5.40 mbsf", DepthDatum.Mbsf),
            AgeRaw = Measurement.ParseAge("5620 yr BP", TimeScale.Bp1950),
            OneSigmaRaw = Measurement.ParseAge("55 yr", TimeScale.Bp1950),
            Distribution = "normal", MaterialType = "marine", ReservoirId = "mat-res-02",
            SampleCode = "BX-C-T1", LabCode = "Lab-70001"
        };
        Workflow.CreateTrialFromFreeze(pack.Id, "main", "trial-new-anchor", trialAnchor,
            new { step = "step-05-freeze-concurrency", purpose = "冻结后比较新锚点 5.40 mbsf 的局部影响" });

        return Workflow.Require(pack.Id);
    }
}
