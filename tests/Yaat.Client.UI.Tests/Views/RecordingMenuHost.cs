using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Data.Airport;

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

    /// <summary>Whether the host answers as one that can open a free-text input popup; true, so input-tier items are built.</summary>
    public bool HasInputPopup { get; set; } = true;

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

    /// <summary>The taxi-route mode each callsign answers with; a callsign not listed follows the global setting.</summary>
    public Dictionary<string, TaxiRouteDisplayMode> TaxiRouteModes { get; } = [];

    public List<(string Callsign, TaxiRouteDisplayMode Mode)> TaxiRouteModeSets { get; } = [];

    public HashSet<string> HiddenDataBlockCallsigns { get; } = [];

    public List<string> HiddenDataBlockToggles { get; } = [];

    public TaxiRouteDisplayMode GetTaxiRouteMode(string callsign) => TaxiRouteModes.GetValueOrDefault(callsign, TaxiRouteDisplayMode.Follow);

    public void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode) => TaxiRouteModeSets.Add((callsign, mode));

    public bool IsDataBlockHidden(string callsign) => HiddenDataBlockCallsigns.Contains(callsign);

    public void ToggleHiddenDataBlock(string callsign) => HiddenDataBlockToggles.Add(callsign);

    /// <summary>The ground traffic the follow and give-way submenus list, whatever the callsign asked about.</summary>
    public List<string> GroundTraffic { get; } = [];

    /// <summary>The hold-short choices the Hold short of… submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> HoldShortChoices { get; } = [];

    public List<TaxiRoute?> RoutePreviews { get; } = [];

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => GroundTraffic;

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => HoldShortChoices;

    public void SetRoutePreview(TaxiRoute? route) => RoutePreviews.Add(route);

    /// <summary>The pushback facings the face items list, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackFaceChoices { get; } = [];

    /// <summary>The stands the Push back to… submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackToChoices { get; } = [];

    /// <summary>The routes the Preset taxi route submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PresetTaxiChoices { get; } = [];

    public List<string> PushRouteCallsigns { get; } = [];

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => PushbackFaceChoices;

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => PushbackToChoices;

    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => PresetTaxiChoices;

    public void EnterPushRoute(string callsign) => PushRouteCallsigns.Add(callsign);

    /// <summary>The callsign lists the assume-selected item was clicked with, each in the order it was given.</summary>
    public List<IReadOnlyList<string>> AssumeSelectedCalls { get; } = [];

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns)
    {
        AssumeSelectedCalls.Add(callsigns);
        return Task.CompletedTask;
    }

    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) =>
        throw new NotSupportedException("The recording host does not build the favorites submenu.");
}
