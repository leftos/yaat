using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// The nav-route overlay's restriction tokens: <see cref="CrossingRestrictionLabel.BuildTokenLines"/> lays a fix's
/// altitude and speed out as the label lines do, marking the altitude tokens latent on request, and
/// <see cref="AltitudeRestrictionInForce.IsShownLatent"/> decides which published altitudes the overlay dims: those not in
/// force by their source, and a published restriction above a climb via's "except maintain" top or below a descend via's
/// bottom (AIM 5-2-9.h.8 Example 4 NOTE; 7110.65 4-5-7.h.11 Example NOTE).
/// </summary>
public class NavRouteRestrictionLatencyTests
{
    private readonly ITestOutputHelper _output;

    public NavRouteRestrictionLatencyTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // --- BuildTokenLines ---

    [Fact]
    public void BuildTokenLines_SingleAltitudeAndSpeed_OneLineOfTwoTokens()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 6000),
            new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: false
        );

        Assert.Equal("≥6000 250", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_WindowAndSpeed_ThreeLines()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 17000, 11000),
            new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: false
        );

        Assert.Equal("≤17000 / ≥11000 / 250", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_SpeedOnly_OneToken()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines = CrossingRestrictionLabel.BuildTokenLines(
            null,
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrAbove),
            altitudeLatent: true
        );

        Assert.Equal("≥210", Render(lines));
    }

    [Fact]
    public void BuildTokenLines_AltitudeLatent_MarksOnlyAltitudeTokens()
    {
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> single = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000),
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow),
            altitudeLatent: true
        );
        IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> window = CrossingRestrictionLabel.BuildTokenLines(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 19000, 17000),
            new CifpSpeedRestriction(280, CifpSpeedRestrictionType.Mandatory),
            altitudeLatent: true
        );

        Assert.Equal("~≥8000~ 210", Render(single));
        Assert.Equal("~≤FL190~ / ~≥17000~ / 280", Render(window));
    }

    // --- IsShownLatent case table ---

    /// <summary>(a) "DM 110" on OAKES3 with no descend via: the STAR's published altitudes are not in force.</summary>
    [Fact]
    public void IsShownLatent_StarProcedure_AfterDmWithoutDvia_Latent()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DM 110");

        CifpAltitudeRestriction published = FirstProcedureAltitude(harness.Aircraft);
        Assert.True(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, published), $"{published} shown active after DM 110");
    }

    /// <summary>(b) "DVIA" on OAKES3: the STAR's published altitudes bind.</summary>
    [Fact]
    public void IsShownLatent_StarProcedure_AfterDvia_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA");

        CifpAltitudeRestriction published = FirstProcedureAltitude(harness.Aircraft);
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, published), $"{published} shown latent after DVIA");
    }

    /// <summary>(c) "CFIX GRUDG A110" on OAKES3: a controller's crossing is always in force.</summary>
    [Fact]
    public void IsShownLatent_AtcCrossing_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "CFIX GRUDG A110");

        CifpAltitudeRestriction crossing = Assert.IsType<CifpAltitudeRestriction>(
            Assert.Single(harness.Aircraft.Targets.NavigationRoute, f => f.Name == "GRUDG").AltitudeRestriction
        );
        Assert.Equal(CifpAltitudeRestrictionSource.Atc, crossing.Source);
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, crossing), $"{crossing} shown latent");
    }

    /// <summary>
    /// (d) A published H12-Z approach altitude is latent for an aircraft not cleared for the approach, and active once the
    /// aircraft is cleared and past HIRMO, the fix the approach joins.
    /// </summary>
    [Fact]
    public void IsShownLatent_ApproachAltitude_LatentBeforeClearance_ActiveAfter()
    {
        AircraftState cleared = DeferredH12Z();
        CifpAltitudeRestriction approachAltitude = cleared
            .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Approach);
        cleared.Targets.NavigationRoute.RemoveRange(0, cleared.Targets.NavigationRoute.FindIndex(t => t.Name == "HIRMO") + 1);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(ApproachAircraft(), approachAltitude), "shown active before the clearance");
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(cleared, approachAltitude), "shown latent cleared and past HIRMO");
    }

    /// <summary>
    /// (e) "CVIA 180" on CNDEL5: a published floor above the 18,000 top is latent; a floor below it, and a ceiling (no
    /// floor), are active.
    /// </summary>
    [Fact]
    public void IsShownLatent_ClimbViaExceptMaintain_FloorAboveTopLatent()
    {
        AircraftState aircraft = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchLine(aircraft, "CVIA 180");
        Assert.Equal(18000, aircraft.Procedure.SidViaCeiling);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 19000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 15000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrBelow, 23000)));
    }

    /// <summary>
    /// (f) "DVIA 100" on OAKES3: a published ceiling below the 10,000 bottom is latent; a ceiling above it, and a floor (no
    /// ceiling), are active.
    /// </summary>
    [Fact]
    public void IsShownLatent_DescendViaBottom_CeilingBelowBottomLatent()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA 100");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(10000, aircraft.Procedure.StarViaFloor);

        Assert.True(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrBelow, 9000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.At, 11000)));
        Assert.False(AltitudeRestrictionInForce.IsShownLatent(aircraft, Published(CifpAltitudeRestrictionType.AtOrAbove, 8000)));
    }

    /// <summary>(g) "DVIA 100" on OAKES3: a published 11,000/9,000 window's ceiling is not below the bottom, so it is active.</summary>
    [Fact]
    public void IsShownLatent_DescendViaBottom_WindowCeilingNotBelowBottom_Active()
    {
        if (OakesJoined() is not { } harness)
        {
            return;
        }

        SendOk(harness, "DVIA 100");
        Assert.Equal(10000, harness.Aircraft.Procedure.StarViaFloor);

        Assert.False(
            AltitudeRestrictionInForce.IsShownLatent(harness.Aircraft, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 11000, 9000))
        );
    }

    private static CifpAltitudeRestriction Published(CifpAltitudeRestrictionType type, int altitudeFt) => new(type, altitudeFt);

    /// <summary>Renders token lines as "a b / c": lines split by " / ", tokens by a space, a latent token as "~text~".</summary>
    private static string Render(IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> lines) =>
        string.Join(" / ", lines.Select(line => string.Join(" ", line.Select(t => t.Latent ? $"~{t.Text}~" : t.Text))));

    private static CifpAltitudeRestriction FirstProcedureAltitude(AircraftState aircraft) =>
        aircraft
            .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Procedure);

    /// <summary>A jet on OAKES3 past its entry fix, re-joined by "JARR OAKES3", as the source tests build it.</summary>
    private DescendViaSupersedesEarlierAltitudeTests.Harness? OakesJoined()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return null;
        }

        string first = harness.Aircraft.Targets.NavigationRoute[0].Name;
        for (int t = 1; harness.Aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(first, StringComparison.OrdinalIgnoreCase)); t++)
        {
            Assert.True(t <= 1800, $"The aircraft did not pass {first} within 1800 s");
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "JARR OAKES3");
        return harness;
    }

    /// <summary>A jet on EMZOH4 with EMZOH and HIRMO ahead, cleared H12-Z, which defers to HIRMO.</summary>
    private static AircraftState DeferredH12Z()
    {
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        (double Lat, double Lon) emzoh = Assert.IsType<(double, double)>(navDb.GetFixPosition("EMZOH"));
        (double Lat, double Lon) hirmo = Assert.IsType<(double, double)>(navDb.GetFixPosition("HIRMO"));

        AircraftState aircraft = ApproachAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzoh.Lat, emzoh.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmo.Lat, hirmo.Lon) });
        aircraft.Approach.Expected = "H12-Z";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Approach.PendingClearance);
        return aircraft;
    }

    private static AircraftState ApproachAircraft() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(320),
            Altitude = 5000,
            Position = new LatLon(37.5, -121.8),
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", Route = "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK" },
            Procedure = new AircraftProcedure { DestinationRunway = "12" },
        };

    private static void SendOk(DescendViaSupersedesEarlierAltitudeTests.Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void DispatchLine(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
