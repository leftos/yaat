namespace Yaat.Sim.Pilot;

/// <summary>One readback clause as <see cref="PhraseologyVerbalizer.VerbalizeClause"/> spoke it.</summary>
/// <param name="Text">The spoken clause, or null when no rule speaks the command.</param>
/// <param name="OfferedQuietRows">
/// Whether the pick ran in the Quiet band with at least one of the rule's Quiet rows left beside the canonical after
/// filtering. Such a readback takes its quiet courtesy from the rows, so it skips the quiet flavour.
/// </param>
public readonly record struct ClauseVerbalization(string? Text, bool OfferedQuietRows);
