using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Turbine traffic-pattern altitude realism (follow-up to issue #412). AIM 4-3-3.a.2: large and
/// turbine-powered aircraft fly the pattern at 1,500 ft AGL, or 500 ft above the established
/// pattern altitude where one is published — the authored airport TPA is the *established*
/// value the category rule applies to, not a verbatim replacement. AIM 4-3-3.a.3 gives
/// helicopters an absolute 500 AGL, so a fixed-wing authored TPA must not drag them up to
/// co-altitude with the aeroplane pattern. Also pins the descent-profile retarget: the
/// past-abeam downwind descent aims at the glideslope-intercept altitude at the base-to-final
/// rollout point, not a fixed fraction of TPA (AIM FIG 4-3-2 key 2 covers the level segment
/// to abeam; the glide-path aim point is the modeled descent).
/// </summary>
public class PatternTurbineTpaTests
{
    public PatternTurbineTpaTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TurbopropCategoryPatternAltitude_Is1500Agl()
    {
        // AIM 4-3-3.a.2 keys on propulsion; every YAAT turboprop is turbine-powered.
        Assert.Equal(1500, CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Turboprop));
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 1109)]
    [InlineData(AircraftCategory.Turboprop, 1109)]
    public void AuthoredTpa_Turbine_Flies500AboveEstablished(AircraftCategory category, double expectedMsl)
    {
        // OAK 28L authors 600 AGL (field elev 9). AIM 4-3-3.a.2: turbine aircraft fly
        // 500 ft above the established pattern altitude → 1,100 AGL = 1,109 MSL.
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(600);

        var (_, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, category, commandSizeNm: null, commandAltitudeMslFt: null);

        Assert.Equal(expectedMsl, alt!.Value, 0);
    }

    [Theory]
    [InlineData(600, 509)] // fixed-wing authored TPA must not drag the helicopter up to co-altitude
    [InlineData(400, 409)] // authored below 500: co-altitude with the aeroplanes — accepted degenerate case (above them would be worse)
    public void AuthoredTpa_Helicopter_StaysAtOrBelow500Agl(double authoredAgl, double expectedMsl)
    {
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(authoredAgl);

        var (_, alt) = PatternGeometry.ResolveAuthoredOverrides(
            rwy,
            authored,
            AircraftCategory.Helicopter,
            commandSizeNm: null,
            commandAltitudeMslFt: null
        );

        Assert.Equal(expectedMsl, alt!.Value, 0);
    }

    [Fact]
    public void AuthoredTpa_Piston_FliesEstablishedVerbatim()
    {
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(600);

        var (_, alt) = PatternGeometry.ResolveAuthoredOverrides(
            rwy,
            authored,
            AircraftCategory.Piston,
            commandSizeNm: null,
            commandAltitudeMslFt: null
        );

        Assert.Equal(609, alt!.Value, 0);
    }

    [Fact]
    public void CommandTpaOverride_WinsVerbatim_ForTurbine()
    {
        // A controller TPA instruction is flyable at any value — no category adjustment.
        var rwy = TestRunwayFactory.Make(elevationFt: 9);
        var authored = MakeAuthored(600);

        var (_, alt) = PatternGeometry.ResolveAuthoredOverrides(rwy, authored, AircraftCategory.Jet, commandSizeNm: null, commandAltitudeMslFt: 800);

        Assert.Equal(800, alt);
    }

    private static Yaat.Sim.Data.Airport.GroundRunway MakeAuthored(double aglFt) =>
        new()
        {
            Name = "28L - 10R",
            Coordinates = [],
            WidthFt = 150,
            PatternAltitudeAglFt = aglFt,
            PatternSizeNm = null,
        };

    [Fact]
    public void DownwindPastAbeam_TargetsGlideslopeInterceptAtRollout_NotFractionOfTpa()
    {
        var rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 9);
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, "", 0, PatternDirection.Left, null, null, null, authoredRunway: null);

        // Place the aircraft on the downwind just past abeam-the-threshold, at pattern altitude.
        var downwindHdg = wp.DownwindHeading;
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);
        var pos = GeoMath.ProjectPoint(abeam.Lat, abeam.Lon, downwindHdg, 0.1);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(pos.Lat, pos.Lon),
            TrueHeading = downwindHdg,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 170,
        };

        var phase = new DownwindPhase { Waypoints = wp };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);
        phase.OnTick(ctx);

        // Expected: the glideslope-intercept altitude at the base-to-final rollout point
        // (base extension actually flown + one turn radius from the threshold), the same
        // aim point BasePhase stabilizes on — not 60% of the way down from TPA.
        double baseExtNm = GeoMath.AlongTrackDistanceNm(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), abeam, downwindHdg);
        double turnRadiusNm = BasePhase.TurnRadiusNm(BasePhase.PlannedSpeedKt(ac, AircraftCategory.Jet), AircraftCategory.Jet);
        double expected = GlideSlopeGeometry.AltitudeAtDistance(baseExtNm + turnRadiusNm, rwy.ElevationFt, AircraftCategory.Jet);

        Assert.NotNull(ctx.Targets.TargetAltitude);
        Assert.Equal(expected, ctx.Targets.TargetAltitude!.Value, 0);

        // Concrete pin so a formula/sign flip in both implementation and expectation can't
        // pass tautologically: a category jet's 3° intercept at ~4.2 nm sits near 1,360 AGL.
        Assert.InRange(ctx.Targets.TargetAltitude!.Value - rwy.ElevationFt, 1300, 1400);
    }
}
