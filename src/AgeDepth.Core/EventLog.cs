using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventKind
{
    ImportMaterial,
    ParameterRevision,
    ExclusionDecision,
    SourceCorrection,   // changes exactly one direct source value
    FreezeModel,
    ReviewDecision      // accept / reject / adjust after seeing the page
}

/// <summary>
/// An immutable audit event. <see cref="ParentEventIds"/> forms a DAG (branching allowed);
/// every event records the rule version it was taken under and an input/parent digest.
/// </summary>
public sealed record AuditEvent(
    string Id,
    EventKind Kind,
    string Label,
    string Actor,
    string AtUtc,
    IReadOnlyList<string> ParentEventIds,
    JsonElement Payload,
    string RuleVersion,
    string? FreezesCandidateId = null,
    string? Branch = null)
{
    public string Digest => Determinism.Sha8(
        $"{Id}|{Kind}|{Label}|{Actor}|{AtUtc}|{string.Join(",", ParentEventIds)}|{Payload.GetRawText()}|{RuleVersion}");
}

/// <summary>
/// A machine-readable judgment attached to a checkout/derivation. Encodes the three hard
/// principles (monotonicity only within unbroken sections, distributions not reduced to a
/// mean, all tied-optimal models retained) with affected objects and a verdict code.
/// </summary>
public sealed record Judgment(
    string Id,
    string Code,
    string Verdict,          // holds | violated | boundary | retained | unresolved
    string Principle,
    string EventId,
    IReadOnlyList<string> AffectedObjectIds,
    JsonElement Evidence,    // structured replayable evidence
    string AtUtc);

/// <summary>State reconstructed by replaying a prefix of the event log from a head event.</summary>
public sealed record CheckoutState(
    MaterialBundle Material,
    RuleSet Rules,
    IReadOnlyList<ExclusionDecision> Decisions,
    IReadOnlyList<string> FrozenCandidateIds,
    IReadOnlyList<AuditEvent> Events,
    IReadOnlyList<string> ActivePath);

/// <summary>
/// Append-only, branching event log. Order and parent links are stable, which is what the
/// export/import round-trip preserves.
/// </summary>
public sealed class EventLog
{
    private readonly List<AuditEvent> _events = new();

    public IReadOnlyList<AuditEvent> Events => _events;

    public AuditEvent Append(EventKind kind, string label, string actor,
        IReadOnlyList<string> parents, object payload, string ruleVersion,
        string? freezesCandidateId = null, string? branch = null)
    {
        var element = JsonSerializer.SerializeToElement(payload, JsonOptions.O);
        var seq = _events.Count;
        var id = $"ev-{seq:D3}-{Determinism.Sha8($"{kind}|{label}|{seq}|{element.GetRawText()}")}";
        var ev = new AuditEvent(id, kind, label, actor,
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            parents, element, ruleVersion, freezesCandidateId, branch);
        _events.Add(ev);
        return ev;
    }

    public void Load(IEnumerable<AuditEvent> events)
    {
        _events.Clear();
        _events.AddRange(events.OrderBy(e => e.Id));
    }

    /// <summary>All ancestor events of <paramref name="headId"/> in append order (the path).</summary>
    public List<AuditEvent> PathTo(string headId)
    {
        var byId = _events.ToDictionary(e => e.Id);
        var path = new List<AuditEvent>();
        var stack = new Stack<string>();
        stack.Push(headId);
        var seen = new HashSet<string>();
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var ev)) continue;
            path.Add(ev);
            foreach (var p in ev.ParentEventIds) stack.Push(p);
        }
        return path.OrderBy(e => e.Id).ToList();
    }

    public List<string> Heads()
    {
        var referenced = new HashSet<string>(_events.SelectMany(e => e.ParentEventIds));
        return _events.Where(e => !referenced.Contains(e.Id)).Select(e => e.Id).ToList();
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions O = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
