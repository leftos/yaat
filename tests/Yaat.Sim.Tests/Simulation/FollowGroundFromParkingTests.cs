using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOWG issued to a parked aircraft. In the S2-OAK-P recording, FTH399 sat parked at
/// KAI7 while KPO83 taxied C B W toward 28R; the user's <c>FOLLOWG KPO83</c> was rejected
/// ("aircraft is parked with engines off") and they had to retype KPO83's full taxi route.
/// A parked aircraft should accept FOLLOWG, start up, and trail the leader — both as a
/// bare command and as the payload of a deferred <c>BEHIND X FOLLOWG X</c> clearance.
///
/// Recording: S2-OAK-P | S2 Rating Practical Exam, trimmed to 220 s. KPO83 receives
/// TAXI C B W HS 28R RWY 30 at t=142; the rejected FOLLOWG attempt was at t≈214.
/// </summary>
public class FollowGroundFromParkingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/followg-from-parking-recording.zip";

    // After KPO83 is established taxiing (t=142) and before the user's manual
    // TAXI correction for FTH399 (t=233, excluded by the trim).
    private const int ReplayTime = 205;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private (SimulationEngine Engine, AircraftState Fth, AircraftState Kpo)? ReplayToParkedFollower()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return null;
        }

        engine.Replay(recording, ReplayTime);

        var fth = engine.FindAircraft("FTH399");
        var kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(fth);
        Assert.NotNull(kpo);

        output.WriteLine(
            $"t={ReplayTime}: FTH399 phase={fth.Phases?.CurrentPhase?.Name ?? "null"} gs={fth.GroundSpeed:F1} | "
                + $"KPO83 phase={kpo.Phases?.CurrentPhase?.Name ?? "null"} gs={kpo.GroundSpeed:F1}"
        );

        Assert.IsType<AtParkingPhase>(fth.Phases?.CurrentPhase);
        Assert.IsType<TaxiingPhase>(kpo.Phases?.CurrentPhase);
        return (engine, fth, kpo);
    }

    [Fact]
    public void FTH399_AcceptsFollowGroundFromParking()
    {
        if (ReplayToParkedFollower() is not var (engine, _, _))
        {
            return;
        }

        var result = engine.SendCommand("FTH399", "FOLLOWG KPO83");
        output.WriteLine($"FOLLOWG KPO83 result: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"FOLLOWG should succeed but got: {result.Message}");

        var fth = engine.FindAircraft("FTH399");
        Assert.NotNull(fth);
        Assert.IsType<FollowingPhase>(fth.Phases?.CurrentPhase);

        // The follower should spool up and close on the leader.
        var startPos = fth.Position;
        double maxSpeed = 0;
        for (int i = 0; i < 60; i++)
        {
            engine.TickOneSecond();
            fth = engine.FindAircraft("FTH399");
            Assert.NotNull(fth);
            maxSpeed = Math.Max(maxSpeed, fth.GroundSpeed);
        }

        var kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(kpo);
        double movedNm = GeoMath.DistanceNm(startPos.Lat, startPos.Lon, fth.Position.Lat, fth.Position.Lon);
        double gapNm = GeoMath.DistanceNm(fth.Position.Lat, fth.Position.Lon, kpo.Position.Lat, kpo.Position.Lon);
        output.WriteLine(
            $"After 60s: FTH399 phase={fth.Phases?.CurrentPhase?.Name} moved={movedNm:F3}nm maxGs={maxSpeed:F1} gapToKPO83={gapNm:F3}nm"
        );

        Assert.True(maxSpeed > 3, $"follower never started moving (max gs {maxSpeed:F1} kt)");
        Assert.True(movedNm > 0.02, $"follower barely moved ({movedNm:F3} nm)");
    }

    /// <summary>
    /// Regression: FOLLOWG must clear any active ground <see cref="AircraftGroundOps.Hold"/> on the
    /// follower. A taxiing aircraft told HOLD POSITION carries a <c>HoldDirective.HoldPosition</c>
    /// (auto-released only by RES/TAXI, never on its own). The b13188b FOLLOWG-from-taxiing path
    /// replaces the phase with <see cref="FollowingPhase"/> but left <c>Ground.Hold</c> set, so
    /// <c>FollowingPhase.OnTick</c> saw <c>IsImmobile</c> and froze the aircraft at 0 kt forever —
    /// the command reported success yet the aircraft never followed.
    /// </summary>
    [Fact]
    public void FollowGround_ClearsActiveHold_SoHeldTaxiingFollowerMoves()
    {
        if (ReplayToParkedFollower() is not var (engine, _, _))
        {
            return;
        }

        // KPO83 is taxiing; hold it in position (a HoldPosition directive that only RES/TAXI clears).
        var holdResult = engine.SendCommand("KPO83", "HOLD");
        Assert.True(holdResult.Success, $"HOLD should succeed but got: {holdResult.Message}");
        var kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(kpo);
        Assert.True(kpo.Ground.IsImmobile, "KPO83 should be immobile after HOLDPOSITION");
        Assert.IsType<TaxiingPhase>(kpo.Phases?.CurrentPhase);

        // Now tell the held aircraft to follow the (on-ground) leader FTH399.
        var result = engine.SendCommand("KPO83", "FOLLOWG FTH399");
        output.WriteLine($"FOLLOWG FTH399 result: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"FOLLOWG should succeed but got: {result.Message}");

        kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(kpo);
        Assert.IsType<FollowingPhase>(kpo.Phases?.CurrentPhase);

        // Settle 15 s first so any residual coast from the pre-HOLD taxi speed bleeds off, then
        // measure sustained movement over the next 45 s. A frozen (still-held) follower produces
        // ~0 nm here; a real follower keeps taxiing toward the leader.
        for (int i = 0; i < 15; i++)
        {
            engine.TickOneSecond();
        }

        kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(kpo);
        var settledPos = kpo.Position;
        for (int i = 0; i < 45; i++)
        {
            engine.TickOneSecond();
        }

        kpo = engine.FindAircraft("KPO83");
        Assert.NotNull(kpo);
        double movedNm = GeoMath.DistanceNm(settledPos.Lat, settledPos.Lon, kpo.Position.Lat, kpo.Position.Lon);
        output.WriteLine($"KPO83 phase={kpo.Phases?.CurrentPhase?.Name} sustained-move={movedNm:F3}nm gs={kpo.GroundSpeed:F1}");

        // With the hold left set (the bug) FollowingPhase.OnTick keeps the follower stopped.
        Assert.True(movedNm > 0.03, $"held follower did not sustain movement ({movedNm:F3} nm) — FOLLOWG did not clear Ground.Hold");
    }

    [Fact]
    public void FTH399_DeferredBehindFollowGroundFromParking()
    {
        if (ReplayToParkedFollower() is not var (engine, _, _))
        {
            return;
        }

        var result = engine.SendCommand("FTH399", "BEHIND KPO83 FOLLOWG KPO83");
        output.WriteLine($"BEHIND KPO83 FOLLOWG KPO83 result: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"deferred FOLLOWG should be accepted but got: {result.Message}");

        // The give-way gate releases on geometry; within a couple of minutes of KPO83
        // taxiing past, the payload must have fired and put FTH399 into FollowingPhase.
        AircraftState? fth = null;
        for (int i = 0; i < 120; i++)
        {
            engine.TickOneSecond();
            fth = engine.FindAircraft("FTH399");
            Assert.NotNull(fth);
            if (fth.Phases?.CurrentPhase is FollowingPhase)
            {
                break;
            }
        }

        output.WriteLine($"After wait: FTH399 phase={fth!.Phases?.CurrentPhase?.Name ?? "null"} gs={fth.GroundSpeed:F1}");
        Assert.IsType<FollowingPhase>(fth.Phases?.CurrentPhase);
    }
}
