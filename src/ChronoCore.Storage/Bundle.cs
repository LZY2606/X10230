using System.Text.Json;
using ChronoCore.Domain.Engine;
using ChronoCore.Domain.Model;

namespace ChronoCore.Storage;

/// <summary>Export bundle. Identity, branch lineage and decision history are
/// part of the payload; on import the same ids and order are preserved.</summary>
public sealed record ExportBundle(
    string BundleId,
    string Format,
    int FormatVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<PackageEnvelope> Packages,
    IReadOnlyList<EventEnvelope> Events,
    IReadOnlyList<CounterfactualEnvelope> Counterfactuals,
    IReadOnlyList<BranchEnvelope> Branches);

public sealed record PackageEnvelope(
    string PackageId, string CaseId, string CaseVersion, string SourceId,
    string RawText, string InputDigest, int Ordinal);

public sealed record EventEnvelope(
    long Seq, string EventId, string BranchId, string? ParentEventId,
    string Kind, string? MaterialPackageId, string RulesetVersion, string InputDigest,
    string PayloadJson, string Actor, string CreatedAt, string? FrozenCandidateId);

public sealed record CounterfactualEnvelope(
    string ArchiveId, string BranchId, string Label,
    string BaselinePackageId, string ChangedPackageId,
    string ChangedMaterial, string ChangeKind,
    string ReportJson, string AdoptedJson, string RejectedJson);

public sealed record BranchEnvelope(string BranchId, string Label, string HeadEventId);

/// <summary>Serializes/loads bundles and imports them transactionally. A
/// failure mid-import rolls the whole batch back (half-write recovery), and
/// identity/order are restored from the envelope rather than regenerated.</summary>
public static class BundleIo
{
    public static string Serialize(ExportBundle bundle) =>
        JsonSerializer.Serialize(bundle, Replay.JsonOpts);

    public static ExportBundle Deserialize(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<ExportBundle>(json, Replay.JsonOpts);
            if (bundle is null || bundle.FormatVersion < 1)
                throw new BundleFormatException("BUNDLE_EMPTY", "导出束为空或缺少格式版本");
            if (bundle.Format != "chrono-bundle")
                throw new BundleFormatException("BUNDLE_FORMAT", $"未知导出格式 {bundle.Format}");
            return bundle;
        }
        catch (JsonException jex)
        {
            throw new BundleFormatException("BUNDLE_CORRUPT", "导出束 JSON 损坏：" + jex.Message);
        }
    }
}

public sealed class BundleFormatException : Exception
{
    public string Code { get; }
    public BundleFormatException(string code, string message) : base(message) => Code = code;
}
