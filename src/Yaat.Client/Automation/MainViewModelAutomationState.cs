using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation;

/// <summary>
/// The <see cref="IAutomationState"/> of a <see cref="MainViewModel"/>. The actions send the raw commands the view model's own
/// command line sends, without its resume confirmation.
/// </summary>
public sealed class MainViewModelAutomationState(MainViewModel viewModel) : IAutomationState
{
    public IReadOnlyList<AircraftModel> Aircraft => viewModel.Aircraft;

    public double ScenarioElapsedSeconds => viewModel.ScenarioElapsedSeconds;

    public bool IsPaused => viewModel.IsPaused;

    public int SimRate => viewModel.SimRate;

    public long TerminalCursor => TerminalEntry.LastSequence;

    public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor)
    {
        IList<TerminalEntry> entries = viewModel.TerminalEntries;
        int first = entries.Count;
        while ((first > 0) && (entries[first - 1].Sequence > cursor))
        {
            first--;
        }

        return [.. entries.Skip(first)];
    }

    public Task<AutomationActionOutcome> PauseAsync() => Send("PAUSE");

    public Task<AutomationActionOutcome> UnpauseAsync() => Send("UNPAUSE");

    public Task<AutomationActionOutcome> SetRateAsync(int rate) => Send($"SIMRATE {rate}");

    private async Task<AutomationActionOutcome> Send(string command)
    {
        CommandResultDto result = await viewModel.Connection.SendCommandAsync("", command, viewModel.Preferences.UserInitials).ConfigureAwait(false);
        return new AutomationActionOutcome(result.Success, result.Success ? null : result.Message);
    }
}
