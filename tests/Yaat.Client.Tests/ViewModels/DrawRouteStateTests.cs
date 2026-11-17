using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.ViewModels;

/// <summary>
/// The draw-route mode both view models share: placing names each point by FRD (else <c>lat,lon</c>), undo and removal keep the
/// conditions on their waypoints, the conditions snapshot follows every change, and a confirm sends one <c>DCTF</c> command.
/// </summary>
public class DrawRouteStateTests
{
    private const string Callsign = "UAL1";

    private readonly List<(string Callsign, string Command, string Initials)> _sent = [];

    private DrawRouteState NewState(IReadOnlyList<(string Name, double Lat, double Lon)>? fixes = null) =>
        new(
            () => fixes,
            (callsign, command, initials) =>
            {
                _sent.Add((callsign, command, initials));
                return Task.CompletedTask;
            }
        );

    // Three waypoints with no fixes loaded, so each is named lat,lon: "37.000,-122.000", "37.100,-122.100", "37.200,-122.200".
    private DrawRouteState WithThreeWaypoints()
    {
        DrawRouteState state = NewState();
        state.Enter(Callsign);
        state.Place(37.0, -122.0);
        state.Place(37.1, -122.1);
        state.Place(37.2, -122.2);
        return state;
    }

    [Fact]
    public void Place_WithNoFixes_NamesThePointLatLon()
    {
        DrawRouteState state = NewState();
        state.Enter(Callsign);

        state.Place(37.7, -122.2);

        Assert.Equal("37.700,-122.200", Assert.Single(state.Waypoints!).ResolvedName);
    }

    [Fact]
    public void Place_NearAFix_NamesThePointByFrd()
    {
        TestVnasData.EnsureInitialized();
        IReadOnlyList<(string Name, double Lat, double Lon)> fixes = ScopeMapSource.BuildChartFixes(true);
        DrawRouteState state = NewState(fixes);
        state.Enter(Callsign);

        state.Place(37.72, -122.22);

        string? expected = ScopeMapSource.ToFrd(fixes, 37.72, -122.22);
        Assert.NotNull(expected);
        Assert.Equal(expected, Assert.Single(state.Waypoints!).ResolvedName);
    }

    [Fact]
    public void Place_WhileNotDrawing_PlacesNothing()
    {
        DrawRouteState state = NewState();

        state.Place(37.7, -122.2);

        Assert.Null(state.Waypoints);
        Assert.False(state.IsDrawing);
    }

    [Fact]
    public void Undo_DropsTheLastWaypointAndItsCondition_FromTheSnapshot()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(2, "080", null);

        state.Undo();

        Assert.Equal(2, state.Waypoints!.Count);
        Assert.Null(state.GetCondition(2));
        Assert.Null(state.ConditionsSnapshot);
    }

    [Fact]
    public void RemoveAt_ShiftsTheLaterConditionsDown_InTheSnapshot()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(0, "050", null);
        state.SetCondition(1, "060", null);
        state.SetCondition(2, "070", null);

        state.RemoveAt(1);

        Assert.Equal(["37.000,-122.000", "37.200,-122.200"], state.Waypoints!.Select(w => w.ResolvedName));
        Assert.Equal("070", state.GetCondition(1)?.Altitude);
        IReadOnlyDictionary<int, WaypointCondition> snapshot = state.ConditionsSnapshot!;
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("050", snapshot[0].Altitude);
        Assert.Equal("070", snapshot[1].Altitude);
    }

    [Fact]
    public void RemoveAfter_DropsTheLaterWaypointsAndConditions_FromTheSnapshot()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(0, "050", null);
        state.SetCondition(2, "070", null);

        state.RemoveAfter(0);

        Assert.Single(state.Waypoints!);
        Assert.Equal([0], state.ConditionsSnapshot!.Keys);
    }

    [Fact]
    public void Cancel_LeavesTheMode_AndClearsTheSnapshot()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(1, "060", "SPD 250");

        state.Cancel();

        Assert.False(state.IsDrawing);
        Assert.Null(state.Callsign);
        Assert.Null(state.Waypoints);
        Assert.Null(state.ConditionsSnapshot);
    }

    [Fact]
    public void Enter_DropsAnEarlierRoutesConditions_FromTheSnapshot()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(0, "050", null);

        state.Enter("DAL2");

        Assert.Equal("DAL2", state.Callsign);
        Assert.Null(state.Waypoints);
        Assert.Null(state.ConditionsSnapshot);
    }

    [Fact]
    public void Changed_FiresOnEveryChange()
    {
        DrawRouteState state = NewState();
        int changes = 0;
        state.Changed += () => changes++;

        state.Enter(Callsign);
        state.Place(37.0, -122.0);
        state.SetCondition(0, "050", null);
        state.Undo();
        state.Cancel();

        Assert.Equal(5, changes);
    }

    [Fact]
    public void BuildCommand_InlinesAnAltitudeOnlyCondition_AndSendsTheRestAsAtBlocks()
    {
        DrawRouteState state = WithThreeWaypoints();
        state.SetCondition(0, "080", null);
        state.SetCondition(1, "050", "SPD 250");

        Assert.Equal("DCTF 37.000,-122.000/080 37.100,-122.100 37.200,-122.200;AT 37.100,-122.100 CFIX 050,SPD 250", state.BuildCommand());
    }

    [Fact]
    public async Task ConfirmAsync_SendsTheRouteOnce_ForTheDrawnCallsign_AndLeavesTheMode()
    {
        DrawRouteState state = WithThreeWaypoints();

        await state.ConfirmAsync("AB");

        (string callsign, string command, string initials) = Assert.Single(_sent);
        Assert.Equal(Callsign, callsign);
        Assert.Equal("DCTF 37.000,-122.000 37.100,-122.100 37.200,-122.200", command);
        Assert.Equal("AB", initials);
        Assert.False(state.IsDrawing);
        Assert.Null(state.Waypoints);
    }

    [Fact]
    public async Task ConfirmAsync_WithNoWaypoints_SendsNothing_AndLeavesTheMode()
    {
        DrawRouteState state = NewState();
        state.Enter(Callsign);

        await state.ConfirmAsync("AB");

        Assert.Empty(_sent);
        Assert.False(state.IsDrawing);
    }
}
