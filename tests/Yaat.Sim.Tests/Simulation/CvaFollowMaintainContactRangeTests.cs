using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression for the maintained-traffic-contact check in
/// <see cref="SimulationEngine.TickVisualDetection"/>. Once a follower has reported the lead
/// in sight on a visual approach, the per-tick re-check must be WEATHER-ONLY
/// (<see cref="VisualDetection.TryMaintainTrafficContact"/>) — it must NOT re-apply the
/// acquisition-range / forward-hemisphere geometry, which models FINDING unknown traffic, not
/// TRACKING traffic already called in sight. A lead that merely pulls ahead is increasing
/// separation, which per docs/conflict-and-visual-detection.md and AIM §5-5-12.a.2 / §4-4-14
/// NOTE is never a reason for the follower to break off — that is the controller's to
/// re-sequence. The airport-maintain sibling (TryMaintainAirportContact) and
/// <see cref="AirborneFollowHelper"/> both already do this correctly.
/// </summary>
public class CvaFollowMaintainContactRangeTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-cva-follow-maintain-range",
                ScenarioName = "CVA Follow Maintain Contact Range",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
    }

    private static AircraftState MakeB738OnFinal(string callsign, double lat, double lon, double headingDeg, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = altitude,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr((int)altitude),
            },
        };

    [Fact]
    public void MaintainedTrafficContact_LeadOpensBeyondDetectionRange_DoesNotFalselyReportLostSight()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return; // navdata absent → skip, per no-synthetic-data convention
        }

        var navDb = NavigationDatabase.Instance;
        var rwy = navDb.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double finalCourse = rwy.TrueHeading.Degrees;
        double reciprocal = (finalCourse + 180) % 360;
        double thLat = rwy.ThresholdLatitude;
        double thLon = rwy.ThresholdLongitude;

        // Establish the follow WITH the lead in range first (5 nm dead-ahead), exactly like the
        // working CVA-follow E2E, so RTIS acquires and CVA FOLLOW wires up the follow.
        var (leadLat, leadLon) = GeoMath.ProjectPointRaw(thLat, thLon, reciprocal, 10.0);
        var (trailLat, trailLon) = GeoMath.ProjectPointRaw(thLat, thLon, reciprocal, 15.0);
        var leader = MakeB738OnFinal("LEAD1", leadLat, leadLon, finalCourse, altitude: 3000);
        var trailer = MakeB738OnFinal("TRAIL1", trailLat, trailLon, finalCourse, altitude: 3500);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);

        Assert.True(engine.SendCommand("LEAD1", "RFIS").Success);
        Assert.True(engine.SendCommand("LEAD1", "CVA 30").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RTIS LEAD1").Success);
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight, "RTIS should resolve within 15 s at 5 nm dead-ahead");
        Assert.True(engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1").Success);
        Assert.Equal("LEAD1", trailer.Approach.FollowingCallsign);
        Assert.Equal("VIS30", trailer.Phases!.ActiveApproach!.ApproachId);

        // Clearing the visual approach resets the in-sight flag; re-establish it (lead still in
        // range, dead-ahead) so the per-tick check is now in the MAINTAINED-contact regime.
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight, "follower should re-acquire the in-range lead on the visual approach");

        // The lead now PULLS AHEAD — a faster B738 opening the gap beyond the visual
        // traffic-detection range for its type. Reposition it dead-ahead of the trailer but
        // beyond range. This is a GROWING gap (increasing separation), never a loss of contact.
        double range = WakeTurbulenceData.TrafficDetectionRangeNm("B738", AircraftCategory.Jet);
        double gapNm = range + 4.0;
        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, gapNm);
        leader.Position = new LatLon(newLeadLat, newLeadLon);

        double dist = GeoMath.DistanceNm(trailer.Position, leader.Position);
        double bearing = GeoMath.BearingTo(trailer.Position, leader.Position);
        double offNose = trailer.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        output.WriteLine(
            $"lead detection range={range:F1}nm, gap={dist:F1}nm, off-nose={offNose:F0}° (dead-ahead, so OutOfRange is the only geometric trip)"
        );
        Assert.True(dist > range, $"setup: gap {dist:F1}nm must exceed detection range {range:F1}nm");
        Assert.True(offNose < 90.0, "setup: lead must be dead-ahead so only OutOfRange (not BehindOwnship) can trip");

        // Weather is clear (no METAR), so a weather-only maintained-contact check acquires.
        engine.TickVisualDetection();

        Assert.True(
            trailer.Approach.HasReportedTrafficInSight,
            "A lead that merely opens the gap must NOT trigger a false 'lost sight of traffic' — "
                + "maintained traffic contact is weather-only and must skip the acquisition-range geometry."
        );
    }
}
