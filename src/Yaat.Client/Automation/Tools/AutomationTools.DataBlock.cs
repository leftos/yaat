using System.ComponentModel;
using SkiaSharp;
using Yaat.Client.Services;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    private const int MinLeaderDirection = 1;

    private const int MaxLeaderDirection = 9;

    private const string LeaderSyncOffNote = " The radar shows it only while the student leader direction sync preference is on; it is off.";

    /// <summary>The command <c>set_leader_direction</c> sends, as the radar menu's leader item does.</summary>
    public static string LeaderDirectionCommand(int direction) => $"LDR {direction}";

    [AutomationTool(
        "set_leader_direction",
        "Send LDR to set an aircraft's leader direction (1-9 as on the keypad, 5 = default); "
            + "the radar shows it only while the student leader direction sync preference is on.",
        nameof(NoScenario)
    )]
    public async Task<AppToolOutcome> SetLeaderDirection(
        [Description("The aircraft's callsign.")] string callsign,
        [Description("1-9, keypad layout; 5 returns the leader to its default.")] int direction
    )
    {
        if (direction is < MinLeaderDirection or > MaxLeaderDirection)
        {
            throw new AppToolArgumentException(
                "direction",
                $"'direction' must be from {MinLeaderDirection} to {MaxLeaderDirection} (keypad layout), not {direction}."
            );
        }

        if (FindAircraft(callsign) is not { } aircraft)
        {
            return NoAircraft(callsign);
        }

        string command = LeaderDirectionCommand(direction);
        CommandResultDto? result = await _viewModel.SendCommandForViewCoreAsync(aircraft.Callsign, command, _viewModel.Preferences.UserInitials);
        string message = result switch
        {
            // The send threw; the view model's catch has put the reason in the status line.
            null => $"{command} for {aircraft.Callsign} was not sent: {_viewModel.StatusText}",
            { Success: false } => $"The room refused {command} for {aircraft.Callsign}: {result.Message}",
            _ => $"{command} sent for {aircraft.Callsign}.",
        };
        string note = _viewModel.Preferences.SyncStudentLeaderDirection ? "" : LeaderSyncOffNote;
        return AppToolOutcome.Done(message + note);
    }

    [AutomationTool(
        "set_datablock_offset",
        "Move an aircraft's data block on the primary radar to a screen-pixel offset from its target symbol, as dragging it does.",
        nameof(RadarNotReady)
    )]
    public Task<AppToolOutcome> SetDatablockOffset(
        [Description("The aircraft's callsign.")] string callsign,
        [Description("Horizontal offset in screen pixels, positive right.")] int dxPx,
        [Description("Vertical offset in screen pixels, positive down.")] int dyPx
    )
    {
        if (FindAircraft(callsign) is not { } aircraft)
        {
            return Task.FromResult(NoAircraft(callsign));
        }

        _viewModel.Radar.SetDataBlockOffset(aircraft.Callsign, new SKPoint(dxPx, dyPx));
        return Task.FromResult(AppToolOutcome.Done($"Data block of {aircraft.Callsign} offset to ({dxPx}, {dyPx}) px."));
    }

    [AutomationTool("reset_datablock_offset", "Return an aircraft's data block on the primary radar to its default position.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> ResetDatablockOffset([Description("The aircraft's callsign.")] string callsign)
    {
        if (FindAircraft(callsign) is not { } aircraft)
        {
            return Task.FromResult(NoAircraft(callsign));
        }

        return Task.FromResult(
            AppToolOutcome.Done(
                _viewModel.Radar.ResetDataBlockOffset(aircraft.Callsign)
                    ? $"Data block of {aircraft.Callsign} reset."
                    : $"Data block of {aircraft.Callsign} was already at its default position."
            )
        );
    }
}
