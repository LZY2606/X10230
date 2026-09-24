namespace Chrono.Core;

public enum ComputeKind { Success, BudgetExhausted, SemanticImpossible }

public sealed record RateInterval
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required double DepthFromM { get; init; }
    public required double DepthToM { get; init; }
    public required double Rate05 { get; init; }  // cm/ka
    public required double Rate50 { get; init; }
    public required double Rate95 { get; init; }
    public string Unit => "cm/ka";
}

public sealed record BandPoint
{
    public required string Id { get; init; }
    public required double DepthM { get; init; }
    public required double Age05Ka { get; init; }
    public required double Age50Ka { get; init; }
    public required double Age95Ka { get; init; }
    public required bool Valid { get; init; }
    public string? InvalidReason { get; init; }
}

public sealed record ModelNode
{
    public required string Id { get; init; }
    public required string SourceId { get; init; } // anchor id / event id
    public required string SourceKind { get; init; }
    public required double DepthM { get; init; }
    public required double Age05Ka { get; init; }
    public required double Age50Ka { get; init; }
    public required double Age95Ka { get; init; }
    public required double[] AgeGrid { get; init; }
    public required double RawAge50Ka { get; init; }
    public required bool Pooled { get; init; }
    public bool LongTailed { get; init; }
}

public sealed record ContinuousRun
{
    public required string Id { get; init; }
    public required int Ordinal { get; init; }
    public required List<string> SegmentIds { get; init; }
    public required double TopM { get; init; }
    public required double BottomM { get; init; }
    public required string UpperBoundary { get; init; } // core-top | gap | unconformity
    public required string LowerBoundary { get; init; }
    public required List<ModelNode> Nodes { get; init; }
    public required List<RateInterval> Rates { get; init; }
    public required List<BandPoint> Band { get; init; }
    public required string RunHash { get; init; }
}

public sealed record CandidateModel
{
    public required string Id { get; init; }
    public required int Rank { get; init; }
    public required bool Adopted { get; init; }
    public required string Status { get; init; } // adopted | adopted-with-pooling | rejected
    public string? RejectReason { get; init; }
    public required double Score { get; init; }
    public required Dictionary<string, string> TieSelection { get; init; }
    public required Dictionary<string, bool> EventUsage { get; init; }
    public required List<ContinuousRun> Runs { get; init; }
    public required string InputHash { get; init; }
}

public sealed record ComputeOutcome
{
    public required ComputeKind Kind { get; init; }
    public required string MaterialHash { get; init; }
    public required RuleParams Params { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public List<CandidateModel> Candidates { get; init; } = new();
    public List<string> AffectedRuns { get; init; } = new();
    public Dictionary<string,string> RunHashes { get; init; } = new();

    public IEnumerable<CandidateModel> Adopted => Candidates.Where(c => c.Adopted);
    public IEnumerable<CandidateModel> Rejected => Candidates.Where(c => !c.Adopted);
}
