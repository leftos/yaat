using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The exit search's memos on an <see cref="AirportGroundLayout"/> are built once the layout is frozen and cleared by
/// <see cref="AirportGroundLayout.RebuildAdjacencyLists"/>, so a rebuilt graph is never searched through a stale memo. The real OAK
/// layout, parsed for this test alone: the shared test layout must not be rebuilt under other tests.
/// </summary>
public class AirportGroundLayoutSearchMemoTests
{
    [Fact]
    public void RebuildAdjacencyLists_ClearsTheExitSearchMemo()
    {
        TestVnasData.EnsureInitialized();
        string? geoJson = new TestAirportGroundData().GetSourceGeoJson("OAK");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        if ((geoJson is null) || (runway is null))
        {
            return;
        }

        AirportGroundLayout layout = GeoJsonParser.Parse("OAK", geoJson, "OAK", FilletMode.Standard);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(ListExitsAheadOnRollout(layout, runway)));
        Assert.True(layout.HoldsExitSearchMemo());

        layout.RebuildAdjacencyLists();

        Assert.False(layout.HoldsExitSearchMemo());
    }

    /// <summary>One exits-ahead query: a B738 on its rollout 1,500 ft past the landing threshold at 120 kt.</summary>
    private static IReadOnlyList<ExitAheadDto>? ListExitsAheadOnRollout(AirportGroundLayout layout, RunwayInfo runway)
    {
        LatLon position = GeoMath.ProjectPoint(LandingThreshold.Resolve(runway, layout), runway.TrueHeading, 1500 / GeoMath.FeetPerNm);
        var aircraft = new AircraftState
        {
            Callsign = "TST463",
            AircraftType = "B738",
            Position = position,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 120,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = new()
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        return phase.ListExitsAhead(aircraft);
    }
}
