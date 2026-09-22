namespace ChronoAge.Core.CasePack;

/// <summary>
/// Concrete domain sample material for the offline case pack. All depths carry a datum token
/// and raw unit; one 14C date is deliberately left uncalibrated-scale in raw form, one value
/// is deliberately unconvertible so import keeps it in place with a diagnostic.
/// </summary>
public static class SamplePackData
{
    public const string Narrative = """
        沉积学小组对 IODP 风格深海岩芯 KC-07 建立深度-年龄模型。工作分三个层次：
        不可变材料（深度基准 SEAFLOOR、四个岩芯分段、两条段界——一条接触、一条不整合、
        一条厚度缺口；放射性测年分布；海洋储层偏移；火山灰事件层及其外部等时对比；
        以及所有排除决定的留痕）；可修改决定（重叠解释、并列锚点排序、候选接受/否定、冻结）；
        派生层（不断层段内的单调年龄曲线、沉积速率区间与不确定度带）。
        叙事先完整导入基线并核对，再故意触发深度重复、不整合、倒置层段、并列锚点、
        年龄分布长尾、局部重算与并发冻结；第二分支专门检验"年龄只能在不断层段中单调、
        一个锚点分布不能被均值替代、多个等优模型全部保留"。
        """;

    public static IReadOnlyList<CaseMaterial> Materials() => new[]
    {
        // --- depth datum ---
        new CaseMaterial("DATUM-SEAFLOOR", "depth_datum", "shipboard/core-log-v3",
            new Dictionary<string, string?>
            {
                ["name"] = "SEAFLOOR",
                ["basis"] = "sea floor at mudline; mbsf positive downward",
                ["offset_m"] = "0.0"
            }),

        // --- four recovered sections ---
        Sec("SEC-1H", "1H", "0.00", "4.20", "shipboard/core-log-v3"),
        Sec("SEC-2H", "2H", "4.50", "9.00", "shipboard/core-log-v3"),
        Sec("SEC-3H", "3H", "9.30", "13.10", "shipboard/core-log-v3"),
        Sec("SEC-4H", "4H", "15.20", "19.80", "shipboard/core-log-v3"),

        // --- boundaries: contact, unconformity, thickness gap ---
        Bnd("BND-1H-2H", "1H", "2H", "contact", null,
            "continuous hemipelagic contact; no missing section recorded"),
        Bnd("BND-2H-3H", "2H", "3H", "unconformity", "0.30",
            "bioturbated hardground; nannofossil zone jump indicates an unconformity"),
        Bnd("BND-3H-4H", "3H", "4H", "gap", "2.10",
            "no recovery 13.10-15.20 mbsf; explicit coring gap, time across it is not interpolated"),

        // --- radiometric assays ---
        new CaseMaterial("AS-001", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-001", ["depth"] = "1.20", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "planktic_foram",
                ["calibrated"] = "true",
                ["dist"] = "normal", ["mean"] = "1850", ["sigma"] = "70",
                ["unit"] = "cal_yr_BP"
            }),
        new CaseMaterial("AS-002", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-002", ["depth"] = "3.40", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "benthic_foram",
                ["calibrated"] = "false",
                ["reported_age"] = "4200", ["reported_sigma"] = "35", ["reported_age_unit"] = "14C_yr_BP",
                ["reservoir"] = "RES-NATL-MODERN"
            }),
        new CaseMaterial("AS-003", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-003", ["depth"] = "6.80", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "planktic_foram",
                ["calibrated"] = "false",
                ["reported_age"] = "8600", ["reported_sigma"] = "40", ["reported_age_unit"] = "14C_yr_BP",
                ["reservoir"] = "RES-NATL-MODERN"
            }),
        // a long-tail calibrated date below the unconformity (lognormal age structure)
        new CaseMaterial("AS-004", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-004", ["depth"] = "10.40", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "bulk_organic",
                ["calibrated"] = "true",
                ["dist"] = "lognormal", ["mulog"] = "9.47", ["sigmalog"] = "0.34",
                ["unit"] = "cal_yr_BP",
                ["note"] = "calibration posterior heavily right-skewed; mean substitution forbidden"
            }),
        new CaseMaterial("AS-005", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-005", ["depth"] = "16.90", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "planktic_foram",
                ["calibrated"] = "true",
                ["dist"] = "normal", ["mean"] = "26500", ["sigma"] = "650",
                ["unit"] = "cal_yr_BP"
            }),

        // --- reservoir offset ---
        new CaseMaterial("RES-NATL-MODERN", "reservoir", "literature/reservoir-corr-2024",
            new Dictionary<string, string?>
            {
                ["name"] = "North Atlantic modern marine reservoir",
                ["applies_to"] = "benthic_foram",
                ["mean_years"] = "408", ["sigma_years"] = "45"
            }),

        // --- event layer + external correlation (tephra) ---
        new CaseMaterial("EV-TEPHRA-A", "event_layer", "shipboard/core-log-v3",
            new Dictionary<string, string?>
            {
                ["code"] = "TEPHRA-A", ["depth"] = "8.20", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["lithology"] = "felsic_tephra_ash_layer"
            }),
        new CaseMaterial("CORR-TEPHRA-A", "event_correlation", "literature/tephra-isochrons-2023",
            new Dictionary<string, string?>
            {
                ["event_log_id"] = "EV-TEPHRA-A",
                ["external_name"] = "Tephra-X (North Atlantic isochron)",
                ["external_basis"] = "major-element glass shard geochemistry + SHard matching",
                ["dist"] = "normal", ["mean"] = "12100", ["sigma"] = "220",
                ["unit"] = "cal_yr_BP"
            }),

        // --- a direct anchor tied in depth with AS-002 (tie / duplicate depth) ---
        new CaseMaterial("ANC-TIE-1", "anchor", "shorelab/osl-2026-02",
            new Dictionary<string, string?>
            {
                ["code"] = "OSL-TIE-1", ["depth"] = "3.40", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["dist"] = "normal", ["mean"] = "5100", ["sigma"] = "420",
                ["unit"] = "cal_yr_BP",
                ["basis"] = "post-IR IRSL on fine silt; deliberately same depth as AMS-002"
            }),

        // --- excluded material: contaminated date, exclusion is retained ---
        new CaseMaterial("AS-BAD", "assay", "shorelab/ams-2026-04",
            new Dictionary<string, string?>
            {
                ["code"] = "AMS-BAD", ["depth"] = "5.60", ["depth_unit"] = "mbsf", ["datum"] = "SEAFLOOR",
                ["material"] = "reworked_shell_hash",
                ["calibrated"] = "true",
                ["dist"] = "normal", ["mean"] = "99000", ["sigma"] = "3000",
                ["unit"] = "cal_yr_BP"
            }),

        // --- deliberately unconvertible value: kept in place with a diagnostic ---
        new CaseMaterial("ANC-STRANGE", "anchor", "field/notebook-page-44",
            new Dictionary<string, string?>
            {
                ["code"] = "FIELD-NOTE-44", ["depth"] = "about seven metres", ["depth_unit"] = "mbsf",
                ["datum"] = "SEAFLOOR",
                ["dist"] = "uniform", ["min"] = "13000", ["max"] = "14000",
                ["unit"] = "cal_yr_BP",
                ["note"] = "illegible handwritten depth; retained verbatim, not plotted"
            }),
    };

    public static IReadOnlyList<CaseExclusion> BaselineExclusions() => new[]
    {
        new CaseExclusion("AS-BAD", true,
            "reworked shell hash mixes fossils of multiple ages; excluded from chronology but record retained"),
        new CaseExclusion("ANC-STRANGE", false,
            "initially retained for review; its unconvertible depth is diagnosed rather than dropped"),
    };

    private static CaseMaterial Sec(string id, string code, string top, string bottom, string source) =>
        new(id, "section", source, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["top"] = top, ["top_unit"] = "mbsf",
            ["bottom"] = bottom, ["bottom_unit"] = "mbsf",
            ["datum"] = "SEAFLOOR"
        });

    private static CaseMaterial Bnd(string id, string upper, string lower, string kind, string? gap, string note) =>
        new(id, "boundary", "shipboard/core-log-v3", new Dictionary<string, string?>
        {
            ["upper_section"] = upper,
            ["lower_section"] = lower,
            ["kind"] = kind,
            ["gap_thickness"] = gap, ["gap_unit"] = "m",
            ["note"] = note
        });
}
