using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>Append-only event log abstraction; implemented by the SQLite store.</summary>
public interface IEventLog
{
    long CurrentSeq { get; }
    IReadOnlyList<Event> Read(long? upToSeq = null);
    Event Append(string type, JsonObject payload, string[] dependsOn, string? inputDigest, int paramVersion);
    /// <summary>Optimistic-concurrency append; returns null when the log moved on.</summary>
    Event? AppendIfUnchanged(string type, JsonObject payload, string[] dependsOn, string? inputDigest, int paramVersion, long expectedSeq);
}

public sealed record FreezeResult(bool Ok, Event? Event, string? ConflictReason);

/// <summary>
/// The review session: immutable materials + mutable decisions layered over the
/// event log. Every operation is directly callable from tests; nothing lives
/// only in the browser.
/// </summary>
public sealed class Session
{
    private readonly IEventLog _log;
    public Session(IEventLog log) => _log = log;

    public IReadOnlyList<Event> Events(long? upToSeq = null) => _log.Read(upToSeq);

    public (Event Event, IReadOnlyList<Diagnostic> Diagnostics) ImportMaterial(JsonObject material)
    {
        var diags = ValidateMaterial(material);
        var payload = new JsonObject { ["material"] = material.DeepClone() };
        var evt = _log.Append(EventTypes.MaterialImported, payload, Array.Empty<string>(), null, CurrentParamVersion());
        return (evt, diags);
    }

    public Event ReviseParameters(DeriveParameters pars, string note)
    {
        var payload = new JsonObject
        {
            ["version"] = pars.Version,
            ["mcBudget"] = pars.McBudget,
            ["minValidDraws"] = pars.MinValidDraws,
            ["equivalenceTolerance"] = pars.EquivalenceTolerance,
            ["seed"] = pars.Seed,
            ["note"] = note
        };
        return _log.Append(EventTypes.ParameterRevised, payload, Array.Empty<string>(), null, pars.Version);
    }

    public DeriveParameters CurrentParameters(long? upToSeq = null)
    {
        var pars = DeriveParameters.Default;
        foreach (var e in _log.Read(upToSeq).Where(e => e.Type == EventTypes.ParameterRevised))
        {
            pars = new DeriveParameters(
                e.Payload["version"]!.GetValue<int>(),
                e.Payload["mcBudget"]!.GetValue<int>(),
                e.Payload["minValidDraws"]!.GetValue<int>(),
                e.Payload["equivalenceTolerance"]!.GetValue<double>(),
                e.Payload["seed"]!.GetValue<int>());
        }
        return pars;
    }

    public int CurrentParamVersion(long? upToSeq = null) => CurrentParameters(upToSeq).Version;

    public MaterialsSnapshot Snapshot(long? upToSeq = null)
    {
        DepthDatum? datum = null;
        var segments = new List<Segment>();
        var boundaries = new List<Boundary>();
        var anchors = new List<AgeAnchor>();
        var offsets = new List<ReservoirOffset>();
        var layers = new List<EventLayer>();
        var exclusions = new List<ExclusionDecision>();
        var diags = new List<Diagnostic>();

        foreach (var e in _log.Read(upToSeq).Where(e => e.Type == EventTypes.MaterialImported))
        {
            var m = e.Payload["material"]!.AsObject();
            try
            {
                var (kind, material) = MaterialJson.Parse(m);
                switch (material)
                {
                    case DepthDatum d: datum = d; break;
                    case Segment s: segments.Add(s); break;
                    case Boundary b: boundaries.Add(b); break;
                    case AgeAnchor a: anchors.Add(a); break;
                    case ReservoirOffset r: offsets.Add(r); break;
                    case EventLayer l: layers.Add(l); break;
                    case ExclusionDecision x: exclusions.Add(x); break;
                }
                diags.AddRange(ValidateMaterial(m));
            }
            catch (FormatException fe)
            {
                diags.Add(new Diagnostic("bad-material", fe.Message, $"event:{e.Id}", "error"));
            }
        }
        return new MaterialsSnapshot(datum, segments, boundaries, anchors, offsets, layers, exclusions, diags);
    }

    private static List<Diagnostic> ValidateMaterial(JsonObject m)
    {
        var diags = new List<Diagnostic>();
        string kind = m["kind"]?.GetValue<string>() ?? "?";
        string id = m["id"]?.GetValue<string>() ?? "?";
        if (kind == MaterialKinds.AgeAnchor || kind == MaterialKinds.EventLayer)
        {
            string unit = m[kind == MaterialKinds.AgeAnchor ? "depthUnit" : "depthUnit"]?.GetValue<string>() ?? "";
            double depth = m["depth"]?.GetValue<double>() ?? 0;
            if (!UnitRegistry.TryNormalizeDepth(new Quantity(depth, unit), out _))
                diags.Add(new Diagnostic("unconvertible-depth-unit",
                    $"unit '{unit}' not recognized; value kept raw at its position",
                    $"{kind}:{id}@depth", "error"));
        }
        if (kind == MaterialKinds.AgeAnchor)
        {
            var age = m["age"]!.AsObject();
            string unit = age["unit"]?.GetValue<string>() ?? "";
            if (!UnitRegistry.TryNormalizeAge(new Quantity(0, unit), out _))
                diags.Add(new Diagnostic("unconvertible-age-unit",
                    $"age unit '{unit}' not recognized", $"age-anchor:{id}@age", "error"));
        }
        return diags;
    }

    /// <summary>Input digest over the materials that feed a derivation.</summary>
    public string InputDigest(long? upToSeq = null)
    {
        var arr = new JsonArray();
        foreach (var e in _log.Read(upToSeq).Where(e => e.Type == EventTypes.MaterialImported))
            arr.Add(e.Payload["material"]!.DeepClone());
        return "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(arr))))[..16].ToLowerInvariant();
    }

    public (DeriveOutcome Outcome, Event? Event) Derive(string[]? dependsOn = null)
    {
        var pars = CurrentParameters();
        var snapshot = Snapshot();
        var outcome = DepthAgeEngine.Derive(snapshot, pars);
        if (outcome is DeriveOutcome.Derived d)
        {
            var payload = ModelSetJson.ToJson(d.Models);
            payload["inputDigest"] = InputDigest();
            var deps = dependsOn ?? _log.Read().Select(e => e.Id).ToArray();
            var evt = _log.Append(EventTypes.ModelDerived, payload, deps, InputDigest(), pars.Version);
            foreach (var v in d.Models.Verdicts)
                _log.Append(EventTypes.RuleVerdict, (JsonObject)v.DeepClone(), new[] { evt.Id }, InputDigest(), pars.Version);
            return (outcome, evt);
        }
        return (outcome, null);
    }

    /// <summary>Freeze with optimistic concurrency; the loser gets a FreezeRejected event.</summary>
    public FreezeResult Freeze(string modelSetId, string frozenBy, long expectedSeq)
    {
        var payload = new JsonObject
        {
            ["modelSetId"] = modelSetId,
            ["frozenBy"] = frozenBy
        };
        var evt = _log.AppendIfUnchanged(EventTypes.ModelFrozen, payload, Array.Empty<string>(), null, CurrentParamVersion(), expectedSeq);
        if (evt is not null) return new FreezeResult(true, evt, null);

        var rejected = _log.Append(EventTypes.FreezeRejected, new JsonObject
        {
            ["modelSetId"] = modelSetId,
            ["frozenBy"] = frozenBy,
            ["reason"] = $"log advanced beyond expected seq {expectedSeq}; concurrent freeze lost"
        }, Array.Empty<string>(), null, CurrentParamVersion());
        return new FreezeResult(false, rejected, "concurrent-modification");
    }

    public Event Decide(string type, string candidateId, string rationale, string decidedBy)
    {
        var payload = new JsonObject
        {
            ["candidateId"] = candidateId,
            ["rationale"] = rationale,
            ["decidedBy"] = decidedBy
        };
        return _log.Append(type, payload, Array.Empty<string>(), null, CurrentParamVersion());
    }

    /// <summary>
    /// Local recompute: derive again after a source change and record exactly
    /// which segments were affected. Unaffected candidates keep their
    /// content-hash identities, so the blast radius is provable.
    /// </summary>
    public (Event RecomputeEvent, DeriveOutcome Outcome, Event? DerivedEvent) LocalRecompute(string triggerEventId, string[] affectedSegments)
    {
        var prior = _log.Read().LastOrDefault(e => e.Type == EventTypes.ModelDerived);
        var (outcome, derivedEvt) = Derive();
        var payload = new JsonObject
        {
            ["triggerEventId"] = triggerEventId,
            ["affectedSegments"] = new JsonArray(affectedSegments.Select(JsonValue.Create).ToArray()),
            ["priorModelSet"] = prior?.Payload["id"]?.GetValue<string>(),
            ["newModelSet"] = derivedEvt?.Payload["id"]?.GetValue<string>()
        };
        var evt = _log.Append(EventTypes.LocalRecompute, payload,
            derivedEvt is null ? new[] { triggerEventId } : new[] { triggerEventId, derivedEvt.Id },
            InputDigest(), CurrentParamVersion());
        return (evt, outcome, derivedEvt);
    }

    /// <summary>State as visible at a given sequence — time travel restores old conclusions.</summary>
    public JsonObject StateAt(long? upToSeq)
    {
        var events = _log.Read(upToSeq);
        var derived = events.LastOrDefault(e => e.Type == EventTypes.ModelDerived);
        var frozen = events.LastOrDefault(e => e.Type == EventTypes.ModelFrozen);
        var decisions = events
            .Where(e => e.Type is EventTypes.CandidateAccepted or EventTypes.CandidateRejected)
            .Select(e => (JsonObject)e.Payload.DeepClone())
            .ToArray();
        return new JsonObject
        {
            ["seq"] = events.Count == 0 ? 0 : events.Max(e => e.Seq),
            ["paramVersion"] = CurrentParamVersion(upToSeq),
            ["modelSet"] = derived is null ? null : derived.Payload.DeepClone(),
            ["frozen"] = frozen is null ? null : frozen.Payload.DeepClone(),
            ["decisions"] = new JsonArray(decisions.Select(d => (JsonNode)d).ToArray())
        };
    }
}

public static class ModelSetJson
{
    public static JsonObject ToJson(ModelSet ms) => new()
    {
        ["id"] = ms.Id,
        ["candidates"] = new JsonArray(ms.Candidates.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["label"] = c.Label,
            ["status"] = c.Status,
            ["support"] = c.Support,
            ["anchorIds"] = new JsonArray(c.AnchorIds.Select(JsonValue.Create).ToArray()),
            ["segments"] = new JsonArray(c.Segments.Select(s => (JsonNode)new JsonObject
            {
                ["segmentId"] = s.SegmentId,
                ["validDraws"] = s.ValidDraws,
                ["totalDraws"] = s.TotalDraws,
                ["rates"] = new JsonArray(s.Rates.Select(r => (JsonNode)new JsonObject
                {
                    ["segmentId"] = r.SegmentId,
                    ["topDepth"] = r.TopDepth,
                    ["baseDepth"] = r.BaseDepth,
                    ["medianRate"] = new JsonObject { ["value"] = r.MedianRate.Value, ["unit"] = r.MedianRate.Unit },
                    ["p05"] = new JsonObject { ["value"] = r.P05.Value, ["unit"] = r.P05.Unit },
                    ["p95"] = new JsonObject { ["value"] = r.P95.Value, ["unit"] = r.P95.Unit }
                }).ToArray()).ToArray(),
                ["band"] = new JsonArray(s.Band.Select(b => (JsonNode)new JsonObject
                {
                    ["depth"] = b.Depth,
                    ["p05"] = b.P05,
                    ["p50"] = b.P50,
                    ["p95"] = b.P95,
                    ["ageUnit"] = b.AgeUnit
                }).ToArray()).ToArray())
            }).ToArray()).ToArray()
        }).ToArray()).ToArray(),
        ["diagnostics"] = new JsonArray(ms.Diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code, ["message"] = d.Message, ["position"] = d.Position, ["severity"] = d.Severity
        }).ToArray()).ToArray(),
        ["verdicts"] = new JsonArray(ms.Verdicts.Select(v => (JsonNode)v.DeepClone()).ToArray()).ToArray()
    };
}
