using System.Text.Json;
using System.Text.Json.Nodes;
using DepthAge.Core;
using Xunit;

namespace DepthAge.Tests;

public class RoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "depthage-tests-" + Guid.NewGuid().ToString("N"));
    public RoundTripTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static string CasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "cases", "core-chronoscale", "case.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("cases/core-chronoscale/case.json not found");
    }

    [Fact]
    public void ExportDeleteImport_PreservesIdentityOrderAndProvenance()
    {
        var pack = CasePack.Load(File.ReadAllText(CasePath()));
        string export1;
        List<MaterialEnvelope> materials1;
        List<ModelEvent> events1;
        var path1 = Path.Combine(_dir, "a.db");
        using (var store = new DepthAgeStore(path1))
        {
            CaseRunner.Run(pack, store);
            materials1 = store.Materials().ToList();
            events1 = store.Events().ToList();
            export1 = store.ExportJson();
        }

        // 删除本地数据库，再导入
        var path2 = Path.Combine(_dir, "b.db");
        using (var store2 = new DepthAgeStore(path2))
        {
            var result = store2.ImportJson(export1);
            var ok = Assert.IsType<ImportResult.Ok>(result);
            Assert.Equal(materials1.Count, ok.Materials);
            Assert.Equal(events1.Count, ok.Events);

            // 身份与顺序保持
            Assert.Equal(materials1.Select(m => m.Id), store2.Materials().Select(m => m.Id));
            Assert.Equal(events1.Select(e => e.Seq), store2.Events().Select(e => e.Seq));
            Assert.Equal(events1.Select(e => e.Id), store2.Events().Select(e => e.Id));
            // 来源关系（父级依赖）保持
            Assert.Equal(events1.Select(e => string.Join(",", e.Parents)), store2.Events().Select(e => string.Join(",", e.Parents)));

            // 再导出：除时间戳外逐字节一致
            var export2 = store2.ExportJson();
            using var d1 = JsonDocument.Parse(export1);
            using var d2 = JsonDocument.Parse(export2);
            Assert.Equal(d1.RootElement.GetProperty("materials").GetRawText(),
                         d2.RootElement.GetProperty("materials").GetRawText());
            Assert.Equal(d1.RootElement.GetProperty("events").GetRawText(),
                         d2.RootElement.GetProperty("events").GetRawText());
            Assert.Equal(d1.RootElement.GetProperty("freezes").GetRawText(),
                         d2.RootElement.GetProperty("freezes").GetRawText());
        }
    }

    [Fact]
    public void CorruptFormat_IsDistinctResult_WithPosition()
    {
        using var store = new DepthAgeStore(Path.Combine(_dir, "c.db"));
        var bad1 = store.ImportJson("this is not json");
        var c1 = Assert.IsType<ImportResult.CorruptFormat>(bad1);
        Assert.Equal("$", c1.Position);

        var bad2 = store.ImportJson("""{"format":"something-else","materials":[],"events":[],"freezes":[]}""");
        var c2 = Assert.IsType<ImportResult.CorruptFormat>(bad2);
        Assert.Equal("$.format", c2.Position);

        // 篡改材料内容 → 哈希校验失败，位置指向具体材料
        var pack = CasePack.Load(File.ReadAllText(CasePath()));
        using var src = new DepthAgeStore(Path.Combine(_dir, "d.db"));
        CaseRunner.Run(pack, src);
        var export = src.ExportJson();
        using var doc = JsonDocument.Parse(export);
        var node = JsonNode.Parse(export)!;
        node!["materials"]![0]!["rawJson"] = """{"tampered":true}""";
        var bad3 = store.ImportJson(node.ToJsonString());
        var c3 = Assert.IsType<ImportResult.CorruptFormat>(bad3);
        Assert.StartsWith("$.materials[", c3.Position);
    }
}
