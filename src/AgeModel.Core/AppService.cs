using System.Text.Json;

namespace AgeModel.Core;

public static class EntityKind
{
    public const string Section = "section";
    public const string Anchor = "anchor";
    public const string EventLayer = "eventlayer";
    public const string Decision = "decision";
}

public sealed record Materials(
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Decisions);

public sealed record ImpactReport(
    bool FrozenModelFound,
    string? FrozenCandidateId,
    string? AffectedSegmentId,
    bool MonotonicityHolds,
    int CandidateCountBefore,
    int CandidateCountAfter,
    IReadOnlyList<string> RateChanges,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Orchestrates immutable materials, mutable decisions, engine derivation and the store.
/// This is the server-side application layer; tests call it directly.
/// </summary>
public sealed class AppService
{
    private readonly SqliteStore _store;
    private static readonly JsonSerializerOptions J = SqliteStore.JsonOpts;

    public AppService(SqliteStore store) => _store = store;

    public SqliteStore Store => _store;

    public string RuleVersion
    {
        get => _store.Meta("ruleVersion") ?? "rules-v1";
        set => _store.SetMeta("ruleVersion", value);
    }

    // ---- loading -----------------------------------------------------------

    public Materials LoadMaterials()
    {
        var rows = _store.ReadEntities();
        var sections = rows.Where(r => r.Kind == EntityKind.Section).Select(r => JsonSerializer.Deserialize<CoreSection>(r.Json, J)!).ToList();
        var anchors = rows.Where(r => r.Kind == EntityKind.Anchor).Select(r => JsonSerializer.Deserialize<AgeAnchor>(r.Json, J)!).ToList();
        var layers = rows.Where(r => r.Kind == EntityKind.EventLayer).Select(r => JsonSerializer.Deserialize<EventLayer>(r.Json, J)!).ToList();
        var decisions = rows.Where(r => r.Kind == EntityKind.Decision).Select(r => JsonSerializer.Deserialize<ExclusionDecision>(r.Json, J)!).ToList();
        return new Materials(sections, anchors, layers, decisions);
    }

    private ModelInput CurrentInput(int budget = 64)
    {
        var m = LoadMaterials();
        return new ModelInput(m.Sections, m.Anchors, m.EventLayers, m.Decisions, RuleVersion, budget);
    }

    // ---- import / revise immutable materials -------------------------------

    public BuildResult ImportMaterials(
        IReadOnlyList<CoreSection>? sections = null,
        IReadOnlyList<AgeAnchor>? anchors = null,
        IReadOnlyList<EventLayer>? eventLayers = null,
        string actor = "importer",
        string? batchId = null,
        HashSet<string>? changedIds = null)
    {
        batchId ??= $"batch-{Guid.NewGuid():N}";
        var existing = _store.ReadEntities();
        var known = existing.ToDictionary(r => (r.Kind, r.Id));
        var upserts = new List<(string Kind, string Id, string Json)>();
        var headEvents = new List<DomainEvent>();
        long seq = _store.LastSeq();

        foreach (var s in sections ?? Array.Empty<CoreSection>())
            U(EntityKind.Section, s.Id, s);
        foreach (var a in anchors ?? Array.Empty<AgeAnchor>())
            U(EntityKind.Anchor, a.Id, a);
        foreach (var e in eventLayers ?? Array.Empty<EventLayer>())
            U(EntityKind.EventLayer, e.Id, e);

        void U(string kind, string id, object entity)
        {
            var json = JsonSerializer.Serialize(entity, J);
            upserts.Add((kind, id, json));
            seq++;
            var existed = known.TryGetValue((kind, id), out var oldRow) && oldRow.Json != json;
            headEvents.Add(new DomainEvent(seq, $"ev-{seq:D6}",
                existed ? Ev.MaterialRevised : Ev.MaterialImported,
                JsonSerializer.Serialize(new { kind, id, entity, revised = existed }, J),
                DateTime.UtcNow.ToString("o"), actor, batchId));
        }

        var merged = Merge(existing, upserts);
        var input = new ModelInput(merged.Sections, merged.Anchors, merged.EventLayers, merged.Decisions,
            RuleVersion, 64);
        var result = new Engine { Actor = "engine" }.Build(input, batchId, seq, changedIds);

        _store.AppendBatch(new SqliteStore.BatchWrite(batchId,
            headEvents.Concat(result.Events).ToList(),
            upserts.Select(u => (u.Kind, u.Id, u.Json,
                UpdatedSeq: headEvents.First(e =>
                    (e.Kind == Ev.MaterialImported || e.Kind == Ev.MaterialRevised)
                    && e.PayloadJson.Contains($"\"id\":\"{u.Id}\"")).Seq)).ToList(),
            result.Candidates));
        return result;
    }

    private static Materials Merge(IReadOnlyList<StoredEntityRow> existing,
        IReadOnlyList<(string Kind, string Id, string Json)> upserts)
    {
        var byKey = existing.ToDictionary(r => (r.Kind, r.Id), r => r.Json);
        foreach (var u in upserts) byKey[(u.Kind, u.Id)] = u.Json;
        T[] Get<T>(string kind) => byKey.Where(kv => kv.Key.Kind == kind)
            .Select(kv => JsonSerializer.Deserialize<T>(kv.Value, J)!).ToArray();
        return new Materials(Get<CoreSection>(EntityKind.Section),
            Get<AgeAnchor>(EntityKind.Anchor),
            Get<EventLayer>(EntityKind.EventLayer),
            Get<ExclusionDecision>(EntityKind.Decision));
    }

    /// <summary>Parameter/rule revision: immutable materials unchanged, new rule version, full re-derivation.</summary>
    public BuildResult ReviseParameters(string newRuleVersion, string actor = "reviewer")
    {
        RuleVersion = newRuleVersion;
        var batchId = $"batch-{Guid.NewGuid():N}";
        long seq = _store.LastSeq() + 1;
        var ev = new DomainEvent(seq, $"ev-{seq:D6}", Ev.ParameterRevised,
            JsonSerializer.Serialize(new { ruleVersion = newRuleVersion }, J),
            DateTime.UtcNow.ToString("o"), actor, batchId);
        var input = CurrentInput();
        var result = new Engine { Actor = "engine" }.Build(input, batchId, seq);
        _store.AppendBatch(new SqliteStore.BatchWrite(batchId,
            new[] { ev }.Concat(result.Events).ToList(),
            new[] { ("meta", "ruleVersion", (string)newRuleVersion, UpdatedSeq: seq) },
            result.Candidates));
        return result;
    }
