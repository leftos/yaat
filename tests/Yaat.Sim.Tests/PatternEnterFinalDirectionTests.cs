using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class PatternEnterFinalDirectionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public PatternEnterFinalDirectionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R(), MakeOak28L(), MakeSingleRunway18()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeOak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.72481,
            thresholdLon: -122.20471,
            endLat: 37.73047,
            endLon: -122.22218,
            heading: 292,
            elevationFt: 9,
            lengthFt: 5336,
            widthFt: 150
        );

    private static RunwayInfo MakeOak28L() =>
        TestRunwayFactory.Make(
            designator: "28L",
            airportId: "KOAK",
            thresholdLat: 37.72205,
            thresholdLon: -122.20620,
            endLat: 37.72871,
            endLon: -122.22580,
            heading: 292,
            elevationFt: 9,
            lengthFt: 10500,
            widthFt: 150
        );

    // Synthetic single-runway airport (no parallel sibling) for default-left coverage.
    private static RunwayInfo MakeSingleRunway18() =>
        TestRunwayFactory.Make(
            designator: "18",
            airportId: "KSOL",
            thresholdLat: 38.00,
            thresholdLon: -121.00,
            endLat: 37.985,
            endLon: -121.00,
            heading: 180,
            elevationFt: 100,
            lengthFt: 5000,
            widthFt: 100
        );

    private static AircraftState MakeAircraft(double lat, double lon, double alt, double heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N564U",
            AircraftType = "C152",
            Position = new LatLon(lat, lon),
            Altitude = alt,
            TrueHeading = new TrueHeading(heading),
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Phases = new PhaseList(),
        };
        aircraft.FlightPlan.FlightRules = "VFR";
        return aircraft;
    }

    /// <summary>
    /// EF (Enter Final) without an explicit L/R verb should default to the runway's
    /// natural pattern direction. For OAK 28R (the outer of two close parallels) that
    /// means RIGHT traffic so any post-COPT or post-GoAround auto-cycle keeps the
    /// downwind north of 28L (AIM 4-3-3 close-parallel convention).
    ///
    /// Reproduces the bug seen in S2-OAK-3 VFR Sequencing recording where N564U auto-
    /// cycled left traffic and flew the downwind south of 28L.
    /// </summary>
    [Fact]
    public void EF_28R_AtOAK_DefaultsToRightTraffic()
    {
        var aircraft = MakeAircraft(37.68, -122.14, 2000, 292);
        aircraft.Phases!.AssignedRunway = MakeOak28R();

        var cmd = new EnterFinalCommand(RunwayId: "28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Right, aircraft.Phases!.TrafficDirection);
    }

    /// <summary>
    /// EF on 28L (the inner parallel) should default to LEFT traffic so the downwind
    /// stays south of 28R. Symmetric counterpart to the 28R case.
    /// </summary>
    [Fact]
    public void EF_28L_AtOAK_DefaultsToLeftTraffic()
    {
        var aircraft = MakeAircraft(37.68, -122.14, 2000, 292);
        aircraft.Phases!.AssignedRunway = MakeOak28L();

        var cmd = new EnterFinalCommand(RunwayId: "28L");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Left, aircraft.Phases!.TrafficDirection);
    }

    /// <summary>
    /// EF on a runway without an L/R suffix (no parallel sibling) should fall through
    /// to the FAA standard left-traffic default (AIM 4-3-3).
    /// </summary>
    [Fact]
    public void EF_SingleRunway_DefaultsToLeftTraffic()
    {
        var aircraft = MakeAircraft(38.05, -121.00, 2000, 180);
        aircraft.Phases!.AssignedRunway = MakeSingleRunway18();

        var cmd = new EnterFinalCommand(RunwayId: "18");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Left, aircraft.Phases!.TrafficDirection);
    }
}
