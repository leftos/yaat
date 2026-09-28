using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="SimulationEngine.TickAltitudeFixPassage"/> latches <see cref="AircraftFlightPlan.AltitudeFixPassed"/> for a
/// fix-qualified altitude (<c>170/SJC/110</c>) once the fix is more than 90° off the aircraft's track while the aircraft is
/// within 10 nm of it, never for a fix the navigation data does not know, and the latch clears when the altitude is replaced.
/// </summary>
public class AltitudeFixPassageTests
{
    private const string Callsign = "AAL123";

    private static readonly TrueHeading East = new(90);
    private static readonly TrueHeading West = new(270);
    private static readonly TrueHeading North = new(0);

    public AltitudeFixPassageTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LatLon Sjc =>
        NavigationDatabase.Instance.GetFixPosition("SJC") is { } sjc
            ? new LatLon(sjc.Lat, sjc.Lon)
            : throw new InvalidOperationException("SJC is not in NavData");

    [Fact]
    public void FlyingThroughTheFix_LatchesOnlyOnceItIsBehind()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC");

        FlyEastAt(engine, ac, 0, [-20, -10, -5, -1, -0.2]);
        Assert.False(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(17000, ac.FlightPlan.EramAltitudeFeet);

        FlyEastAt(engine, ac, 0, [0.5]);
        Assert.True(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(11000, ac.FlightPlan.EramAltitudeFeet);
        Assert.Equal(17000, ac.FlightPlan.Altitude.CruiseFeet);
    }

    [Fact]
    public void PassingAbeamWithinTenMiles_Latches()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC");

        FlyEastAt(engine, ac, 8, [-15, -2]);
        Assert.False(ac.FlightPlan.AltitudeFixPassed);

        FlyEastAt(engine, ac, 8, [1]);
        Assert.True(ac.FlightPlan.AltitudeFixPassed);
    }

    [Fact]
    public void PassingAbeamBeyondTenMiles_NeverLatches()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC");

        FlyEastAt(engine, ac, 12, [-20, -10, -1, 0, 1, 5, 10, 20]);

        Assert.False(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(17000, ac.FlightPlan.EramAltitudeFeet);
    }

    [Fact]
    public void AnFrdFix_LatchesAtTheResolvedPoint()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC090020");
        LatLon frd = FrdResolver.Resolve("SJC090020", NavigationDatabase.Instance)!.Value;

        // Already past SJC itself, 19 nm behind; the FRD point is still ahead.
        Place(engine, ac, GeoMath.ProjectPoint(frd, West, 1), East);
        Assert.False(ac.FlightPlan.AltitudeFixPassed);

        Place(engine, ac, GeoMath.ProjectPoint(frd, East, 1), East);
        Assert.True(ac.FlightPlan.AltitudeFixPassed);
    }

    [Fact]
    public void AnUnresolvableFix_NeverLatches()
    {
        (SimulationEngine engine, AircraftState ac) = Build("ZZZZZ");

        FlyEastAt(engine, ac, 0, [-20, -1, 0, 1, 5, 20]);

        Assert.False(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(17000, ac.FlightPlan.EramAltitudeFeet);
    }

    [Fact]
    public void ReplacingTheAltitude_ClearsTheLatch()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC");
        FlyEastAt(engine, ac, 0, [1]);
        Assert.True(ac.FlightPlan.AltitudeFixPassed);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.UntilFix(17000, "SJC", 9000)));

        Assert.False(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(17000, ac.FlightPlan.EramAltitudeFeet);

        // Still east of SJC with the fix behind: the new altitude latches on the next second.
        engine.TickAltitudeFixPassage();
        Assert.True(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(9000, ac.FlightPlan.EramAltitudeFeet);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(15000)));
        Assert.False(ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(15000, ac.FlightPlan.EramAltitudeFeet);
    }

    [Fact]
    public void ALatch_HoldsWhenTheAircraftTurnsBack()
    {
        (SimulationEngine engine, AircraftState ac) = Build("SJC");
        FlyEastAt(engine, ac, 0, [1]);

        Place(engine, ac, GeoMath.ProjectPoint(Sjc, East, 1), West);

        Assert.True(ac.FlightPlan.AltitudeFixPassed);
    }

    /// <summary>Places the aircraft <paramref name="northOffsetNm"/> north of SJC at each east offset in turn, tracking east, and runs the step.</summary>
    private static void FlyEastAt(SimulationEngine engine, AircraftState ac, double northOffsetNm, double[] eastOffsetsNm)
    {
        LatLon track = GeoMath.ProjectPoint(Sjc, North, northOffsetNm);
        foreach (double east in eastOffsetsNm)
        {
            LatLon position = east >= 0 ? GeoMath.ProjectPoint(track, East, east) : GeoMath.ProjectPoint(track, West, -east);
            Place(engine, ac, position, East);
        }
    }

    private static void Place(SimulationEngine engine, AircraftState ac, LatLon position, TrueHeading track)
    {
        ac.Position = position;
        ac.TrueTrack = track;
        engine.TickAltitudeFixPassage();
    }

    private static (SimulationEngine Engine, AircraftState Aircraft) Build(string fix)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = Sjc,
            Altitude = 17000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.UntilFix(17000, fix, 11000),
            },
            Track = new AircraftTrack(),
        };

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "altitude-fix",
                ScenarioName = "Altitude fix",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(aircraft);
        return (engine, aircraft);
    }
}
