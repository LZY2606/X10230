using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeModel.Store;

/// <summary>Project export/import. Export carries every event with its stable identity,
/// order and hash chain; import into an empty store preserves identity, order and provenance.</summary>
public static class ExportImport
{
    public static JsonObject Export(EventStore store)
    {
        var events = new JsonArray();
        foreach (var e in store.ReadAll())
            events.Add(new JsonObject
            {
                ["seq"] = e.Seq, ["id"] = e.Id, ["type"] = e.Type,
                ["payload"] = JsonNode.Parse(e.Payload),
                ["hash"] = e.Hash, ["prevHash"] = e.PrevHash
            });
        var freeze = store.GetFreeze(Projector.ModelId);
        return new JsonObject
        {
            ["format"] = "agemodel-export/1",
            ["events"] = events,
            ["freeze"] = freeze is { } f
                ? new JsonObject { ["modelId"] = Projector.ModelId, ["version"] = f.Version, ["payload"] = JsonNode.Parse(f.Payload) }
                : null
        };
    }

    /// <summary>Imports an export document into an empty store. Verifies the hash chain
    /// before writing; refuses to merge into a non-empty store.</summary>
    public static (bool Ok, string? Error) Import(EventStore store, JsonObject doc)
    {
        if (doc["format"]?.GetValue<string>() != "agemodel-export/1")
            return (false, "unknown export format");
        if (store.CommittedCount() != 0)
            return (false, "target store is not empty");
        var events = doc["events"]?.AsArray();
        if (events is null) return (false, "missing events");

        var prev = "GENESIS";
        long expectedSeq = 0;
        var parsed = new List<(long seq, string id, string type, string payload, string hash)>();
        foreach (var n in events)
        {
            var seq = n!["seq"]!.GetValue<long>();
            var id = n["id"]!.GetValue<string>();
            var type = n["type"]!.GetValue<string>();
            var payload = JsonSerializer.Serialize(n["payload"]);
            var hash = n["hash"]!.GetValue<string>();
            if (n["prevHash"]!.GetValue<string>() != prev) return (false, $"broken chain at seq {seq}");
            if (EventStore.ComputeHash(id, type, payload, prev) != hash) return (false, $"hash mismatch at seq {seq}");
            if (seq <= expectedSeq) return (false, $"non-increasing seq {seq}");
            expectedSeq = seq;
            prev = hash;
            parsed.Add((seq, id, type, payload, hash));
        }
        foreach (var (seq, id, type, payload, hash) in parsed)
            store.ImportRaw(seq, id, type, payload, hash);
        if (doc["freeze"] is { } f)
            store.RestoreFreeze(Projector.ModelId, f["version"]!.GetValue<long>(),
                JsonSerializer.Serialize(f["payload"]));
        return (true, null);
    }
}
