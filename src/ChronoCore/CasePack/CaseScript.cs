namespace ChronoCore.CasePack;

/// <summary>
/// 案例剧本（机器可读）。初始导入后审阅者先核对基线，再依次触发：
/// 参数修订 → 仅影响局部依赖的源数据改动 → 互相冲突的人工决定；
/// 第二个分支检验单调/显式边界/分布不可均值化/等优候选全保留。
/// </summary>
public static class CaseScript
{
    public static IEnumerable<CaseStep> Build() => new[]
    {
        new CaseStep
        {
            StepId = "step-00-import",
            Title = "初始导入并核对基线",
            Expectation = "深度基准、分段缺口（G1 取芯缺口）、不整合（U1）、放射性年龄分布、储层偏移、ASH-1580 事件层相关、mat-date-13 排除决定全部保留；mat-date-12 无法转换的值留在原位置并附诊断。",
            BoundaryTags = new[] { "baseline", "unconverted-value", "exclusion-retained" }
        },
        new CaseStep
        {
            StepId = "step-01-boundaries",
            Title = "触发边界组合：深度重复/并列锚点/不整合/倒置层段/长尾",
            Expectation = "mat-date-04 与 mat-date-10 在 8.90 mbsf 并列（独立来源）→ 各自保留分布禁止均值；U1 不整合与 G1 缺口为显式边界，9.60 与 14.20 mbsf 两侧不插值；mat-date-07 长上尾保留；mat-date-08 位于倒转块 R-OVT，只能单独展示。",
            BoundaryTags = new[] { "duplicate-depth", "unconformity", "inverted-interval", "tied-anchor", "age-long-tail" }
        },
        new CaseStep
        {
            StepId = "step-02-param-revision",
            Title = "一次参数修订：储层偏移 410±30 → 380±25 yr",
            Expectation = "新参数版本 params-v2 产生新事件与新结果，父级依赖指向前一事件；受影响的仅为海相锚点校正年龄，事件层与陆生锚点不变；旧事件仍读回旧参数下的结论。",
            BoundaryTags = new[] { "parameter-revision", "provenance" }
        },
        new CaseStep
        {
            StepId = "step-03-local-source-change",
            Title = "一次只影响局部依赖的源数据改动：重测 mat-date-06（13.10 mbsf）",
            Expectation = "仅 seg L3（10.40–13.10 mbsf 区间）速率与带重算，记录 scope=mat-date-06；L1/L2 与 L5 的派生值、指纹不变；新结果的 ParentMaterialIds 与 RecomputedSegmentIds 明示局部范围。",
            BoundaryTags = new[] { "local-recompute" }
        },
        new CaseStep
        {
            StepId = "step-04-conflict-decision",
            Title = "一次互相冲突的人工决定：同时恢复 mat-date-13 与维持排除",
            Expectation = "两条 Accepted/冲突人工决定不能静默覆盖；系统给出 Conflict 状态与 E-CONFLICT 诊断，审阅者必须消解后才能产生 Ok 结果，两个候选裁决都留档。",
            BoundaryTags = new[] { "conflicting-decisions" }
        },
        new CaseStep
        {
            StepId = "step-05-freeze-concurrency",
            Title = "冻结模型并比较新锚点影响（并发冻结检验）",
            Expectation = "冻结 main 时记录 freeze_digest；并发第二笔冻结/在冻结分支上追加修改必须被拒绝（冲突结果），新锚点在 trial 分支上比较，影响读回档案时显示值身份不变。",
            BoundaryTags = new[] { "concurrent-freeze", "stable-display-ids" }
        },
        new CaseStep
        {
            StepId = "step-06-branch-monotonic",
            Title = "第二分支：单调性、显式边界与多候选谱系",
            Expectation = "年龄只能在不断层段内单调；缺口/不整合是显式边界；任何锚点分布不被均值替代；等优候选全部保留并稳定排序；无法唯一决定时返回全部合法候选。",
            BoundaryTags = new[] { "monotonic-within-segment", "explicit-boundaries", "distribution-not-mean", "multiple-optimal" }
        },
        new CaseStep
        {
            StepId = "step-07-roundtrip",
            Title = "导出→删除本地数据库→再导入",
            Expectation = "身份、分支顺序、决定历史、显示值稳定身份与来源关系完全保持；半写注入后从 .preimport 恢复，且与格式损坏、语义无解说的是三种不同结果。",
            BoundaryTags = new[] { "export-import-roundtrip", "half-write-recovery", "three-failure-kinds" }
        }
    };
}
