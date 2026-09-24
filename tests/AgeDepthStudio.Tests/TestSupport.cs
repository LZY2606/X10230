using AgeDepthStudio.CasePackages;
using AgeDepthStudio.Engine;
using AgeDepthStudio.Store;

namespace AgeDepthStudio.Tests;

public static class TestSupport
{
    public static string TempDb() => Path.Combine(Path.GetTempPath(), $"ads-{Guid.NewGuid():N}.db");

    public static (StudioService svc, EventStore store) New()
    {
        var store = new EventStore(TempDb());
        return (new StudioService(store), store);
    }

    public static double[] Samples(double mu, double sig, int n = 120, int seed = 1)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ =>
        {
            var z = 0.0;
            for (var j = 0; j < 12; j++) z += r.NextDouble() * 2 - 1;
            return mu + sig * z / 12 * 3.4641;
        }).ToArray();
    }

    public static double[] Skew(double mu, double sig, double tail, int n, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ =>
        {
            if (r.NextDouble() < 0.02) return mu + tail * r.NextDouble();
            var z = 0.0;
            for (var j = 0; j < 12; j++) z += r.NextDouble() * 2 - 1;
            return mu + sig * z / 12 * 3.4641;
        }).ToArray();
    }

    public static void MiniProject(StudioService svc, string ws = "T")
    {
        svc.CreateWorkspace(ws, "测试孔", "narrative");
        svc.RegisterProject(ws, new Domain.CoreProject(ws, "测试孔", "MDRT(m)",
            Domain.DepthDirection.DownIncreases, "m", "ka BP", "BP1950"));
        svc.AddSegment(ws, new Domain.CoreSegment("S1", ws, "0m", "10m", "泥"));
        svc.AddSegment(ws, new Domain.CoreSegment("S2", ws, "12m", "20m", "砂"));
        svc.AddGap(ws, new Domain.SegmentGap("G1", ws, "S1", "S2", Domain.GapKind.Unconformity, "显式边界"));
        svc.IssueParams(ws, new Domain.RuleParams("P1", 1, 1000, 1.0, 2.5, "测试参数"), true);
    }

    public static CasePackage LoadEmbedded(string name)
    {
        var dir = FindCaseDir();
        return System.Text.Json.JsonSerializer.Deserialize<CasePackage>(
            File.ReadAllText(Path.Combine(dir, name)),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static string FindCaseDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "CasePackages");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "core-chronostrat*.json").Length > 0) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        throw new DirectoryNotFoundException("CasePackages not found");
    }
}
