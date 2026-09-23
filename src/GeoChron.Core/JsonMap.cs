using System.Text.Json.Nodes;

namespace GeoChron.Core;

/// <summary>Deterministic entity &lt;-&gt; JSON mapping used by events, export and digests.</summary>
public static class JsonMap
{
    public static JsonObject ToJson(DepthDatum d) => new()
    {
        ["id"] = d.Id, ["name"] = d.Name, ["basis"] = d.Basis, ["description"] = d.Description
    };

    public static DepthDatum DatumFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(),
        o["basis"]!.GetValue<string>(), o["description"]!.GetValue<string>());

    public static JsonObject ToJson(CoreSegment s) => new()
    {
        ["id"] = s.Id, ["datumId"] = s.DatumId, ["topRaw"] = s.TopRaw, ["bottomRaw"] = s.BottomRaw,
        ["topM"] = s.TopM, ["bottomM"] = s.BottomM, ["unit"] = s.Unit,
        ["inverted"] = s.Inverted, ["boundaryBelow"] = s.BoundaryBelow
    };

    public static CoreSegment SegmentFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["datumId"]!.GetValue<string>(),
        o["topRaw"]!.GetValue<string>(), o["bottomRaw"]!.GetValue<string>(),
        o["topM"]!.GetValue<double>(), o["bottomM"]!.GetValue<double>(),
        o["unit"]!.GetValue<string>(), o["inverted"]!.GetValue<bool>(),
        o["boundaryBelow"]?.GetValue<string>());

    public static JsonObject ToJson(AgeDistribution d) => new()
    {
        ["kind"] = d.Kind, ["unit"] = d.Unit, ["mean"] = d.Mean, ["sd"] = d.Sd,
        ["samples"] = new JsonArray(d.Samples.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>())
    };

    public static AgeDistribution DistributionFrom(JsonObject o) => new(
        o["kind"]!.GetValue<string>(), o["unit"]!.GetValue<string>(),
        o["mean"]!.GetValue<double>(), o["sd"]!.GetValue<double>(),
        o["samples"] is JsonArray a ? a.Select(x => x!.GetValue<double>()).ToArray() : Array.Empty<double>());

    public static JsonObject ToJson(ReservoirOffset r) => new()
    {
        ["mean"] = r.Mean, ["sd"] = r.Sd, ["unit"] = r.Unit, ["basis"] = r.Basis
    };

    public static ReservoirOffset OffsetFrom(JsonObject o) => new(
        o["mean"]!.GetValue<double>(), o["sd"]!.GetValue<double>(),
        o["unit"]!.GetValue<string>(), o["basis"]!.GetValue<string>());

    public static JsonObject ToJson(AgeAnchor a)
    {
        var o = new JsonObject
        {
            ["id"] = a.Id, ["segmentId"] = a.SegmentId, ["depthRaw"] = a.DepthRaw, ["depthM"] = a.DepthM,
            ["distribution"] = ToJson(a.Distribution),
            ["eventLayerId"] = a.EventLayerId, ["source"] = a.Source
        };
        o["offset"] = a.Offset != null ? ToJson(a.Offset) : null;
        return o;
    }

    public static AgeAnchor AnchorFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["segmentId"]!.GetValue<string>(),
        o["depthRaw"]!.GetValue<string>(), o["depthM"]!.GetValue<double>(),
        DistributionFrom(o["distribution"]!.AsObject()),
        o["offset"] is JsonObject off ? OffsetFrom(off) : null,
        o["eventLayerId"]?.GetValue<string>(),
        o["source"]!.GetValue<string>());

    public static JsonObject ToJson(EventLayer e) => new()
    {
        ["id"] = e.Id, ["segmentId"] = e.SegmentId, ["depthRaw"] = e.DepthRaw, ["depthM"] = e.DepthM,
        ["name"] = e.Name, ["correlatesTo"] = e.CorrelatesTo
    };

    public static EventLayer EventLayerFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["segmentId"]!.GetValue<string>(),
        o["depthRaw"]!.GetValue<string>(), o["depthM"]!.GetValue<double>(),
        o["name"]!.GetValue<string>(), o["correlatesTo"]!.GetValue<string>());

    public static JsonObject ToJson(ExclusionDecision x) => new()
    {
        ["id"] = x.Id, ["anchorId"] = x.AnchorId, ["reason"] = x.Reason,
        ["byEventId"] = x.ByEventId, ["active"] = x.Active
    };

    public static ExclusionDecision ExclusionFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["anchorId"]!.GetValue<string>(),
        o["reason"]!.GetValue<string>(), o["byEventId"]!.GetValue<string>(),
        o["active"]!.GetValue<bool>());

    public static JsonObject ToJson(ModelParameters p) => new()
    {
        ["version"] = p.Version, ["iterations"] = p.Iterations, ["maxCandidates"] = p.MaxCandidates,
        ["tieToleranceM"] = p.TieToleranceM, ["overlapToleranceSd"] = p.OverlapToleranceSd, ["seed"] = p.Seed
    };

    public static ModelParameters ParametersFrom(JsonObject o) => new(
        o["version"]!.GetValue<int>(), o["iterations"]!.GetValue<int>(), o["maxCandidates"]!.GetValue<int>(),
        o["tieToleranceM"]!.GetValue<double>(), o["overlapToleranceSd"]!.GetValue<double>(),
        o["seed"]!.GetValue<int>());

    public static JsonObject ToJson(DepthNode n) => new()
    {
        ["depthM"] = n.DepthM, ["p5"] = n.P5, ["p50"] = n.P50, ["p95"] = n.P95
    };

    public static DepthNode NodeFrom(JsonObject o) => new(
        o["depthM"]!.GetValue<double>(), o["p5"]!.GetValue<double>(),
        o["p50"]!.GetValue<double>(), o["p95"]!.GetValue<double>());

    public static JsonObject ToJson(RateInterval r) => new()
    {
        ["segmentId"] = r.SegmentId, ["topM"] = r.TopM, ["bottomM"] = r.BottomM,
        ["rateP5"] = r.RateP5, ["rateP50"] = r.RateP50, ["rateP95"] = r.RateP95, ["unit"] = r.Unit
    };

    public static RateInterval RateFrom(JsonObject o) => new(
        o["segmentId"]!.GetValue<string>(), o["topM"]!.GetValue<double>(), o["bottomM"]!.GetValue<double>(),
        o["rateP5"]!.GetValue<double>(), o["rateP50"]!.GetValue<double>(), o["rateP95"]!.GetValue<double>(),
        o["unit"]!.GetValue<string>());

    public static JsonObject ToJson(ModelCandidate m) => new()
    {
        ["id"] = m.Id, ["segmentId"] = m.SegmentId,
        ["anchorIds"] = new JsonArray(m.AnchorIds.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
        ["droppedAnchorIds"] = new JsonArray(m.DroppedAnchorIds.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
        ["inputDigest"] = m.InputDigest, ["paramVersion"] = m.ParamVersion,
        ["dependsOn"] = new JsonArray(m.DependsOn.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>()),
        ["envelope"] = new JsonArray(m.Envelope.Select(n => ToJson(n)).ToArray<JsonNode?>()),
        ["rates"] = new JsonArray(m.Rates.Select(r => ToJson(r)).ToArray<JsonNode?>()),
        ["acceptedIterations"] = m.AcceptedIterations, ["rejectedIterations"] = m.RejectedIterations,
        ["status"] = m.Status, ["rationale"] = m.Rationale
    };

    public static ModelCandidate ModelFrom(JsonObject o) => new(
        o["id"]!.GetValue<string>(), o["segmentId"]!.GetValue<string>(),
        o["anchorIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
        o["droppedAnchorIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
        o["inputDigest"]!.GetValue<string>(), o["paramVersion"]!.GetValue<int>(),
        o["dependsOn"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
        o["envelope"]!.AsArray().Select(x => NodeFrom(x!.AsObject())).ToArray(),
        o["rates"]!.AsArray().Select(x => RateFrom(x!.AsObject())).ToArray(),
        o["acceptedIterations"]!.GetValue<int>(), o["rejectedIterations"]!.GetValue<int>(),
        o["status"]!.GetValue<string>(), o["rationale"]!.GetValue<string>());

    public static JsonObject ToJson(Diagnostic d) => new()
    {
        ["code"] = d.Code, ["message"] = d.Message, ["path"] = d.Path, ["severity"] = d.Severity
    };

    public static Diagnostic DiagnosticFrom(JsonObject o) => new(
        o["code"]!.GetValue<string>(), o["message"]!.GetValue<string>(),
        o["path"]!.GetValue<string>(), o["severity"]!.GetValue<string>());
}
