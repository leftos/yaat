using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Pilot;

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
        var end9 = new LatLon(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon);
        var end27 = new LatLon(rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);

        // An air taxi to a runway ends at that runway's holding position, never on the pavement, so each
        // designator resolves to the full-length bar of the end the controller named. The zero-padded identity
        // "09" must pick the rwy-9 end (first-named), not silently fall through to the opposite "27" end —
        // FindRunway matches single-digit padded designators, so the end-selection compare must normalize too.
        Assert.True(GroundCommandHandler.TryResolveAirTaxiDestination(layout, "09", out var bar09));
        Assert.Equal(GroundCommandHandler.AirTaxiDestinationKind.Runway, bar09.Kind);
        Assert.True(
            GeoMath.DistanceNm(bar09.Target, end9) < GeoMath.DistanceNm(bar09.Target, end27),
            "ATXI 09 must hold short at the runway 9 end, not the 27 end"
        );

        Assert.True(GroundCommandHandler.TryResolveAirTaxiDestination(layout, "27", out var bar27));
        Assert.Equal(GroundCommandHandler.AirTaxiDestinationKind.Runway, bar27.Kind);
        Assert.True(
            GeoMath.DistanceNm(bar27.Target, end27) < GeoMath.DistanceNm(bar27.Target, end9),
            "ATXI 27 must hold short at the runway 27 end, not the 9 end"
        );
    }

    /// <summary>
    /// A single-digit runway still gets a readback. The unpadded "9" is not runway-shaped to the verbalizer (and
    /// cannot be padded at parse time — gates named "7" and "9" are real), so the handler reports the destination
    /// as the layout resolved it and the readback verbalizes that. The located form is unambiguous and is padded
    /// by the parser.
    /// </summary>
    [Fact]
    public void AirTaxi_SingleDigitRunway_ReadsBackTheRunway()
    {
        Assert.Equal("09@T", Assert.IsType<AirTaxiCommand>(CommandParser.Parse("ATXI 9@T").Value).Destination);
        Assert.Equal("09L", Assert.IsType<AirTaxiCommand>(CommandParser.Parse("ATXI 9L").Value).Destination);

        // An explicit sigil names a gate or a spot, so the token is kept verbatim — "$9L" is not runway 09L.
        Assert.Equal("9L", Assert.IsType<AirTaxiCommand>(CommandParser.Parse("ATXI $9L").Value).Destination);
        Assert.Equal("9L", Assert.IsType<AirTaxiCommand>(CommandParser.Parse("ATXI @9L").Value).Destination);
        Assert.Equal("air taxi to runway nine at tango", PhraseologyVerbalizer.Verbalize(new AirTaxiCommand("09@T")));
        Assert.Equal("air taxi to runway 9 at T", PhraseologyVerbalizer.VerbalizeTerminal(new AirTaxiCommand("09@T")));
        Assert.Equal("air taxi to runway nine", PhraseologyVerbalizer.Verbalize(new AirTaxiCommand("09")));

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        var heli = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "EC35",
            Position = layout.Nodes.Values.OrderBy(n => n.Id).First().Position,
            TrueHeading = new TrueHeading(90),
            Altitude = 8,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KMIA" },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();

        var result = GroundCommandHandler.TryAirTaxi(heli, "9", layout);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Air taxi to runway 9, holding short", result.Message);
        var effective = Assert.IsType<AirTaxiCommand>(result.EffectiveCommand);
        Assert.Equal("09", effective.Destination);
        Assert.Equal("air taxi to runway nine", PhraseologyVerbalizer.Verbalize(effective));
    }
}
