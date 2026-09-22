namespace ChronoAge.Core;

/// <summary>A continuously deposited segment: monotonic ages apply ONLY inside it.</summary>
public sealed record DepoSegment(
    string SegmentId,
    string Label,
    double TopDepthM,
    double BottomDepthM,
    IReadOnlyList<SegmentEdge> Edges,
    string? BreakAboveId,
    string? BreakBelowId);

public sealed record SegmentEdge(string BoundaryLogId, BoundaryKind Kind, double DepthM, string Interpretation);

/// <summary>An anchor usable by the engine, assembled from direct anchors, calibrated assays and correlated events.</summary>
public sealed record DerivedAnchor(
    string AnchorId,
    string SourceLogId,
    string SourceKind,
    double DepthM,
    AgeDistributionSpec Distribution,
    string Unit,
    string Basis,
    bool DuplicateDepthWithNext);

/// <summary>
/// One legal candidate age-depth model. Choices at gaps/unconformities, inverted sections,
/// overlaps and ties each branch the candidate tree. Every legal candidate is retained;
/// equi-optimal candidates are kept in a stable order rather than arbitrarily collapsed.
/// </summary>
public sealed record ModelCandidate(
    string CandidateId,
    string Genealogy,
    IReadOnlyDictionary<string, string> Choices,
    IReadOnlyList<DerivedAnchor> AnchorsInOrder,
    bool RequiresHumanCall,
    IReadOnlyList<string> HumanCallKeys,
    IReadOnlyList<Diagnostic> Notices);

public static class Engine
{
    /// <summary>
    /// Build continuously-deposited segments from sections + explicit boundaries.
    /// A <see cref="BoundaryKind.Gap"/> or <see cref="BoundaryKind.Unconformity"/> is an
    /// explicit edge: interpolation and monotonicity never cross it.
    /// </summary>
    public static IReadOnlyList<DepoSegment> BuildSegments(ProjectState st)
    {
        var sections = st.OrderedSections().ToList();
        var boundaries = st.ActiveBoundaries().ToList();
        var result = new List<DepoSegment>();
        if (sections.Count == 0) return result;

        var segIndex = 0;
        var segStart = 0;
        string? breakAbove = null;

        for (var i = 0; i < sections.Count; i++)
        {
            SegmentBoundary? bnd = null;
            if (i + 1 < sections.Count)
            {
                bnd = boundaries.FirstOrDefault(b =>
                    (b.UpperSection == sections[i].LogId || b.UpperSection == sections[i].Code) &&
                    (b.LowerSection == sections[i + 1].LogId || b.LowerSection == sections[i + 1].Code));
            }

            var atBreak = bnd is not null && bnd.IsDepositionalBreak;
            var last = i == sections.Count - 1;
            if (atBreak || last)
            {
                var members = sections.Skip(segStart).Take(i - segStart + 1).ToList();
                var edges = new List<SegmentEdge>();
                if (breakAbove is not null)
                {
                    var eb = boundaries.FirstOrDefault(b => b.LogId == breakAbove);
                    if (eb is not null)
                        edges.Add(new SegmentEdge(eb.LogId, eb.Kind, eb.GapThickness?.Canonical ?? members[0].Top.Canonical!.Value,
                            st.BoundaryCalls.GetValueOrDefault(eb.LogId)?.Interpretation ?? "break"));
                }
                var breakBelow = atBreak ? bnd!.LogId : null;
                if (atBreak)
                    edges.Add(new SegmentEdge(bnd!.LogId, bnd.Kind, bnd.GapThickness?.Canonical ?? sections[i + 1].Top.Canonical!.Value,
                        st.BoundaryCalls.GetValueOrDefault(bnd.LogId)?.Interpretation ?? "break"));

                segIndex++;
                result.Add(new DepoSegment(
                    $"SEG{segIndex:D2}",
                    string.Join("+", members.Select(m => m.Code)),
                    members.First().Top.Canonical!.Value,
                    members.Last().Bottom.Canonical!.Value,
                    edges, breakAbove, breakBelow));

                if (atBreak) { segStart = i + 1; breakAbove = bnd!.LogId; }
            }
        }
        return result;
    }

    /// <summary>
    /// Assemble anchors from three source families. Radiocarbon assays are calibrated with the
    /// parameter table and shifted by their reservoir offset; uncalibratable values produce a
    /// positioned diagnostic instead of a fake calendar age.
    /// </summary>
    public static (IReadOnlyList<DerivedAnchor> anchors, IReadOnlyList<Diagnostic> diags) BuildAnchors(ProjectState st)
    {
        var diags = new List<Diagnostic>();
        var anchors = new List<DerivedAnchor>();

        foreach (var a in st.ActiveOfType<DirectAnchor>().Where(a => a.Depth.Canonical.HasValue))
            anchors.Add(new DerivedAnchor($"ANC-{a.LogId}", a.LogId, "anchor", a.Depth.Canonical!.Value,
                a.Distribution, "yr_BP", a.Basis ?? "direct", false));

        var reservoirs = st.ActiveOfType<ReservoirOffset>().ToDictionary(r => r.LogId);
        foreach (var assay in st.ActiveOfType<RadiometricAssay>().Where(x => x.Depth.Canonical.HasValue))
        {
            if (assay.IsCalibrated && assay.CalibratedDist is not null)
            {
                anchors.Add(new DerivedAnchor($"ANC-{assay.LogId}", assay.LogId, "assay", assay.Depth.Canonical!.Value,
                    assay.CalibratedDist, "yr_BP", "calibrated_radiometric", false));
                continue;
            }
            if (!assay.IsCalibrated && assay.ReportedAge is double rc)
            {
                var sigma = assay.ReportedSigma ?? 0.0;
                if (st.Parameters.Calibration is null || !st.Parameters.Calibration.TryMap(rc, out var calMu, out var calSigma))
                {
                    diags.Add(Diag.Warn("ENG_NO_CALIB",
                        $"Assay {assay.Code} is radiocarbon age {rc} but no calibration table is available; held out of the model.",
                        assay.Id, "reported_age"));
                    continue;
                }
                var totalSigma = Math.Sqrt(calSigma * calSigma + sigma * sigma);
                var mean = calMu;
                if (assay.ReservoirLogId is not null && reservoirs.TryGetValue(assay.ReservoirLogId, out var ro))
                {
                    // calendar age of the carbon = apparent calendar age minus reservoir R
                    mean -= ro.MeanYears;
                    totalSigma = Math.Sqrt(totalSigma * totalSigma + ro.SigmaYears * ro.SigmaYears);
                }
                anchors.Add(new DerivedAnchor($"ANC-{assay.LogId}", assay.LogId, "assay", assay.Depth.Canonical!.Value,
                    new NormalSpec(mean, totalSigma), "yr_BP",
                    assay.ReservoirLogId is null ? "calibrated_radiometric" : "calibrated_radiometric+reservoir_corrected", false));
            }
        }

        foreach (var ev in st.ActiveOfType<EventLayer>().Where(e => e.Depth.Canonical.HasValue))
        {
            var corr = st.ActiveOfType<EventCorrelation>().FirstOrDefault(c => c.EventLogId == ev.LogId);
            if (corr is null)
            {
                diags.Add(Diag.Info("ENG_EVENT_UNDATED",
                    $"Event layer {ev.Code} has no accepted correlation; plotted structurally, not dated.", ev.Id));
                continue;
            }
            anchors.Add(new DerivedAnchor($"ANC-{ev.LogId}", ev.LogId, "event_layer", ev.Depth.Canonical!.Value,
                corr.AgeDistribution, "yr_BP", $"event:{corr.ExternalName}", false));
        }

        var eps = st.Parameters.DepthEpsilonM;
        var sorted = anchors.OrderBy(a => a.DepthM).ThenBy(a => a.AnchorId, StringComparer.Ordinal).ToList();
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].DepthM - sorted[i + 1].DepthM) <= eps)
            {
                sorted[i] = sorted[i] with { DuplicateDepthWithNext = true };
                diags.Add(Diag.Warn("ENG_DUP_DEPTH",
                    $"Anchors {sorted[i].AnchorId} and {sorted[i + 1].AnchorId} share depth {sorted[i].DepthM.ToString("0.###")} m; both retained; tie ordering is a candidate branch.",
                    sorted[i].SourceLogId, "depth"));
            }
        }
        return (sorted, diags);
    }
}
