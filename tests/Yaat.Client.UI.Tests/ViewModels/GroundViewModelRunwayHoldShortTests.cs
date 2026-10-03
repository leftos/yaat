using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Views;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The ground view model's runway hold-short picks behind Taxi to runway, on the real KOAK layout: the hold short nearest
/// a clicked point, and the full-length hold short at each end's threshold.
/// </summary>
public class GroundViewModelRunwayHoldShortTests
{
    private static GroundLayoutDto Oak => MenuGoldenFixtures.OakLayoutForClient;

    [AvaloniaFact]
    public void FullLength_IsTheHoldShortAtThatEndsThreshold()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundViewModel ground, AircraftModel ac) = OakGround();
        GroundRunwayDto runway = Runway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        var end1Threshold = new LatLon(runway.Coordinates[0][0], runway.Coordinates[0][1]);
        var end2Threshold = new LatLon(runway.Coordinates[^1][0], runway.Coordinates[^1][1]);

        int? end1 = ground.FindFullLengthHoldShortNode(ac, runway.Name, ids.End1);
        int? end2 = ground.FindFullLengthHoldShortNode(ac, runway.Name, ids.End2);

        Assert.NotNull(end1);
        Assert.NotNull(end2);
        Assert.NotEqual(end1, end2);
        AssertIsRunwayHoldShort(ground, end1.Value, ids);
        AssertIsRunwayHoldShort(ground, end2.Value, ids);
        // Each end's pick lies at its own threshold: nearer it than the far end's, and within the runway's first 1,000 ft.
        LatLon end1Pos = PositionOf(ground, end1.Value);
        LatLon end2Pos = PositionOf(ground, end2.Value);
        Assert.True(GeoMath.DistanceNm(end1Pos, end1Threshold) < GeoMath.DistanceNm(end1Pos, end2Threshold));
        Assert.True(GeoMath.DistanceNm(end2Pos, end2Threshold) < GeoMath.DistanceNm(end2Pos, end1Threshold));
        Assert.InRange(GeoMath.DistanceNm(end1Pos, end1Threshold) * GeoMath.FeetPerNm, 0, 1000);
        Assert.InRange(GeoMath.DistanceNm(end2Pos, end2Threshold) * GeoMath.FeetPerNm, 0, 1000);
    }

    [AvaloniaFact]
    public void NearClick_IsTheHoldShortNearestTheClick()
    {
        (GroundViewModel ground, _) = OakGround();
        GroundRunwayDto runway = Runway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        List<GroundNodeDto> holdShorts = [.. Oak.Nodes.Where(n => IsHoldShortOf(n, ids))];
        Assert.True(holdShorts.Count >= 2, $"KOAK {runway.Name} has {holdShorts.Count} hold-short nodes");

        // A click on each hold short picks it.
        foreach (GroundNodeDto holdShort in holdShorts)
        {
            Assert.Equal(holdShort.Id, ground.FindHoldShortNodeNearestPoint(runway.Name, new LatLon(holdShort.Latitude, holdShort.Longitude)));
        }

        // A click on another runway's hold short picks one of this runway's, the one nearest that click.
        GroundNodeDto other = Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is not null) && !IsHoldShortOf(n, ids));
        var click = new LatLon(other.Latitude, other.Longitude);
        GroundNodeDto expected = holdShorts.MinBy(n => GeoMath.DistanceNm(click, new LatLon(n.Latitude, n.Longitude)))!;
        Assert.Equal(expected.Id, ground.FindHoldShortNodeNearestPoint(runway.Name, click));
    }

    // --- Fixtures ---------------------------------------------------------------------------

    private static (GroundViewModel Ground, AircraftModel Aircraft) OakGround()
    {
        GroundNodeDto spot = Oak.Nodes.First(n => (n.Type == "Spot") && (n.Name == "I30"));
        var ac = new AircraftModel
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Position = new LatLon(spot.Latitude, spot.Longitude),
        };
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(Oak);
        return (main.Ground, ac);
    }

    private static GroundRunwayDto Runway(string name)
    {
        var id = RunwayIdentifier.Parse(name);
        return Assert.Single(Oak.Runways!, r => RunwayIdentifier.Parse(r.Name) == id);
    }

    private static bool IsHoldShortOf(GroundNodeDto node, RunwayIdentifier runway) =>
        (node.Type == "RunwayHoldShort") && (node.RunwayId is { } id) && (RunwayIdentifier.Parse(id) == runway);

    private static void AssertIsRunwayHoldShort(GroundViewModel ground, int nodeId, RunwayIdentifier runway)
    {
        GroundNodeDto? node = ground.GetNode(nodeId);
        Assert.NotNull(node);
        Assert.True(IsHoldShortOf(node, runway), $"Node {nodeId} is {node.Type} of {node.RunwayId}, not a hold short of {runway}");
    }

    private static LatLon PositionOf(GroundViewModel ground, int nodeId)
    {
        GroundNodeDto node = ground.GetNode(nodeId)!;
        return new LatLon(node.Latitude, node.Longitude);
    }
}
