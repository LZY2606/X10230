using System.Text.Json;

namespace AgeDepth;

public record CaseState(
    List<CoreSegment> Segments, List<AgeAnchor> Anchors, List<EventLayer> Layers,
    EngineParams Params, List<EventRecord> Decisions, EventRecord? Frozen, ComputationResult Computation);

/// <summary>Parses the offline case pack; raw representations are preserved in materials.</summary>
public static class CasePack
{
    public static JsonDocument Load(string path) => JsonDocument.Parse(File.ReadAllText(path));

    public static Quantity ParseQuantity(JsonElement q, string defaultDatum)
    {
        var raw = q.GetProperty("raw").GetString()!;
        var unit = q.GetProperty("unit").GetString()!;
        var datum = q.TryGetProperty("datum", out var d) ? d.GetString()! : defaultDatum;
        return double.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Quantity.Of(v, unit, datum)
            : Quantity.Unconvertible(raw, unit, datum, $"cannot convert '{raw}' to {unit}");
    }

    public static CoreSegment ParseSegment(JsonElement m) => new(
        m.GetProperty("id").GetString()!, m.GetProperty("coreId").GetString()!,
        ParseQuantity(m.GetProperty("top"), "mbsf"), ParseQuantity(m.GetProperty("base"), "mbsf"),
        m.GetProperty("kind").GetString()!, m.GetProperty("facies").GetString()!);

    public static AgeAnchor ParseAnchor(JsonElement m) => new(
        m.GetProperty("id").GetString()!, m.GetProperty("segmentId").GetString()!,
        ParseQuantity(m.GetProperty("depth"), "mbsf"),
        new AgeDistribution(m.GetProperty("age").GetProperty("kind").GetString()!,
            m.GetProperty("age").GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray(),
            m.GetProperty("age").GetProperty("unit").GetString()!),
        m.TryGetProperty("reservoirOffset", out var ro) && ro.ValueKind == JsonValueKind.Object
            ? ParseQuantity(ro, "ka") : null,
        m.GetProperty("source").GetString()!);

    public static EventLayer ParseLayer(JsonElement m) => new(
        m.GetProperty("id").GetString()!, m.GetProperty("segmentId").GetString()!,
        ParseQuantity(m.GetProperty("depth"), "mbsf"),
        m.GetProperty("name").GetString()!, m.GetProperty("correlatedEventId").GetString()!);

    public static Material ToMaterial(JsonElement m)
    {
        var json = m.GetRawText();
        return new Material(m.GetProperty("id").GetString()!, m.GetProperty("type").GetString()!,
            Hashing.Sha256(json), json);
    }

    public static object ParseTyped(Material m)
    {
        using var doc = JsonDocument.Parse(m.Json);
        return m.Type switch
        {
            "segment" => ParseSegment(doc.RootElement),
            "anchor" => ParseAnchor(doc.RootElement),
            "eventLayer" => ParseLayer(doc.RootElement),
            _ => throw new InvalidOperationException($"unknown material type {m.Type}")
        };
    }

    /// <summary>
    /// Seed the store: import materials, replay the scripted parameter revision,
    /// local source change and conflicting decisions, recording a computation
    /// event after each stage with input hash, parameter version and parents.
    /// </summary>
    public static void Seed(Store store, string packPath)
    {
        using var pack = Load(packPath);
        var materials = pack.RootElement.GetProperty("materials").EnumerateArray().Select(ToMaterial).ToList();
        foreach (var m in materials) store.PutMaterial(m);

        var importEv = store.AppendEvent("import", Array.Empty<string>(), ModelEngine.DefaultParams.Version,
            Hashing.Sha256(string.Join(",", materials.Select(x => x.Hash))),
            JsonSerializer.Serialize(new
            {
                packId = pack.RootElement.GetProperty("packId").GetString(),
                materialIds = materials.Select(m => m.Id).ToArray()
            }, Hashing.Json), "ok");

        RecordComputation(store, importEv.Id);

        foreach (var step in pack.RootElement.GetProperty("script").EnumerateArray())
        {
            var op = step.GetProperty("op").GetString()!;
            switch (op)
            {
                case "reviseParameter":
                    store.AppendEvent("parameter-revision", new[] { importEv.Id },
                        step.GetProperty("version").GetString()!,
                        Hashing.Sha256(step.GetRawText()),
                        JsonSerializer.Serialize(new
                        {
                            version = step.GetProperty("version").GetString(),
                            monotonicToleranceKa = step.GetProperty("monotonicToleranceKa").GetDouble(),
                            reason = step.GetProperty("reason").GetString()
                        }, Hashing.Json), "ok");
                    break;
                case "changeSource":
                {
                    var matEl = step.GetProperty("material");
                    var mat = ToMaterial(matEl);
                    store.PutMaterial(mat);
                    var ev = store.AppendEvent("source-change", new[] { importEv.Id },
                        CurrentParams(store).Version, mat.Hash,
                        JsonSerializer.Serialize(new
                        {
                            replaces = step.GetProperty("replaces").GetString(),
                            material = JsonSerializer.Deserialize<JsonElement>(mat.Json),
                            note = step.GetProperty("note").GetString()
                        }, Hashing.Json), "ok");
                    RecordComputation(store, ev.Id);
                    break;
                }
                case "decision":
                    store.AppendEvent("decision", new[] { importEv.Id }, CurrentParams(store).Version,
                        Hashing.Sha256(step.GetRawText()),
                        JsonSerializer.Serialize(new
                        {
                            action = step.GetProperty("action").GetString(),
                            target = step.GetProperty("target").GetString(),
                            rationale = step.GetProperty("rationale").GetString()
                        }, Hashing.Json), "recorded");
                    break;
            }
        }
    }

    private static EngineParams CurrentParams(Store store)
    {
        var p = ModelEngine.DefaultParams;
        foreach (var e in store.Events())
            if (e.Kind == "parameter-revision")
            {
                using var doc = JsonDocument.Parse(e.Payload);
                p = p with
                {
                    Version = doc.RootElement.GetProperty("version").GetString()!,
                    MonotonicToleranceKa = doc.RootElement.GetProperty("monotonicToleranceKa").GetDouble()
                };
            }
        return p;
    }

    public static EventRecord RecordComputation(Store store, string parentEventId)
    {
        var state = Replay(store, null);
        var payload = JsonSerializer.Serialize(new
        {
            kind = state.Computation.Kind.ToString(),
            segments = state.Computation.Segments.Select(s => new
            {
                s.SegmentId, s.InputHash,
                accepted = s.Candidates.Count(c => c.Status == "accepted"),
                rejected = s.Candidates.Count(c => c.Status == "rejected")
            }).ToArray(),
            diagnostics = state.Computation.Segments.SelectMany(s => s.Diagnostics)
                .Select(d => new { d.Code, d.SegmentId, d.Depth, d.MaterialId }).ToArray()
        }, Hashing.Json);
        return store.AppendEvent("computation", new[] { parentEventId }, state.Params.Version,
            Hashing.Sha256(payload), payload, state.Computation.Kind == ResultKind.Ok ? "ok" : "diagnostic");
    }

    /// <summary>
    /// Replay events up to a sequence number and rebuild the visible state with
    /// the dependency versions of that moment — history is never overwritten by
    /// the latest computation.
    /// </summary>
    public static CaseState Replay(Store store, long? upToSeq)
    {
        var materialsById = store.Materials().ToDictionary(m => m.Id);
        var segments = new List<CoreSegment>();
        var anchors = new List<AgeAnchor>();
        var layers = new List<EventLayer>();
        var decisions = new List<EventRecord>();
        EventRecord? frozen = null;
        var p = ModelEngine.DefaultParams;

        void AddMaterial(Material m)
        {
            switch (CasePack.ParseTyped(m))
            {
                case CoreSegment s: segments.Add(s); break;
                case AgeAnchor a: anchors.Add(a); break;
                case EventLayer l: layers.Add(l); break;
            }
        }

        foreach (var e in store.Events(upToSeq))
        {
            using var doc = JsonDocument.Parse(e.Payload);
            var root = doc.RootElement;
            switch (e.Kind)
            {
                case "import":
                    foreach (var id in root.GetProperty("materialIds").EnumerateArray().Select(x => x.GetString()!))
                        if (materialsById.TryGetValue(id, out var m)) AddMaterial(m);
                    break;
                case "parameter-revision":
                    p = p with
                    {
                        Version = root.GetProperty("version").GetString()!,
                        MonotonicToleranceKa = root.GetProperty("monotonicToleranceKa").GetDouble()
                    };
                    break;
                case "source-change":
                    var replaces = root.GetProperty("replaces").GetString()!;
                    anchors.RemoveAll(a => a.Id == replaces);
                    AddMaterial(CasePack.ToMaterial(root.GetProperty("material")));
                    break;
                case "decision":
                    decisions.Add(e);
                    break;
                case "freeze" when e.Status == "frozen":
                    frozen = e;
                    break;
            }
        }

        var computation = ModelEngine.Compute(segments, anchors, p);
        return new CaseState(segments, anchors, layers, p, decisions, frozen, computation);
    }
}
