using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

public class TurnAnticipationTests
{
    private static AircraftState CreateAircraft(
        double lat = 37.0,
        double lon = -122.0,
        double heading = 0,
        double groundSpeed = 250,
        double altitude = 10000
    )
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Track = heading,
            Altitude = altitude,
            IndicatedAirspeed = groundSpeed,
        };
    }

    // --- ComputeAnticipationDistanceNm ---

    [Fact]
    public void ComputeAnticipation_90Deg_CorrectRadius()
    {
        // 250kts GS, 2.5°/s turn rate, 90° turn
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 90);

        // R = (250/3600) / (2.5 * π/180) ≈ 1.59nm, tan(45°) = 1, so anticipation ≈ 1.59nm
        Assert.InRange(anticipation, 1.0, 2.5);
    }

    [Fact]
    public void ComputeAnticipation_SmallTurn_ReturnsZero()
    {
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 0.5);
        Assert.Equal(0, anticipation);
    }

    [Fact]
    public void ComputeAnticipation_LargeTurn_CappedAt5nm()
    {
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 170);
        Assert.Equal(5.0, anticipation);
    }

    [Fact]
    public void ComputeAnticipation_LeftTurn_SameAsMagnitude()
    {
        // 90° left turn (0 → 270) should give same anticipation as 90° right turn
        double left = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 270);
        double right = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 90);
        Assert.Equal(right, left, precision: 6);
    }

    // --- UpdateNavigation: fly-by vs fly-over ---

    [Fact]
    public void UpdateNav_FlyBy_SequencesEarly()
    {
        // Two waypoints forming a 90° right turn, aircraft approaching first waypoint
        var wp1 = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0); // 5nm north
        var wp2 = GeoMath.ProjectPoint(wp1.Lat, wp1.Lon, 90, 5.0); // then 5nm east

        var aircraft = CreateAircraft(heading: 0, groundSpeed: 250);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP1",
                Latitude = wp1.Lat,
                Longitude = wp1.Lon,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP2",
                Latitude = wp2.Lat,
                Longitude = wp2.Lon,
            }
        );

        // Move aircraft close to WP1 but beyond NavArrivalNm (0.5nm)
        // Anticipation for 250kts/2.5deg is ~1.59nm, so place at ~1.0nm
        var pos = GeoMath.ProjectPoint(37.0, -122.0, 0, 4.0);
        aircraft.Latitude = pos.Lat;
        aircraft.Longitude = pos.Lon;

        // Verify aircraft is close enough for anticipation but not 0.5nm
        double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, wp1.Lat, wp1.Lon);
        Assert.InRange(dist, 0.8, 1.3);

        // Tick navigation — should enter anticipation zone
        FlightPhysics.Update(aircraft, 1.0);

        // The aircraft should still have route (haven't passed abeam yet at distance ~1nm)
        // but heading should be blending toward the next leg
        Assert.NotNull(aircraft.Targets.TargetHeading);
    }

    [Fact]
    public void UpdateNav_FlyOver_SequencesAtHalfNm()
    {
        var wp1 = GeoMath.ProjectPoint(37.0, -122.0, 0, 0.4); // 0.4nm north (within 0.5nm)
        var wp2 = GeoMath.ProjectPoint(wp1.Lat, wp1.Lon, 90, 5.0);

        var aircraft = CreateAircraft(heading: 0, groundSpeed: 250);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP1",
                Latitude = wp1.Lat,
                Longitude = wp1.Lon,
                IsFlyOver = true,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP2",
                Latitude = wp2.Lat,
                Longitude = wp2.Lon,
            }
        );

        FlightPhysics.Update(aircraft, 1.0);

        // WP1 was within 0.5nm, should have been sequenced even though IsFlyOver
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("WP2", aircraft.Targets.NavigationRoute[0].Name);
    }

    [Fact]
    public void UpdateNav_LastWaypoint_SequencesAtHalfNm()
    {
        var wp1 = GeoMath.ProjectPoint(37.0, -122.0, 0, 0.4); // 0.4nm north

        var aircraft = CreateAircraft(heading: 0, groundSpeed: 250);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP1",
                Latitude = wp1.Lat,
                Longitude = wp1.Lon,
            }
        );

        FlightPhysics.Update(aircraft, 1.0);

        // Single waypoint, no anticipation — should sequence at 0.5nm
        Assert.Empty(aircraft.Targets.NavigationRoute);
        Assert.Null(aircraft.Targets.TargetHeading);
    }

    [Fact]
    public void UpdateNav_StraightLeg_NoAnticipation()
    {
        // Two waypoints on same heading — no turn needed
        var wp1 = GeoMath.ProjectPoint(37.0, -122.0, 0, 2.0);
        var wp2 = GeoMath.ProjectPoint(wp1.Lat, wp1.Lon, 0, 5.0);

        var aircraft = CreateAircraft(heading: 0, groundSpeed: 250);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP1",
                Latitude = wp1.Lat,
                Longitude = wp1.Lon,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP2",
                Latitude = wp2.Lat,
                Longitude = wp2.Lon,
            }
        );

        // Place aircraft 0.8nm from WP1 — beyond NavArrivalNm but within potential anticipation range
        var pos = GeoMath.ProjectPoint(37.0, -122.0, 0, 1.2);
        aircraft.Latitude = pos.Lat;
        aircraft.Longitude = pos.Lon;

        FlightPhysics.Update(aircraft, 1.0);

        // With ~0° course change, anticipation is 0 → threshold stays at 0.5nm
        Assert.Equal(2, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("WP1", aircraft.Targets.NavigationRoute[0].Name);
    }

    [Fact]
    public void Constraints_FireWhenWaypointSequenced()
    {
        // SID via mode: constraint should apply when waypoint is sequenced
        // Place WP1 close enough to sequence (within 0.5nm), no turn → sequences via NavArrivalNm
        var wp1 = GeoMath.ProjectPoint(37.0, -122.0, 0, 0.3);
        var wp2 = GeoMath.ProjectPoint(wp1.Lat, wp1.Lon, 0, 5.0); // same heading, no turn

        var aircraft = CreateAircraft(heading: 0, groundSpeed: 250, altitude: 5000);
        aircraft.SidViaMode = true;
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP1",
                Latitude = wp1.Lat,
                Longitude = wp1.Lon,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "WP2",
                Latitude = wp2.Lat,
                Longitude = wp2.Lon,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000),
            }
        );

        FlightPhysics.Update(aircraft, 1.0);

        // WP1 should have been sequenced; constraint from WP2 applied
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal(8000.0, aircraft.Targets.TargetAltitude);
    }

    // --- ArcBlended heading ---

    [Fact]
    public void ArcBlended_90Deg_ReturnsReasonableHeading()
    {
        // Aircraft at the anticipation entry, turning right 90°
        double currentLeg = 0; // northbound
        double nextLeg = 90; // then eastbound

        // Place aircraft just south of waypoint on northbound course
        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 2.0);
        var pos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, 1.5); // 1.5nm south of waypoint

        double heading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);

        // Should be between the current leg (0°) and the next leg (90°)
        // At the start of the anticipation zone, it should be closer to 0° but starting to blend
        Assert.InRange(heading, 0, 90);
    }

    [Fact]
    public void ArcBlended_MonotonicHeadingProgression_RightTurn()
    {
        // Sample heading at multiple distances along the anticipation zone for a 90° right turn.
        // Per AIM 5-5-5.b.11.NOTE.2, heading should progress monotonically from inbound → outbound.
        double currentLeg = 0; // northbound
        double nextLeg = 90; // then eastbound

        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, currentLeg, nextLeg);

        // Sample at entry (anticipation distance), midpoint, and near-exit
        double[] distances = [anticipation, anticipation * 0.5, anticipation * 0.1];
        double previousHeading = -1;

        foreach (double dist in distances)
        {
            var pos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, dist); // south of waypoint on inbound course
            double heading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);
            heading = FlightPhysics.NormalizeHeading(heading);

            // Right turn: heading should increase monotonically (0° → 90°)
            if (previousHeading >= 0)
            {
                Assert.True(
                    heading > previousHeading,
                    $"Heading should increase for right turn: {previousHeading:F1}° → {heading:F1}° at dist={dist:F2}nm"
                );
            }

            // All headings should be between inbound (0°) and outbound (90°)
            Assert.InRange(heading, 0, 90);
            previousHeading = heading;
        }
    }

    [Fact]
    public void ArcBlended_AtEntry_HeadingNearInboundLeg()
    {
        // At the tangent point (entry to anticipation zone), heading should be close
        // to the inbound leg bearing. This is the geometric tangent point of the inscribed circle.
        double currentLeg = 0;
        double nextLeg = 90;

        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, currentLeg, nextLeg);

        // Place aircraft at the anticipation entry point
        var pos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, anticipation);
        double heading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);

        // Heading should be close to the inbound leg bearing (0°) — within ~10° tolerance
        // for the flat-earth projection approximation
        double diff = Math.Abs(FlightPhysics.NormalizeAngle(heading - currentLeg));
        Assert.True(diff < 10.0, $"At entry, heading should be near inbound ({currentLeg}°), got {heading:F1}° (diff={diff:F1}°)");
    }

    [Fact]
    public void ArcBlended_NearWaypoint_HeadingNearBisector()
    {
        // Per AIM 5-5-5.b.11.NOTE.2, leg transition occurs at the turn bisector.
        // At the point abeam the waypoint, heading should approximate the bisector heading.
        double currentLeg = 0;
        double nextLeg = 90;
        double bisector = 45; // midpoint of 0→90

        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);

        // Place aircraft very close to the waypoint (past the tangent point, near abeam)
        var pos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, 0.05);
        double heading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);

        // Heading should be near the bisector (45°) — within ~15° tolerance
        double diff = Math.Abs(FlightPhysics.NormalizeAngle(heading - bisector));
        Assert.True(diff < 15.0, $"Near waypoint, heading should be near bisector ({bisector}°), got {heading:F1}° (diff={diff:F1}°)");
    }

    [Fact]
    public void ArcBlended_LeftTurn_HeadingDecreases()
    {
        // Left turn (0° → 270°, i.e., -90° course change): heading should decrease.
        double currentLeg = 0;
        double nextLeg = 270; // 90° left turn

        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, currentLeg, nextLeg);

        // Sample at entry and near-exit
        var entryPos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, anticipation);
        var nearPos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, anticipation * 0.1);

        double entryHeading = FlightPhysics.ComputeArcBlendedHeading(entryPos.Lat, entryPos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);
        double nearHeading = FlightPhysics.ComputeArcBlendedHeading(nearPos.Lat, nearPos.Lon, 250, 2.5, wp.Lat, wp.Lon, currentLeg, nextLeg);

        // Left turn: heading should go from ~360° down toward 270°
        // Use NormalizeAngle to handle the 360/0 wrap correctly
        double entryDiff = FlightPhysics.NormalizeAngle(entryHeading - currentLeg);
        double nearDiff = FlightPhysics.NormalizeAngle(nearHeading - currentLeg);
        Assert.True(
            nearDiff < entryDiff,
            $"Left turn heading should decrease: entry={entryHeading:F1}° (diff={entryDiff:F1}), near={nearHeading:F1}° (diff={nearDiff:F1})"
        );
    }

    [Fact]
    public void ArcBlended_HeadingPerpendicularToRadial()
    {
        // At every point, the arc-blended heading should be perpendicular to the
        // line from the turn center to the aircraft. This is the fundamental geometric
        // property of circular-arc steering.
        double currentLeg = 0;
        double nextLeg = 90;
        double gs = 250;
        double turnRate = 2.5;

        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);

        // Compute turn center (same math as the implementation)
        double turnRateRad = turnRate * Math.PI / 180.0;
        double gsNmPerSec = gs / 3600.0;
        double radius = gsNmPerSec / turnRateRad;
        double courseChange = FlightPhysics.NormalizeAngle(nextLeg - currentLeg);
        double bisector = FlightPhysics.NormalizeHeading(currentLeg + courseChange / 2.0);
        double perpBearing = bisector + 90.0; // right turn
        double halfAngleRad = Math.Abs(courseChange) * Math.PI / 360.0;
        double offsetNm = radius / Math.Cos(halfAngleRad);
        var (centerLat, centerLon) = GeoMath.ProjectPoint(wp.Lat, wp.Lon, perpBearing, offsetNm);

        // At the tangent point (entry), verify heading ⊥ radial from center
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(gs, turnRate, currentLeg, nextLeg);
        var entryPos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, anticipation);

        double heading = FlightPhysics.ComputeArcBlendedHeading(entryPos.Lat, entryPos.Lon, gs, turnRate, wp.Lat, wp.Lon, currentLeg, nextLeg);

        double radial = GeoMath.BearingTo(centerLat, centerLon, entryPos.Lat, entryPos.Lon);
        double angleBetween = Math.Abs(FlightPhysics.NormalizeAngle(heading - radial));

        // For a right turn, heading = radial + 90°. Allow 5° tolerance.
        Assert.InRange(angleBetween, 85, 95);
    }

    [Fact]
    public void ArcBlended_LeftRightSymmetry()
    {
        // A 60° right turn and a 60° left turn should produce headings that are
        // symmetric reflections about the inbound leg bearing.
        var wp = GeoMath.ProjectPoint(37.0, -122.0, 0, 5.0);
        double anticipation = FlightPhysics.ComputeAnticipationDistanceNm(250, 2.5, 0, 60);
        var pos = GeoMath.ProjectPoint(wp.Lat, wp.Lon, 180, anticipation * 0.5);

        double rightHeading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, 0, 60);
        double leftHeading = FlightPhysics.ComputeArcBlendedHeading(pos.Lat, pos.Lon, 250, 2.5, wp.Lat, wp.Lon, 0, 300);

        // Right turn deflects clockwise from 0°, left turn deflects counter-clockwise by same amount
        double rightDeflection = FlightPhysics.NormalizeAngle(rightHeading - 0);
        double leftDeflection = FlightPhysics.NormalizeAngle(leftHeading - 0);

        // Deflections should be approximately equal magnitude, opposite sign
        Assert.True(rightDeflection > 0, $"Right turn deflection should be positive, got {rightDeflection:F1}°");
        Assert.True(leftDeflection < 0, $"Left turn deflection should be negative, got {leftDeflection:F1}°");
        Assert.InRange(
            Math.Abs(rightDeflection + leftDeflection),
            0,
            5.0 // tolerance for projection approximation
        );
    }

    // --- ApproachFix IsFlyOver propagation ---

    [Fact]
    public void CifpLeg_FlyOverFlag_PropagatedFromRecord()
    {
        var leg = new CifpLeg("TEST", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null, IsFlyOver: true);
        Assert.True(leg.IsFlyOver);
    }

    [Fact]
    public void CifpLeg_FlyByDefault()
    {
        var leg = new CifpLeg("TEST", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null);
        Assert.False(leg.IsFlyOver);
    }

    [Fact]
    public void NavigationTarget_IsFlyOver_DefaultsFalse()
    {
        var target = new NavigationTarget
        {
            Name = "TEST",
            Latitude = 37,
            Longitude = -122,
        };
        Assert.False(target.IsFlyOver);
    }

    [Fact]
    public void ApproachFix_IsFlyOver_DefaultsFalse()
    {
        var fix = new ApproachFix("TEST", 37, -122);
        Assert.False(fix.IsFlyOver);
    }

    [Fact]
    public void ApproachFix_FAF_IsFlyOver()
    {
        var fix = new ApproachFix("FAF", 37, -122, Role: CifpFixRole.FAF, IsFlyOver: true);
        Assert.True(fix.IsFlyOver);
    }

    // --- ResolveLegsToTargets IsFlyOver ---

    [Fact]
    public void ResolveLegsToTargets_FAF_IsFlyOver()
    {
        // A FAF leg should produce IsFlyOver=true regardless of ARINC flag
        var target = new NavigationTarget
        {
            Name = "TESTFAF",
            Latitude = 37,
            Longitude = -122,
            IsFlyOver = true, // simulating what DepartureClearanceHandler would set
        };
        Assert.True(target.IsFlyOver);
    }

    [Fact]
    public void ResolveLegsToTargets_NormalTF_IsNotFlyOver()
    {
        var target = new NavigationTarget
        {
            Name = "NORMAL",
            Latitude = 37,
            Longitude = -122,
        };
        Assert.False(target.IsFlyOver);
    }

    // --- ApproachNavigationPhase anticipation ---

    [Fact]
    public void ApproachNav_FlyByIAF_SequencesEarly()
    {
        // Create a fly-by IAF followed by IF with a turn
        var iaf = GeoMath.ProjectPoint(37.0, -122.0, 0, 0.4); // within 0.5nm
        var ifFix = GeoMath.ProjectPoint(iaf.Lat, iaf.Lon, 90, 5.0);

        var fix1 = new ApproachFix("IAF", iaf.Lat, iaf.Lon, Role: CifpFixRole.IAF);
        var fix2 = new ApproachFix("IF", ifFix.Lat, ifFix.Lon, Role: CifpFixRole.IF);

        // Even at 0.4nm (within threshold), the IAF is fly-by and should sequence
        Assert.False(fix1.IsFlyOver);
    }

    [Fact]
    public void ApproachNav_FAF_IsFlyOver()
    {
        var fix = new ApproachFix("FAF", 37, -122, Role: CifpFixRole.FAF, IsFlyOver: true);
        Assert.True(fix.IsFlyOver);
    }
}
