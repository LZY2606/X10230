using ChronoCore.Domain.Engine;
using ChronoCore.Domain.Model;
using Microsoft.Data.Sqlite;

namespace ChronoCore.Storage;

public static class Portable
{
    public static ExportBundle Export(this ChronoStore store)
    {
        var packages = store.ListPackages().Select((p, i) =>
            new PackageEnvelope(p.PackageId, p.CaseId, p.Version, "source", p.Raw, p.Digest, i)).ToList();

        var events = store.AllEvents().Select(e => new EventEnvelope(
            e.Sequence, e.EventId, e.BranchId, e.ParentEventId, e.Kind.ToString(),
            e.MaterialPackageId, e.RulesetVersion, e.InputDigest, e.PayloadJson,
            e.Actor, e.CreatedAt.ToString("O"), e.FrozenCandidateId)).ToList();

        var branches = events.GroupBy(e => e.BranchId).Select(g =>
        {
            var head = g.OrderBy(e => e.Seq).Last();
            return new BranchEnvelope(g.Key, g.Key, head.EventId);
        }).ToList();

        var cfs = store.ListCounterfactuals().Select(c => new CounterfactualEnvelope(
            c.ArchiveId, c.BranchId, c.Label, c.BaselinePackageId, c.ChangedPackageId,
            c.ChangedMaterial, c.ChangeKind,
            System.Text.Json.JsonSerializer.Serialize(c.Report, Replay.JsonOpts),
            System.Text.Json.JsonSerializer.Serialize(c.AdoptedOutcomeIds, Replay.JsonOpts),
            System.Text.Json.JsonSerializer.Serialize(c.RejectedOutcomeIds, Replay.JsonOpts))).ToList();

        return new ExportBundle(
            "BND-" + Stable.Short(string.Join("|", events.Select(e => e.EventId))),
            "chrono-bundle", 1, DateTimeOffset.UtcNow, packages, events, cfs, branches);
    }

    /// <summary>Import a bundle. Everything is inserted in one transaction with
    /// explicit seq values; a torn write rolls back with no partial state.</summary>
    public static void Import(this ChronoStore store, ExportBundle bundle)
    {
        // validate referential lineage before writing anything
        var eventIds = bundle.Events.Select(e => e.EventId).ToHashSet();
        foreach (var e in bundle.Events)
            if (e.ParentEventId is not null && !eventIds.Contains(e.ParentEventId))
                throw new BundleFormatException("BUNDLE_LINEAGE",
                    $"事件 {e.EventId} 的父事件 {e.ParentEventId} 不在束内，来源关系断裂");

        using var cn = new SqliteConnection(store.ConnectionStringForImport);
        cn.Open();
        using var tx = cn.BeginTransaction();

        foreach (var p in bundle.Packages.OrderBy(p => p.Ordinal))
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO packages
                (package_id,case_id,case_version,source_id,raw_text,input_digest,imported_at,ordinal)
                VALUES($id,$c,$cv,'imported',$raw,$d,$at,$o)
                """;
            Add(cmd, ("$id", p.PackageId), ("$c", p.CaseId), ("$cv", p.CaseVersion),
                ("$raw", p.RawText), ("$d", p.InputDigest),
                ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$o", p.Ordinal));
            cmd.ExecuteNonQuery();
        }

        // Reset autoincrement so imported seqs and their relative order survive.
        using (var del = cn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM events; DELETE FROM sqlite_sequence WHERE name='events'; DELETE FROM freezes; DELETE FROM counterfactuals; DELETE FROM derivations;";
            del.ExecuteNonQuery();
        }

        foreach (var e in bundle.Events.OrderBy(e => e.Seq))
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events
                (seq,event_id,branch_id,parent_event_id,kind,material_package_id,ruleset_version,
                 input_digest,payload_json,actor,created_at,frozen_candidate_id)
                VALUES($s,$eid,$b,$p,$k,$m,$r,$d,$pl,$a,$at,$f)
                """;
            Add(cmd, ("$s", e.Seq), ("$eid", e.EventId), ("$b", e.BranchId),
                ("$p", (object?)e.ParentEventId ?? DBNull.Value), ("$k", e.Kind),
                ("$m", (object?)e.MaterialPackageId ?? DBNull.Value),
                ("$r", e.RulesetVersion), ("$d", e.InputDigest), ("$pl", e.PayloadJson),
                ("$a", e.Actor), ("$at", e.CreatedAt),
                ("$f", (object?)e.FrozenCandidateId ?? DBNull.Value));
            cmd.ExecuteNonQuery();

            if (e.FrozenCandidateId is not null)
            {
                using var fc = cn.CreateCommand();
                fc.Transaction = tx;
                fc.CommandText = """
                    INSERT OR REPLACE INTO freezes
                    (branch_id,candidate_id,input_digest,ruleset_fingerprint,lock_version,created_event_id)
                    VALUES($b,$c,$d,$r,1,$e)
                    """;
                Add(fc, ("$b", e.BranchId), ("$c", e.FrozenCandidateId), ("$d", e.InputDigest),
                    ("$r", e.RulesetVersion), ("$e", e.EventId));
                fc.ExecuteNonQuery();
            }
        }

        foreach (var c in bundle.Counterfactuals)
        {
            using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO counterfactuals
                (archive_id,branch_id,label,baseline_pkg_id,changed_pkg_id,changed_material,change_kind,
                 report_json,adopted_json,rejected_json,created_at)
                VALUES($id,$b,$l,$bp,$cp,$m,$k,$rj,$aj,$xj,$at)
                """;
            Add(cmd, ("$id", c.ArchiveId), ("$b", c.BranchId), ("$l", c.Label),
                ("$bp", c.BaselinePackageId), ("$cp", c.ChangedPackageId),
                ("$m", c.ChangedMaterial), ("$k", c.ChangeKind),
                ("$rj", c.ReportJson), ("$aj", c.AdoptedJson), ("$xj", c.RejectedJson),
                ("$at", DateTimeOffset.UtcNow.ToString("O")));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void Add(SqliteCommand cmd, params (string, object)[] ps)
    {
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
