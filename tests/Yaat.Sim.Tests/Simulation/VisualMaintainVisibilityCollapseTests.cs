using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Maintained visual contact (traffic in sight, field in sight) is a weather-only check:
/// the target-finding geometry (type detection range, forward hemisphere, bank occlusion)
/// never breaks an established contact. But flight visibility IS weather — when the
/// reported visibility collapses below the distance to the target (fog, heavy haze), the
/// pilot genuinely cannot see it any more and must report losing sight, exactly as an
/// obscuring BKN/OVC layer already does. Visibility at or above the censored METAR
/// maximum ("10SM or more") asserts no limit and never breaks contact.
/// </summary>
public class VisualMaintainVisibilityCollapseTests(ITestOutputHelper output)
{
    private const double SmToNm = 0.869;

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
                ScenarioId = "test-visual-maintain-visibility",
                ScenarioName = "Visual Maintain Visibility Collapse",
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

    private static WeatherProfile OakVisibility(string visToken) => new() { Metars = [$"KOAK 121853Z 27012KT {visToken} CLR 20/12 A2992"] };

    [Fact]
    public void MaintainedTrafficContact_VisibilityCollapsesBelowGap_ReportsLostSight()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return; // navdata absent → skip, per no-synthetic-data convention
        }

        var (leader, trailer, finalCourse) = EstablishCvaFollowWithTrafficInSight(engine);

        // Weather collapses to 3SM. The lead sits 5 nm ahead — beyond the
        // 3 SM ≈ 2.6 nm flight-visibility range, so contact is genuinely lost.
        var weather = OakVisibility("3SM");
        var parsed = weather.GetWeatherForAirport("OAK");
        Assert.NotNull(parsed);
        Assert.Equal(3.0, parsed.VisibilityStatuteMiles);
        engine.World.Weather = weather;

        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, 5.0);
        leader.Position = new LatLon(newLeadLat, newLeadLon);
        Assert.True(GeoMath.DistanceNm(trailer.Position, leader.Position) > (3.0 * SmToNm));

        engine.TickVisualDetection();

        Assert.False(
            trailer.Approach.HasReportedTrafficInSight,
            "A visibility collapse below the gap to the lead must break maintained traffic contact"
        );
    }

    [Fact]
    public void MaintainedTrafficContact_GapWithinCollapsedVisibility_KeepsContact()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer, finalCourse) = EstablishCvaFollowWithTrafficInSight(engine);

        // Same 3SM collapse, but the lead is only 2 nm ahead — inside the
        // 2.6 nm flight-visibility range, so contact holds.
        engine.World.Weather = OakVisibility("3SM");
        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, 2.0);
        leader.Position = new LatLon(newLeadLat, newLeadLon);

        engine.TickVisualDetection();

        Assert.True(trailer.Approach.HasReportedTrafficInSight, "A lead within the collapsed visibility range must remain in sight");
    }

    [Fact]
    public void MaintainedTrafficContact_CensoredTenMileVisibility_NeverCapsRange()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer, finalCourse) = EstablishCvaFollowWithTrafficInSight(engine);

        // 10SM is the censored METAR maximum ("10 or more") — it asserts no limit,
        // so even a 14 nm gap (also beyond the B738 type detection range) holds.
        engine.World.Weather = OakVisibility("10SM");
        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, 14.0);
        leader.Position = new LatLon(newLeadLat, newLeadLon);

        engine.TickVisualDetection();

        Assert.True(trailer.Approach.HasReportedTrafficInSight, "Censored 10SM visibility asserts no limit — maintained contact must not range-cap");
    }

    [Fact]
    public void MaintainedFieldContact_VisibilityCollapsesBelowFieldDistance_ReportsLostSight()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var ac = EstablishCvaWithFieldInSight(engine, finalDistanceNm: 6.0, altitude: 2000);

        // Weather collapses to 2SM while the aircraft is ~6 nm out — far beyond the
        // 2 SM ≈ 1.7 nm flight-visibility range to the field.
        engine.World.Weather = OakVisibility("2SM");

        engine.TickVisualDetection();

        Assert.False(
            ac.Approach.HasReportedFieldInSight,
            "A visibility collapse below the distance to the field must break maintained field contact"
        );
    }

    [Fact]
    public void MaintainedFieldContact_CloseInUnderCollapsedVisibility_KeepsContact()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var ac = EstablishCvaWithFieldInSight(engine, finalDistanceNm: 6.0, altitude: 2000);

        // Reposition to short final (1 nm, 500 ft) BEFORE the collapse: at 1 nm the
        // field is inside the 2 SM ≈ 1.7 nm visibility range and stays in sight.
        var navDb = NavigationDatabase.Instance;
        var rwy = navDb.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (lat, lon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 1.0);
        ac.Position = new LatLon(lat, lon);
        ac.Altitude = 500;
        engine.World.Weather = OakVisibility("2SM");

        engine.TickVisualDetection();

        Assert.True(ac.Approach.HasReportedFieldInSight, "A field within the collapsed visibility range must remain in sight");
    }

    /// <summary>
    /// At a sprawling field the assigned runway threshold can sit several nm FARTHER from
    /// the aircraft than the ARP the acquisition path measures to. The maintain datum is
    /// min(ARP, threshold) so it can never exceed the acquire datum — otherwise an
    /// aircraft mid-field would acquire (ARP in range) and immediately "lose" the field
    /// (threshold out of range) with no weather change, chattering at 1 Hz.
    /// </summary>
    [Fact]
    public void MaintainedFieldContact_SprawlingField_MaintainDatumNeverExceedsAcquireDatum()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var arp = navDb.GetFixPosition("DEN");
        Assert.NotNull(arp);
        double? aptElev = navDb.GetAirportElevation("DEN");
        Assert.NotNull(aptElev);
        var arpPos = new LatLon(arp.Value.Lat, arp.Value.Lon);

        // Pick whichever DEN runway end sits farthest from the ARP — the test needs a
        // threshold the collapsed visibility range cannot reach from mid-field.
        RunwayInfo? rwy = null;
        double arpToThr = 0.0;
        foreach (var candidate in navDb.GetRunways("DEN"))
        {
            double d = GeoMath.DistanceNm(arpPos, new LatLon(candidate.ThresholdLatitude, candidate.ThresholdLongitude));
            if (d > arpToThr)
            {
                arpToThr = d;
                rwy = candidate;
            }
        }

        Assert.NotNull(rwy);
        var thr = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        Assert.True(arpToThr > 2.2, $"setup: DEN {rwy.Designator} threshold must sit well away from the ARP (got {arpToThr:F1} nm)");

        // Establish field-in-sight on a 6 nm final for that runway in clear weather.
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (lat, lon) = GeoMath.ProjectPointRaw(thr.Lat, thr.Lon, reciprocal, 6.0);
        var ac = MakeB738OnFinal("DEN1", lat, lon, rwy.TrueHeading.Degrees, altitude: aptElev.Value + 2000);
        ac.FlightPlan.Destination = "DEN";
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand("DEN1", "RFIS").Success);
        Assert.True(engine.SendCommand("DEN1", $"CVA {rwy.Designator}").Success);
        for (int t = 1; t <= 15 && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(ac.Approach.HasReportedFieldInSight, "field should be in sight in clear weather on final");

        // Mid-field: 1 nm from the ARP on the side OPPOSITE the 34L threshold, so the
        // assigned threshold lies arpToThr + 1 nm away — well beyond the 2SM maintained
        // range (2 × 0.869 × 1.25 ≈ 2.2 nm) — while the field itself is right off the wing.
        double awayFromThr = (GeoMath.BearingTo(arpPos, thr) + 180) % 360;
        var (midLat, midLon) = GeoMath.ProjectPointRaw(arpPos.Lat, arpPos.Lon, awayFromThr, 1.0);
        ac.Position = new LatLon(midLat, midLon);
        ac.Altitude = aptElev.Value + 1500;
        double thrDist = GeoMath.DistanceNm(ac.Position, thr);
        Assert.True(thrDist > 2.5, $"setup: assigned threshold must lie beyond the maintained range (got {thrDist:F1} nm)");
        engine.World.Weather = new WeatherProfile { Metars = ["KDEN 121853Z 27012KT 2SM CLR 20/12 A2992"] };

        engine.TickVisualDetection();

        Assert.True(
            ac.Approach.HasReportedFieldInSight,
            "maintain datum = min(ARP, threshold): a field 1 nm off the wing stays in sight even when the assigned threshold lies beyond the collapsed visibility range"
        );
    }

    /// <summary>
    /// Spawns a B738 leader (10 nm final) + trailer (15 nm final) on OAK 30, establishes
    /// RFIS/CVA on the leader and RTIS + CVA FOLLOW on the trailer, and re-establishes
    /// maintained traffic contact after the CVA reset. Mirrors CvaFollowMaintainContactRangeTests.
    /// </summary>
    private static (AircraftState Leader, AircraftState Trailer, double FinalCourse) EstablishCvaFollowWithTrafficInSight(SimulationEngine engine)
    {
        var navDb = NavigationDatabase.Instance;
        var rwy = navDb.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double finalCourse = rwy.TrueHeading.Degrees;
        double reciprocal = (finalCourse + 180) % 360;

        var (leadLat, leadLon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 10.0);
        var (trailLat, trailLon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, 15.0);
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

        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight, "follower should re-acquire the in-range lead on the visual approach");

        return (leader, trailer, finalCourse);
    }

    /// <summary>
    /// Spawns a single B738 on an OAK 30 final at the given distance/altitude and
    /// establishes RFIS + CVA 30 with the field in sight (clear weather).
    /// </summary>
    private static AircraftState EstablishCvaWithFieldInSight(SimulationEngine engine, double finalDistanceNm, double altitude)
    {
        var navDb = NavigationDatabase.Instance;
        var rwy = navDb.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (lat, lon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, finalDistanceNm);
        var ac = MakeB738OnFinal("FLD1", lat, lon, rwy.TrueHeading.Degrees, altitude);
        engine.World.AddAircraft(ac);

        Assert.True(engine.SendCommand("FLD1", "RFIS").Success);
        Assert.True(engine.SendCommand("FLD1", "CVA 30").Success);
        for (int t = 1; t <= 15 && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(ac.Approach.HasReportedFieldInSight, "field should be in sight in clear weather on final");

        return ac;
    }
}
