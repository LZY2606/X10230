namespace DepthAge.Domain;

/// <summary>
/// Executes a case pack against an event store and produces a machine-readable replay
/// report. Deterministic clock: timestamps derive from the step index so replays are
/// byte-identical. Every step records its events, diagnostics, affected objects and
/// assertion results; the baseline step pins the all-material-retained digest.
/// </summary>
public sealed class CaseReplayer
{
    private readonly IEventStore _store;
    private readonly Dictionary<string, string> _latestDerivationByBranch = new();
    private readonly Dictionary<string, List<string>> _stepEvents = new();
    private int _clock;
    private string? _baselineDigest;

    public CaseReplayer(IEventStore store) => _store = store;

    private string NextTimestamp()
    {
        _clock += 1;
        return $"2024-09-01T{_clock / 3600:D2}:{_clock / 60 % 60:D2}:{_clock % 60:D2}Z";
    }

    public ReplayReport Run(CasePack pack)
    {
        var results = new List<ReplayStepResult>();
        foreach (var step in pack.Steps)
        {
            var result = ExecuteStep(pack, step);
            results.Add(result);
            if (step.MarksBaseline)
                _baselineDigest = DigestOfEverything();
        }
        var globalAsserts = pack.GlobalAsserts
            .Select(token => CaseAsserts.Evaluate(token, ContextFor(pack.Steps[^1].Branch), _stepEvents))
            .ToArray();
        var passed = results.All(r => r.Passed) && globalAsserts.All(a => a.Passed);
        return new ReplayReport
        {
            CasePackId = pack.CasePackId,
            ReplayedAt = "deterministic",
            Passed = passed,
            BaselineDigest = _baselineDigest,
            Steps = results.ToArray(),
            GlobalAssertions = globalAsserts
        };
    }

    /// <summary>Re-evaluate assertions against the current store without appending events.</summary>
    public ReplayReport Verify(CasePack pack)
    {
        var results = new List<ReplayStepResult>();
        foreach (var step in pack.Steps)
        {
            var context = ContextFor(step.Branch);
            var assertions = step.Asserts
                .Select(token => CaseAsserts.Evaluate(token, context, _stepEvents))
                .ToArray();
            results.Add(new ReplayStepResult
            {
                StepId = step.Id,
                Label = step.Label,
                Branch = step.Branch,
                Passed = assertions.All(a => a.Passed),
                EventIds = [],
                DiagnosticCodes = context.DiagnosticCodes,
                DerivationId = context.LatestDerivationId,
                Status = context.LatestStatus,
                AffectedObjects = [],
                Error = null,
                Assertions = assertions
            });
        }
        var globalAsserts = pack.GlobalAsserts
            .Select(token => CaseAsserts.Evaluate(token, ContextFor(pack.Steps[^1].Branch), _stepEvents))
            .ToArray();
        return new ReplayReport
        {
            CasePackId = pack.CasePackId,
            ReplayedAt = "verify",
            Passed = results.All(r => r.Passed) && globalAsserts.All(a => a.Passed),
            BaselineDigest = _baselineDigest,
            Steps = results.ToArray(),
            GlobalAssertions = globalAsserts
        };
    }

    private string DigestOfEverything()
    {
        var branches = Projector.ProjectAll(_store.ReadAll());
        var content = branches.OrderBy(kv => kv.Key).Select(kv => new
        {
            branch = kv.Key,
            segments = kv.Value.Segments.Select(s => s.Id).OrderBy(x => x).ToArray(),
            boundaries = kv.Value.Boundaries.Select(b => b.Id).OrderBy(x => x).ToArray(),
            anchors = kv.Value.Anchors.Select(a => a.Id).OrderBy(x => x).ToArray(),
            eventBeds = kv.Value.EventBeds.Select(b => b.Id).OrderBy(x => x).ToArray(),
            correlations = kv.Value.Correlations.Select(c => c.Id).OrderBy(x => x).ToArray(),
            reservoirs = kv.Value.ReservoirModels.Keys.OrderBy(x => x).ToArray(),
            exclusions = kv.Value.Exclusions.Select(e => $"{e.AnchorId}:{e.Active}").ToArray(),
            derivations = kv.Value.Derivations.Select(d => d.DerivationId).ToArray()
        }).ToArray();
        return Canon.StableId("baseline", content);
    }

    private CaseAsserts.Context ContextFor(string branchId)
    {
        var branches = Projector.ProjectAll(_store.ReadAll());
        branches.TryGetValue(branchId, out var state);
        var derivationId = state?.Derivations.LastOrDefault()?.DerivationId
                           ?? _latestDerivationByBranch.GetValueOrDefault(branchId);
        var record = derivationId is null ? null : _store.GetDerivation(derivationId);
        return new CaseAsserts.Context
        {
            Store = _store,
            BranchId = branchId,
            State = state,
            LatestDerivation = record,
            LatestDerivationId = derivationId,
            LatestStatus = record?.Status.ToString(),
            DiagnosticCodes = (state?.Diagnostics.Select(d => d.Code) ?? Enumerable.Empty<string>())
                .Concat(record?.Diagnostics.Select(d => d.Code) ?? Enumerable.Empty<string>())
                .Distinct().OrderBy(c => c).ToArray()
        };
    }

    // --------------------------------------------------------------- step execution

    private ReplayStepResult ExecuteStep(CasePack pack, CaseStep step)
    {
        var eventIds = new List<string>();
        var affected = new List<string>();
        var diagCodes = new List<string>();
        string? derivationId = null;
        string? status = null;
        string? error = null;

        string Append(EventPayload payload, string? cause = null)
        {
            var env = EventEnvelope.Of(payload, step.Branch, "case-replayer", NextTimestamp(),
                correlationId: pack.CasePackId, causeEventId: cause);
            _store.Append(env);
            eventIds.Add(env.EventId);
            return env.EventId;
        }

        try
        {
            switch (step.Action)
            {
                case CreateProjectAction a:
                    Append(new ProjectCreated
                    {
                        ProjectId = pack.ProjectId,
                        Title = a.Title,
                        CoreId = pack.CoreId,
                        AgeDatum = pack.AgeDatum
                    });
                    affected.Add(pack.ProjectId);
                    break;

                case DeclareDatumAction:
                    Append(new DatumDeclared { Datum = pack.Datum });
                    affected.Add(pack.Datum.Id);
                    break;

                case ImportSegmentsAction a:
                {
                    var (segments, diags) = BuildSegments(pack, a);
                    Append(new SegmentsImported { Segments = segments, Diagnostics = diags, SourceRef = a.SourceRef });
                    affected.AddRange(segments.Select(s => s.Id));
                    diagCodes.AddRange(diags.Select(d => d.Code));
                    break;
                }

                case DeclareBoundaryAction a:
                    Append(new BoundaryDeclared
                    {
                        Boundary = new BoundarySpec
                        {
                            Id = a.Id,
                            AboveSegmentId = a.AboveSegmentId,
                            BelowSegmentId = a.BelowSegmentId,
                            Kind = a.Kind,
                            Note = a.Note,
                            SourceRef = a.SourceRef
                        }
                    });
                    affected.Add(a.Id);
                    break;

                case ImportAnchorsAction a:
                {
                    var (anchors, diags) = BuildAnchors(pack, a.Anchors);
                    Append(new AnchorsImported { Anchors = anchors, Diagnostics = diags, SourceRef = a.SourceRef });
                    affected.AddRange(anchors.Select(x => x.Id));
                    diagCodes.AddRange(diags.Select(d => d.Code));
                    break;
                }

                case ImportEventBedsAction a:
                {
                    var (beds, diags) = BuildBeds(pack, a);
                    Append(new EventBedsImported { Beds = beds, Diagnostics = diags, SourceRef = a.SourceRef });
                    affected.AddRange(beds.Select(b => b.Id));
                    diagCodes.AddRange(diags.Select(d => d.Code));
                    break;
                }

                case ImportCorrelationsAction a:
                {
                    var correlations = a.Correlations.Select(c => new EventCorrelation
                    {
                        Id = c.Id,
                        EventBedId = c.EventBedId,
                        RegionalEvent = c.RegionalEvent,
                        Age = c.Age,
                        SourceRef = c.SourceRef
                    }).ToArray();
                    Append(new CorrelationsImported
                    {
                        Correlations = correlations,
                        Diagnostics = [],
                        SourceRef = a.SourceRef
                    });
                    affected.AddRange(correlations.Select(c => c.Id));
                    break;
                }

                case ImportReservoirAction a:
                {
                    var diags = new List<Diagnostic>();
                    if (a.Model.Offset.Unit != Units.C14Canonical)
                        diags.Add(DerivationEngine.Diag(DiagSeverity.Warning, "ReservoirUnitUnusual",
                            $"reservoir:{a.Model.Id}",
                            $"reservoir offset unit '{a.Model.Offset.Unit}' is not '{Units.C14Canonical}'",
                            a.Model.Id));
                    Append(new ReservoirModelImported
                    {
                        Model = new ReservoirModel
                        {
                            Id = a.Model.Id,
                            Offset = a.Model.Offset,
                            AppliesTo = a.Model.AppliesTo,
                            SourceRef = a.Model.SourceRef,
                            SupersedesId = a.Model.SupersedesId
                        },
                        Diagnostics = diags.ToArray()
                    });
                    affected.Add(a.Model.Id);
                    diagCodes.AddRange(diags.Select(d => d.Code));
                    break;
                }

                case AssignReservoirAction a:
                    Append(new ReservoirAssignmentChanged
                    {
                        AnchorId = a.AnchorId,
                        ReservoirModelId = a.ReservoirModelId,
                        Reason = a.Reason
                    });
                    affected.Add(a.AnchorId);
                    break;

                case PublishRulesAction a:
                    Append(new RuleSetPublished { RuleSet = a.RuleSet, Reason = a.Reason });
                    affected.Add(a.RuleSet.Version);
                    break;

                case ExcludeAnchorAction a:
                    Append(new AnchorExcluded
                    {
                        Exclusion = new Exclusion
                        {
                            Id = Canon.StableId("excl", new { a.AnchorId, a.Reason, at = NextTimestamp() }),
                            AnchorId = a.AnchorId,
                            Reason = a.Reason,
                            DecidedBy = a.DecidedBy,
                            DecidedAt = "case",
                            Active = true
                        }
                    });
                    affected.Add(a.AnchorId);
                    break;

                case RevokeExclusionAction a:
                {
                    var state = Projector.ProjectBranch(_store.ReadAll(), step.Branch);
                    var exclusion = state.Exclusions.LastOrDefault(e => e.AnchorId == a.ExclusionAnchorId && e.Active);
                    if (exclusion is null)
                        throw new InvalidOperationException($"no active exclusion for {a.ExclusionAnchorId}");
                    Append(new ExclusionRevoked
                    {
                        ExclusionId = exclusion.Id,
                        Reason = a.Reason,
                        DecidedBy = a.DecidedBy
                    });
                    affected.Add(a.ExclusionAnchorId);
                    break;
                }

                case AssertPolicyAction a:
                {
                    var evidence = a.EvidenceStepIds
                        .SelectMany(id => _stepEvents.GetValueOrDefault(id) ?? [])
                        .ToArray();
                    Append(new PolicyAsserted
                    {
                        PolicyId = a.PolicyId,
                        Statement = a.Statement,
                        EvidenceEventIds = evidence
                    });
                    affected.Add(a.PolicyId);
                    break;
                }

                case DeriveAction a:
                {
                    var record = DerivationService.DeriveAndStore(_store, step.Branch,
                        new DeriveOptions { RealizationBudget = a.RealizationBudget },
                        "case-replayer", NextTimestamp(), pack.CasePackId);
                    derivationId = record.DerivationId;
                    status = record.Status.ToString();
                    _latestDerivationByBranch[step.Branch] = record.DerivationId;
                    affected.AddRange(record.Groups.Select(g => g.GroupKey));
                    diagCodes.AddRange(record.Diagnostics.Select(d => d.Code));
                    var derivedEvent = _store.ReadBranch(step.Branch)
                        .Where(e => e.Envelope.Payload is ModelsDerived md && md.DerivationId == record.DerivationId)
                        .Select(e => e.Envelope.EventId)
                        .LastOrDefault();
                    if (derivedEvent is not null) eventIds.Add(derivedEvent);
                    break;
                }

                case FreezeAction a:
                {
                    var state = Projector.ProjectBranch(_store.ReadAll(), step.Branch);
                    var latest = state.Derivations.LastOrDefault()
                                 ?? throw new InvalidOperationException("no derivation to freeze");
                    var record = _store.GetDerivation(latest.DerivationId)
                                 ?? throw new InvalidOperationException("derivation record missing");
                    var best = record.Groups.SelectMany(g => g.Candidates)
                        .Where(c => c.RejectedReason is null && c.Accepted)
                        .OrderBy(c => c.Rank).ThenBy(c => c.CandidateKey)
                        .FirstOrDefault()
                               ?? throw new InvalidOperationException("no accepted candidate to freeze");
                    Append(new ModelFrozen
                    {
                        ModelKey = best.CandidateKey,
                        DerivationId = record.DerivationId,
                        Label = a.Label,
                        FrozenBy = a.FrozenBy
                    });
                    affected.Add(best.CandidateKey);
                    break;
                }

                case FreezeStaleAction a:
                {
                    var state = Projector.ProjectBranch(_store.ReadAll(), step.Branch);
                    var latest = state.Derivations.LastOrDefault()
                                 ?? throw new InvalidOperationException("no derivation to freeze");
                    var record = _store.GetDerivation(latest.DerivationId)!;
                    var best = record.Groups.SelectMany(g => g.Candidates)
                        .First(c => c.RejectedReason is null);
                    var env = EventEnvelope.Of(new ModelFrozen
                    {
                        ModelKey = best.CandidateKey,
                        DerivationId = record.DerivationId,
                        Label = a.Label,
                        FrozenBy = a.FrozenBy
                    }, step.Branch, "case-replayer", NextTimestamp(), pack.CasePackId);
                    try
                    {
                        _store.AppendIfUnchanged(env, a.StaleExpectedSeq);
                        error = "stale freeze unexpectedly succeeded";
                    }
                    catch (ConcurrencyConflictException)
                    {
                        diagCodes.Add("ConcurrencyConflict");
                        affected.Add(best.CandidateKey);
                    }
                    break;
                }

                case CreateTrialAction a:
                {
                    var (anchors, diags) = BuildAnchors(pack, [a.TrialAnchor]);
                    var mainState = Projector.ProjectBranch(_store.ReadAll(), Projector.MainBranch);
                    Append(new TrialBranchCreated
                    {
                        BranchId = a.TrialBranchId,
                        BaseSeq = mainState.LastSeq,
                        TrialAnchor = anchors[0],
                        Reason = a.Reason
                    });
                    affected.Add(a.TrialBranchId);
                    affected.Add(anchors[0].Id);
                    diagCodes.AddRange(diags.Select(d => d.Code));
                    break;
                }

                case DecideAction a:
                {
                    var state = Projector.ProjectBranch(_store.ReadAll(), step.Branch);
                    var latest = state.Derivations.LastOrDefault()
                                 ?? throw new InvalidOperationException("no derivation for decision");
                    var record = _store.GetDerivation(latest.DerivationId)!;
                    var key = a.ModelKey ?? record.Groups.SelectMany(g => g.Candidates)
                        .Where(c => c.RejectedReason is null)
                        .OrderBy(c => c.Rank).ThenBy(c => c.CandidateKey)
                        .First().CandidateKey;
                    EventPayload payload = a.Decision == "accept"
                        ? new ModelAccepted
                        {
                            ModelKey = key,
                            DerivationId = record.DerivationId,
                            DecidedBy = a.DecidedBy,
                            Rationale = a.Rationale
                        }
                        : new ModelRejected
                        {
                            ModelKey = key,
                            DerivationId = record.DerivationId,
                            DecidedBy = a.DecidedBy,
                            Rationale = a.Rationale
                        };
                    Append(payload);
                    affected.Add(key);
                    break;
                }

                case RecordConflictAction a:
                    Append(new ConflictRecorded
                    {
                        ConflictId = a.ConflictId,
                        Subject = a.Subject,
                        Sides = a.Sides,
                        Resolution = a.Resolution,
                        DecidedBy = a.DecidedBy
                    });
                    affected.Add(a.ConflictId);
                    break;

                default:
                    throw new InvalidOperationException($"unknown action {step.Action.GetType().Name}");
            }
        }
        catch (ConcurrencyConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _stepEvents[step.Id] = eventIds;
        var context = ContextFor(step.Branch);
        var assertions = step.Asserts
            .Select(token => CaseAsserts.Evaluate(token, context, _stepEvents))
            .ToArray();
        var passed = error is null && assertions.All(x => x.Passed);
        return new ReplayStepResult
        {
            StepId = step.Id,
            Label = step.Label,
            Branch = step.Branch,
            Passed = passed,
            EventIds = eventIds.ToArray(),
            DiagnosticCodes = diagCodes.Distinct().OrderBy(c => c).ToArray(),
            DerivationId = derivationId,
            Status = status,
            AffectedObjects = affected.Distinct().ToArray(),
            Error = error,
            Assertions = assertions
        };
    }

    // --------------------------------------------------------------- material builders

    private static (Segment[], Diagnostic[]) BuildSegments(CasePack pack, ImportSegmentsAction action)
    {
        var segments = new List<Segment>();
        var diags = new List<Diagnostic>();
        foreach (var draft in action.Segments)
        {
            var top = ParseDepth(draft.TopRaw, $"segment:{draft.Id}.top", draft.Id, diags);
            var bottom = ParseDepth(draft.BottomRaw, $"segment:{draft.Id}.bottom", draft.Id, diags);
            segments.Add(new Segment
            {
                Id = draft.Id,
                CoreId = pack.CoreId,
                Label = draft.Label,
                Top = top,
                Bottom = bottom,
                Lithology = draft.Lithology,
                SourceRef = draft.SourceRef
            });
        }
        return (segments.ToArray(), diags.ToArray());
    }

    private static (Anchor[], Diagnostic[]) BuildAnchors(CasePack pack, AnchorDraft[] drafts)
    {
        var anchors = new List<Anchor>();
        var diags = new List<Diagnostic>();
        foreach (var draft in drafts)
        {
            var depth = ParseDepth(draft.DepthRaw, $"anchor:{draft.Id}.depth", draft.Id, diags);
            anchors.Add(new Anchor
            {
                Id = draft.Id,
                CoreId = pack.CoreId,
                Kind = draft.Kind,
                Depth = depth,
                Age = draft.Age,
                ReservoirModelId = draft.ReservoirModelId,
                CorrelationId = draft.CorrelationId,
                LabRef = draft.LabRef,
                SourceRef = draft.SourceRef
            });
        }
        return (anchors.ToArray(), diags.ToArray());
    }

    private static (EventBed[], Diagnostic[]) BuildBeds(CasePack pack, ImportEventBedsAction action)
    {
        var beds = new List<EventBed>();
        var diags = new List<Diagnostic>();
        foreach (var draft in action.Beds)
        {
            var depth = ParseDepth(draft.DepthRaw, $"eventBed:{draft.Id}.depth", draft.Id, diags);
            beds.Add(new EventBed
            {
                Id = draft.Id,
                CoreId = pack.CoreId,
                Label = draft.Label,
                Depth = depth,
                SourceRef = draft.SourceRef
            });
        }
        return (beds.ToArray(), diags.ToArray());
    }

    /// <summary>
    /// Depths that cannot be parsed or converted stay in place with the raw text and a
    /// positioned diagnostic; nothing is silently dropped or replaced by a default.
    /// </summary>
    private static Measured ParseDepth(string raw, string position, string objectId,
        List<Diagnostic> diags)
    {
        var parsed = Units.ParseDepth(raw);
        if (parsed is null)
        {
            diags.Add(DerivationEngine.Diag(DiagSeverity.Error, "UnparseableDepth", position,
                $"depth '{raw}' cannot be parsed; value kept raw and excluded from derivation",
                objectId));
            return Measured.Unconvertible(raw, "unknown");
        }
        if (parsed.Value is null)
        {
            diags.Add(DerivationEngine.Diag(DiagSeverity.Error, "UnconvertibleDepth", position,
                $"depth '{raw}' uses unknown unit '{parsed.Unit}'; value kept raw in place",
                objectId));
        }
        return parsed;
    }
}
