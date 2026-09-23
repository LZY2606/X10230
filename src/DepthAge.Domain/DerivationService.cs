namespace DepthAge.Domain;

/// <summary>
/// Shared derive-and-record flow used by the web layer, the case replayer and tests.
/// Derivation results are content-addressed; a ModelsDerived event is appended only when
/// the branch does not already point at this exact result.
/// </summary>
public static class DerivationService
{
    public static DerivationRecord DeriveAndStore(
        IEventStore store,
        string branchId,
        DeriveOptions options,
        string actor,
        string timestamp,
        string? correlationId = null)
    {
        var events = store.ReadAll();
        var state = Projector.ProjectBranch(events, branchId);
        var rules = state.ActiveRuleSet;
        var priors = store.AllDerivations();
        var engine = new DerivationEngine();
        var record = engine.Derive(state, rules, options, priors, actor, timestamp);
        store.SaveDerivation(record);

        var latest = state.Derivations.LastOrDefault();
        if (latest?.DerivationId != record.DerivationId)
        {
            var envelope = EventEnvelope.Of(new ModelsDerived
            {
                DerivationId = record.DerivationId,
                RuleSetVersion = record.RuleSetVersion,
                InputsDigest = record.InputsDigest,
                Status = record.Status.ToString(),
                CandidateKeys = record.Groups.SelectMany(g => g.Candidates)
                    .Select(c => c.CandidateKey).ToArray(),
                GroupDigests = record.Groups.Select(g => g.GroupDigest).ToArray()
            }, branchId, actor, timestamp, correlationId);
            store.Append(envelope);
        }
        return record;
    }
}
