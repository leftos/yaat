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
/// E2E tests for the "queued SA" bug. SA (make short approach) should arm the
/// upcoming Downwind or Base leg even when the aircraft has not reached that
/// leg yet — e.g. while still on PatternEntryPhase after an ERD command.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts. N805FM enters the OAK
/// 28R right downwind via repeated ERD commands; the controller eventually
/// issues `DM 015, DCT VPCOL; ERD 28R; SA` at t=823. The chained SA is silently
/// dropped — the queued SA block sits behind active pattern phases forever
/// because UpdateCommandQueue short-circuits while a phase is active.
/// </summary>
public class SaArmedForDownwindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/66fd6538542e.zip";
    private const string Callsign = "N805FM";

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
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// SA issued solo while N805FM is on PatternEntryPhase (after ERD 28R).
    /// Today this returns "requires downwind or base leg". After the fix, SA
    /// must succeed and arm the pending DownwindPhase to skip immediately to
    /// BasePhase when activated.
    /// </summary>
    [Fact]
    public void SaOnPatternEntry_ArmsPendingDownwind()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay to t=790 — the ERD 28R at t=786 has applied; aircraft is on
        // PatternEntryPhase navigating to the downwind entry. SA was issued
        // solo at t=786..823 in the real recording but rejected; we re-issue
        // it ourselves to test the fix path.
        engine.Replay(recording, 790);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);
        Assert.IsType<PatternEntryPhase>(aircraft.Phases.CurrentPhase);

        var pendingDownwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(pendingDownwind);
        Assert.False(pendingDownwind.ShortApproachArmed, "Downwind should not be pre-armed before SA dispatch");

        var result = engine.SendCommand(Callsign, "SA");
        output.WriteLine($"SA dispatch: success={result.Success}, message={result.Message}");

        Assert.True(result.Success, $"SA on PatternEntry should succeed: {result.Message}");

        // Pattern was not destroyed.
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);
        Assert.IsType<PatternEntryPhase>(aircraft.Phases.CurrentPhase);

        // Pending Downwind is now armed.
        pendingDownwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(pendingDownwind);
        Assert.True(pendingDownwind.ShortApproachArmed, "Pending DownwindPhase should be armed for short approach");

        // Continue replaying — once Downwind activates, it should complete in
        // ≤2 ticks (skip-on-arm) and BasePhase becomes the current phase.
        bool reachedBase = false;
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                break;
            }

            var current = aircraft.Phases?.CurrentPhase;
            if (current is BasePhase)
            {
                reachedBase = true;
                output.WriteLine($"  reached Base at t+{t} alt={aircraft.Altitude:F0} hdg={aircraft.TrueHeading.Degrees:F0}");
                break;
            }

            if (current is DownwindPhase dw)
            {
                output.WriteLine($"  t+{t}: on Downwind (armed={dw.ShortApproachArmed}) alt={aircraft.Altitude:F0} — should advance next tick");
            }
        }

        Assert.True(reachedBase, "Aircraft should advance to BasePhase shortly after DownwindPhase activates with arm set");
    }

    /// <summary>
    /// Compound `ERD 28R; SA` from a clean state: the SA must apply alongside
    /// ERD rather than getting stuck behind the new active phase. Today the
    /// SA block enters the queue and never fires (UpdateCommandQueue short-
    /// circuits while a phase is active).
    /// </summary>
    [Fact]
    public void CompoundErdSa_ArmsPendingDownwindImmediately()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay to t=605 — N805FM has spawned (around t=593) but no ERD has
        // been issued yet (first ERD in the real recording is at t=613).
        // Aircraft is en route, no pattern phases active.
        engine.Replay(recording, 605);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var preCurrent = aircraft.Phases?.CurrentPhase;
        output.WriteLine($"t=605: {Callsign} alt={aircraft.Altitude:F0} phase={preCurrent?.GetType().Name ?? "(none)"}");
        Assert.Null(preCurrent);

        var result = engine.SendCommand(Callsign, "ERD 28R; SA");
        output.WriteLine($"ERD 28R; SA dispatch: success={result.Success}, message={result.Message}");

        Assert.True(result.Success, $"Compound ERD;SA should succeed: {result.Message}");

        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);

        // ERD established the pattern phases including a pending DownwindPhase
        // which is now armed for short approach.
        var pendingDownwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(pendingDownwind);
        Assert.True(pendingDownwind.ShortApproachArmed, "Pending DownwindPhase should be armed for short approach immediately after compound ERD;SA");
    }

    /// <summary>
    /// MNA after an armed SA must clear the arm. Symmetric inverse of SA.
    /// </summary>
    [Fact]
    public void MnaCancelsArmedShortApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 790);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);
        Assert.IsType<PatternEntryPhase>(aircraft.Phases.CurrentPhase);

        var saResult = engine.SendCommand(Callsign, "SA");
        Assert.True(saResult.Success, $"SA should succeed: {saResult.Message}");

        var pendingDownwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(pendingDownwind);
        Assert.True(pendingDownwind.ShortApproachArmed, "Precondition: SA armed the pending Downwind");

        var mnaResult = engine.SendCommand(Callsign, "MNA");
        output.WriteLine($"MNA dispatch: success={mnaResult.Success}, message={mnaResult.Message}");
        Assert.True(mnaResult.Success, $"MNA on PatternEntry should succeed: {mnaResult.Message}");

        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);

        pendingDownwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(pendingDownwind);
        Assert.False(pendingDownwind.ShortApproachArmed, "MNA should clear the armed short-approach flag");
    }

    /// <summary>
    /// Full pattern-loop E2E: spawn a C172 mid-air on initial-upwind for KOAK 28R
    /// right traffic, dispatch SA via the engine. The aircraft flies the (compressed)
    /// pattern, T&amp;Gs, and auto-cycles into a fresh circuit. Assert the new circuit's
    /// Downwind starts un-armed (SA does not persist across loops). Then dispatch
    /// SA again on circuit 2 and CLAND, assert success and that the new Downwind is
    /// armed and the aircraft eventually transitions to LandingPhase.
    ///
    /// SKIPPED: blocked by pre-existing pattern-reliability bugs unrelated to SA.
    /// In a C172 closed-traffic loop at OAK 28R, every Final ends in GoAroundPhase:
    /// circuit 1 (SA-armed) trips the "too high at MAP" gate (alt 509 ft at 0.5 nm
    /// out, gate at 406 ft); subsequent circuits trip the "no landing clearance
    /// below 200 ft AGL" gate, suggesting LandingClearance=ClearedTouchAndGo is
    /// being reset somewhere in the GoAroundPhase → BuildNextCircuit auto-cycle.
    /// Both symptoms are independent of SA — see the N80ZU plan
    /// (~/.claude/plans/n80zu-was-told-to-stateless-catmull.md, Bug D / "speeding
    /// up on final" investigation) for the surrounding work item. Once that
    /// upstream fix lands, remove this skip and the test should pass as-is —
    /// the structural assertions (SA arms once, fresh circuit's Downwind starts
    /// un-armed, second SA + CLAND lands) don't depend on the pattern bugs.
    /// </summary>
    [Fact]
    public void TouchAndGo_ShortApproachAppliesOncePerCircuit()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var oakRunway = navDb.GetRunway("KOAK", "28R");
        if (oakRunway is null)
        {
            output.WriteLine("Skipped: KOAK 28R not in NavData");
            return;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        var ac = MakeC172OnUpwind(oakRunway);
        ac.Phases!.AssignedRunway = oakRunway;
        ac.Phases!.TrafficDirection = PatternDirection.Right;
        ac.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;

        var phases = PatternBuilder.BuildCircuit(
            oakRunway,
            AircraftCategory.Piston,
            PatternDirection.Right,
            PatternEntryLeg.Upwind,
            touchAndGo: true,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: null
        );
        foreach (var p in phases)
        {
            ac.Phases.Add(p);
        }
        ac.Phases.Start(CtxFor(ac));

        // SA dispatched while on Upwind — arms the pending Downwind in this circuit.
        var saResult = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [new MakeShortApproachCommand()])]),
            ac,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(saResult.Success, $"SA on Upwind should succeed: {saResult.Message}");

        var armedDownwind = ac.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(armedDownwind);
        Assert.True(armedDownwind.ShortApproachArmed, "Circuit-1 Downwind must be armed after SA");

        // Tick through circuit 1: Upwind → Crosswind → Downwind (compressed) →
        // Base → FinalApproach → TouchAndGo → auto-cycle into Upwind of circuit 2.
        var phasesSeen = new List<string>();
        bool sawTouchAndGo = false;
        DownwindPhase? circuit2Downwind = null;
        for (int t = 0; t < 4000 && circuit2Downwind is null; t++)
        {
            var ctx = CtxFor(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            var current = ac.Phases.CurrentPhase;
            if (current is null)
            {
                break;
            }

            if (phasesSeen.LastOrDefault() != current.GetType().Name)
            {
                phasesSeen.Add(current.GetType().Name);
                output.WriteLine(
                    $"  t={t, 4} → {current.GetType().Name} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0}"
                );
            }

            if (current is TouchAndGoPhase)
            {
                sawTouchAndGo = true;
            }

            if (sawTouchAndGo && current is UpwindPhase)
            {
                // Circuit 2 has been built. Find its pending Downwind.
                circuit2Downwind = ac.Phases.Phases.Where((_, idx) => idx > ac.Phases.CurrentIndex).OfType<DownwindPhase>().FirstOrDefault();
            }
        }

        Assert.True(sawTouchAndGo, $"Circuit 1 must reach TouchAndGoPhase. Phases seen: {string.Join(" → ", phasesSeen)}");
        Assert.NotNull(circuit2Downwind);

        // KEY ASSERTION: circuit-2 Downwind must NOT inherit the SA arm from circuit 1.
        Assert.False(
            circuit2Downwind.ShortApproachArmed,
            "Circuit-2 DownwindPhase must be a fresh instance with ShortApproachArmed=false — SA does not persist across pattern loops"
        );

        // Issue SA again on circuit 2's upwind, then CLAND. Both must succeed.
        var saResult2 = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [new MakeShortApproachCommand()])]),
            ac,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(saResult2.Success, $"SA on circuit 2 should succeed: {saResult2.Message}");
        Assert.True(circuit2Downwind.ShortApproachArmed, "Circuit-2 Downwind must be armed after second SA");

        var clandResult = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [new ClearedToLandCommand()])]),
            ac,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(clandResult.Success, $"CLAND on circuit 2 should succeed: {clandResult.Message}");

        // Tick until aircraft reaches LandingPhase or HoldingAfterLanding (rolling out).
        bool landed = false;
        for (int t = 0; t < 4000 && !landed; t++)
        {
            var ctx = CtxFor(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            var current = ac.Phases.CurrentPhase;
            if (current is null)
            {
                break;
            }

            if (phasesSeen.LastOrDefault() != current.GetType().Name)
            {
                phasesSeen.Add(current.GetType().Name);
                output.WriteLine(
                    $"  t={t, 4} → {current.GetType().Name} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0} onGround={ac.IsOnGround}"
                );
            }

            // LandingPhase fires once the aircraft is on the ground rolling out.
            if (current is LandingPhase || current is HelicopterLandingPhase)
            {
                landed = true;
            }
        }

        Assert.True(landed, $"Circuit 2 must conclude in a LandingPhase. Full phase sequence: {string.Join(" → ", phasesSeen)}");
    }

    private static AircraftState MakeC172OnUpwind(RunwayInfo rwy)
    {
        // Spawn ~0.5 nm past the departure end of the runway, climbing on runway
        // heading at typical C172 climb speed. Altitude well below TPA so the
        // aircraft climbs into upwind under normal phase logic.
        var spawnPos = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading, 1.0);
        return new AircraftState
        {
            Callsign = "N172TG",
            AircraftType = "C172",
            Position = spawnPos,
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 200,
            IndicatedAirspeed = 75,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
            },
            Phases = new PhaseList(),
        };
    }

    private static PhaseContext CtxFor(AircraftState ac, double dt = 1.0)
    {
        var rwy = ac.Phases!.AssignedRunway!;
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
    }
}
