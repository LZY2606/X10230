namespace DepthAge.Domain;

/// <summary>
/// Machine-readable assertion tokens for the case pack, e.g. "status:Ok",
/// "diag:InvertedAges", "accepted:&gt;=2", "raw:A08". Evaluated against the projected
/// branch state plus the latest derivation record.
/// </summary>
public static class CaseAsserts
{
    public sealed class Context
    {
        public required IEventStore Store { get; init; }
        public required string BranchId { get; init; }
        public BranchState? State { get; init; }
        public DerivationRecord? LatestDerivation { get; init; }
        public string? LatestDerivationId { get; init; }
        public string? LatestStatus { get; init; }
        public required string[] DiagnosticCodes { get; init; }
    }

    public static AssertionResult Evaluate(string token, Context ctx,
        IReadOnlyDictionary<string, List<string>> stepEvents)
    {
        try
        {
            var (passed, detail) = Eval(token, ctx, stepEvents);
            return new AssertionResult { Token = token, Passed = passed, Detail = detail };
        }
        catch (Exception ex)
        {
            return new AssertionResult { Token = token, Passed = false, Detail = "error: " + ex.Message };
        }
    }

    private static (bool, string) Eval(string token, Context ctx,
        IReadOnlyDictionary<string, List<string>> stepEvents)
    {
        var state = ctx.State;
        var record = ctx.LatestDerivation;

        if (token == "noDiag")
        {
            var errors = (state?.Diagnostics ?? []).Concat(record?.Diagnostics ?? [])
                .Where(d => d.Severity == DiagSeverity.Error).Select(d => d.Code).ToArray();
            return (errors.Length == 0, errors.Length == 0 ? "no error diagnostics" : string.Join(",", errors));
        }
        if (token.StartsWith("diag:"))
        {
            var rest = token["diag:".Length..];
            var parts = rest.Split(':', 2);
            var code = parts[0];
            var matches = (state?.Diagnostics ?? []).Concat(record?.Diagnostics ?? [])
                .Where(d => d.Code == code)
                .Where(d => parts.Length == 1 ||
                            d.Message.Contains(parts[1], StringComparison.OrdinalIgnoreCase) ||
                            d.Position.Contains(parts[1], StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return (matches.Length > 0,
                matches.Length > 0 ? $"found {matches.Length} diagnostic(s) {code}" : $"no diagnostic {code}");
        }
        if (token.StartsWith("status:"))
        {
            var want = token["status:".Length..];
            return (ctx.LatestStatus == want, $"status={ctx.LatestStatus}");
        }
        if (token.StartsWith("candidates:>="))
        {
            var n = int.Parse(token["candidates:>=".Length..]);
            var count = record?.Groups.SelectMany(g => g.Candidates)
                .Count(c => c.RejectedReason is null) ?? 0;
            return (count >= n, $"candidates={count}");
        }
        if (token.StartsWith("accepted:>="))
        {
            var n = int.Parse(token["accepted:>=".Length..]);
            var count = record?.Groups.SelectMany(g => g.Candidates).Count(c => c.Accepted) ?? 0;
            return (count >= n, $"accepted={count}");
        }
        if (token.StartsWith("groups:"))
        {
            var n = int.Parse(token["groups:".Length..]);
            var count = record?.Groups.Length ?? 0;
            return (count == n, $"groups={count}");
        }
        if (token.StartsWith("reused:>="))
        {
            var n = int.Parse(token["reused:>=".Length..]);
            var count = record?.ReusedGroupDigests.Length ?? 0;
            return (count >= n, $"reused={count}");
        }
        if (token.StartsWith("recomputed:>="))
        {
            var n = int.Parse(token["recomputed:>=".Length..]);
            var count = record?.RecomputedGroupDigests.Length ?? 0;
            return (count >= n, $"recomputed={count}");
        }
        if (token.StartsWith("frozen:>="))
        {
            var n = int.Parse(token["frozen:>=".Length..]);
            var count = state?.Freezes.Count ?? 0;
            return (count >= n, $"frozen={count}");
        }
        if (token.StartsWith("branches:>="))
        {
            var n = int.Parse(token["branches:>=".Length..]);
            var count = Projector.ProjectAll(ctx.Store.ReadAll()).Count;
            return (count >= n, $"branches={count}");
        }
        if (token == "conflictRecorded")
        {
            var count = state?.Conflicts.Count ?? 0;
            return (count > 0, $"conflicts={count}");
        }
        if (token == "staleRejected")
            return (ctx.DiagnosticCodes.Contains("ConcurrencyConflict"), "concurrency conflict observed");
        if (token.StartsWith("raw:"))
        {
            var id = token["raw:".Length..];
            var measured = FindMeasured(state, id);
            return (measured is not null && !string.IsNullOrEmpty(measured.Raw),
                measured is null ? $"object {id} not found" : $"raw='{measured.Raw}'");
        }
        if (token.StartsWith("unconvertible:"))
        {
            var id = token["unconvertible:".Length..];
            var measured = FindMeasured(state, id);
            return (measured is not null && measured.Value is null,
                measured is null ? $"object {id} not found" : $"value={measured.Value?.ToString() ?? "null"}");
        }
        if (token.StartsWith("excluded:"))
        {
            var id = token["excluded:".Length..];
            var active = state?.Exclusions.Any(e => e.AnchorId == id && e.Active) ?? false;
            return (active, $"activeExclusion={active}");
        }
        if (token.StartsWith("notExcluded:"))
        {
            var id = token["notExcluded:".Length..];
            var active = state?.Exclusions.Any(e => e.AnchorId == id && e.Active) ?? false;
            return (!active, $"activeExclusion={active}");
        }
        if (token.StartsWith("policy:"))
        {
            var id = token["policy:".Length..];
            var found = state?.Policies.Any(p => p.PolicyId == id) ?? false;
            return (found, $"policy={found}");
        }
        if (token.StartsWith("acceptedModel:"))
        {
            var key = token["acceptedModel:".Length..];
            var found = state?.Acceptances.Any(a => a.ModelKey == key) ?? false;
            return (found, $"accepted={found}");
        }
        if (token.StartsWith("event:"))
        {
            var stepId = token["event:".Length..];
            var found = stepEvents.TryGetValue(stepId, out var ids) && ids.Count > 0;
            return (found, $"events={ids?.Count ?? 0}");
        }
        return (false, $"unknown assertion token '{token}'");
    }

    private static Measured? FindMeasured(BranchState? state, string objectId)
    {
        if (state is null) return null;
        var anchor = state.Anchors.FirstOrDefault(a => a.Id == objectId);
        if (anchor is not null) return anchor.Depth;
        var bed = state.EventBeds.FirstOrDefault(b => b.Id == objectId);
        if (bed is not null) return bed.Depth;
        var segment = state.Segments.FirstOrDefault(s => s.Id == objectId);
        return segment?.Top;
    }
}
