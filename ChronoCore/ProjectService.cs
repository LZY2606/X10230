using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChronoCore;

public static class DocType
{
    public const string Materials = "materials";
    public const string Parameters = "parameters";
    public const string Candidate = "candidate";
    public const string Trial = "trial";
    public const string Exclusions = "exclusions";
}

public sealed record ActionResult(
    bool Ok, string Outcome, List<Diagnostic> Diagnostics, ChronoEvent Event, object Data = null);

/// <summary>
/// Application layer. All business rules execute here (callable directly from tests);
/// the web layer is a thin adapter over this service.
/// </summary>
public sealed class ProjectService
{
    public SqliteStore Store { get; }
    private readonly AgeModelEngine _engine = new();
    private static readonly JsonSerializerOptions J = SqliteStore.JsonOpts;

    public ProjectService(SqliteStore store) { Store = store; }

    public static string Fingerprint(string json)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..16];

    // -------------------------- import / parsing --------------------------

    /// <summary>
    /// Import raw case JSON. Original representations are retained verbatim; only the derived
    /// layer normalizes. Unconvertible values stay in place with diagnostics — never dropped.
    /// </summary>
    public (MaterialsDoc materials, List<Diagnostic> diagnostics) ParseMaterials(JsonElement root)
    {
        var diag = new List<Diagnostic>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            diag.Add(new Diagnostic { Code = DiagnosticCodes.FormatCorrupt, Location = "$",
                Message = "root must be an object" });
            return (null, diag);
        }
        string pid = root.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(pid))
            diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput, Location = "$.projectId",
                Message = "projectId is required" });
        string datum = root.TryGetProperty("depthDatum", out var dd) ? dd.GetString() : "mbsf@seafloor";

        var doc = new MaterialsDoc
        {
            ProjectId = pid,
            ProjectName = root.TryGetProperty("projectName", out var pn) ? pn.GetString() ?? "" : "",
            DepthDatum = string.IsNullOrWhiteSpace(datum) ? "mbsf@seafloor" : datum,
            TimeBasis = root.TryGetProperty("timeBasis", out var tb) ? tb.GetString() : "a BP (before 1950 CE)"
        };

        if (!root.TryGetProperty("segments", out var segs) || segs.ValueKind != JsonValueKind.Array)
            diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput, Location = "$.segments",
                Message = "segments array is required" });
        else
            foreach (var el in segs.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var i) ? i.GetString() : "";
                if (string.IsNullOrWhiteSpace(id))
                {
                    diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput, Location = "$.segments[].id",
                        Message = "segment id is required" });
                    continue;
                }
                var seg = new CoreSegment
                {
                    Id = id,
                    Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : id,
                    Top = DepthPoint.Parse(el.TryGetProperty("top", out var t) ? t.GetRawText().Trim('"') : "", datum),
                    Bottom = DepthPoint.Parse(el.TryGetProperty("bottom", out var b) ? b.GetRawText().Trim('"') : "", datum),
                    UpperBoundary = el.TryGetProperty("upperBoundary", out var ub) ? ub.GetString() : BoundaryKind.Continuous,
                    LowerBoundary = el.TryGetProperty("lowerBoundary", out var lb) ? lb.GetString() : BoundaryKind.Continuous,
                    GapYearsBp = el.TryGetProperty("gapYearsBp", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : null,
                    Notes = el.TryGetProperty("notes", out var nt) ? nt.GetString() ?? "" : ""
                };
                doc.Segments.Add(seg);
            }

        if (root.TryGetProperty("anchors", out var an) && an.ValueKind == JsonValueKind.Array)
            foreach (var el in an.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var i) ? i.GetString() : "";
                if (string.IsNullOrWhiteSpace(id))
                {
                    diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput, Location = "$.anchors[].id",
                        Message = "anchor id is required" });
                    continue;
                }
                string rawDepth = el.TryGetProperty("depth", out var d) ? d.GetRawText().Trim('"') : "";
                string rawAge = el.TryGetProperty("rawAge", out var ra) ? ra.GetRawText().Trim('"') : "";
                if (string.IsNullOrWhiteSpace(rawAge))
                    diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput, Location = $"$.anchors[{id}].rawAge",
                        Message = "rawAge with unit is required (original lab representation)" });
                var anchor = new Anchor
                {
                    Id = id,
                    Method = el.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "",
                    Depth = DepthPoint.Parse(rawDepth, datum),
                    RawAge = AgeQuantity.Parse(rawAge),
                    LabCode = el.TryGetProperty("labCode", out var lc) ? lc.GetString() ?? "" : "",
                    EventLayerId = el.TryGetProperty("eventLayerId", out var ev) ? ev.GetString() ?? "" : "",
                    Notes = el.TryGetProperty("notes", out var no) ? no.GetString() ?? "" : "",
                    SourceRef = el.TryGetProperty("sourceRef", out var sr) ? sr.GetString() ?? $"$.anchors[{id}]" : ""
                };
                if (el.TryGetProperty("distribution", out var dist) && dist.ValueKind == JsonValueKind.Object)
                {
                    anchor.Distribution = new AgeDistribution
                    {
                        Kind = dist.TryGetProperty("kind", out var k) ? k.GetString() ?? "normal" : "normal",
                        MeanYearsBp = Num(dist, "meanYearsBp") ?? anchor.RawAge.YearsBp,
                        StdYearsBp = Num(dist, "stdYearsBp"),
                        Shape = Num(dist, "shape"),
                        Scale = Num(dist, "scale"),
                        OffsetYearsBp = Num(dist, "offsetYearsBp")
                    };
                    if (dist.TryGetProperty("empiricalSamples", out var es) && es.ValueKind == JsonValueKind.Array)
                        foreach (var smp in es.EnumerateArray())
                            if (smp.ValueKind == JsonValueKind.Number)
                                anchor.Distribution.EmpiricalSamples.Add(smp.GetDouble());
                }
                else
                {
                    anchor.Distribution.MeanYearsBp = anchor.RawAge.YearsBp;
                    diag.Add(new Diagnostic { Code = DiagnosticCodes.IncompleteInput,
                        Location = $"$.anchors[{id}].distribution",
                        Message = "age distribution missing; mean derived from rawAge but distribution is required" });
                }
                if (el.TryGetProperty("reservoir", out var res) && res.ValueKind == JsonValueKind.Object)
                    anchor.Reservoir = new ReservoirOffset
                    {
                        MeanYears = Num(res, "meanYears") ?? 0,
                        StdYears = Num(res, "stdYears") ?? 0,
                        Basis = res.TryGetProperty("basis", out var bs) ? bs.GetString() ?? "" : ""
                    };
                doc.Anchors.Add(anchor);
            }

        if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            foreach (var el in evs.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var i) ? i.GetString() : "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                var layer = new EventLayer
                {
                    Id = id,
                    Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : id,
                    Depth = DepthPoint.Parse(el.TryGetProperty("depth", out var d) ? d.GetRawText().Trim('"') : "", datum),
                    CorrelationBasis = el.TryGetProperty("correlationBasis", out var cb) ? cb.GetString() ?? "" : "",
                    CorrelationConfidence = Num(el, "correlationConfidence") ?? 1.0
                };
                if (el.TryGetProperty("referenceDistribution", out var rd) && rd.ValueKind == JsonValueKind.Object)
                    layer.ReferenceDistribution = new AgeDistribution
                    {
                        Kind = rd.TryGetProperty("kind", out var k) ? k.GetString() ?? "normal" : "normal",
                        MeanYearsBp = Num(rd, "meanYearsBp"),
                        StdYearsBp = Num(rd, "stdYearsBp"),
                        Shape = Num(rd, "shape"),
                        Scale = Num(rd, "scale"),
                        OffsetYearsBp = Num(rd, "offsetYearsBp")
                    };
                doc.Events.Add(layer);
            }

        doc.Diagnostics = diag;
        return (doc, diag);
    }

    private static double? Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public Snapshot CurrentSnapshot() => Replayer.Replay(Store.LoadEvents());
    public Snapshot SnapshotAt(long seq) => Replayer.Replay(Store.LoadEvents(), seq);

    public Snapshot CreateProject(string projectId, string projectName, string actor = "system")
    {
        using var conn = Store.Begin(out var tx);
        Store.AppendEvent(conn, tx, EventType.ProjectCreated,
            new { projectId, projectName }, actor, projectId);
        tx.Commit();
        return CurrentSnapshot();
    }

    /// <summary>Baseline import: materials + recorded exclusions + first model build, in one commit.</summary>
    public (bool ok, string outcome, List<Diagnostic> diagnostics, Snapshot snapshot)
        ImportBaseline(string rawJson, string actor = "sed-team", List<ExclusionDecision> initialExclusions = null)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(rawJson).RootElement; }
        catch (JsonException ex)
        {
            return (false, DiagnosticCodes.FormatCorrupt, new List<Diagnostic>
            {
                new() { Code = DiagnosticCodes.FormatCorrupt, Location = "$", Message = ex.Message }
            }, null);
        }
        var (materials, diagnostics) = ParseMaterials(root);
        if (materials == null)
            return (false, DiagnosticCodes.FormatCorrupt, diagnostics, null);
        if (diagnostics.Any(d => d.Severity == "error" && d.Code == DiagnosticCodes.IncompleteInput
            && d.Location is "$.projectId" or "$.segments"))
        {
            return (false, DiagnosticCodes.IncompleteInput, diagnostics, null);
        }

        using var conn = Store.Begin(out var tx);
        Store.AppendEvent(conn, tx, EventType.ProjectCreated,
            new { projectId = materials.ProjectId, projectName = materials.ProjectName }, actor,
            inputSummary: Fingerprint(rawJson));
        var impEvent = Store.AppendEvent(conn, tx, EventType.MaterialsImported, materials, actor,
            inputSummary: $"anchors={materials.Anchors.Count},segments={materials.Segments.Count},events={materials.Events.Count}",
            parentHashes: "raw:" + Fingerprint(rawJson));
        Store.PutDocument(conn, tx, DocType.Materials, materials.ProjectId, 1, impEvent.Seq,
            JsonSerializer.Serialize(materials, J));
        Store.AppendEvent(conn, tx, EventType.ParameterRevised, new ParameterSet(), actor,
            inputSummary: RuleSet.Version);

        var exclusions = initialExclusions ?? new List<ExclusionDecision>();
        if (root.TryGetProperty("exclusions", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
            foreach (var e in exEl.EnumerateArray())
                exclusions.Add(new ExclusionDecision
                {
                    Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : $"EX-{Guid.NewGuid():N}"[..8],
                    SubjectType = e.TryGetProperty("subjectType", out var st) ? st.GetString() ?? "anchor" : "anchor",
                    SubjectId = e.TryGetProperty("subjectId", out var si) ? si.GetString() ?? "" : "",
                    Excluded = !(e.TryGetProperty("excluded", out var x) && x.ValueKind == JsonValueKind.False),
                    Reason = e.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    DecidedBy = e.TryGetProperty("decidedBy", out var db) ? db.GetString() ?? actor : actor
                });
        foreach (var d in exclusions.Where(d => d.Excluded))
            Store.AppendEvent(conn, tx, EventType.DecisionExclude, d, d.DecidedBy,
                inputSummary: $"{d.SubjectType}:{d.SubjectId}:{d.Reason}");

        tx.Commit();

        var build = Rebuild("baseline-import", actor);
        return (true, build.Outcome, diagnostics, CurrentSnapshot());
    }

    /// <summary>Rebuild models from the current event-derived state.</summary>
    public BuildResult Rebuild(string trigger, string actor,
        List<string> changedAnchorIds = null, bool persist = true)
    {
        var snap = CurrentSnapshot();
        var previous = snap.Candidates;
        var result = _engine.Build(snap.Materials, snap.Parameters, snap.Excluded,
            snap.Generation, trigger, changedAnchorIds, previous);

        if (persist)
        {
            using var conn = Store.Begin(out var tx);
            var ev = Store.AppendEvent(conn, tx, EventType.ModelBuilt, result, actor,
                inputSummary: $"candidates={result.Candidates.Count},outcome={result.Outcome},gen={result.Generation}",
                parentHashes: string.Join(",", result.Candidates.ConvertAll(c => c.Id)));
            foreach (var c in result.Candidates)
                Store.PutDocument(conn, tx, DocType.Candidate, c.Id, c.ParameterVersion, ev.Seq,
                    JsonSerializer.Serialize(c, J));
            tx.Commit();
        }
        return result;
    }
