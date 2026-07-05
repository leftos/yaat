using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Recording-free E2E tests for the "RTIS → CVA FOLLOW → CLAND → land" flow
/// at KOAK runway 30 with a pair of B738s. Covers the controller-realistic
/// pattern where a leader is on approach (visual or ILS) and a trailer behind
/// is cleared visually with a follow instruction. Exercises the full FOLLOW
/// flight — RTIS acquisition gate (<see cref="Yaat.Sim.Commands.ApproachCommandHandler.TryClearedVisualApproach"/>
/// line 312), <c>aircraft.Approach.FollowingCallsign</c> wiring, and
/// <see cref="Yaat.Sim.Phases.AirborneFollowHelper"/> speed-spacing through
/// final approach and landing.
/// </summary>
public class CvaFollowVisualE2ETests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-cva-follow-e2e",
                ScenarioName = "CVA Follow Visual E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        return engine;
    }

    /// <summary>
    /// Leader on a straight-in CVA, trailer 5 nm behind on a CVA FOLLOW.
    /// Both aircraft must reach the runway and stop without a go-around.
    /// </summary>
    [Fact]
    public void CvaFollow_LeaderVisual_TrailerVisual_BothLand()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer) = SpawnPairOnFinalForRunway30(engine);

        var leadRfis = engine.SendCommand("LEAD1", "RFIS");
        output.WriteLine($"LEAD1 RFIS: {leadRfis.Success} — {leadRfis.Message}");
        Assert.True(leadRfis.Success, $"Leader RFIS failed: {leadRfis.Message}");

        var leadCva = engine.SendCommand("LEAD1", "CVA 30");
        output.WriteLine($"LEAD1 CVA 30: {leadCva.Success} — {leadCva.Message}");
        Assert.True(leadCva.Success, $"Leader CVA 30 failed: {leadCva.Message}");
        AssertNoPatternPhases(leader, "LEAD1");
        Assert.Contains(typeof(FinalApproachPhase), leader.Phases!.Phases.Select(p => p.GetType()));
        Assert.Contains(typeof(LandingPhase), leader.Phases.Phases.Select(p => p.GetType()));

        var leadCland = engine.SendCommand("LEAD1", "CLAND");
        output.WriteLine($"LEAD1 CLAND: {leadCland.Success} — {leadCland.Message}");
        Assert.True(leadCland.Success, $"Leader CLAND failed: {leadCland.Message}");
        Assert.Equal(ClearanceType.ClearedToLand, leader.Phases.LandingClearance);

        IssueTrailerRtisAndCvaFollow(engine, trailer);

        var trailCland = engine.SendCommand("TRAIL1", "CLAND");
        output.WriteLine($"TRAIL1 CLAND: {trailCland.Success} — {trailCland.Message}");
        Assert.True(trailCland.Success, $"Trailer CLAND failed: {trailCland.Message}");
        Assert.Equal(ClearanceType.ClearedToLand, trailer.Phases?.LandingClearance);

        TickUntilAllLandedOrGoAround(engine, [leader, trailer], maxSeconds: 500);
    }

    /// <summary>
    /// Leader on the published ILS 30 (CAPP I30); trailer 5 nm behind on
    /// CVA FOLLOW. Common real-world mix: IFR airline ahead on the
    /// instrument approach, visual-cleared follower behind.
    /// </summary>
    [Fact]
    public void CvaFollow_LeaderIls_TrailerVisual_BothLand()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer) = SpawnPairOnFinalForRunway30(engine);

        var leadCapp = engine.SendCommand("LEAD1", "CAPP I30");
        output.WriteLine($"LEAD1 CAPP I30: {leadCapp.Success} — {leadCapp.Message}");
        Assert.True(leadCapp.Success, $"Leader CAPP I30 failed: {leadCapp.Message}");
        var leaderPhases = leader.Phases!.Phases.Select(p => p.GetType()).ToList();
        output.WriteLine($"LEAD1 phases after CAPP I30: [{string.Join(", ", leaderPhases.Select(t => t.Name))}]");
        // CAPP picks InterceptCoursePhase or ApproachNavigationPhase as the lead-in
        // depending on whether localizer capture is needed; both end with
        // FinalApproachPhase + LandingPhase.
        Assert.Contains(typeof(FinalApproachPhase), leaderPhases);
        Assert.Contains(typeof(LandingPhase), leaderPhases);
        AssertNoPatternPhases(leader, "LEAD1");

        var leadCland = engine.SendCommand("LEAD1", "CLAND");
        output.WriteLine($"LEAD1 CLAND: {leadCland.Success} — {leadCland.Message}");
        Assert.True(leadCland.Success, $"Leader CLAND failed: {leadCland.Message}");

        IssueTrailerRtisAndCvaFollow(engine, trailer);

        var trailCland = engine.SendCommand("TRAIL1", "CLAND");
        output.WriteLine($"TRAIL1 CLAND: {trailCland.Success} — {trailCland.Message}");
        Assert.True(trailCland.Success, $"Trailer CLAND failed: {trailCland.Message}");

        TickUntilAllLandedOrGoAround(engine, [leader, trailer], maxSeconds: 500);
    }

    private (AircraftState leader, AircraftState trailer) SpawnPairOnFinalForRunway30(SimulationEngine engine)
    {
        var navDb = NavigationDatabase.Instance;
        var runway30 = navDb.GetRunway("OAK", "30");
        Assert.NotNull(runway30);

        double finalCourse = runway30.TrueHeading.Degrees;
        double reciprocal = (finalCourse + 180) % 360;
        double thresholdLat = runway30.ThresholdLatitude;
        double thresholdLon = runway30.ThresholdLongitude;

        var (leadLat, leadLon) = GeoMath.ProjectPointRaw(thresholdLat, thresholdLon, reciprocal, 10.0);
        var (trailLat, trailLon) = GeoMath.ProjectPointRaw(thresholdLat, thresholdLon, reciprocal, 15.0);

        var leader = MakeB738OnFinal("LEAD1", leadLat, leadLon, finalCourse, altitude: 3000);
        var trailer = MakeB738OnFinal("TRAIL1", trailLat, trailLon, finalCourse, altitude: 3500);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);

        output.WriteLine(
            $"Spawned LEAD1 at ({leadLat:F6},{leadLon:F6}) hdg={finalCourse:F0} alt=3000, "
                + $"TRAIL1 5nm behind at ({trailLat:F6},{trailLon:F6}) alt=3500"
        );
        return (leader, trailer);
    }

    private static AircraftState MakeB738OnFinal(string callsign, double lat, double lon, double headingDeg, double altitude)
    {
        return new AircraftState
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
    }

    private void IssueTrailerRtisAndCvaFollow(SimulationEngine engine, AircraftState trailer)
    {
        // A following visual approach requires only traffic-in-sight, not field-in-sight
        // (7110.65 §7-4-3.a.2 NOTE). Report the preceding traffic in sight, then CVA FOLLOW.
        var rtis = engine.SendCommand("TRAIL1", "RTIS LEAD1");
        output.WriteLine($"TRAIL1 RTIS LEAD1: {rtis.Success} — {rtis.Message}");
        Assert.True(rtis.Success, $"Trailer RTIS failed: {rtis.Message}");

        // RTIS may resolve immediately (5 nm dead-ahead, B738→B738 7.6 nm range)
        // or soft-fail and resolve on the next tick via PilotObservationUpdater.
        // 15 ticks is plenty of margin.
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight, "RTIS should resolve within 15 s at 5 nm dead-ahead with no METAR");
        Assert.Equal("LEAD1", trailer.Approach.LastReportedTrafficCallsign);

        var cva = engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1");
        output.WriteLine($"TRAIL1 CVA 30 FOLLOW LEAD1: {cva.Success} — {cva.Message}");
        Assert.True(cva.Success, $"Trailer CVA FOLLOW failed: {cva.Message}");
        Assert.Equal("LEAD1", trailer.Approach.FollowingCallsign);
        AssertNoPatternPhases(trailer, "TRAIL1");
        Assert.Contains(typeof(FinalApproachPhase), trailer.Phases!.Phases.Select(p => p.GetType()));
        Assert.Contains(typeof(LandingPhase), trailer.Phases.Phases.Select(p => p.GetType()));
    }

    private static void AssertNoPatternPhases(AircraftState ac, string label)
    {
        var phaseTypes = ac.Phases!.Phases.Select(p => p.GetType()).ToList();
        Assert.DoesNotContain(typeof(DownwindPhase), phaseTypes);
        Assert.DoesNotContain(typeof(BasePhase), phaseTypes);
        Assert.DoesNotContain(typeof(PatternEntryPhase), phaseTypes);
        Assert.True(true, $"{label} phases: [{string.Join(", ", phaseTypes.Select(t => t.Name))}]");
    }

    private void TickUntilAllLandedOrGoAround(SimulationEngine engine, IReadOnlyList<AircraftState> aircraft, int maxSeconds)
    {
        var landed = aircraft.ToDictionary(a => a.Callsign, _ => false);
        string? goAroundDetail = null;

        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();

            foreach (var ac in aircraft)
            {
                if (!landed[ac.Callsign] && ac.IsOnGround && ac.GroundSpeed < 40)
                {
                    landed[ac.Callsign] = true;
                    output.WriteLine($"t+{t}s: {ac.Callsign} landed, gs={ac.GroundSpeed:F1}kt, pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})");
                }

                foreach (var w in ac.PendingWarnings)
                {
                    if (w.Contains("going around", StringComparison.OrdinalIgnoreCase))
                    {
                        goAroundDetail = $"{ac.Callsign}: {w}";
                        output.WriteLine($"t+{t}s: WARNING {goAroundDetail}");
                    }
                }
            }

            if (landed.Values.All(v => v))
            {
                break;
            }

            if (t % 30 == 0)
            {
                foreach (var ac in aircraft)
                {
                    output.WriteLine(
                        $"  t+{t}s: {ac.Callsign} alt={ac.Altitude:F0}ft gs={ac.GroundSpeed:F1}kt hdg={ac.TrueHeading.Degrees:F0} "
                            + $"phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}"
                    );
                }
            }
        }

        Assert.Null(goAroundDetail);
        foreach (var (callsign, didLand) in landed)
        {
            var ac = aircraft.First(a => a.Callsign == callsign);
            Assert.True(
                didLand,
                $"{callsign} did not land within {maxSeconds}s. Final state: alt={ac.Altitude:F0}ft, "
                    + $"gs={ac.GroundSpeed:F1}kt, phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}, "
                    + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
            );
        }
    }
}
