using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Traffic-contact acquisition and maintenance judge the air mass the FOLLOWER is flying
/// in — the nearest reporting station to its position — never the destination airport's
/// METAR. <see cref="Phases.AirborneFollowHelper.CheckLeadLifecycle"/> already sources
/// weather that way via <see cref="VisualAcquisition"/>; these tests pin that
/// <see cref="SimulationEngine.TickVisualDetection"/>'s traffic branch agrees, so the same
/// follower is never evaluated against two different cloud decks / visibilities in the
/// same tick (issue #344). Field (airport) contact keeps destination sourcing — there the
/// destination is the thing being looked at.
///
/// Geometry: the OAK 30 final approaches from the southeast, over Hayward. A pair placed
/// 10–15 nm out on that final sits closer to KHWD than to KOAK, so a KHWD METAR in the
/// profile is the ownship-nearest station while KOAK remains the destination.
/// </summary>
public class VisualTrafficWeatherSourceTests(ITestOutputHelper output)
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
                ScenarioId = "test-visual-traffic-weather-source",
                ScenarioName = "Visual Traffic Weather Source",
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

    private static WeatherProfile TwoStations(string oakTail, string hwdTail) =>
        new() { Metars = [$"KOAK 121853Z 27012KT {oakTail}", $"KHWD 121853Z 27012KT {hwdTail}"] };

    private static void AssertNearestStationIsHayward(WeatherProfile weather, AircraftState follower)
    {
        var near = weather.GetWeatherNearPosition(follower.Position, MetarInterpolator.MaxInterpolationRangeNm);
        Assert.NotNull(near);
        Assert.Equal("KHWD", near.Value.Item2, ignoreCase: true);
    }

    [Fact]
    public void MaintainedTrafficContact_DestinationMetarBad_LocalStationClear_HoldsContact()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return; // navdata absent → skip, per no-synthetic-data convention
        }

        var (leader, trailer, finalCourse) = EstablishCvaFollowWithTrafficInSight(engine);

        // Destination reports 2SM fog, but the air the follower is flying in (KHWD,
        // the nearest station) is clear. The 5 nm gap far exceeds the destination's
        // 2 SM maintained range (2 × 0.869 × 1.25 ≈ 2.2 nm) — only destination-sourced
        // weather would break this contact.
        var weather = TwoStations("2SM CLR 20/12 A2992", "10SM CLR 20/12 A2992");
        AssertNearestStationIsHayward(weather, trailer);
        engine.World.Weather = weather;

        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, 5.0);
        leader.Position = new LatLon(newLeadLat, newLeadLon);

        engine.TickVisualDetection();

        Assert.True(
            trailer.Approach.HasReportedTrafficInSight,
            "Maintained traffic contact must be judged against the follower's local air mass (clear), not the destination METAR (2SM)"
        );
    }

    [Fact]
    public void MaintainedTrafficContact_DestinationClear_LocalStationBad_BreaksContact()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer, finalCourse) = EstablishCvaFollowWithTrafficInSight(engine);

        // Converse: the destination is clear but the follower's own air mass has
        // collapsed to 2SM. A 5 nm gap is genuinely out of sight — destination-sourced
        // weather would wrongly keep the contact alive.
        var weather = TwoStations("10SM CLR 20/12 A2992", "2SM CLR 20/12 A2992");
        AssertNearestStationIsHayward(weather, trailer);
        engine.World.Weather = weather;

        var (newLeadLat, newLeadLon) = GeoMath.ProjectPointRaw(trailer.Position.Lat, trailer.Position.Lon, finalCourse, 5.0);
        leader.Position = new LatLon(newLeadLat, newLeadLon);
        Assert.True(GeoMath.DistanceNm(trailer.Position, leader.Position) > (2.0 * SmToNm * 1.25));

        engine.TickVisualDetection();

        Assert.False(
            trailer.Approach.HasReportedTrafficInSight,
            "Maintained traffic contact must break when the follower's local visibility collapses below the gap, regardless of the destination METAR"
        );
    }

    [Fact]
    public void TrafficAcquisition_UsesOwnshipNearestStation()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (_, trailer, _) = EstablishCvaFollowWithTrafficInSight(engine);

        // Probe the acquire path directly: force the transient lost-contact state (in
        // production it exists only between a loss event and its consequence) while keeping
        // the field report so the lost-reference consequence stays disarmed.
        trailer.Approach.HasReportedTrafficInSight = false;
        trailer.Approach.HasReportedFieldInSight = true;

        // A BKN deck at the follower's local station lies between the follower (3500 ft)
        // and the lead (3000 ft); the destination is clear. Acquisition must fail —
        // destination-sourced weather would see a clear sky and acquire immediately.
        var weather = TwoStations("10SM CLR 20/12 A2992", "10SM BKN032 20/12 A2992");
        AssertNearestStationIsHayward(weather, trailer);
        engine.World.Weather = weather;

        for (int t = 0; t < 3; t++)
        {
            engine.TickVisualDetection();
        }

        Assert.False(
            trailer.Approach.HasReportedTrafficInSight,
            "Traffic acquisition must be judged against the follower's local air mass (BKN deck between), not the clear destination METAR"
        );
    }

    /// <summary>
    /// The concrete #344 flap: <see cref="Phases.AirborneFollowHelper.CheckLeadLifecycle"/>
    /// (ownship-nearest weather, 4×/s) keeps the follow alive while a destination-sourced
    /// <see cref="SimulationEngine.TickVisualDetection"/> would flip the in-sight flag every
    /// second and re-acquire the next — a lost→regained transmission flap driven purely by
    /// the two call sites disagreeing on the weather source.
    /// </summary>
    [Fact]
    public void NoFlapAgainstCheckLeadLifecycle()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (_, trailer, _) = EstablishCvaFollowWithTrafficInSight(engine);

        var weather = TwoStations("2SM CLR 20/12 A2992", "10SM CLR 20/12 A2992");
        AssertNearestStationIsHayward(weather, trailer);
        engine.World.Weather = weather;

        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
            Assert.True(
                trailer.Approach.HasReportedTrafficInSight,
                $"t={t}s: the in-sight flag flapped — TickVisualDetection disagreed with CheckLeadLifecycle on the weather source"
            );
            Assert.NotNull(trailer.Approach.FollowingCallsign);
        }
    }

    /// <summary>
    /// Spawns a B738 leader (10 nm final) + trailer (15 nm final) on OAK 30, establishes
    /// RFIS/CVA on the leader and RTIS + CVA FOLLOW on the trailer. The CVA preserves the
    /// traffic report it gated on, so the follow emerges with maintained contact already
    /// established. Mirrors VisualMaintainVisibilityCollapseTests.
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
        Assert.True(trailer.Approach.HasReportedTrafficInSight, "the CVA preserves the traffic report it gated on");

        return (leader, trailer, finalCourse);
    }
}
