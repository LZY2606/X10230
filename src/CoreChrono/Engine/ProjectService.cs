using System.Text.Json;
using CoreChrono.Domain;

namespace CoreChrono.Engine;

/// <summary>Outcome of a command: success, a freeze conflict (structured), or an engine failure.</summary>
public sealed record CommandOutcome
{
    public required bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public ProjectEvent? Event { get; init; }
    public EngineResult? EngineResult { get; init; }
    public List<ImportDiagnostic> Diagnostics { get; init; } = new();
}

public sealed class ProjectService
{
    private readonly Func<DateTimeOffset> _clock;
    public ProjectService(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public static Project FromImport(string projectId, string name, ImportReport report)
    {
        if (report.Bundle is null) throw new ArgumentException("import did not produce a bundle");
        var p = new Project { Id = projectId, Name = name, Evidence = report.Bundle };
        var now = DateTimeOffset.UtcNow.ToString("O");
        var payload = JsonSerializer.SerializeToElement(new { schemaVersion = report.Bundle.SchemaVersion },
            Importer.JsonOpts);
        p.Events.Add(new ProjectEvent
        {
            Id = "EV-001", Seq = 1, At = now, Type = "evidence.imported",
            Actor = "importer", Payload = payload,
            ParamVersion = report.Bundle.Parameters.Version,
            InputDigest = ComputeDigest(report.Bundle),
            Note = "baseline import: datum, gaps, distributions, reservoir offsets, event correlations, exclusions preserved",
        });
        return p;
    }

    public static string ComputeDigest(EvidenceBundle ev)
    {
        var engine = new DepthAgeEngine(ev.Parameters);
        var probe = engine.Derive(ev, "probe", "probe");
        return probe.Derivation?.InputDigest ?? "none";
    }

    public CommandOutcome Derive(Project project, string actor, string? focusAnchorId = null, string note = "")
    {
        var ev = project.Evidence;
        var engine = new DepthAgeEngine(ev.Parameters);
        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var derivId = $"DV-{seq:D3}";
        var parent = project.Events.LastOrDefault()?.Id;
        var digest = ComputeDigest(ev);

        var result = engine.Derive(ev, derivId, parent ?? id, focusAnchorId,
            frozenInputDigest: project.FrozenDerivationId is null ? null :
                project.Derivations[project.FrozenDerivationId].FrozenInputDigest);

        var statusCode = result.Status switch
        {
            EngineStatus.Ok => "model.derived",
            EngineStatus.Incomplete => "model.incomplete",
            EngineStatus.BudgetExhausted => "model.budget-exhausted",
            EngineStatus.SemanticallyUnsolvable => "model.semantically-unsolvable",
            _ => "model.derived"
        };
        var payload = JsonSerializer.SerializeToElement(new
        {
            status = result.Status.ToString(),
            focusAnchorId,
            candidateCount = result.Derivation?.Candidates.Count ?? 0,
        }, Importer.JsonOpts);
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"), Type = statusCode,
            Actor = actor, Payload = payload, ParentEventId = parent,
            ParamVersion = ev.Parameters.Version, InputDigest = digest, Note = note,
            DerivationId = result.Derivation?.Id,
        };
        project.Events.Add(pe);
        if (result.Derivation is not null) project.Derivations[result.Derivation.Id] = result.Derivation;
        return new CommandOutcome
        {
            Ok = result.Status == EngineStatus.Ok,
            ErrorCode = result.Status == EngineStatus.Ok ? null : result.Status.ToString(),
            Message = pe.Note,
            Event = pe,
            EngineResult = result,
            Diagnostics = result.Diagnostics,
        };
    }

    public CommandOutcome Freeze(Project project, string actor, string candidateId)
    {
        var current = project.CurrentDerivation;
        if (current is null)
            return Fail("NO_DERIVATION", "cannot freeze before a derivation exists");
        var chosen = current.Candidates.FirstOrDefault(c => c.Id == candidateId);
        if (chosen is null)
            return Fail("CANDIDATE_NOT_FOUND", $"candidate {candidateId} is not in derivation {current.Id}");

        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var parent = project.Events.Last().Id;
        var frozen = current with { Frozen = true, FrozenEventId = id, FrozenInputDigest = current.InputDigest };
        project.Derivations[current.Id] = frozen;
        project.FrozenDerivationId = current.Id;
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"), Type = "model.frozen",
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new { derivationId = current.Id, candidateId, current.InputDigest },
                Importer.JsonOpts),
            ParentEventId = parent, ParamVersion = current.ParamVersion,
            InputDigest = current.InputDigest, DerivationId = current.Id,
            Note = $"frozen on {candidateId}; later anchors cannot silently replace it",
        };
        project.Events.Add(pe);
        return new CommandOutcome { Ok = true, Event = pe };
    }

    /// <summary>Add a new anchor after freezing. This produces a CONFLICT structured
    /// outcome rather than mutating the frozen model; the impact is computed as a
    /// separate side-branch derivation and both accepted/rejected results are kept.</summary>
    public CommandOutcome ProposeAnchorAfterFreeze(Project project, string actor, AgeAnchor anchor)
    {
        if (project.FrozenDerivationId is null)
            return Fail("NOT_FROZEN", "freeze-compare requires a frozen model first");

        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var parent = project.Events.Last().Id;
        var frozen = project.Derivations[project.FrozenDerivationId];

        // Side-branch evidence: original material + the proposed anchor, nothing replaced.
        var trialEv = project.Evidence with
        {
            Anchors = project.Evidence.Anchors.Append(anchor).ToList(),
        };
        var engine = new DepthAgeEngine(trialEv.Parameters);
        var side = engine.Derive(trialEv, $"DV-{seq:D3}-side", id, anchor.Id, frozen.FrozenInputDigest);

        var changed = CompareDerivations(frozen, side.Derivation!);
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"),
            Type = "anchor.proposed-after-freeze",
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new
            {
                anchorId = anchor.Id,
                frozenDerivationId = frozen.Id,
                sideDerivationId = side.Derivation?.Id,
                frozenInputDigest = frozen.FrozenInputDigest,
                changedObjects = changed,
            }, Importer.JsonOpts),
            ParentEventId = parent, ParamVersion = trialEv.Parameters.Version,
            InputDigest = side.Derivation?.InputDigest, DerivationId = side.Derivation?.Id,
            Note = "freeze conflict: new anchor evaluated on a side branch; frozen model untouched",
        };
        project.Evidence.Anchors.Add(anchor); // immutable material record keeps the proposed input
        project.Events.Add(pe);
        if (side.Derivation is not null) project.Derivations[side.Derivation.Id] = side.Derivation;
        return new CommandOutcome
        {
            Ok = true, Event = pe, EngineResult = side,
            Message = $"FREEZE_CONFLICT:{changed.Count}changed",
            Diagnostics = side.Diagnostics,
        };
    }

    public CommandOutcome ResolveFreezeConflict(Project project, string actor, bool accept, string reason)
    {
        var last = project.Events.LastOrDefault(e => e.Type == "anchor.proposed-after-freeze");
        if (last is null) return Fail("NO_PENDING_CONFLICT", "no pending freeze comparison");
        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"),
            Type = accept ? "freeze.conflict-accepted" : "freeze.conflict-rejected",
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new { proposedEventId = last.Id, reason }, Importer.JsonOpts),
            ParentEventId = last.Id,
            ParamVersion = project.Evidence.Parameters.Version,
            Note = accept
                ? "side-branch adopted as the new working model; frozen model retained in history"
                : "frozen model retained; proposed anchor kept as material but excluded by decision",
        };
        project.Events.Add(pe);
        if (!accept)
        {
            var anchorId = last.Payload.TryGetProperty("anchorId", out var av) ? av.GetString() : null;
            if (anchorId is not null)
                project.Evidence.Exclusions.Add(new ExclusionDecision
                {
                    Id = $"X-{seq:D3}", TargetKind = ExclusionTarget.Anchor, TargetId = anchorId,
                    Reason = $"rejected after freeze comparison: {reason}", SourceRef = last.Id,
                    Status = DecisionStatus.Accepted, SupersededByEventId = id,
                });
        }
        else
        {
            project.FrozenDerivationId = null; // unfreeze; a new freeze may follow a fresh derivation
        }
        return new CommandOutcome { Ok = true, Event = pe };
    }

    /// <summary>Parameter revision: creates a new parameter version; never overwrites prior ones.</summary>
    public CommandOutcome ReviseParameters(Project project, string actor, Action<ParameterSet> mutate, string note)
    {
        var oldVersion = project.Evidence.Parameters.Version;
        var digits = new string(oldVersion.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        var versionNumber = digits.Length == 0 ? 1 : int.Parse(digits);
        var ps = project.Evidence.Parameters with { Version = $"P{versionNumber + 1}", Note = note };
        mutate(ps);
        project.Evidence.Parameters = ps;

        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"), Type = "parameters.revised",
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new
            {
                fromVersion = oldVersion, toVersion = ps.Version,
                draws = ps.MonteCarloDraws, budget = ps.CandidateBudget, interpolation = ps.Interpolation, note,
            }, Importer.JsonOpts),
            ParentEventId = project.Events.Last().Id,
            ParamVersion = ps.Version, InputDigest = ComputeDigest(project.Evidence), Note = note,
        };
        project.Events.Add(pe);
        return new CommandOutcome { Ok = true, Event = pe };
    }

    /// <summary>Change exactly one direct source item. The engine then reports which
    /// derived objects depend on it (local recompute) and which are unaffected.</summary>
    public CommandOutcome ChangeSource(Project project, string actor, AgeAnchor replacement)
    {
        var idx = project.Evidence.Anchors.FindIndex(a => a.Id == replacement.Id);
        if (idx < 0) return Fail("SOURCE_NOT_FOUND", $"anchor {replacement.Id} not found");
        var original = project.Evidence.Anchors[idx];

        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var parent = project.Events.Last().Id;

        var before = project.CurrentDerivation;
        project.Evidence.Anchors[idx] = replacement;
        var derive = Derive(project, actor, replacement.Id,
            $"single direct source changed: anchor {replacement.Id}");
        var after = derive.EngineResult?.Derivation;
        var affectedSegment = replacement.SegmentId;
        var impacted = new List<string>();
        if (before is not null && after is not null)
        {
            var afterBest = after.Candidates.FirstOrDefault(c => c.EquiOptimal) ?? after.Candidates.FirstOrDefault();
            if (afterBest is not null)
                impacted = afterBest.Rates.Where(r => r.SegmentId == affectedSegment || r.AnchorIds.Contains(replacement.Id))
                                          .SelectMany(r => r.AnchorIds).Distinct().ToList();
        }
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"), Type = "source.changed",
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new
            {
                anchorId = replacement.Id,
                before = new { depthRaw = original.Depth.Raw, distribution = original.Distribution.Describe() },
                after = new { depthRaw = replacement.Depth.Raw, distribution = replacement.Distribution.Describe() },
                impactedObjects = impacted,
                localSegmentId = affectedSegment,
                derivationEventId = derive.Event?.Id,
            }, Importer.JsonOpts),
            ParentEventId = parent,
            ParamVersion = project.Evidence.Parameters.Version,
            InputDigest = after?.InputDigest,
            Note = "only one direct source item changed; dependent objects recomputed locally",
        };
        project.Events.Add(pe);
        return derive with { Ok = true, Event = derive.Event, Message = "source.changed" };
    }

    /// <summary>A human decision that conflicts with another standing decision is
    /// recorded; the conflict is surfaced, not silently resolved.</summary>
    public CommandOutcome AddDecision(Project project, string actor, ExclusionDecision decision)
    {
        var seq = project.NextSeq();
        var id = $"EV-{seq:D3}";
        var conflicting = project.Evidence.Exclusions
            .FirstOrDefault(e => e.TargetId == decision.TargetId && e.Status == DecisionStatus.Accepted
                              && decision.Status == DecisionStatus.Accepted);
        var code = conflicting is null ? "decision.recorded" : "decision.conflict";
        project.Evidence.Exclusions.Add(decision);
        var pe = new ProjectEvent
        {
            Id = id, Seq = seq, At = _clock().ToString("O"), Type = code,
            Actor = actor,
            Payload = JsonSerializer.SerializeToElement(new
            {
                decisionId = decision.Id, targetId = decision.TargetId,
                status = decision.Status.ToString(), reason = decision.Reason,
                conflictsWith = conflicting?.Id,
            }, Importer.JsonOpts),
            ParentEventId = project.Events.Last().Id,
            ParamVersion = project.Evidence.Parameters.Version,
            InputDigest = ComputeDigest(project.Evidence),
            Note = conflicting is null ? "decision recorded" : $"CONFLICT with standing decision {conflicting.Id}",
        };
        project.Events.Add(pe);
        return new CommandOutcome { Ok = true, Event = pe, Message = code };
    }

    private static List<string> CompareDerivations(Derivation frozen, Derivation side)
    {
        var changed = new List<string>();
        var fb = frozen.Candidates.FirstOrDefault(c => c.EquiOptimal) ?? frozen.Candidates.First();
        var sb = side.Candidates.FirstOrDefault(c => c.EquiOptimal) ?? side.Candidates.FirstOrDefault();
        if (sb is null) return changed;
        for (var i = 0; i < Math.Min(fb.Band.Count, sb.Band.Count); i++)
            if (Math.Abs(fb.Band[i].MedianKa - sb.Band[i].MedianKa) > 1e-6)
            {
                changed.Add($"curve@{sb.Band[i].DepthM:G4}m");
                break;
            }
        var fr = fb.Rates.Count; var sr = sb.Rates.Count;
        if (fr != sr) changed.Add($"rate-count:{fr}->{sr}");
        else
            for (var i = 0; i < fr; i++)
                if (Math.Abs(fb.Rates[i].MedianKaPerM - sb.Rates[i].MedianKaPerM) > 1e-9)
                {
                    changed.Add($"rate@{sb.Rates[i].FromDepthM:G3}-{sb.Rates[i].ToDepthM:G3}m");
                    break;
                }
        return changed;
    }

    private static CommandOutcome Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Message = message };
}
