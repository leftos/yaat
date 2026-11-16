using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Simulation;

// YAAT-2338 (GitHub #1383): N83CW spawned on "CORKK BRIXX4" into KSJC reached LUYTA and turned direct KSJC instead of
// flying the STAR's published FM 140 "expect radar vectors" leg. BRIXX4 has no runway transitions: its FM leg is the last
// leg of the common route (BRIXX IF, LUYTA TF, LUYTA FM 140.0), and the spawn route only read terminal courses from a
// runway transition, so LUYTA carried none and the route-end fallback sent the aircraft to the airport.
public class Brixx4CommonRouteFmCourseTests
{
    public Brixx4CommonRouteFmCourseTests() => TestVnasData.EnsureInitialized();

    // N83CW's scenario block from the S3-NCTA-2 (C) bundle, spawned at once and without presets.
    private const string ScenarioJson = """
        { "id": "test", "name": "Test", "aircraft": [{
            "aircraftId": "N83CW", "aircraftType": "GLF6", "transponderMode": "C",
            "startingConditions": { "type": "FixOrFrd", "fix": "COMMO", "altitude": 16000, "speed": 270,
                                    "navigationPath": "CORKK BRIXX4" },
            "onAltitudeProfile": false,
            "flightplan": { "rules": "IFR", "departure": "KBFI", "destination": "KSJC",
                            "route": "JJAMM ELMAA FAMUK Q3 FINER CHBLI BRIXX4", "cruiseAltitude": 0, "cruiseSpeed": 0,
                            "aircraftType": "GLF6" },
            "presetCommands": [], "airportId": "SJC", "expectedApproach": "I30L" }] }
        """;

    private static AircraftState Spawn()
    {
        ScenarioLoadResult result = ScenarioLoader.Load(ScenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc);
        return Assert.Single(result.ImmediateAircraft).State;
    }

    [Fact]
    public void Brixx4_PublishesItsFmLegInTheCommonRoute_AndHasNoRunwayTransitions()
    {
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar("KSJC", "BRIXX4");
        Assert.NotNull(star);
        Assert.Empty(star.RunwayTransitions);
        CifpLeg last = star.CommonLegs[^1];
        Assert.Equal(CifpPathTerminator.FM, last.PathTerminator);
        Assert.Equal("LUYTA", last.FixIdentifier);
        Assert.Equal(140.0, last.OutboundCourse!.Value, 1);
    }

    [Fact]
    public void SpawnOnBrixx4_LuytaCarriesTheCommonRouteFmCourse()
    {
        AircraftState ac = Spawn();

        Assert.Equal(["CORKK", "BRIXX", "LUYTA"], ac.Targets.NavigationRoute.Select(t => t.Name));
        NavigationTarget luyta = ac.Targets.NavigationRoute[^1];
        Assert.NotNull(luyta.TerminalCourseMagnetic);
        Assert.Equal(140.0, luyta.TerminalCourseMagnetic!.Value, 1);
    }

    [Fact]
    public void SpawnOnBrixx4_SequencingLuyta_FliesTheFmCourse_NotDirectKsjc()
    {
        AircraftState ac = Spawn();
        List<NavigationTarget> route = ac.Targets.NavigationRoute;
        NavigationTarget brixx = route.Single(t => t.Name == "BRIXX");
        NavigationTarget luyta = route.Single(t => t.Name == "LUYTA");

        // Past BRIXX, on the BRIXX-LUYTA leg, at LUYTA: the spawned LUYTA target is the last fix left.
        route.RemoveAll(t => t.Name != "LUYTA");
        ac.Position = luyta.Position;
        ac.TrueHeading = new TrueHeading(GeoMath.BearingTo(brixx.Position, luyta.Position));
        ac.Altitude = 7000;
        ac.IndicatedAirspeed = 250;

        FlightPhysics.Update(ac, 1.0);

        Assert.DoesNotContain(ac.Targets.NavigationRoute, t => t.IsClearanceLimitDirect || (t.Name == "KSJC"));
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        TrueHeading expected = new MagneticHeading(140.0).ToTrue(ac.Declination);
        Assert.True(
            ac.Targets.TargetTrueHeading!.Value.AbsAngleTo(expected) < 1.0,
            $"expected the FM 140 course, ~{expected.Degrees:F1} true, got {ac.Targets.TargetTrueHeading.Value.Degrees:F1} true; "
                + $"route [{string.Join(", ", ac.Targets.NavigationRoute.Select(t => t.Name))}]"
        );
    }
}
