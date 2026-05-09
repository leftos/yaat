using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the piston-arrival-overspeed bug. Recording: S2-OAK-1 (1) | VFR
/// Takeoff/Landing — ENY3196 spawned by an AircraftGenerator on FinalApproach to OAK
/// 28R. The snapshot identifies the type as "PA28" (Piper Cherokee, single-engine
/// piston). Pre-fix, the type string mismatches the ICAO designator "P28A" used by
/// every per-type lookup (AircraftProfileDatabase, FaaAircraftDatabase,
/// AircraftCategorization). Every lookup misses, defaults fall through to
/// AircraftCategory.Jet, and the spawn schedule applies jet speeds:
///   - spawn IAS = 224 KIAS (FAS 140 × 1.6 for &gt; 10 NM)
///   - config target = 182 KIAS (FAS × 1.3)
///   - FAS target = 140 KIAS (jet category default)
/// All three are well outside the PA28's structural envelope (Vne ≈ 160 KIAS,
/// cruise ≈ 118-123 KIAS, Vref ≈ 65 KIAS). Touchdown at ~138 KIAS then ate the
/// entire 10520-ft 28R runway (aircraft stopped past the departure end).
///
/// Post-fix the generator emits "P28A" (and a sibling-fallback covers any ACD-known
/// code missing a profile), so the perf lookups resolve and the aircraft flies a
/// real piston profile.
/// </summary>
public class PistonArrivalOverspeedTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/piston-arrival-overspeed-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// PA28 type lookup chain must resolve through the perf databases instead of
    /// falling through to category-default Jet. This is the unit-level guard for the
    /// root cause: if Categorize("PA28") returns Jet, every downstream speed is wrong.
    /// Both the ICAO key "P28A" and the informal "PA28" the old generator emitted must
    /// resolve to Piston (the latter via the sibling-map alias).
    /// </summary>
    [Fact]
    public void PistonType_ResolvesToPistonCategory()
    {
        TestVnasData.EnsureInitialized();

        Assert.Equal(AircraftCategory.Piston, AircraftCategorization.Categorize("P28A"));

        // The Cherokee profile must exist and report a piston-realistic FAS (Vref).
        var profile = AircraftProfileDatabase.Get("P28A");
        Assert.NotNull(profile);
        Assert.True(profile!.FinalApproachSpeed <= 90, $"P28A FAS should be piston-realistic (≤ 90), got {profile.FinalApproachSpeed}");

        // Recordings frozen with the old generator's "PA28" string (non-ICAO) must
        // still resolve correctly via the sibling-map alias — otherwise replay-driven
        // tests against historical bundles silently use jet defaults.
        Assert.Equal(AircraftCategory.Piston, AircraftCategorization.Categorize("PA28"));
        var aliased = AircraftProfileDatabase.Get("PA28");
        Assert.NotNull(aliased);
        Assert.True(aliased!.FinalApproachSpeed <= 90, $"PA28 alias should resolve to P28A's piston FAS, got {aliased.FinalApproachSpeed}");
    }

    /// <summary>
    /// ENY3196 spawned at t=195 — IAS at spawn must respect the airframe envelope.
    /// PA28 Vne ≈ 160 KIAS; the recording showed 224 KIAS (jet 1.6 × Vref schedule).
    /// </summary>
    [Fact]
    public void Eny3196_SpawnSpeedRespectsPistonEnvelope()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay just past spawn (t=195). Snapshot at t=200 has the post-spawn state.
        engine.Replay(recording, 200);

        var ac = engine.FindAircraft("ENY3196");
        Assert.NotNull(ac);
        output.WriteLine($"ENY3196 type={ac!.AircraftType} ias={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0}");

        Assert.True(
            ac.IndicatedAirspeed <= 130,
            $"PA28 spawn IAS should be within piston envelope (≤ 130 KIAS / Vno area), got {ac.IndicatedAirspeed:F0} KIAS"
        );
    }

    /// <summary>
    /// Once the FinalApproach FAS gate fires (recording shows it at t=355), the
    /// commanded TargetSpeed must be the PA28's real Vref (~81 KIAS), not the jet
    /// category default (140).
    /// </summary>
    [Fact]
    public void Eny3196_FinalApproachTargetIsPistonVref()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 200);

        // Tick forward up to the recording's end. Watch TargetSpeed once FAS fires.
        // We track the lowest-ever target so the test reports the actual minimum
        // (the FAS gate may briefly set a target then clear it once IAS catches up).
        double? fasObserved = null;
        bool sawAircraft = false;
        bool sawFinalApproach = false;
        for (int t = 1; t <= 500; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("ENY3196");
            if (ac is null)
            {
                continue;
            }
            sawAircraft = true;

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase != "FinalApproach")
            {
                continue;
            }
            sawFinalApproach = true;

            if (ac.Targets.TargetSpeed is { } tgt)
            {
                if (fasObserved is null || tgt < fasObserved.Value)
                {
                    fasObserved = tgt;
                    output.WriteLine($"FAS observed at t+{t}s: targetSpeed={tgt:F1} ias={ac.IndicatedAirspeed:F0}");
                }
            }
        }
        output.WriteLine($"sawAircraft={sawAircraft} sawFinalApproach={sawFinalApproach} lowestTarget={fasObserved}");

        Assert.True(sawAircraft, "ENY3196 never appeared during replay");
        Assert.True(sawFinalApproach, "ENY3196 never reached FinalApproach phase");
        // P28A profile FAS is 81 KIAS; the Eurocontrol correction adapter can push the
        // commanded value up to ~90-95 KIAS for piston aircraft. Anything in that band
        // is a piston-realistic FAS, not the jet-category default of 140.
        Assert.True(fasObserved is not null, "FinalApproach never commanded a TargetSpeed at all");
        Assert.True(
            fasObserved!.Value <= 95,
            $"PA28 FAS should be piston-realistic (~81 KIAS), got {fasObserved.Value:F1} KIAS — the FAS gate is firing with jet-default 140 or higher"
        );
    }

    /// <summary>
    /// Diagnostic: log ENY3196's IAS / target / distance every 30 s through the
    /// approach. Useful for verifying the speed schedule walks cruise → config →
    /// FAS, not jumping straight to FAS at 14 NM.
    /// </summary>
    [Fact]
    public void Diagnostic_LogApproachSpeedProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 200);

        for (int t = 1; t <= 1100; t++)
        {
            engine.ReplayOneSecond();
            if (t % 30 != 0)
            {
                continue;
            }

            var ac = engine.FindAircraft("ENY3196");
            if (ac is null)
            {
                continue;
            }
            var runway = ac.Phases?.AssignedRunway;
            string phase = ac.Phases?.CurrentPhase?.Name ?? "?";
            double dist = runway is null
                ? 0
                : GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude);
            string tgt = ac.Targets.TargetSpeed?.ToString("F0") ?? "-";
            output.WriteLine($"t+{t, 4}s  phase={phase, -15} dist={dist, 5:F1}nm  ias={ac.IndicatedAirspeed, 5:F1}  tgt={tgt}");
        }
    }

    /// <summary>
    /// Aircraft must clear the 10520-ft 28R runway and not roll past the departure
    /// end. With the bug it touched down at 138 KIAS and stopped at lat 37.7325 /
    /// lon -122.2285 — past the west (departure) end. With piston perf data
    /// (touchdown ~65 KIAS, piston decel ~2 kt/s), it should stop and exit well
    /// before the end. Asserts that at first wheel-down on RunwayExit, the aircraft
    /// is still inside the runway box.
    /// </summary>
    [Fact]
    public void Eny3196_ExitsRunwayBeforeDepartureEnd()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 200);

        bool reachedExit = false;
        double exitLat = 0;
        double exitLon = 0;
        double thresholdLat = 0;
        double thresholdLon = 0;
        string lastPhase = "(never seen)";
        double lastIas = 0;
        // 14 NM final at piston speeds (FAS ~80, decel/touchdown to ~60) takes
        // 12-15 minutes vs the pre-fix jet schedule that completed in ~4. Give
        // the loop room to reach landing + rollout + exit.
        for (int t = 1; t <= 1100; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("ENY3196");
            if (ac is null)
            {
                continue;
            }

            var runway = ac.Phases?.AssignedRunway;
            if (runway is not null && thresholdLat == 0)
            {
                thresholdLat = runway.ThresholdLatitude;
                thresholdLon = runway.ThresholdLongitude;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase != lastPhase)
            {
                output.WriteLine($"t+{t}s phase {lastPhase} -> {phase} ias={ac.IndicatedAirspeed:F0}");
                lastPhase = phase;
            }
            lastIas = ac.IndicatedAirspeed;
            if (phase == "Runway Exit" && !reachedExit)
            {
                reachedExit = true;
                exitLat = ac.Position.Lat;
                exitLon = ac.Position.Lon;
                output.WriteLine($"RunwayExit at t+{t}s: pos={ac.Position.Lat:F5}/{ac.Position.Lon:F5} ias={ac.IndicatedAirspeed:F0}");
                break;
            }
        }

        Assert.True(reachedExit, $"ENY3196 never reached Runway Exit within 1100 s of spawn (last phase {lastPhase} ias {lastIas:F0})");
        Assert.True(thresholdLat != 0, "Runway threshold was never recorded");

        // OAK 28R is approximately 10520 ft long. Verify the aircraft's distance from
        // the threshold along the runway heading is less than the runway length.
        double distFromThresholdNm = GeoMath.DistanceNm(thresholdLat, thresholdLon, exitLat, exitLon);
        double distFromThresholdFt = distFromThresholdNm * 6076.12;
        const double Oak28rLengthFt = 10520;

        output.WriteLine($"Distance from threshold at exit: {distFromThresholdFt:F0} ft (runway length {Oak28rLengthFt} ft)");

        Assert.True(
            distFromThresholdFt <= Oak28rLengthFt,
            $"Aircraft rolled past the departure end of 28R: {distFromThresholdFt:F0} ft from threshold "
                + $"(runway is {Oak28rLengthFt} ft). Touchdown was too fast — perf lookup likely returned jet defaults."
        );
    }
}
