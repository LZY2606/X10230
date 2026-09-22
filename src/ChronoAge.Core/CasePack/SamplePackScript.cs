namespace ChronoAge.Core.CasePack;

public static class SamplePackScript
{
    public static CasePack Build()
    {
        var all = SamplePackData.Materials().Select(m => m.LogId).ToList();

        var mainSteps = new List<CaseStep>
        {
            // 1) Baseline import — the whole evidence picture lands in one audit-able batch.
            new StepImport("BATCH-BASELINE", "initial-expedition-import", all,
                "导入基线", "深度基准、四段岩芯、接触/不整合/缺口、5 个放射性测年、储层偏移、火山灰与等时对比、并列锚点、被排除材料、不可转换值"),
            new StepDecideExclusion("AS-BAD", true,
                "reworked shell hash: excluded with reason; row retained"),
            new StepExpect("material_count", MinCount: 19, MaxCount: 19,
                Label: "基线材料件数（含被排除与不可转换）"),
            new StepExpect("diagnostic_code", TargetId: "ANC-STRANGE", EqualsValue: "QTY_NOT_NUMERIC",
                Label: "不可转换深度留在原位并带诊断"),
            new StepExpect("diagnostic_code", TargetId: "AS-002", EqualsValue: "QTY_NEEDS_CALIBRATION",
                Label: "14C 原始值未被偷偷当日历年"),
            new StepExpect("active_anchor_count", MinCount: 8,
                Label: "可用锚点（含等时事件；被排除与未定位者不参与）"),

            // 2) First derivation — multiple candidates, equi-optimal tie ordering retained.
            new StepDerive("基线推演",
                "3.40 mbsf 处 OSL/AMS 并列锚点产生两套合法顺序候选，按稳定身份排序并列保留"),
            new StepExpect("candidate_count", MinCount: 2,
                Label: "并列锚点产生 ≥2 套候选"),
            new StepExpect("equioptimal_count", MinCount: 2,
                Label: "两套等优候选均保留"),
            new StepExpect("segment_count", EqualsValue: "3",
                Label: "不整合与缺口把岩芯切成 3 个不断层段"),

            // 3) Parameter revision (rule v1.1): tighter plausible rate window.
            new StepReviseParams(
                ModelParameters.Default with { Version = "rules/v1.1", PlausibleRateMax = 60.0, PlausibleRateMin = 1.0 },
                "narrow the plausible sedimentation window based on regional hemipelagic rates",
                Label: "参数修订 rules/v1.1"),
            new StepDerive("按新参数重算"),
            new StepExpect("rule_version", EqualsValue: "rules/v1.1", Label: "推演钉在 v1.1"),

            // 4) Local-only source change: revise reservoir offset; only dependent derivations move.
            new StepReviseMaterial("RES-NATL-MODERN",
                new Dictionary<string, string?>
                {
                    ["name"] = "North Atlantic modern marine reservoir (2026 re-evaluation)",
                    ["applies_to"] = "benthic_foram",
                    ["mean_years"] = "440", ["sigma_years"] = "60"
                },
                "new regional reservoir compilation raises R from 408±45 to 440±60 yr",
                LocalScopeOnly: "AS-002,AS-003",
                Label: "局部源数据改动：储层偏移"),
            new StepDerive("储层偏移局部重算"),
            new StepExpect("local_recompute_targets", EqualsValue: "AS-002,AS-003",
                Label: "只有依赖该偏移的测年受影响"),
            new StepExpect("artifact_identity_changes", MinCount: 1,
                Label: "受影响派生结果身份改变"),

            // 5) Conflicting human decisions on the 2H/3H unconformity region (inverted section probe).
            new StepDecideOverlap("BND-2H-3H", "keep_unconformity",
                "first reviewer: treat hardground as a real time gap (unconformity)"),
            new StepDecideOverlap("BND-2H-3H", "tectonic_repeat",
                "second reviewer: argues an overturned/tectonically repeated interval; conflicts with the first call"),
            new StepExpect("conflict_count", MinCount: 1,
                Label: "互相冲突的人工决定同时保留为冲突事件"),

            // 6) Resolve the tie manually; candidate tree collapses deterministically.
            new StepDecideTie("ANC-AS-002", "ANC-ANC-TIE-1", "ANC-AS-002<ANC-ANC-TIE-1",
                "chronologist prefers the reservoir-corrected AMS order at the shared depth"),
            new StepDerive("人工排序后推演"),
            new StepExpect("candidate_count", EqualsValue: "1", Label: "人工排序后候选收敛为 1"),

            // 7) Freeze, then probe a new anchor against the frozen model.
            new StepFreeze(null, "冻结当前模型",
                "冻结后基线不可变；新锚点只能做对比，不能改冻结视图"),
            new StepCompareProbe(new Dictionary<string, string?>
            {
                ["code"] = "PROBE-NEW", ["depth"] = "11.80", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["dist"] = "normal", ["mean"] = "9000", ["sigma"] = "300",
                ["unit"] = "cal_yr_BP",
                ["basis"] = "new core-top radiocarbon arriving after freeze"
            }, Label: "冻结后对比新锚点",
                Note: "新锚点年龄偏轻，位于不整合之下层段，检验影响范围与单调违规"),
            new StepExpect("frozen_blocks_mutation", EqualsValue: "true",
                Label: "冻结期材料修订被拒（并发冻结）"),
            new StepExpect("probe_monotonicity", EqualsValue: "false",
                Label: "探测锚点在其不断层段内破坏单调"),
            new StepExpect("probe_affected_scope", EqualsValue: "segment_local",
                Label: "影响被限制在所在不断层段，不跨不整合/缺口传播"),
        };

        // Second branch: the monotonicity-only legal-candidates narrative.
        var branchSteps = new List<CaseStep>
        {
            new StepBranch("monotonicity-check", Label: "第二分支：单调性合法候选"),
            new StepImport("BATCH-MONO", "monotonicity-branch-fixture",
                MonoFixture().Select(m => m.LogId).ToList(),
                "导入单调检验夹具",
                "一个正常层段 + 一条不整合；不整合之上年龄老于之下（倒置），合法候选必须切断"),
            new StepDerive("第二分支推演"),
            new StepExpect("monotonicity_within_segment", EqualsValue: "enforced",
                Label: "年龄只在不断层段内要求向下变老"),
            new StepExpect("no_interpolation_across_unconformity", EqualsValue: "true",
                Label: "不整合两侧不做插值"),
            new StepExpect("distribution_preserved", EqualsValue: "lognormal",
                TargetId: "MONO-LONGTAIL",
                Label: "长尾分布完整保留，不以均值替代"),
            new StepExpect("all_legal_candidates_kept", MinCount: 2,
                Label: "无法唯一决定时保留全部合法候选"),
        };

        // Mono fixture materials are attached after the branch import step for replay convenience.
        var steps = mainSteps.Concat(branchSteps).ToList();

        return new CasePack(
            CasePack.PackId,
            CasePack.PackVersion,
            "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系",
            "sedimentology/stratigraphic-chronology/depth-age-model",
            SamplePackData.Narrative,
            SamplePackData.Materials().Concat(MonoFixture()).ToList(),
            SamplePackData.BaselineExclusions(),
            steps);
    }

    /// <summary>
    /// Fixture for branch two: two sections separated by an unconformity; below-unconformity
    /// deposits are younger-looking (inverted), a long-tail anchor is included, and a tie pair
    /// forces >=2 legal candidates. Monotonicity holds only per continuous segment.
    /// </summary>
    public static IReadOnlyList<CaseMaterial> MonoFixture() => new[]
    {
        new CaseMaterial("MONO-DATUM", "depth_datum", "fixture/mono",
            new Dictionary<string, string?> { ["name"] = "SEAFLOOR", ["basis"] = "fixture datum", ["offset_m"] = "0" }),
        new CaseMaterial("MONO-SEC-A", "section", "fixture/mono",
            new Dictionary<string, string?>
            {
                ["code"] = "MA", ["top"] = "0.0", ["top_unit"] = "mbsf",
                ["bottom"] = "5.0", ["bottom_unit"] = "mbsf", ["datum"] = "SEAFLOOR"
            }),
        new CaseMaterial("MONO-SEC-B", "section", "fixture/mono",
            new Dictionary<string, string?>
            {
                ["code"] = "MB", ["top"] = "6.0", ["top_unit"] = "mbsf",
                ["bottom"] = "10.0", ["bottom_unit"] = "mbsf", ["datum"] = "SEAFLOOR"
            }),
        new CaseMaterial("MONO-BND", "boundary", "fixture/mono",
            new Dictionary<string, string?>
            {
                ["upper_section"] = "MA", ["lower_section"] = "MB", ["kind"] = "unconformity",
                ["gap_thickness"] = "1.0", ["gap_unit"] = "m",
                ["note"] = "major unconformity: no monotonic relation across it"
            }),
        // upper segment: old dates
        AnchorMat("MONO-A1", "1.5", 14000, 300, "normal"),
        AnchorMat("MONO-A2", "4.2", 18000, 500, "normal"),
        // lower segment (below unconformity): younger, inverted relative to upper — legal across an unconformity
        AnchorMat("MONO-B1", "7.0", 6000, 250, "normal"),
        // long-tail anchor
        new CaseMaterial("MONO-LONGTAIL", "anchor", "fixture/mono",
            new Dictionary<string, string?>
            {
                ["code"] = "MONO-LT", ["depth"] = "8.5", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["dist"] = "lognormal", ["mulog"] = "9.05", ["sigmalog"] = "0.42",
                ["unit"] = "cal_yr_BP", ["basis"] = "heavily skewed calibration posterior"
            }),
        // tie pair in the lower segment => two legal orderings retained
        AnchorMat("MONO-TIE-X", "9.2", 9000, 700, "normal"),
        AnchorMat("MONO-TIE-Y", "9.2", 9300, 700, "normal"),
    };

    private static CaseMaterial AnchorMat(string id, string depth, double mean, double sigma, string dist) =>
        new(id, "anchor", "fixture/mono", new Dictionary<string, string?>
        {
            ["code"] = id, ["depth"] = depth, ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
            ["dist"] = dist, ["mean"] = mean.ToString("0.###"), ["sigma"] = sigma.ToString("0.###"),
            ["unit"] = "cal_yr_BP", ["basis"] = "fixture anchor"
        });
}
