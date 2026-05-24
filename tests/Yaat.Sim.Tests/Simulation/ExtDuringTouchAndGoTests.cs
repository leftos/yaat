using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// EXT pre-arms the upcoming UpwindPhase when issued during a phase that is not a
/// pattern leg (TouchAndGo, FinalApproach pre-T/G, HoldingShort/LineUp/Takeoff,
/// InitialClimb). Bug report bundle: S2-OAK-3 — N172SP doing closed pattern on
/// OAK 28R. The user issued <c>EXT</c> repeatedly during TouchAndGoPhase (~t=578
/// onward); each attempt rejected with "Extend applies on upwind, crosswind, or
/// downwind" until the aircraft happened to transition into UpwindPhase at t=594.
///
/// Expected after fix:
///   * EXT during TouchAndGo when the next circuit is not yet queued → sets
///     <c>AircraftPattern.ExtendNextUpwind</c>; PhaseRunner applies IsExtended to
///     the first Upwind of the appended circuit when T/G completes.
///   * EXT during FinalApproach pre-T/G → same behavior.
///   * EXT during HoldingShort/LineUp/Takeoff/InitialClimb when the initial
///     circuit's UpwindPhase already exists as a pending phase in the queue →
///     sets IsExtended directly on that pending UpwindPhase (no flag needed).
///   * EXT CROSSWIND from a non-pattern-leg phase still rejects (scope decision).
/// </summary>
public class ExtDuringTouchAndGoTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/c0c9f6aa6cb7.zip";
    private const string Callsign = "N172SP";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("PhaseRunner", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ExtDuringTouchAndGo_ArmsNextUpwind()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=575 — 15 s into N172SP's first TouchAndGo (which started at
        // t=560 in the recording). The next Upwind has NOT yet been queued by
        // PhaseRunner (that only happens when TouchAndGo completes).
        engine.Replay(recording, 575);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<TouchAndGoPhase>(aircraft.Phases?.CurrentPhase);

        // Pre-condition: no pending UpwindPhase exists yet, ExtendNextUpwind is false.
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is UpwindPhase { Status: PhaseStatus.Pending });

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during TouchAndGo should succeed but got: {result.Message}");
        Assert.True(aircraft.Pattern.ExtendNextUpwind, "ExtendNextUpwind flag should be set");

        // Tick forward until the aircraft enters the next UpwindPhase. The current
        // T/G phase completes once the rollout is done; PhaseRunner appends the next
        // circuit and consumes the flag, setting IsExtended on the new Upwind.
        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 120; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                output.WriteLine($"t={575 + dt}: entered UpwindPhase, IsExtended={up.IsExtended}, ExtendNextUpwind={ac.Pattern.ExtendNextUpwind}");
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended, "First Upwind of the next circuit should have IsExtended=true (consumed from flag)");
        Assert.False(aircraft.Pattern.ExtendNextUpwind, "Flag should be cleared after consumption");
    }

    [Fact]
    public void ExtDuringFinalApproach_BeforeTouchAndGo_ArmsNextUpwind()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=540 — 40 s into N172SP's first FinalApproach (started t=500).
        // The aircraft is committed to T/G (TouchAndGoPhase is queued next).
        engine.Replay(recording, 540);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Contains(aircraft.Phases!.Phases, p => p is TouchAndGoPhase);

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during pre-T/G FinalApproach should succeed but got: {result.Message}");
        Assert.True(aircraft.Pattern.ExtendNextUpwind);

        // Tick through landing + T/G rollout + climb into the next Upwind.
        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 180; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended);
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }

    [Fact]
    public void ExtDuringHoldingShort_ArmsPendingUpwind_Directly()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=200 — just after the CTO MRT at t=195 built the initial
        // pattern chain. The aircraft is still at the runway in LineUp/HoldingShort
        // territory and a pending UpwindPhase already exists in the queue.
        engine.Replay(recording, 200);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var pendingUpwindBefore = aircraft.Phases?.Phases.OfType<UpwindPhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
        Assert.NotNull(pendingUpwindBefore);
        Assert.False(pendingUpwindBefore!.IsExtended);

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during HoldingShort/LineUp with pending Upwind should succeed but got: {result.Message}");
        Assert.True(pendingUpwindBefore.IsExtended, "Pending Upwind in queue should have IsExtended=true set directly");

        // Layer 1 path → flag stays false because we mutated the queued phase directly.
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }

    [Fact]
    public void CoptThenExtUpwind_DuringFinalApproach_ArmsNextUpwind()
    {
        // Reproduction of the S2-OAK-4 bug: the controller issued `COPT; EXT UPWIND`
        // to an aircraft on FinalApproach during a closed-pattern T/G cycle. COPT
        // applied (touch-and-go ending stayed in place) but the chained EXT UPWIND
        // was silently dropped — the next upwind ran for ~10 s before turning
        // crosswind. Root cause: `IsImmediatePhaseModifierBlock` only whitelisted
        // SA/MNA, so the EXT block was enqueued and never fired while phases were
        // active (UpdateCommandQueue short-circuits with an active phase).
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 540);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Contains(aircraft.Phases!.Phases, p => p is TouchAndGoPhase);

        var result = engine.SendCommand(Callsign, "COPT; EXT UPWIND");
        output.WriteLine($"COPT; EXT UPWIND result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"COPT; EXT UPWIND should succeed but got: {result.Message}");
        Assert.True(
            aircraft.Pattern.ExtendNextUpwind,
            "ExtendNextUpwind flag should be set immediately after dispatch — without the fix the EXT block sits enqueued"
        );

        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 180; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended, "First Upwind of the next circuit should have IsExtended=true (consumed from flag)");
        Assert.False(aircraft.Pattern.ExtendNextUpwind, "Flag should be cleared after consumption");
    }

    [Fact]
    public void CoptThenBareExt_DuringFinalApproach_ArmsNextUpwind()
    {
        // Same chained-command bug as CoptThenExtUpwind, but with bare EXT (no leg
        // arg). Bare EXT on a non-pattern-leg phase routes through TryExtendCurrentLeg
        // → default branch → TryArmNextUpwind, identical end-state to EXT UPWIND.
        // Without the IsImmediatePhaseModifierBlock fix, the EXT block is enqueued
        // and never fires while T/G + new circuit phases are continuously active.
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 540);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);

        var result = engine.SendCommand(Callsign, "COPT; EXT");
        output.WriteLine($"COPT; EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"COPT; EXT should succeed but got: {result.Message}");
        Assert.True(aircraft.Pattern.ExtendNextUpwind, "ExtendNextUpwind flag should be set immediately");

        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 180; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended);
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }

    [Fact]
    public void ErdThenExtBare_NoPreExistingPhase_ExtendsJustInstalledDownwind()
    {
        // Covers the mirror guard at CommandDispatcher.cs:237 — the path that
        // applies when the aircraft has no active phase before the compound and
        // the first block (ERD here) installs pattern phases. Subsequent blocks
        // need the same immediate-modifier whitelist or they sit in the queue
        // until phases clear. Without the fix, EXT remains enqueued and the
        // just-installed Downwind is never marked extended.
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        // Place the aircraft right at the right-pattern downwind abeam point so
        // ERD installs Downwind as the current phase immediately (no upstream
        // PatternEntryPhase / MidfieldCrossingPhase). That way the assertion can
        // inspect the just-installed Downwind directly.
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Right, null, null, null);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon),
            // Downwind heading is the reciprocal of the runway heading.
            TrueHeading = wp.FinalHeading.ToReciprocal(),
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                HasFlightPlan = true,
            },
        };
        // No pre-existing PhaseList — exercises the "no current phase" branch in
        // DispatchCompoundCore (skips DispatchWithPhase entirely, falls into the
        // ApplyBlock + post-apply tower-modifier loop at line 222–254).

        var parseResult = CommandParser.ParseCompound("ERD 28R; EXT", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var dispatchResult = CommandDispatcher.DispatchCompound(
            parseResult.Value!,
            ac,
            TestDispatch.Context(new Random(42), validateDctFixes: false)
        );
        output.WriteLine($"ERD 28R; EXT result: Success={dispatchResult.Success}, Message={dispatchResult.Message}");

        Assert.True(dispatchResult.Success, $"ERD 28R; EXT should succeed but got: {dispatchResult.Message}");

        // ERD installs Downwind → Base → FinalApproach → Landing; Downwind becomes
        // the current phase. The chained EXT must be applied immediately so
        // Downwind.IsExtended is set before any tick runs.
        var downwind = ac.Phases?.CurrentPhase as DownwindPhase;
        Assert.NotNull(downwind);
        Assert.True(
            downwind!.IsExtended,
            "Downwind installed by ERD should have IsExtended=true after the chained EXT — without the fix the EXT block stays unapplied in the queue"
        );
    }

    [Fact]
    public void ExtCrosswind_DuringTouchAndGo_StillRejects()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=575 (during TouchAndGo, no pending Crosswind in queue yet).
        engine.Replay(recording, 575);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<TouchAndGoPhase>(aircraft.Phases?.CurrentPhase);

        var result = engine.SendCommand(Callsign, "EXT C");
        output.WriteLine($"EXT C result: Success={result.Success}, Message={result.Message}");

        // Scope guard: EXT CROSSWIND / EXT DOWNWIND keep original rejection from
        // non-pattern-leg phases. Only EXT UPWIND (and bare EXT) get the pre-arm.
        Assert.False(result.Success);
        Assert.Contains("upwind", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }
}
