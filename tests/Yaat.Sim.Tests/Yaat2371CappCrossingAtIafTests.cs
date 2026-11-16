using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

/// <summary>
/// A CAPP whose route ends at a fix that is another transition's through-fix enters the approach at the transition
/// that begins there. N71MB on a 250 vector, CFIX KLIDE 40 then CAPP for KSJC I30L, must fly KLIDE, ZULUP, JENES,
/// HIVAK — never the BORED transition, which carries KLIDE as a later leg.
/// </summary>
public class Yaat2371CappCrossingAtIafTests
{
    [Fact]
    public void SelectBestTransition_RouteFixIsAnotherTransitionsIaf_SelectsTheTransitionBeginningThere()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase navDb = NavigationDatabase.Instance;
        CifpApproachProcedure procedure = KsjcI30l();
        AircraftState aircraft = N71mb();
        (double Lat, double Lon) klide = Assert.NotNull(navDb.GetFixPosition("KLIDE"));
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "KLIDE", Position = new LatLon(klide.Lat, klide.Lon) });
        CifpTransition? selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);
        Assert.Equal("KLIDE", selected?.Name);
    }

    [Fact]
    public void SelectBestTransition_FiledRouteFixIsAnotherTransitionsIaf_SelectsTheTransitionBeginningThere()
    {
        CifpApproachProcedure procedure = KsjcI30l();
        AircraftState aircraft = N71mb();
        aircraft.FlightPlan.Route = "DEZRT2 MVA GYLET STUBL KLIDE";
        Assert.Empty(aircraft.Targets.NavigationRoute);
        CifpTransition? selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);
        Assert.Equal("KLIDE", selected?.Name);
    }

    [Fact]
    public void Capp_OnVectorAfterCrossingAtKlide_DoesNotRouteViaBored()
    {
        AircraftState aircraft = N71mb();
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(250);
        DispatchContext ctx = TestDispatch.Context(new Random(1));
        Dispatch(aircraft, "CFIX KLIDE 40", ctx);
        Dispatch(aircraft, "CAPP", ctx);
        var route = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        Assert.DoesNotContain("BORED", route);
        Assert.DoesNotContain("SWIGS", route);
        Assert.NotNull(aircraft.Phases);
        ApproachNavigationPhase nav = Assert.Single(aircraft.Phases.Phases.OfType<ApproachNavigationPhase>());
        Assert.Equal("KLIDE", nav.Fixes[0].Name);
        Assert.Equal("KLIDE", aircraft.Phases.ActiveApproach?.TransitionName);
    }

    private static CifpApproachProcedure KsjcI30l()
    {
        TestVnasData.EnsureInitialized();
        CifpApproachProcedure? procedure = NavigationDatabase.Instance.GetApproach("KSJC", "I30L");
        Assert.NotNull(procedure);
        Assert.True(procedure.Transitions.ContainsKey("BORED"), "fixture: I30L has the BORED transition");
        Assert.True(procedure.Transitions.ContainsKey("KLIDE"), "fixture: I30L has the KLIDE transition");
        return procedure;
    }

    private static AircraftState N71mb()
    {
        TestVnasData.EnsureInitialized();
        var aircraft = new AircraftState
        {
            Callsign = "N71MB",
            AircraftType = "C25B",
            TrueHeading = new TrueHeading(262.5),
            Altitude = 6000,
            IndicatedAirspeed = 210,
            Position = new LatLon(37.0620, -121.4757),
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSLC",
                Destination = "KSJC",
                Route = "DEZRT2 MVA GYLET STUBL RAZRR5",
            },
        };
        aircraft.Targets.AssignedAltitude = 6000;
        aircraft.Targets.TargetAltitude = 6000;
        aircraft.Approach.Expected = "I30L";
        return aircraft;
    }

    private static void Dispatch(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
