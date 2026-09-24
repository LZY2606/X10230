using DepthAge.Core;
using Xunit;

namespace DepthAge.Tests;

public class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "depthage-tests-" + Guid.NewGuid().ToString("N"));
    public StoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string Db(string name) => Path.Combine(_dir, name);

    [Fact]
    public void HalfWrittenWork_IsRolledBackOnRecovery()
    {
        var path = Db("half.db");
        string token;
        using (var store = new DepthAgeStore(path))
        {
            token = store.BeginWrite();
            store.AddMaterial("age-anchor", """{"anchor":"A1"}""", 1, token);
            store.AppendEvent("fit", "{}", "sha256:x", "params/v1", Array.Empty<string>(), token);
            Assert.Single(store.Events());
            // 模拟崩溃：不调 CompleteWrite，直接丢弃连接
        }
        using (var reopened = new DepthAgeStore(path))
        {
            Assert.Empty(reopened.Events());
            Assert.Empty(reopened.Materials());
        }
    }

    [Fact]
    public void CommittedWrite_SurvivesRecovery()
    {
        var path = Db("committed.db");
        using (var store = new DepthAgeStore(path))
        {
            var token = store.BeginWrite();
            store.AppendEvent("fit", "{}", "sha256:x", "params/v1", Array.Empty<string>(), token);
            store.CompleteWrite(token);
        }
        using (var reopened = new DepthAgeStore(path))
        {
            Assert.Single(reopened.Events());
        }
    }

    [Fact]
    public void ConcurrentFreeze_ExactlyOneWins()
    {
        var path = Db("freeze.db");
        using var s1 = new DepthAgeStore(path);
        using var s2 = new DepthAgeStore(path);
        var results = new[]
        {
            Task.Run(() => s1.TryFreeze("sha256:model1", "main", "reviewer-a", 1)),
            Task.Run(() => s2.TryFreeze("sha256:model1", "main", "reviewer-b", 2))
        };
        Task.WaitAll(results);
        Assert.Equal(1, results.Count(r => r.Result == FreezeOutcome.Frozen));
        Assert.Equal(1, results.Count(r => r.Result == FreezeOutcome.AlreadyFrozen));
        Assert.Single(s1.Freezes());
    }
}
