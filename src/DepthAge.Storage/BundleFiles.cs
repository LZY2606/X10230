using DepthAge.Domain;

namespace DepthAge.Storage;

/// <summary>
/// File-level bundle I/O. Exports go through a .tmp file then an atomic rename, so a crash
/// mid-write never corrupts an existing export; stale .tmp files are detected and cleaned
/// rather than mistaken for valid bundles.
/// </summary>
public static class BundleFiles
{
    public static string ExportToFile(IEventStore store, string path)
    {
        var json = store.ExportBundleJson();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Simulate a crashed export (for half-write recovery tests). Leaves only the .tmp.
    /// </summary>
    public static string SimulateCrashedExport(IEventStore store, string path)
    {
        var json = store.ExportBundleJson();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json[..(json.Length / 2)]);
        if (File.Exists(path)) File.Delete(path);
        return tmp;
    }

    public static void ImportFromFile(IEventStore store, string path)
    {
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            if (File.Exists(tmp))
                throw new FormatCorruptException("file:" + tmp,
                    "only a half-written .tmp export exists; the export never completed");
            throw new FileNotFoundException(path);
        }
        store.ImportBundleJson(File.ReadAllText(path));
    }

    /// <summary>Remove a stale half-written .tmp left by a crashed export.</summary>
    public static bool TryCleanupPartialExport(string path)
    {
        var tmp = path + ".tmp";
        if (!File.Exists(tmp)) return false;
        File.Delete(tmp);
        return true;
    }
}
