using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression guards that approach- and pattern-entry commands resolve a single-digit
/// runway given in the FAA form ("8R", no leading zero) against the zero-padded canonical
/// ("08R") that CIFP/NavData store. These paths already normalize via
/// <see cref="Yaat.Sim.Data.Airport.RunwayIdentifier"/> (GetRunway / approach-shorthand
/// resolution) — the tests pin that invariant so the comparison-migration work can't regress
/// it. The CLAND path's red→green coverage lives in <c>ClandRunwayTests</c>.
/// </summary>
[Collection("NavDbMutator")]
public class SingleDigitRunwayCommandTests
{
    public SingleDigitRunwayCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo MakeMia8R() =>
        TestRunwayFactory.Make(
            designator: "8R",
            airportId: "MIA",
            thresholdLat: 25.796,
            thresholdLon: -80.288,
            endLat: 25.797,
            endLon: -80.258,
            heading: 87,
            elevationFt: 9
        );

    private static NavigationDatabase MakeMiaNavDb()
    {
        var procedure = new CifpApproachProcedure("MIA", "I08R", 'I', "ILS", "08R", [], new Dictionary<string, CifpTransition>(), [], false, null);

        return TestNavDbFactory.WithRunwayAndApproaches(MakeMia8R(), [procedure]);
    }

    private static AircraftState MakeArrival() =>
        new()
        {
            Callsign = "ENY3516",
            AircraftType = "E75L",
            TrueHeading = new TrueHeading(87),
            Altitude = 3000,
            Position = new LatLon(25.80, -80.40),
            FlightPlan = new AircraftFlightPlan { Destination = "MIA" },
        };

    [Fact]
    public void Capp_SingleDigit8R_ResolvesCanonicalApproach()
    {
        var aircraft = MakeArrival();
        using var _ = NavigationDatabase.ScopedOverride(MakeMiaNavDb());

        // Controller types the FAA form "ILS8R"; the approach resolves to the canonical "I08R".
        var cmd = new ClearedApproachCommand("ILS8R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I08R", aircraft.Phases.ActiveApproach.ApproachId);
        Assert.Equal("08R", aircraft.Phases.ActiveApproach.RunwayId);
        Assert.NotNull(aircraft.Phases.AssignedRunway);
        Assert.True(aircraft.Phases.AssignedRunway.Id.Contains("8R"));
        Assert.True(aircraft.Phases.AssignedRunway.Id.Contains("08R"));
    }

    [Fact]
    public void Pt_SingleDigit8R_ResolvesRunway()
    {
        var aircraft = MakeArrival();
        aircraft.Altitude = 1500;
        aircraft.Phases = new PhaseList();
        using var _ = NavigationDatabase.ScopedOverride(MakeMiaNavDb());

        // PT with the FAA form "8R" must resolve the 08R runway.
        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, "8R", null);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases?.AssignedRunway);
        Assert.True(aircraft.Phases.AssignedRunway.Id.Contains("8R"));
        Assert.True(aircraft.Phases.AssignedRunway.Id.Contains("08R"));
    }

    [Fact]
    public void AirTaxi_PaddedSingleDigitRunway_ResolvesNamedEndNotOpposite()
    {
        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        var rwy = layout.Runways.First(r => r.Name == "9 - 27");
        var end9 = rwy.Coordinates[0];
        var end27 = rwy.Coordinates[^1];

        // The zero-padded identity "09" must air-taxi to the rwy-9 threshold (first-named end), not
        // silently fall through to the opposite "27" end. FindRunway now matches single-digit padded
        // designators, so the end-selection compare must normalize too.
        Assert.True(GroundCommandHandler.TryResolveAirTaxiDestination(layout, "09", out double lat09, out double lon09));
        Assert.Equal(end9.Lat, lat09, 9);
        Assert.Equal(end9.Lon, lon09, 9);

        Assert.True(GroundCommandHandler.TryResolveAirTaxiDestination(layout, "27", out double lat27, out double lon27));
        Assert.Equal(end27.Lat, lat27, 9);
        Assert.Equal(end27.Lon, lon27, 9);
    }
}
