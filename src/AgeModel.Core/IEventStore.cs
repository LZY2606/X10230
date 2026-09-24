using System.Text.Json;

namespace AgeModel.Core;

/// <summary>事件存储抽象：SQLite 实现与内存实现共用，存储层可被测试直接调用。</summary>
public interface IEventStore : IDisposable
{
    void Append(StoredEvent ev);
    IReadOnlyList<StoredEvent> GetEvents(string projectId);
    long HeadSeq(string projectId, string branch);
    IReadOnlyList<string> ListProjects();
    void DeleteProject(string projectId);

    /// <summary>恢复未完成写入（半写恢复）：返回被修复/丢弃的片段数。</summary>
    int Recover();
}
