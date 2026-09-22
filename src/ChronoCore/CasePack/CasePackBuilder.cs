using ChronoCore.Domain;

namespace ChronoCore.CasePack;

/// <summary>
/// 离线案例包「岩芯年代尺：深度锚点、沉积速率与多候选模型谱系」BX-2026-C。
/// 非通用表格：材料 ID、原始单位表示、岩性、储层名与事件名均为沉积学领域材料。
/// </summary>
public static class CasePackBuilder
{
    public const string ProjectId = "BX-2026-C";
    public const string ProjectName = "BX-2026-C 岩芯年代尺：深度锚点、沉积速率与多候选模型谱系";

    public static Project BuildInitial()
    {
        var t0 = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);
        var p = new Project
        {
            Id = ProjectId,
            Name = ProjectName,
            CreatedAt = t0,
            NarrativeTitle = "沉积学小组把岩芯分段、年龄锚点和事件层组成深度年龄模型，保留多套可辩护候选",
            ParameterSets =
            {
                new ParameterSet
                {
                    Id = "params-v1", Version = 1, RulesetVersion = Engine.AgeModelEngine.RulesetVersion,
                    MonteCarloDraws = 4000, CreatedAt = t0, Note = "初始规则：海相碳十四扣储层偏移；旧碳长尾保留"
                },
                new ParameterSet
                {
                    Id = "params-v2", Version = 2, RulesetVersion = Engine.AgeModelEngine.RulesetVersion,
                    MonteCarloDraws = 4000, CreatedAt = t0.AddDays(1),
                    Note = "参数修订：该孔储层偏移 410±30 → 380±25 yr（区域海洋碳库新曲线）"
                }
            },
            Branches = { new Branch { Id = "main", Name = "主干（初始导入→修订→局部改动→冲突决定）", CreatedAt = t0 } }
        };

        // 1) 深度基准 + 分段缺口 + 不整合（材料：core_log_v7.csv，原始 mbsf 表示保留）
        var seg = new[]
        {
            ("mat-seg-01", "0.00 mbsf", "4.80 mbsf", BoundaryKind.Continuous, "L1 灰绿软泥", "seg L1", false),
            ("mat-seg-02", "4.80 mbsf", "9.60 mbsf", BoundaryKind.Continuous, "L2 纹层硅质泥", "seg L2", false),
            ("mat-seg-03", "9.60 mbsf", "9.60 mbsf", BoundaryKind.Unconformity, "U1 不整合面（缺失带）", "U1", false),
            ("mat-seg-04", "9.60 mbsf", "14.20 mbsf", BoundaryKind.Continuous, "L3 粉砂质泥", "seg L3", false),
            ("mat-seg-05", "14.20 mbsf", "14.20 mbsf", BoundaryKind.Gap, "G1 取芯缺口 14.20–15.00 mbsf", "G1", false),
            ("mat-seg-06", "15.00 mbsf", "18.40 mbsf", BoundaryKind.Continuous, "L4 纹层泥（局部倒转块 R-OVT 在 16.9–17.4 m）", "seg L4", true),
            ("mat-seg-07", "18.40 mbsf", "20.00 mbsf", BoundaryKind.Continuous, "L5 灰质软泥", "seg L5", false),
        };
        foreach (var (id, top, @base, bnd, lith, code, ovt) in seg)
        {
            p.Materials.Add(new Material
            {
                Id = id, Kind = MaterialKind.SegmentGap, SourceRef = "field/core_log_v7.csv",
                SourceFragment = $"lithology_section,{code}", SourceLine = 20 + int.Parse(id.Split('-')[^1]),
                ImportedAt = t0, DepthDatum = DepthDatum.Mbsf,
                TopDepthRaw = Measurement.ParseDepth(top, DepthDatum.Mbsf),
                BaseDepthRaw = Measurement.ParseDepth(@base, DepthDatum.Mbsf),
                BoundaryAtBase = bnd, Lithology = lith, SegmentCode = code, OverturnedSection = ovt
            });
        }

        // 2) 放射性年龄分布（原始表示保留；含一个 cm/基准混用、一个无法转换值用于诊断展示）
        var dates = new[]
        {
            // id, 深度原始, 基准, 年龄原始, 时间基准, 1σ原始, 分布, 材料类型, 储层, 备注
            ("mat-date-01", "1.10 mbsf", DepthDatum.Mbsf, "2850 yr BP", TimeScale.Bp1950, "45 yr", "normal", "marine", "mat-res-01", "有孔虫 *G. bulloides*"),
            ("mat-date-02", "3.40 mbsf", DepthDatum.Mbsf, "4120 yr BP", TimeScale.Bp1950, "60 yr", "normal", "marine", "mat-res-01", "有孔虫混合样"),
            ("mat-date-03", "6.20 mbsf", DepthDatum.Mbsf, "6480 cal yr BP", TimeScale.CalBp, "90 yr", "normal", "marine", "mat-res-01", "纹层内单样"),
            ("mat-date-04", "8.90 mbsf", DepthDatum.Mbsf, "9050 yr BP", TimeScale.Bp1950, "70 yr", "normal", "marine", "mat-res-01", "近不整合面"),
            ("mat-date-05", "10.40 mbsf", DepthDatum.Mbsf, "11820 yr BP", TimeScale.Bp1950, "110 yr", "normal", "marine", "mat-res-01", "L3 顶部（不整合之下）"),
            ("mat-date-06", "13.10 mbsf", DepthDatum.Mbsf, "13640 yr BP", TimeScale.Bp1950, "140 yr", "normal", "marine", "mat-res-01", "L3 中部"),
            ("mat-date-07", "15.60 mbsf", DepthDatum.Mbsf, "15120 yr BP", TimeScale.Bp1950, "160 yr", "gamma-longtail", "marine", "mat-res-01", "疑似再搬运旧碳（长上尾 k=2.4 θ=320 w=0.14）"),
            ("mat-date-08", "17.80 mbsf", DepthDatum.Mbsf, "17050 yr BP", TimeScale.Bp1950, "180 yr", "normal", "marine", "mat-res-01", "倒转块 R-OVT 下缘（证据保留，不进单调模型）"),
            ("mat-date-09", "19.20 mbsf", DepthDatum.Mbsf, "18480 yr BP", TimeScale.Bp1950, "210 yr", "normal", "marine", "mat-res-01", "L5"),
            ("mat-date-10", "8.90 mbsf", DepthDatum.Mbsf, "8980 yr BP", TimeScale.Bp1950, "85 yr", "normal", "terrestrial", null, "并列锚点：同深独立来源（陆生植物残体，另一岩芯瓶号）"),
            ("mat-date-11", "620 cm", DepthDatum.CmBelowCoreTop, "6.51 ka", TimeScale.KaBp, "0.08 ka", "normal", "marine", "mat-res-01", "原始 cm/ka 表示，派生层才规范化"),
            ("mat-date-12", "7.40 mbsf", DepthDatum.Mbsf, "约七千 年", TimeScale.Bp1950, "?", "normal", "marine", "mat-res-01", "无法转换的原始记录：留在原位置并附诊断，绝不丢弃"),
        };
        var line = 40;
        foreach (var (id, depth, datum, age, scale, sig, dist, mtype, res, note) in dates)
        {
            var m = new Material
            {
                Id = id, Kind = MaterialKind.RadiometricDate, SourceRef = "lab/radiocarbon_table_v3.tsv",
                SourceFragment = $"radiocarbon,{id}", SourceLine = line++, ImportedAt = t0,
                DepthDatum = datum,
                DepthRaw = Measurement.ParseDepth(depth, datum),
                AgeRaw = Measurement.ParseAge(age, scale),
                AgeScale = scale,
                OneSigmaRaw = dist == "gamma-longtail"
                    ? Measurement.ParseAge(sig, scale)
                    : ParseSigma(sig),
                Distribution = dist, MaterialType = mtype, ReservoirId = res,
                SampleCode = "BX-C-" + id.Split('-')[^1], LabCode = "Lab-" + (60100 + line),
                ShapeK = dist == "gamma-longtail" ? 2.4 : null,
                ScaleTheta = dist == "gamma-longtail" ? 320.0 : null,
                MinClamp = dist == "gamma-longtail" ? 15120 : null,
                TailWeight = dist == "gamma-longtail" ? 0.14 : null
            };
            m.DeltaR = null;
            p.Materials.Add(m);
        }

        // 3) 储层偏移（有效区间显式）
        p.Materials.Add(new Material
        {
            Id = "mat-res-01", Kind = MaterialKind.ReservoirOffset, SourceRef = "lab/marine_reservoir_curve_2025.csv",
            SourceFragment = "reservoir,regional-E", SourceLine = 7, ImportedAt = t0,
            ReservoirName = "区域 E 海相碳库（湾内）",
            DeltaR = Measurement.ParseOffset("410 yr"), DeltaRSigma = Measurement.ParseOffset("30 yr"),
            ValidFromAge = Measurement.ParseAge("1000 yr BP", TimeScale.Bp1950),
            ValidToAge = Measurement.ParseAge("20000 yr BP", TimeScale.Bp1950)
        });

        // 4) 事件层相关（火山灰，独立等时面）
        p.Materials.Add(new Material
        {
            Id = "mat-ev-01", Kind = MaterialKind.EventCorrelation, SourceRef = "tephra/tephra_horizons.csv",
            SourceFragment = "tephra,ASH-1580", SourceLine = 12, ImportedAt = t0,
            DepthDatum = DepthDatum.Mbsf,
            EventDepthRaw = Measurement.ParseDepth("12.30 mbsf", DepthDatum.Mbsf),
            EventAgeRaw = Measurement.ParseAge("13180 cal yr BP", TimeScale.CalBp),
            EventAgeSigmaRaw = Measurement.ParseAge("90 yr", TimeScale.Bp1950),
            EventName = "ASH-1580 火山灰（区域等时面）",
            CorrelationBasis = "玻璃屑地球化学指纹与近端 ASH-1580 匹配（SIMS n=18）"
        });

        // 5) 排除决定（材料与决定都保留，可追溯到事件）
        p.Materials.Add(new Material
        {
            Id = "mat-date-13", Kind = MaterialKind.RadiometricDate, SourceRef = "lab/radiocarbon_table_v3.tsv",
            SourceFragment = "radiocarbon,mat-date-13", SourceLine = 53, ImportedAt = t0,
            DepthDatum = DepthDatum.Mbsf,
            DepthRaw = Measurement.ParseDepth("2.05 mbsf", DepthDatum.Mbsf),
            AgeRaw = Measurement.ParseAge("910 yr BP", TimeScale.Bp1950),
            OneSigmaRaw = ParseSigma("35 yr"), Distribution = "normal", MaterialType = "bulk sediment",
            SampleCode = "BX-C-13", LabCode = "Lab-60153",
            Excluded = true,
            ExclusionReason = "全沉积物有机质受现代根屑污染（初判）：排除决定保留，等待复核可恢复"
        });

        return p;
    }

    private static Measurement ParseSigma(string raw)
    {
        // σ 用与年龄相同的解析路径（单位跟随年龄字段）
        return Measurement.ParseAge(raw, TimeScale.Bp1950);
    }
}
