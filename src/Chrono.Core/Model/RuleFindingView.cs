namespace Chrono.Core.Model;

/// <summary>Projector-independent view of a principle judgement for reports and export.</summary>
public sealed record RuleFindingView(
    string Code, string Subject, bool Holds, string Justification,
    IReadOnlyList<string> AffectedObjectIds, string EventId);
