using System.Text.Json.Nodes;

namespace GeoChron.Core;

public static class EventKinds
{
    public const string MaterialsImported = "materials.imported";
    public const string AnchorAdded = "anchor.added";
    public const string AnchorExcluded = "anchor.excluded";
    public const string AnchorReinstated = "anchor.reinstated";
    public const string AnchorDepthChanged = "anchor.depthChanged";
    public const string ParametersRevised = "parameters.revised";
    public const string ModelsDerived = "models.derived";
    public const string ModelFrozen = "model.frozen";
    public const string ModelDecided = "model.decided";
    public const string DecisionConflict = "decision.conflict";
    public const string StoreRecovered = "store.recovered";
    public const string BundleImported = "bundle.imported";
}

/// <summary>
/// A machine-readable event. Every computation records its input digest,
/// parameter version and parent dependencies; judgment basis is expressed
/// as payload codes, never only as log text.
/// </summary>
public sealed record ChronEvent(
    string EventId,
    long Seq,
    string Kind,
    JsonObject Payload,
    string[] Parents,
    string InputDigest,
    string ParamVersion,
    string At)
{
    public string CanonicalPayload() => Hashing.Canonical(Payload);
}
