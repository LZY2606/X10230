namespace ChronoCore;

public static class CaseSteps
{
    public static IReadOnlyList<CaseStep> Build() => new List<CaseStep>
    {
        new("step-import", "import", "初始导入并核对基线",
            "导入全部材料：深度基准、连续分段、两个显式边界、放射性年龄分布（含长尾）、" +
            "储层偏移、火山灰/浊积事件层、两条排除决定。审阅者逐项核对后再派生 v1 候选。",
            "v1", null, null, null, null, null, null),

        new("step-param-revise", "revise-parameter", "参数修订 v1→v2（PCHIP→线性）",
            "小组想看速率平台差异，把插值法从 PCHIP 改为分段线性；参数产生新版本 v2，" +
            "v1 的派生结果与结论保留，不被覆盖。",
            "v2", null, null, null, null, null, null),

        new("step-local-change", "local-source-change", "局部源数据改动：an-07 重测",
            "PL-205 用新稀释剂重测，年龄中位数由 176 ka 下移至 172.5 ka（局部长尾保留）。" +
            "只重算段 B（iv-2）；段 A、段 C 的派生结果沿用父依赖，输入摘要只在受影响子图变化。",
            "v2", "an-07",
            """{"id":"an-07","sampleCode":"PL-205","depthRaw":"28.90 mbsf","datumId":"dat-mbsf","method":"U系-珊瑚","ord":7,"note":"重测版本：172.5 ka，局部重算仅影响 iv-2","samples":"rebuild:student-t:172500:4800:7"}""",
            null, null, null, "iv-2"),

        new("step-conflict", "conflicting-decision", "互相冲突的人工决定（储层校正开/关）",
            "一名审阅者接受 v2（应用储层偏移）；另一名审阅者坚持关闭储层校正（v3）。" +
            "两条决定都保留为事件，v2 与 v3 下的候选谱系并列可回看，系统不替小组二选一。",
            "v3", null, null,
            "dec-v3-noreservoir", "accept", "model-family-v3",
            "区域证据表明局地 ΔR 可能不适用；保留关闭校正的候选谱系", null),

        new("step-review", "review", "冻结并比较新锚点",
            "在纵剖/锚点分布/速率区间/不确定度带页面冻结 v2 最优模型，" +
            "再放入试验性新锚点 PL-X（26.0 m，160 ka），比较影响是否被限制在段 B、单调性是否保持；" +
            "接受/否定/调整均生成新事件，可回到任意旧事件复核当时版本。",
            "v2", null, null, null, null, null, null)
    };
}

public static class ReplayScenarios
{
    public static IReadOnlyList<ReplayScenario> Build() => new List<ReplayScenario>
    {
        new("depth-duplicate", "深度重复（未声明并列）",
            Proposition.MonoInSection,
            """{"kind":"anchor","anchor":{"id":"an-x-dup","sampleCode":"PL-DUP","depthRaw":"15.20 mbsf","datumId":"dat-mbsf","method":"AMS14C","ord":99,"samples":"rebuild:normal:330000:3000","reservoirOffsetId":"res-local"}}""",
            "add-anchor", "semantic-impossible",
            "iv-1@15.200m:an-04+an-x-dup",
            "同深度出现未用 AlternativeGroupId 声明的第二个锚点 → D-DEPTH-DUPLICATE，语义无解，不静默取均值"),

        new("unconformity", "不整合是显式边界（跨边界允许年龄倒置）",
            Proposition.ExplicitBoundary,
            """{"kind":"anchor","anchor":{"id":"an-x-unc","sampleCode":"PL-UNC","depthRaw":"23.10 mbsf","datumId":"dat-mbsf","method":"U系-珊瑚","ord":98,"samples":"rebuild:normal:120000:4000"}}""",
            "add-anchor", "success-contained",
            "iv-2 仅在段内检查单调；与 iv-1 的年龄差跨越 unc-1/gap-1，不判倒置",
            "新锚点 120 ka 老/浅于上覆 iv-1 底部(~39 ka) 是允许的：缺口与不整合是显式边界"),

        new("inverted-section", "倒置层段（段内下浅上老）",
            Proposition.MonoInSection,
            """{"kind":"anchor","anchor":{"id":"an-x-inv","sampleCode":"PL-INV","depthRaw":"16.80 mbsf","datumId":"dat-mbsf","method":"AMS14C","ord":97,"samples":"rebuild:normal:20000:2000","reservoirOffsetId":"res-local"}}""",
            "add-anchor", "semantic-impossible",
            "iv-1: an-04(15.2m~33.5ka) → an-x-inv(16.8m~20ka)",
            "同一段连续层内年龄随深度变年轻 → D-NONMONOTONIC，语义无解；若改放到缺口另一侧则合法"),

        new("tie-anchor", "并列锚点与等优模型全部保留",
            Proposition.KeepEquallyOptimal,
            """{"kind":"parameter","maxCandidates":4}""",
            "parameter", "success-contained",
            "iv-1@12.0m: grp-tie-12 → 每个插值谱系至少 2 套候选",
            "并列锚点不被均值替代；每个合法选择生成一套候选，FitScore 并列时 Tie=true 且稳定排序"),

        new("long-tail", "年龄分布长尾（99% 带比 95% 显著宽）",
            Proposition.DistributionNotMean,
            """{"kind":"inspect","anchorId":"an-04"}""",
            "inspect", "success-contained",
            "an-04 student-t df=6：env 的 low99/high99 宽于 low95/high95",
            "整条分布参与不确定度带，任何显示都不允许只用均值点"),

        new("local-recompute", "局部重算（只影响依赖 an-07 的段 B）",
            Proposition.ExplicitBoundary,
            """{"kind":"anchor-revise","anchorId":"an-07","samples":"rebuild:student-t:172500:4800:7","scopeIntervalId":"iv-2"}""",
            "local-source-change", "success-contained",
            "仅 iv-2 的 envelope/rate 指纹变化；iv-1、iv-3 沿用父模型",
            "内容寻址：未改动输入的段得到相同模型 Id，受影响段产生新模型并记录父依赖"),

        new("concurrent-freeze", "并发冻结（后到者形成冲突事件）",
            Proposition.KeepEquallyOptimal,
            """{"kind":"freeze","modelRank":1,"holderActor":"reviewer-A","contenderActor":"reviewer-B"}""",
            "concurrent-freeze", "success-contained",
            "freeze_locks=reviewer-A；reviewer-B 产生 freeze.conflict 事件，冻结不生效",
            "冻结不可被后来者覆盖；冲突结构化保留，采纳/未采纳都进档")
    };
}

/// <summary>四条核心命题的机器可读取代码。</summary>
public static class Proposition
{
    public const string MonoInSection = "MONO-IN-SECTION";          // 年龄只能在不断层段中单调
    public const string ExplicitBoundary = "EXPLICIT-BOUNDARY";     // 深度缺口和不整合是显式边界
    public const string DistributionNotMean = "DISTRIBUTION-NOT-MEAN"; // 锚点分布不能被均值替代
    public const string KeepEquallyOptimal = "KEEP-EQUALLY-OPTIMAL";   // 多个等优模型需保留
}
