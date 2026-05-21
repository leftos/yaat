namespace Yaat.Sim.Training;

/// <summary>
/// Generates the one-line coaching note shown on each M12.4 Aircraft tab row.
/// Pure template substitution — no NLG, no LLM. The note picks the highest-severity
/// finding for the aircraft and surfaces its title; absent any finding, the note
/// reflects the operation kind and completion reason so the row still reads
/// naturally for clean sessions.
/// </summary>
internal static class AircraftDebriefCoachingTemplates
{
    /// <param name="topFinding">The highest-severity finding involving this aircraft, or null if none.</param>
    /// <param name="totalFindings">Total finding count for this aircraft across all categories.</param>
    public static string Build(
        OperationKind operation,
        CompletionReason completionReason,
        string? completionDetail,
        SoloTrainingEvent? topFinding,
        int totalFindings
    )
    {
        if (topFinding is null)
        {
            return CleanRunNote(operation, completionReason, completionDetail);
        }

        var headline = $"{SeverityWord(topFinding.Severity)}: {topFinding.Title}";
        if (totalFindings == 1)
        {
            return headline + ".";
        }

        return $"{headline} (+{totalFindings - 1} more).";
    }

    private static string CleanRunNote(OperationKind operation, CompletionReason completionReason, string? completionDetail)
    {
        return (operation, completionReason) switch
        {
            (_, CompletionReason.Landed) when !string.IsNullOrEmpty(completionDetail) => $"Clean landing {completionDetail}.",
            (_, CompletionReason.Landed) => "Clean landing.",
            (_, CompletionReason.HandedOff) when !string.IsNullOrEmpty(completionDetail) => $"Clean handoff to {completionDetail}.",
            (_, CompletionReason.HandedOff) => "Clean handoff.",
            (_, CompletionReason.Dropped) => "Dropped before completion.",
            (OperationKind.Departure, CompletionReason.Active) => "In service.",
            (OperationKind.Arrival, CompletionReason.Active) => "On approach.",
            (OperationKind.Transit, CompletionReason.Active) => "In transit.",
            _ => "In service.",
        };
    }

    private static string SeverityWord(SoloTrainingEventSeverity severity) =>
        severity switch
        {
            SoloTrainingEventSeverity.Safety => "Safety",
            SoloTrainingEventSeverity.Warning => "Warning",
            SoloTrainingEventSeverity.Coach => "Coach",
            _ => "Note",
        };
}
