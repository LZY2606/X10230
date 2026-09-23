namespace DepthAge.Core.Models;

/// <summary>三种必须彼此不同的结果：格式损坏 / 语义无解 / 预算耗尽。</summary>
public sealed class FormatCorruptException : Exception
{
    public List<Diagnostic> Diagnostics { get; }
    public FormatCorruptException(string message, List<Diagnostic>? diagnostics = null)
        : base(message) => Diagnostics = diagnostics ?? new();
}

public sealed class SemanticImpasseException : Exception
{
    public List<Diagnostic> Diagnostics { get; }
    public SemanticImpasseException(string message, List<Diagnostic>? diagnostics = null)
        : base(message) => Diagnostics = diagnostics ?? new();
}

public sealed class BudgetExhaustedException : Exception
{
    public int Budget { get; }
    public BudgetExhaustedException(string message, int budget) : base(message) => Budget = budget;
}

/// <summary>一次导入/修订的结果包：成功材料、诊断、位置。</summary>
public sealed record ImportOutcome
{
    public List<DepthDatum> Datums { get; init; } = new();
    public List<CoreSection> Sections { get; init; } = new();
    public List<AnchorMaterial> Anchors { get; init; } = new();
    public List<ReservoirOffset> ReservoirOffsets { get; init; } = new();
    public List<CalibrationCurve> CalibrationCurves { get; init; } = new();
    public List<EventBed> EventBeds { get; init; } = new();
    public List<EventCorrelation> EventCorrelations { get; init; } = new();
    public List<Diagnostic> Diagnostics { get; init; } = new();

    public IEnumerable<object> AllMaterials()
    {
        foreach (var m in Datums) yield return m;
        foreach (var m in Sections) yield return m;
        foreach (var m in Anchors) yield return m;
        foreach (var m in ReservoirOffsets) yield return m;
        foreach (var m in CalibrationCurves) yield return m;
        foreach (var m in EventBeds) yield return m;
        foreach (var m in EventCorrelations) yield return m;
    }
}
