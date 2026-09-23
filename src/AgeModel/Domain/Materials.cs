using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel.Domain;

/// <summary>
/// Immutable source material. Materials are never edited in place; a correction
/// produces a new material that supersedes the old one via a decision event.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CoreMaterial), "core")]
[JsonDerivedType(typeof(SegmentMaterial), "segment")]
[JsonDerivedType(typeof(AnchorMaterial), "anchor")]
[JsonDerivedType(typeof(EventLayerMaterial), "eventlayer")]
public abstract record Material
{
    public required string Id { get; init; }          // stable identity, survives export/import
    public required string CoreId { get; init; }
    public string SourceRef { get; init; } = "";       // provenance: lab id, publication, hole log
    public string Supersedes { get; init; } = "";      // id of the material this one replaces, if any

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public string ToJson() => JsonSerializer.Serialize<Material>(this, JsonOpts);

    public static Material FromJson(string json) =>
        JsonSerializer.Deserialize<Material>(json, JsonOpts)
        ?? throw new FormatException("material payload did not deserialize");

    /// <summary>Content hash: identical payloads yield identical hashes, so identity
    /// and ordering are stable across export/import round-trips.</summary>
    public string ContentHash()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ToJson()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}

public sealed record CoreMaterial : Material
{
    public required string Name { get; init; }
    public required string DepthDatum { get; init; }   // e.g. "mcd"
    public required string DepthUnit { get; init; }    // e.g. "m"
}

public sealed record SegmentMaterial : Material
{
    public required Quantity Top { get; init; }
    public required Quantity Base { get; init; }
    /// <summary>Boundary at the TOP of this segment: "continuous" | "gap" | "unconformity".
    /// Gaps and unconformities are explicit model boundaries (rule R1).</summary>
    public required string TopBoundary { get; init; }
}

public sealed record AnchorMaterial : Material
{
    public required Quantity Depth { get; init; }
    /// <summary>Age as measured, in its original unit (e.g. "14C ka BP").</summary>
    public required Quantity Age { get; init; }
    /// <summary>Full reported age distribution; never reduced to a mean (rule R2).</summary>
    public required AgeDistribution Distribution { get; init; }
    /// <summary>Reservoir offset ΔR, itself a distribution (marine shell dates).</summary>
    public AgeDistribution? ReservoirOffset { get; init; }   // in years
    public string LabCode { get; init; } = "";
}

public sealed record EventLayerMaterial : Material
{
    public required Quantity Depth { get; init; }
    public required string EventName { get; init; }    // e.g. "tephra GSB-7 / K-Ah equivalent"
    public required AgeDistribution EventAge { get; init; }  // correlated event age, ka cal BP
}

/// <summary>A machine-readable judgment emitted by a versioned rule. These are the
/// structured state behind the hard cases — not log lines.</summary>
public sealed record Judgment(
    string RuleId,
    string RuleVersion,
    string SubjectId,
    string Status,                 // "applied" | "violated" | "diagnostic" | "excluded"
    string DetailJson);

public static class Rules
{
    public const string MonotonicWithinSegment = "R1-monotonic-within-continuous-segment";
    public const string DistributionNotMean = "R2-distribution-never-mean";
    public const string RetainEqualCandidates = "R3-retain-equal-candidates";
    public const string ExplicitBoundary = "R4-gap-unconformity-are-boundaries";
    public const string UnitNormalization = "R5-normalize-only-in-derived-layer";
    public const string RuleVersion = "1.0.0";
}
