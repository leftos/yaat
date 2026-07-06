using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for the "VFR flight plan with only a destination breaks CTO from the
/// departure runway" report (bundle "S2-OAK-P | S2 Rating Practical Exam", aircraft
/// N346G). A C150 parked at OAK was given a VFR flight plan with only a destination
/// (KAPC / Napa, which has no runway 28R) and no departure airport, then could not be
/// cleared for takeoff from 28R:
/// <c>Runway lookup failed for N346G: runway '28R' not found at KAPC</c> →
/// <c>CTO: FAIL — Cannot resolve runway 28R</c>.
///
/// Root cause: <see cref="CommandDispatcher.ResolveRunway"/> derived the airport to look
/// a runway up at from <c>FlightPlan.Departure</c> → <c>FlightPlan.Destination</c> →
/// <c>Ground.Layout</c>, so a filed destination pre-empted the airport the aircraft is
/// physically on. This is the exact sibling of the ground-layout bug already fixed in
/// <see cref="SimulationEngine.ResolveGroundLayout"/> (see
/// <see cref="Issue12ImplicitDestinationLayoutTests"/>) — an aircraft on the ground
/// departs/taxis on the airport its wheels are on, never on a filed destination. The fix
/// makes <c>ResolveRunway</c> prefer the physical/operational airport first.
///
/// These are constructed for determinism; the end-to-end replay of the real recording
/// lives in <see cref="VfrDestinationOnlyCtoReplayTests"/>.
/// </summary>
public class VfrDestinationOnlyRunwayResolutionTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private static AircraftState MakeOnGroundDeparture(string airportId, string filedDeparture, string filedDestination)
    {
        return new AircraftState
        {
            Callsign = "N346G",
            AircraftType = "C150",
            Position = new LatLon(37.7389, -122.2256), // OAK North Field parking
            TrueHeading = new TrueHeading(7),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                Departure = filedDeparture,
                Destination = filedDestination,
            },
            AirportId = airportId,
        };
    }

    /// <summary>
    /// The bug: on the ground at OAK with a VFR plan filed to only KAPC (empty departure).
    /// The runway lookup for 28R must resolve against OAK — the aircraft's physical airport —
    /// not KAPC. Before the fix the filed destination pre-empted the physical airport and
    /// <c>ResolveRunway</c> returned null (KAPC has no 28R).
    /// </summary>
    [Fact]
    public void ResolveRunway_OnGround_FiledDestinationElsewhere_UsesPhysicalAirport()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeOnGroundDeparture(airportId: "OAK", filedDeparture: "", filedDestination: "KAPC");

        var runway = CommandDispatcher.ResolveRunway(ac, "28R");

        Assert.NotNull(runway);
        Assert.Equal("OAK", runway.AirportId);
        Assert.True(runway.Id.Contains("28R"), $"Expected 28R, got {runway.Designator}");
    }

    /// <summary>
    /// Control: a filed departure still resolves against that airport exactly as before —
    /// the physical-first reorder must not perturb the existing flight-plan fallback for an
    /// aircraft with no operational airport context.
    /// </summary>
    [Fact]
    public void ResolveRunway_NoAirportContext_FiledDeparture_StillResolves()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeOnGroundDeparture(airportId: "", filedDeparture: "OAK", filedDestination: "");

        var runway = CommandDispatcher.ResolveRunway(ac, "28R");

        Assert.NotNull(runway);
        Assert.Equal("OAK", runway.AirportId);
    }

    /// <summary>
    /// The command-handler path the controller actually exercised: <c>RWY 28R</c> on the
    /// same destination-only VFR aircraft must assign OAK's 28R. Before the fix
    /// <see cref="GroundCommandHandler.TryAssignRunway"/> rejected with "Unknown runway 28R"
    /// because <c>ResolveRunway</c> searched KAPC.
    /// </summary>
    [Fact]
    public void Rwy_OnGround_FiledDestinationElsewhere_AssignsPhysicalRunway()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeOnGroundDeparture(airportId: "OAK", filedDeparture: "", filedDestination: "KAPC");

        var result = GroundCommandHandler.TryAssignRunway(ac, "28R");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Phases?.AssignedRunway);
        Assert.Equal("OAK", ac.Phases.AssignedRunway.AirportId);
    }
}
