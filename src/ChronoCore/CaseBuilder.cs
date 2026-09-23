namespace ChronoCore;

/// <summary>
/// “岩芯年代尺：深度锚点、沉积速率与多候选模型谱系”专用离线案例。
/// 虚构孔位 CORESITE-7B（MBSF 基准）：三段连续取心，两个显式边界
/// （取心缺口 gap-1 与不整合 unc-1）。
/// </summary>
public static class CaseBuilder
{
    private const string Datum = "dat-mbsf";

    public static CaseBundle Build()
    {
        var datums = new List<DepthDatum>
        {
            new("dat-mbsf", "海底以下米 MBSF", "m",
                "岩芯拼接后深度基准；所有深度保留原始表示，派生层才规范化", 1),
            new("dat-mcd", "复合深度 MCD", "m", "跨孔对比用复合基准（本案例仅登记，不混用）", 2)
        };

        var intervals = new List<CoreInterval>
        {
            new("iv-1", "7B-1H/2H 连续段 A", Datum, "2.00 mbsf", "18.40 mbsf", 1, "顶部至不整合前连续取心"),
            new("iv-2", "7B-3H 连续段 B", Datum, "23.00 mbsf", "34.20 mbsf", 2, "不整合面之下，独立单调约束"),
            new("iv-3", "7B-4H 连续段 C", Datum, "35.00 mbsf", "48.00 mbsf", 3, "含浊积层与火山灰层")
        };

        var gaps = new List<GapBoundary>
        {
            new("gap-1", "recovery-gap", Datum, "18.40 mbsf", "23.00 mbsf", 1,
                "2H/3H 之间 4.60 m 未回收到岩芯；显式缺口，跨缺口不插值、不检查单调"),
            new("unc-1", "unconformity", Datum, "34.20 mbsf", "35.00 mbsf", 2,
                "区域不整合（缺失约 MIS 8 段）；显式时间边界，B/C 段年龄可能倒置")
        };

        var reservoirs = new List<ReservoirOffset>
        {
            new("res-local", "局地表层水储层偏移 ΔR", "320 cal a", "75 cal a", true, 1,
                "附近海湾现生贝类配对样本，ΔR=320±75 cal a"),
            new("res-marine20", "Marine20 全球平均", "400 cal a", "0 cal a", false, 2,
                "备选全局曲线；未采纳时保留")
        };

        // 年龄随深度（向下变老）：约 40-50 cm/ka。经验/正态/学生-t 三类分布，均保留全分布
        var anchors = new List<Anchor>
        {
            Anc("an-01", "PL-101", "2.40 mbsf", SamplesFactory.Normal(5200, 140),
                "AMS14C-底栖有孔虫", 1, reservoir: "res-local",
                note: "已校正 cal a BP 分布"),
            Anc("an-02", "PL-104", "6.10 mbsf", Empirical(new[]
            {
            12820.0,13050.0,13180.0,13310.0,13420.0,13560.0,13680.0,13790.0,13900.0,14020.0,14150.0,14260.0,
            14380.0,14500.0,14620.0,14710.0,14830.0,14960.0,15080.0,15220.0,15360.0,15510.0,15680.0,15920.0,16340.0
            }, longTail: true), "AMS14C-混合种有孔虫", 2, reservoir: "res-local",
                note: "树轮校正后经验分布，右尾长（海洋碳库+校正曲线平台）"),
            Anc("an-03", "PL-107", "10.80 mbsf", SamplesFactory.Normal(24800, 520),
                "AMS14C-底栖有孔虫", 3, reservoir: "res-local"),
            Anc("an-04", "PL-110", "15.20 mbsf", SamplesFactory.StudentT(33500, 700, 6),
                "AMS14C-底栖有孔虫", 4, reservoir: "res-local",
                note: "df=6 学生-t：重尾，99% 区间显著宽于 95%"),
            Anc("an-05", "PL-113", "17.60 mbsf", SamplesFactory.Normal(38900, 800),
                "AMS14C-底栖有孔虫", 5, reservoir: "res-local",
                note: "不整合前最后的锚点"),
            Anc("an-06", "PL-201", "23.50 mbsf", SamplesFactory.Normal(148000, 4200),
                "U系-珊瑚", 6, note: "不整合面之下；年龄可老于上覆段，边界两侧允许倒置"),
            Anc("an-07", "PL-205", "28.90 mbsf", SamplesFactory.StudentT(176000, 5200, 7),
                "U系-珊瑚", 7, note: "长尾 U 系分布"),
            Anc("an-08", "PL-209", "33.70 mbsf", SamplesFactory.Normal(205000, 6000),
                "U系-珊瑚", 8),
            Anc("an-09", "PL-302", "36.80 mbsf", SamplesFactory.Normal(232000, 5000),
                "U系-珊瑚", 9),
            Anc("an-10", "PL-306", "41.50 mbsf", SamplesFactory.StudentT(268000, 6500, 8),
                "U系-珊瑚", 10, note: "火山灰层之下 0.4 m"),
            Anc("an-11", "PL-310", "46.20 mbsf", SamplesFactory.Normal(305000, 7000),
                "U系-珊瑚", 11),
            // —— 并列锚点：同深度两个实验室对同一层位的等优解释 ——
            Anc("an-tie-a", "PL-TIE-A", "12.00 mbsf", SamplesFactory.Normal(27200, 400),
                "AMS14C-实验室A", 12, reservoir: "res-local", group: "grp-tie-12",
                note: "与 PL-TIE-B 同深度并列，两个候选都合法"),
            Anc("an-tie-b", "PL-TIE-B", "12.00 mbsf", SamplesFactory.Normal(27850, 450),
                "AMS14C-实验室B", 13, reservoir: "res-local", group: "grp-tie-12",
                note: "并列解释；不得取均值合并为一个锚点"),
            // —— 无法转换 / 被排除的材料：保留在原位并附诊断，不为画图丢弃 ——
            Anc("an-bad", "PL-BADDEPTH", "29.6 ???", SamplesFactory.Normal(180000, 5000),
                "U系-珊瑚", 14, note: "深度单位无法识别：原值保留，派生层诊断，不能转换即不入模")
        };

        var eventLayers = new List<EventLayer>
        {
            new("ev-tephra-1", "AT-7b 火山灰层", "41.90 mbsf", Datum, "270000",
                "tephra:AT-corr-X", 1, "与邻孔 5A 同一地球化学指纹，外部年龄 270 ka（等时面）"),
            new("ev-turb-1", "浊积层 TB-3", "38.40 mbsf", Datum, null,
                "lith:turbidite", 2, "瞬时沉积事件；作为岩性标志，无独立年龄")
        };

        var exclusions = new List<Exclusion>
        {
            new("exc-an-rework", "an-rework", "anchor",
                "样品 PL-REWORK 的有孔虫碎屑呈再搬运磨圆，年龄偏老，不代表层位年龄",
                "sed-team", 1, "ev-import"),
            new("exc-an-bad", "an-bad", "anchor",
                "深度单位无法转换（Q-UNKNOWN-UNIT），材料保留在原位并展示诊断，排除出建模直到补齐基准",
                "sed-team", 2, "ev-import")
        };

        var parameters = new List<ParameterSet>
        {
            new("v1", "pchip", true, 16, 0.005, 1,
                "基线参数：PCHIP 单调三次、应用储层校正、候选预算 16、长尾概率 0.5%"),
            new("v2", "linear", true, 16, 0.005, 2,
                "参数修订：改用分段线性，便于小组比较速率平台；其余保持"),
            new("v3", "pchip", false, 16, 0.005, 3,
                "冲突决定备选：关闭储层校正（与 v1/v2 冲突，两套决定都保留）")
        };

        var baseline = new CaseBaseline(datums, intervals, gaps, anchors, reservoirs,
            eventLayers, exclusions, parameters,
            "基线=深度基准+分段缺口+放射性年龄分布+储层偏移+事件层相关+全部排除决定");

        return new CaseBundle("core-chronicle-7b",
            "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系",
            "沉积学/地层年代", Narrative(), baseline,
            CaseSteps.Build(), ReplayScenarios.Build(), 1);
    }

    private static Anchor Anc(string id, string code, string depth, AgeSamples s,
        string method, int ord, string? reservoir = null, string? group = null,
        string? note = null)
        => new(id, code, depth, Datum, s, method, ord, reservoir, null, group, note);

    private static AgeSamples Empirical(double[] xs, bool longTail = false)
        => SamplesFactory.Empirical(xs, longTail);

    private static string Narrative() =>
        "沉积学小组把岩芯分段、年龄锚点和事件层组成深度-年龄模型，保留多套可辩护候选。" +
        "初始导入后先核对深度基准、分段缺口、放射性年龄分布、储层偏移、事件层相关与全部排除决定；" +
        "随后经历一次参数修订（v1→v2）、一次只影响局部依赖的源数据改动（an-07 重算，仅段 B），" +
        "以及一次互相冲突的人工决定（开启/关闭储层校正 v2/v3，两条决定都保留）。" +
        "第二分支检验：年龄只在不断层段内单调、缺口与不整合为显式边界、" +
        "锚点分布不可被均值替代、等优模型全部保留。最终在纵剖/分布/速率/不确定度带页面冻结并比较新锚点。";
}
