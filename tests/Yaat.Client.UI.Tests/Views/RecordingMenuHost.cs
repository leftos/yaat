using Avalonia.Controls;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Records every send and popup, and answers a picker at once: an input or filtered list with
/// <paramref name="input"/>, a list with the item whose text is <paramref name="input"/> (an empty input only records the list).
/// </summary>
internal sealed class RecordingMenuHost(string input) : IMenuHost
{
    public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

    public string[]? FixNames { get; init; }

    /// <summary>The field elevation the altitude picker is answered with; sea level by default, so the list starts at 100 ft.</summary>
    public double FieldElevation { get; init; }

    public List<string?> FieldElevationRequests { get; } = [];

    public List<(IReadOnlyList<string> Items, object? Selected)> ListPopups { get; } = [];

    public List<(string[] Names, IReadOnlyList<string>? Priority)> FilteredListPopups { get; } = [];

    public List<string> DrawRouteCallsigns { get; } = [];

    public List<(string Callsign, int Heading, int Altitude, int Speed)> WarpPopups { get; } = [];

    public Func<string, int, int, int, Task>? WarpSubmit { get; private set; }

    public int FlightPlanEditorOpens { get; private set; }

    public HashSet<string> MinifiedCallsigns { get; } = [];

    public HashSet<string> ManualOffsetCallsigns { get; } = [];

    public HashSet<string> PathShownCallsigns { get; } = [];

    public MenuMeasureState MeasureState { get; set; } = MenuMeasureState.None;

    public List<string> MinifiedToggles { get; } = [];

    public List<string> DataBlockOffsetResets { get; } = [];

    public List<string> PathToggles { get; } = [];

    public List<string> MeasurePicks { get; } = [];

    public Task SendAsync(string callsign, string command, string initials)
    {
        Sent.Add((callsign, command, initials));
        return Task.CompletedTask;
    }

    public List<string> InputPlaceholders { get; } = [];

    /// <summary>The text an input popup is answered with when it differs from a picker's answer; the picker's answer when null.</summary>
    public string? InputAnswer { get; init; }

    public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit)
    {
        InputPlaceholders.Add(placeholder);
        _ = onSubmit(InputAnswer ?? input);
    }

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick)
    {
        ListPopups.Add(([.. items.Select(i => i.ToString() ?? "")], selected));
        if (input.Length > 0)
        {
            _ = onPick(items.First(i => i.ToString() == input));
        }
    }

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick)
    {
        FilteredListPopups.Add((sortedNames, priorityItems?.Select(i => i.ToString() ?? "").ToList()));
        _ = onPick(input);
    }

    /// <summary>Records the destination asked about and answers <see cref="FieldElevation"/>.</summary>
    public double GetFieldElevation(string? destination)
    {
        FieldElevationRequests.Add(destination);
        return FieldElevation;
    }

    public void EnterDrawRoute(string callsign) => DrawRouteCallsigns.Add(callsign);

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit)
    {
        WarpPopups.Add((callsign, heading, altitude, speed));
        WarpSubmit = onSubmit;
    }

    public void OpenFlightPlanEditor() => FlightPlanEditorOpens++;

    public bool IsMinified(string callsign) => MinifiedCallsigns.Contains(callsign);

    public void ToggleMinified(string callsign) => MinifiedToggles.Add(callsign);

    public bool HasManualDataBlockOffset(string callsign) => ManualOffsetCallsigns.Contains(callsign);

    public void ResetDataBlockOffset(string callsign) => DataBlockOffsetResets.Add(callsign);

    public bool IsPathShown(string callsign) => PathShownCallsigns.Contains(callsign);

    public void ToggleShowPath(string callsign) => PathToggles.Add(callsign);

    public MenuMeasureState GetMeasureState() => MeasureState;

    public void MeasurePickOnAircraft(string callsign) => MeasurePicks.Add(callsign);

    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) =>
        throw new NotSupportedException("The recording host does not build the favorites submenu.");
}
