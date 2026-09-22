using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

public enum ProjectOutcome { Imported, Derived, Frozen, FreezeConflict }

public sealed record ProjectEvent
{
    public required string Id { get; init; }                 // stable event id
    public required int Seq { get; init; }                  // replay order
    public required string At { get; init; }                // ISO timestamp
    public required string Type { get; init; }              // machine-readable event type
    public required string Actor { get; init; }
    public required JsonElement Payload { get; init; }
    public string? DerivationId { get; init; }              // derived result produced here
    public string? ParentEventId { get; init; }             // causal parent
    public string? ParamVersion { get; init; }              // rule/parameter version in force
    public string? InputDigest { get; init; }               // input summary hash
    public string? Note { get; init; }
}

/// <summary>Machine-readable judgment records emitted by the engine. These are
/// first-class structured state (not free-text logs).</summary>
public sealed record Judgment
{
    public required string Code { get; init; }
    public required string Severity { get; init; }          // info|warning|violation|diagnostic
    public required string Statement { get; init; }
    public List<string> AffectedIds { get; init; } = new();
    public string? SegmentId { get; init; }
    public string? RuleVersion { get; init; }
}

public sealed record RateBand
{
    public required string SegmentId { get; init; }
    public required double FromDepthM { get; init; }
    public required double ToDepthM { get; init; }
    public double MedianKaPerM { get; init; }
    public double LowKaPerM { get; init; }
    public double HighKaPerM { get; init; }
    public List<string> AnchorIds { get; init; } = new();
}

/// <summary>A depth-age curve node carrying a distribution sample band.</summary>
public sealed record CurvePoint
{
    public required double DepthM { get; init; }
    public double MedianKa { get; init; }
    public double Q05Ka { get; init; }
    public double Q95Ka { get; init; }
}

public sealed record ModelCandidate
{
    public required string Id { get; init; }                // stable within derivation
    public required int Rank { get; init; }
    public required string Method { get; init; }            // linear|pchip
    public required List<string> UsedAnchorIds { get; init; }
    public required List<string> ExcludedAnchorIds { get; init; }
    public double Score { get; init; }                      // lower better
    public bool EquiOptimal { get; init; }                  // tied for best
    public int TieGroup { get; init; }                      // equal-score groups
    public required List<RateBand> Rates { get; init; }
    public required List<CurvePoint> Band { get; init; }
    public List<Judgment> Judgments { get; init; } = new();
    public string? SelectedReason { get; init; }
}

/// <summary>The derived depth-age model family at one event/parameter version.</summary>
public sealed record Derivation
{
    public required string Id { get; init; }
    public required string ParamVersion { get; init; }
    public required string InputDigest { get; init; }
    public required string ParentEventId { get; init; }
    public required string Status { get; init; }            // ok|incomplete|budget-exhausted
    public List<ModelCandidate> Candidates { get; init; } = new();
    public List<Judgment> Judgments { get; init; } = new();
    public List<string> ActiveSegmentIds { get; init; } = new();
    public bool Frozen { get; init; }
    public string? FrozenEventId { get; init; }
    /// <summary>Hash of the frozen input set; later anchors must not silently replace it.</summary>
    public string? FrozenInputDigest { get; init; }
}

public sealed class Project
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public EvidenceBundle Evidence { get; set; } = null!;
    public List<ProjectEvent> Events { get; } = new();
    /// <summary>Derivation keyed by event id (one derived layer per producing event).</summary>
    public Dictionary<string, Derivation> Derivations { get; } = new();
    public string? FrozenDerivationId { get; set; }

    public int NextSeq() => Events.Count == 0 ? 1 : Events.Max(e => e.Seq) + 1;

    public Derivation? CurrentDerivation =>
        Events.Where(e => e.DerivationId is not null)
              .Select(e => Derivations.GetValueOrDefault(e.DerivationId!))
              .LastOrDefault(d => d is not null);
}

public static class Hashing
{
    public static string Sha256(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16];
    }
}
