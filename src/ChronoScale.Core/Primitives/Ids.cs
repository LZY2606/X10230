namespace ChronoScale.Core.Primitives;

/// <summary>稳定身份：所有原始材料、事件、派生值都带稳定 ID。
/// 材料 ID 在导入时由内容与来源坐标决定（不随派生结果变化）。</summary>
public static class StableIds
{
    public static string Material(string projectKey, string kind, string sourceLocalId)
        => $"M::{projectKey}::{kind}::{sourceLocalId}";

    public static string Event(ulong seq) => $"E::{seq:D8}";

    public static string Derived(string eventId, string branchId, string kind, int ordinal)
        => $"D::{eventId}::{branchId}::{kind}::{ordinal:D4}";
}
