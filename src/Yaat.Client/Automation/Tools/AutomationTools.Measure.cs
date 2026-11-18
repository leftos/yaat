using System.ComponentModel;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    [AutomationTool(
        "place_rbl",
        "Place a range-bearing line on the Scope view between two endpoints, each a callsign, fix or FRD; returns its slot number.",
        nameof(ScopeNotReady)
    )]
    public Task<AppToolOutcome> PlaceRbl(
        [Description("First endpoint: a callsign, fix or FRD.")] string from,
        [Description("Second endpoint: a callsign, fix or FRD.")] string to
    )
    {
        MeasurePlacement placement = PlaceScopeRbl(from, to);
        return Task.FromResult(
            placement.Slot is { } slot
                ? AppToolOutcome.Done($"RBL {slot} placed: {from} to {to}.")
                : AppToolOutcome.Unavailable(
                    placement.Error ?? throw new InvalidOperationException("PlaceMeasurementFromText returned neither a slot nor an error.")
                )
        );
    }

    [AutomationTool("remove_rbl", "Remove the range-bearing line in the given slot.", nameof(ScopeNotReady))]
    public Task<AppToolOutcome> RemoveRbl([Description("The slot number place_rbl returned.")] int slot) =>
        Task.FromResult(
            _viewModel.Measure.Remove(slot) ? AppToolOutcome.Done($"RBL {slot} removed.") : AppToolOutcome.Unavailable($"No RBL in slot {slot}.")
        );

    /// <summary>Removes the Scope view's lines only; the Radar's and the Ground view's lines in the shared store keep their slots.</summary>
    [AutomationTool("clear_rbls", "Remove every range-bearing line from the Scope view.", nameof(ScopeNotReady))]
    public Task<AppToolOutcome> ClearRbls() => Task.FromResult(AppToolOutcome.Done($"Cleared {ClearPrimaryScopeRbls()} RBL(s)."));

    /// <summary>
    /// Draws a line on the Scope view between two typed endpoints, as <c>.rbl A B</c> typed with a Scope active does; the
    /// placed line's reading, or a full store's error, goes to the status line.
    /// </summary>
    private MeasurePlacement PlaceScopeRbl(string from, string to) =>
        _viewModel.PlaceMeasurementFromText(from, to, new StatusLineSink(_viewModel), ScopeViewModel.MeasureView, _viewModel.PrimaryScope);

    /// <summary>Removes every Scope-view line from the shared store and returns how many it removed.</summary>
    private int ClearPrimaryScopeRbls()
    {
        List<int> slots = [.. PrimaryScopeRbls().Select(line => line.Slot)];
        foreach (int slot in slots)
        {
            _viewModel.Measure.Remove(slot);
        }

        return slots.Count;
    }

    /// <summary>
    /// The outcome of a measurement an app tool places: its reading or error on the status line, as the command line shows
    /// one; no line was typed, so there is no input to clear and nothing to record in the history.
    /// </summary>
    private sealed class StatusLineSink(MainViewModel main) : ICommandTextSink
    {
        public void ReportStatus(string status) => main.StatusText = status;

        public void ReportError(string message) => main.StatusText = message;

        public void ClearInput() { }

        public void AddHistory(string callsign, string command) { }

        public void AddWarning(string message) => main.AddWarningEntry(message);
    }
}
