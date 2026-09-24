using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

public enum RefKind { DepthDatum, Section, Boundary, Control, ReservoirOffset, EventBed, Exclusion, ParameterSet }

public enum DistributionKind { Normal, LogNormal, Uniform, MixtureLongTail, Empirical }

public enum AnchorKind { Calendar, Radiocarbon, Event }

public enum BoundaryKind { Continuous, CoreGap, Unconformity, Fault }

public enum ExclusionReason { DuplicateDepth, InvertedOrder, UnconvertibleValue, Reworked, ConflictingManual, OutOfSection }

public enum DiagnosticCode
{
    Ok,
    DuplicateDepth,
    UnconformityBoundary,
    CoreGapBoundary,
    InvertedSection,
    TiedAnchors,
    LongTailDistribution,
    LocalRecompute,
    ConcurrentFreezeConflict,
    FormatCorrupt,
    SemanticNoSolution,
    ResourceBudgetExhausted,
    IncompleteInput,
    UnconvertibleValue,
    NotFound
}

public enum ProbeStatus { Adopted, Rejected, Limited }

public sealed record Quantity(double Value, string Unit, string? Original = null, string? Diagnostic = null)
{
    public bool Converted => Diagnostic is null;
}

public sealed record DepthMeasure(double Value, string Datum, string? Original = null, string? Diagnostic = null);

public sealed record AgeSample(double Age, double Weight);

public sealed record AgeDistribution
{
    public DistributionKind Kind { get; init; }
    public double[] Parameters { get; init; } = Array.Empty<double>();
    public string Unit { get; init; } = "cal a BP";
    public string? Original { get; init; }
    public bool LongTail { get; init; }
    public double? MeanSnapshot { get; init; }

    public AgeDistribution() { }
    [JsonConstructor]
    public AgeDistribution(DistributionKind Kind, double[] Parameters, string Unit, string? Original, bool LongTail, double? MeanSnapshot)
    {
        this.Kind = Kind; this.Parameters = Parameters; this.Unit = Unit; this.Original = Original;
        this.LongTail = LongTail; this.MeanSnapshot = MeanSnapshot;
    }
}

public sealed record Stamp(string Author, DateTimeOffset At);

public abstract class RefRecord
{
    public string Id { get; init; } = "";
    public Stamp? Created { get; init; }
    public string? RawRepresentation { get; init; }
    public string? SourceRef { get; init; }
    public Dictionary<string, string> Provenance { get; init; } = new();
    public abstract RefKind Kind { get; }
}

public sealed class DepthDatum : RefRecord
{
    public override RefKind Kind => RefKind.DepthDatum;
    public string Name { get; init; } = "";
    public string DatumCode { get; init; } = "";
    public double ShiftToReferenceM { get; init; }
}

public sealed class Section : RefRecord
{
    public override RefKind Kind => RefKind.Section;
    public double TopM { get; init; }
    public double BottomM { get; init; }
    public string Datum { get; init; } = "";
    public string Lithology { get; init; } = "";
    public string? UnitCode { get; init; }
}

public sealed class Boundary : RefRecord
{
    public override RefKind Kind => RefKind.Boundary;
    public double DepthM { get; init; }
    public BoundaryKind BoundaryKind { get; init; }
    public Quantity? MissingSectionM { get; init; }
    public string? AboveUnit { get; init; }
    public string? BelowUnit { get; init; }
}

public sealed class ControlPoint : RefRecord
{
    public override RefKind Kind => RefKind.Control;
    public AnchorKind AnchorKind { get; init; }
    public double DepthM { get; init; }
    public string UnitCode { get; init; } = "";
    public AgeDistribution? Age { get; init; }
    public double? RadiocarbonBP { get; init; }
    public double? RadiocarbonSigma { get; init; }
    public string? ReservoirId { get; init; }
    public string? EventBedId { get; init; }
    public bool Excluded { get; init; }
    public string? ExclusionId { get; init; }
}

public sealed class ReservoirOffset : RefRecord
{
    public override RefKind Kind => RefKind.ReservoirOffset;
    public double DeltaR { get; init; }
    public double SigmaR { get; init; }
    public string Unit { get; init; } = "14C a";
}

public sealed class EventBed : RefRecord
{
    public override RefKind Kind => RefKind.EventBed;
    public double DepthM { get; init; }
    public string Correlation { get; init; } = "";
    public double CorrelatedAgeBP { get; init; }
    public double CorrelatedSigma { get; init; }
    public string AgeUnit { get; init; } = "cal a BP";
}

public sealed class Exclusion : RefRecord
{
    public override RefKind Kind => RefKind.Exclusion;
    public string TargetId { get; init; } = "";
    public RefKind TargetKind { get; init; }
    public ExclusionReason Reason { get; init; }
    public string Detail { get; init; } = "";
    public double? AtDepthM { get; init; }
}

public sealed class ParameterSet : RefRecord
{
    public override RefKind Kind => RefKind.ParameterSet;
    public int MonteCarloDraws { get; init; } = 1000;
    public int MaxCandidates { get; init; } = 64;
    public double TieToleranceA { get; init; } = 25;
    public double InversionToleranceA { get; init; } = 0;
    public bool CollapseToMean { get; init; }
    public string CalibrationCurve { get; init; } = "synthetic-marine-1";
}

public sealed record CalPoint(double RadiocarbonBP, double CalendarBP);

public sealed record Diagnosis(DiagnosticCode Code, string Message, string? Position, string? ObjectId);
