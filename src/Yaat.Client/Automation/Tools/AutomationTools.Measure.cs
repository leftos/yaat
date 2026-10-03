using System.ComponentModel;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    [AutomationTool(
        "place_rbl",
        "Place a range-bearing line on the primary radar between two endpoints, each a callsign, fix or FRD; returns its slot number.",
        nameof(RadarNotReady)
    )]
    public Task<AppToolOutcome> PlaceRbl(
        [Description("First endpoint: a callsign, fix or FRD.")] string from,
        [Description("Second endpoint: a callsign, fix or FRD.")] string to
    )
    {
        MeasurePlacement placement = _viewModel.PlaceMeasurementFromText(from, to);
        return Task.FromResult(
            placement.Slot is { } slot
                ? AppToolOutcome.Done($"RBL {slot} placed: {from} to {to}.")
                : AppToolOutcome.Unavailable(
                    placement.Error ?? throw new InvalidOperationException("PlaceMeasurementFromText returned neither a slot nor an error.")
                )
        );
    }

    [AutomationTool("remove_rbl", "Remove the range-bearing line in the given slot from the primary radar.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> RemoveRbl([Description("The slot number place_rbl returned.")] int slot) =>
        Task.FromResult(
            _viewModel.Measure.Remove(slot) ? AppToolOutcome.Done($"RBL {slot} removed.") : AppToolOutcome.Unavailable($"No RBL in slot {slot}.")
        );

    [AutomationTool("clear_rbls", "Remove every range-bearing line from the primary radar.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> ClearRbls()
    {
        int count = _viewModel.Measure.Lines.Count;
        _viewModel.Measure.Clear();
        return Task.FromResult(AppToolOutcome.Done($"Cleared {count} RBL(s)."));
    }
}
