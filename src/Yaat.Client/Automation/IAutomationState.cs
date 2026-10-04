using Yaat.Client.Models;

namespace Yaat.Client.Automation;

/// <summary>
/// The simulation state the automation pipe reads, and the client actions it may take, as the main window's view model holds
/// them. Every member is used on the UI thread.
/// </summary>
public interface IAutomationState
{
    /// <summary>The aircraft the client shows.</summary>
    IReadOnlyList<AircraftModel> Aircraft { get; }

    /// <summary>Scenario-elapsed seconds: the sim clock, or the replay position in playback.</summary>
    double ScenarioElapsedSeconds { get; }

    /// <summary>Whether the room's sim is paused.</summary>
    bool IsPaused { get; }

    /// <summary>The room's sim rate.</summary>
    int SimRate { get; }

    /// <summary>The sequence number of the newest terminal entry so far; every entry added later has a higher one.</summary>
    long TerminalCursor { get; }

    /// <summary>
    /// The terminal entries still in the log whose <see cref="TerminalEntry.Sequence"/> is above <paramref name="cursor"/>,
    /// oldest first.
    /// </summary>
    /// <param name="cursor">A sequence number read from <see cref="TerminalCursor"/> or from an entry.</param>
    IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor);

    /// <summary>Sends the room a <c>PAUSE</c>.</summary>
    Task<AutomationActionOutcome> PauseAsync();

    /// <summary>Sends the room an <c>UNPAUSE</c>.</summary>
    Task<AutomationActionOutcome> UnpauseAsync();

    /// <summary>Sends the room a <c>SIMRATE</c> of <paramref name="rate"/>, with no check of the rate.</summary>
    /// <param name="rate">The sim rate to set.</param>
    Task<AutomationActionOutcome> SetRateAsync(int rate);
}

/// <summary>How a client action ended: whether the room accepted it, and its refusal when it did not.</summary>
public sealed record AutomationActionOutcome(bool Ok, string? Error);
