using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// An aircraft told to extend the downwind (EXT; 7110.65 §3-8-1 "EXTEND DOWNWIND") holds its
/// altitude and never commands a climb back up the pattern. <see cref="DownwindPhase"/> latches the
/// held level once, on the first held tick past abeam, at the lower of the aircraft's altitude and
/// pattern altitude, and only a new controller altitude moves it. An aircraft already past the
/// nominal base-turn point and below pattern altitude — the defining case for an extended downwind —
/// therefore targets its present altitude, not a level recomputed from its growing distance out.
/// </summary>
public class ExtendedDownwindNoClimbTests
{
    public ExtendedDownwindNoClimbTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ExtendedDownwind_PastBaseTurn_HoldsAltitude_DoesNotClimb()
    {
        RunwayInfo rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 9);
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            null,
            authoredRunway: null
        );

        TrueHeading downwindHdg = wp.DownwindHeading;
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        // The altitude the past-abeam descent aims at: the 3° glideslope-intercept altitude at
        // the base-to-final rollout point (base extension actually flown + one turn radius). This
        // is the altitude the aircraft has legitimately descended to by the base turn.
        double baseExtNm = GeoMath.AlongTrackDistanceNm(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), abeam, downwindHdg);
        double turnRadiusNm = BasePhase.TurnRadiusNm(
            BasePhase.PlannedSpeedKt(MakeJet(abeam, downwindHdg, 0), AircraftCategory.Jet),
            AircraftCategory.Jet
        );
        double baseTurnInterceptAlt = GlideSlopeGeometry.AltitudeAtDistance(baseExtNm + turnRadiusNm, rwy.ElevationFt, AircraftCategory.Jet);

        // Place the aircraft 0.6 nm PAST the nominal base turn (extended downwind), level at the
        // altitude it descended to at the base turn — it is at, not below, its intended profile.
        (double Lat, double Lon) pos = GeoMath.ProjectPoint(abeam.Lat, abeam.Lon, downwindHdg, baseExtNm + 0.6);
        AircraftState ac = MakeJet(new LatLon(pos.Lat, pos.Lon), downwindHdg, baseTurnInterceptAlt);

        var phase = new DownwindPhase { Waypoints = wp, IsExtended = true };
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

        double currentAlt = ac.Altitude;
        double? target = ctx.Targets.TargetAltitude;
        Assert.NotNull(target);

        // The extended downwind must hold (or descend), never climb: TargetAltitude may not exceed
        // the aircraft's current altitude. 1 ft tolerance for float noise.
        Assert.True(
            target!.Value <= currentAlt + 1.0,
            $"Extended downwind commanded a CLIMB: TargetAltitude={target.Value:F0} MSL > currentAlt={currentAlt:F0} MSL "
                + $"(baseExt={baseExtNm:F2}nm, r={turnRadiusNm:F2}nm, patternAlt={wp.PatternAltitude:F0} MSL)"
        );
    }

    private static AircraftState MakeJet(LatLon pos, TrueHeading hdg, double altitude) =>
        new()
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = pos,
            TrueHeading = hdg,
            Altitude = altitude,
            IndicatedAirspeed = 170,
        };
}
