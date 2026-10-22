using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A menu host with its sends captured into <see cref="Sent"/> instead of going to a server, which a test has none of,
/// its session sending with <paramref name="sessionInitials"/> and its clock a manual one; every other member is <paramref name="inner"/>'s own, so a
/// menu built over it answers, previews and draws through the real host.
/// </summary>
internal sealed class SendCapturingHost(IMenuHost inner, string sessionInitials) : IMenuHost
{
    public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

    public MenuSession Session => inner.Session with { Initials = sessionInitials };

    public Task SendAsync(string callsign, string command, string initials)
    {
        Sent.Add((callsign, command, initials));
        return Task.CompletedTask;
    }

    /// <summary>Captured into <see cref="Sent"/> like an ungated send; the gate itself is the real host's.</summary>
    public Task SendGatedAsync(string callsign, string command, string initials) => SendAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task<string?>> onSubmit) =>
        inner.ShowInputPopup(placeholder, blank, initialText, caretIndex, onSubmit);

    public string? DescribePoint(LatLon position) => inner.DescribePoint(position);

    public IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd) =>
        inner.GetTaxiChoices(callsign, node, runwayEnd);

    public IReadOnlyList<RunwayHoldShortTarget> GetRunwayHoldShortTargets(string callsign, string runwayName, string runwayEnd, LatLon click) =>
        inner.GetRunwayHoldShortTargets(callsign, runwayName, runwayEnd, click);

    public MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node, string? runwayEnd) => inner.GetCustomTaxiSeed(node, runwayEnd);

    public IReadOnlyList<string> GetNodeTaxiwayNames(GroundNodeDto node) => inner.GetNodeTaxiwayNames(node);

    public string? GetHoldShortTaxiwayName(GroundNodeDto node) => inner.GetHoldShortTaxiwayName(node);

    public bool CanTugReach(string callsign, GroundNodeDto node) => inner.CanTugReach(callsign, node);

    public Task<CommandResultDto?> SendForResultAsync(string callsign, string command, string initials) =>
        inner.SendForResultAsync(callsign, command, initials);

    public IReadOnlyList<TrafficAdvisoryCandidate> GetAdvisoryCandidates() => inner.GetAdvisoryCandidates();

    public IDisposable ObserveAircraftUpdates(Action onUpdate) => inner.ObserveAircraftUpdates(onUpdate);

    public LatLon? GetFixPosition(string name) => inner.GetFixPosition(name);

    public IReadOnlyList<string> GetReportingPointsNear(LatLon position) => inner.GetReportingPointsNear(position);

    public (string Sector, int FloorFtMsl)? GetMva(LatLon position) => inner.GetMva(position);

    public IReadOnlyList<PatternRunwayChoice> GetPatternRunwayChoices(string airportId, string aircraftType) =>
        inner.GetPatternRunwayChoices(airportId, aircraftType);

    public IReadOnlyList<string> GetActiveArrivalEnds(string airportId) => inner.GetActiveArrivalEnds(airportId);

    public bool IsToweredAirport(string airportId) => inner.IsToweredAirport(airportId);

    public IReadOnlyList<string> SuggestFixes(string partial) => inner.SuggestFixes(partial);

    public double GetFieldElevation(string? destination) => inner.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) => inner.EnterDrawRoute(callsign);

    public void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        inner.ShowWarpPopup(callsign, frd, heading, altitude, speed, onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) => inner.ShowCommandFlyout(callsign, initials);

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        inner.ShowNoteFlyout(callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor(string callsign) => inner.OpenFlightPlanEditor(callsign);

    public IReadOnlyList<MenuGroundTrafficRow> GetGroundTrafficRows(string callsign) => inner.GetGroundTrafficRows(callsign);

    /// <summary>A clock of its own that moves only when the test advances it, not the real host's, so no flyout timer outlives a test.</summary>
    public TimeProvider Clock { get; } = new ManualClock();

    public MenuPositions GetPositions() => inner.GetPositions();

    public bool IsOnTaxiRoute(string callsign, string otherCallsign) => inner.IsOnTaxiRoute(callsign, otherCallsign);

    public void HighlightAircraft(string? callsign) => inner.HighlightAircraft(callsign);

    public IReadOnlyList<MenuAirborneTrafficRow> GetAirborneTraffic(string callsign) => inner.GetAirborneTraffic(callsign);

    public HoldShortMenu GetHoldShortChoices(string callsign) => inner.GetHoldShortChoices(callsign);

    public void SetRoutePreview(TaxiRoute? route) => inner.SetRoutePreview(route);

    public TaxiRoute? GetRemainingTaxiRoute(string callsign) => inner.GetRemainingTaxiRoute(callsign);

    public void SetScopePreview(ScopePreview? preview) => inner.SetScopePreview(preview);

    public void SetRadarCue(RadarCue? cue) => inner.SetRadarCue(cue);

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => inner.GetPushbackFaceChoices(callsign);

    public PushTargetList GetPushbackTargets(string callsign) => inner.GetPushbackTargets(callsign);

    public IReadOnlyList<TaxiRouteRow> GetPresetTaxiChoices(string callsign) => inner.GetPresetTaxiChoices(callsign);

    public TaxiToRunwayMenu GetTaxiToRunwayChoices(string callsign) => inner.GetTaxiToRunwayChoices(callsign);

    public IReadOnlyList<ExitAheadDto> GetRunwayExits(string callsign, ExitSide side) => inner.GetRunwayExits(callsign, side);

    public AirTaxiMenu GetAirTaxiChoices(string callsign) => inner.GetAirTaxiChoices(callsign);

    public void EnterPushRoute(string callsign) => inner.EnterPushRoute(callsign);

    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) => inner.BuildFavorites(aircraft, context);

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns) => inner.BuildRpoItems(callsigns);

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => inner.AssumeSelectedLiveTrafficAsync(callsigns);
}
