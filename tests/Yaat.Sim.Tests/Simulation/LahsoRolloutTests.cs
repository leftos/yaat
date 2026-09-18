using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end LAHSO rollout over the real SFO layout: land 28R holding short of 1L and tick the production loop.
///
/// <para>
/// AIM 4-3-11.b.6 gives the two acceptable outcomes: exit at the first convenient taxiway before the hold-short
/// point, or stop and hold at it. Both are measured from the landing threshold along the runway heading — the
/// datum <c>LahsoTarget.DistFromThresholdNm</c> was computed against — so the aircraft may never be found past
/// the point, at any speed.
/// </para>
/// </summary>
public class LahsoRolloutTests(ITestOutputHelper output)
{
    /// <summary>The layouts under <c>TestData</c>, cached across every run in this class.</summary>
    private static readonly TestAirportGroundData GroundData = new();

    /// <summary>
    /// One second of the run, measured along the runway from the landing threshold. <c>AlongFt</c> is the
    /// aircraft's centroid, which is what <c>AircraftState.Position</c> holds; <c>NoseAlongFt</c> adds half the
    /// airframe, and that is the datum the hold-short rule is written against — no part of the aircraft may
    /// extend beyond the marking (AIM 2-3-5.a.1).
    /// </summary>
    private sealed record Sample(int Second, double AlongFt, double NoseAlongFt, double GroundSpeedKts, Type? Phase);

    private sealed class LahsoRun
    {
        /// <summary>The hold-short point, in feet from the landing threshold — read from the target, never assumed.</summary>
        public required double HoldShortFt { get; init; }

        /// <summary>Every second of the run.</summary>
        public required List<Sample> Samples { get; init; }

        /// <summary>The seconds from touchdown onward.</summary>
        public required List<Sample> Rollout { get; init; }

        public required bool SawRunwayHolding { get; init; }
        public required bool SawRunwayExit { get; init; }
        public required bool StoppedForLahso { get; init; }
        public required bool LahsoTargetClearedAtEnd { get; init; }
        public required bool EndedInRunwayHold { get; init; }
        public required string FinalPhase { get; init; }
        public required string? CandidateExit { get; init; }

        /// <summary>The hold-short node of the exit the rollout committed to, if it committed to one.</summary>
        public required GroundNode? CandidateHoldShort { get; init; }

        /// <summary>
        /// The engine's occupied hold-short set after the first tick, from
        /// <c>SimulationEngine.ComputeOccupiedHoldShortNodes</c> — what the exit planner reads.
        /// </summary>
        public required IReadOnlySet<int> OccupiedAfterFirstTick { get; init; }

        public required string? CurrentTaxiway { get; init; }
        public required double FinalNoseAlongFt { get; init; }
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("LandingPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(GroundData);
    }

    /// <summary>
    /// Spawns <paramref name="aircraftType"/> on 1 nm final for SFO 28R, clears it to land and hold short of 1L,
    /// and ticks the production loop until the aircraft stops on the runway or clears it. Null when the layout or
    /// nav data is absent (silent skip).
    ///
    /// <para>
    /// <paramref name="blockedHoldShort"/> parks a second aircraft on that hold-short node, holding after its own
    /// exit — the state <c>SimulationEngine.ComputeOccupiedHoldShortNodes</c> reads to mark a node claimed.
    /// </para>
    /// </summary>
    private LahsoRun? RunLahsoLanding(string aircraftType, double approachSpeedKts, GroundNode? blockedHoldShort, string? exitCommand)
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        RunwayInfo? runway28R = navDb.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318, // ~3° glide slope at 1 nm
            IndicatedAirspeed = approachSpeedKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };

        // The chain ends at the landing, as a cleared-to-land chain does in production: PhaseRunner appends the
        // runway hold after a LAHSO stop and the exit pair after any other full stop. Pre-adding the exit pair
        // (as the non-LAHSO exit tests do) makes the runway hold land behind them and the aircraft skips it.
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso",
            ScenarioName = "SFO 28R LAHSO Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        if (blockedHoldShort is not null)
        {
            engine.World.AddAircraft(MakeHoldingAfterExitBlocker(runway28R, layout, blockedHoldShort));
        }

        CommandResult lahsoResult = engine.SendCommand("TST001", "LAHSO 1L");
        Assert.True(lahsoResult.Success, $"LAHSO 1L failed: {lahsoResult.Message}");

        if (exitCommand is not null)
        {
            CommandResult exitResult = engine.SendCommand("TST001", exitCommand);
            Assert.True(exitResult.Success, $"{exitCommand} failed: {exitResult.Message}");
        }

        LahsoTarget? target = aircraft.Phases.LahsoHoldShort;
        Assert.NotNull(target);
        double holdShortFt = target.DistFromThresholdNm * GeoMath.FeetPerNm;

        LandingPhase landingPhase = aircraft.Phases.Phases.OfType<LandingPhase>().Single();
        LatLon threshold = LandingThreshold.Resolve(runway28R, layout);

        // The same length source and 60 ft unknown-type fallback the phase uses to hold its nose clear of the point.
        double noseOffsetFt = (FaaAircraftDatabase.Get(aircraftType)?.LengthFt ?? 60.0) / 2.0;

        var samples = new List<Sample>();
        var rollout = new List<Sample>();
        bool sawRunwayHolding = false;
        bool sawRunwayExit = false;
        bool touchedDown = false;
        IReadOnlySet<int> occupiedAfterFirstTick = new HashSet<int>();

        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();

            if (t == 1)
            {
                occupiedAfterFirstTick = engine.ComputeOccupiedHoldShortNodes();
            }

            Phase? current = aircraft.Phases.CurrentPhase;
            double alongFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, runway28R.TrueHeading) * GeoMath.FeetPerNm;
            var sample = new Sample(t, alongFt, alongFt + noseOffsetFt, aircraft.GroundSpeed, current?.GetType());
            samples.Add(sample);

            touchedDown = touchedDown || aircraft.IsOnGround;
            if (touchedDown)
            {
                rollout.Add(sample);
            }

            sawRunwayHolding = sawRunwayHolding || (current is RunwayHoldingPhase);
            sawRunwayExit = sawRunwayExit || (current is RunwayExitPhase);

            if ((current is RunwayHoldingPhase) && (aircraft.GroundSpeed < 0.5))
            {
                break;
            }

            if (current is HoldingAfterExitPhase)
            {
                break;
            }
        }

        return new LahsoRun
        {
            HoldShortFt = holdShortFt,
            Samples = samples,
            Rollout = rollout,
            SawRunwayHolding = sawRunwayHolding,
            SawRunwayExit = sawRunwayExit,
            StoppedForLahso = landingPhase.StoppedForLahso,
            LahsoTargetClearedAtEnd = aircraft.Phases.LahsoHoldShort is null,
            EndedInRunwayHold = aircraft.Phases.CurrentPhase is RunwayHoldingPhase,
            FinalPhase = aircraft.Phases.CurrentPhase?.Name ?? "none",
            CandidateExit = landingPhase.CandidateExit?.TaxiwayName,
            CandidateHoldShort = landingPhase.CandidateExit?.HoldShortNode,
            OccupiedAfterFirstTick = occupiedAfterFirstTick,
            CurrentTaxiway = aircraft.Ground.CurrentTaxiway,
            FinalNoseAlongFt = samples.Count > 0 ? samples[^1].NoseAlongFt : 0,
        };
    }

    /// <summary>
    /// An aircraft that has already cleared <paramref name="runway"/> and is holding at <paramref name="holdShort"/>,
    /// the one state short of a taxi clearance that makes the engine report the node as claimed.
    /// </summary>
    private static AircraftState MakeHoldingAfterExitBlocker(RunwayInfo runway, AirportGroundLayout layout, GroundNode holdShort)
    {
        var blocker = new AircraftState
        {
            Callsign = "BLK001",
            AircraftType = "C172",
            Position = holdShort.Position,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        blocker.Ground.Layout = layout;
        blocker.Phases.Add(new HoldingAfterExitPhase(runway.Designator, "N", holdShort.Id));
        blocker.Phases.Start(CommandDispatcher.BuildMinimalContext(blocker, layout));
        return blocker;
    }

    /// <summary>Dumps the rollout so a failure carries the profile that produced it.</summary>
    private void DumpRollout(LahsoRun run, string label)
    {
        output.WriteLine($"[{label}] hold-short point {run.HoldShortFt:F0} ft from the landing threshold");
        foreach (Sample s in run.Rollout)
        {
            output.WriteLine(
                $"  t+{s.Second, 3}s nose={s.NoseAlongFt, 7:F0}ft remaining={run.HoldShortFt - s.NoseAlongFt, 7:F0}ft "
                    + $"gs={s.GroundSpeedKts, 5:F1}kt {s.Phase?.Name ?? "none"}"
            );
        }

        output.WriteLine(
            $"[{label}] final={run.FinalPhase} nose={run.FinalNoseAlongFt:F0}ft stoppedForLahso={run.StoppedForLahso} "
                + $"candidate={run.CandidateExit ?? "(none)"}/node={run.CandidateHoldShort?.Id.ToString() ?? "(none)"} "
                + $"taxiway={run.CurrentTaxiway ?? "(none)"} lahsoCleared={run.LahsoTargetClearedAtEnd}"
        );
    }

    /// <summary>
    /// AIM 2-3-5.a.2: a pilot cleared to land and hold short must exit before the holding position markings or
    /// stop at them; AIM 4-3-11.b.2 is the available landing distance the stop has to fit inside, threshold to
    /// hold-short point. Measured at the nose, because no part of the aircraft may extend beyond the marking.
    /// </summary>
    [Fact]
    public void B738_LahsoRollout_StopsShortOfTheHoldShortPoint()
    {
        LahsoRun? run = RunLahsoLanding("B738", approachSpeedKts: 145, blockedHoldShort: null, exitCommand: null);
        if (run is null)
        {
            return;
        }

        DumpRollout(run, "B738");

        Assert.NotEmpty(run.Rollout);

        // Sign sanity: the first sample is on short final, behind the threshold; the roll grows past it.
        Assert.True(run.Samples[0].AlongFt < 0, $"first sample should sit behind the threshold, was {run.Samples[0].AlongFt:F0} ft");
        Assert.True(run.Rollout[^1].AlongFt > 0, $"the roll should end past the threshold, was {run.Rollout[^1].AlongFt:F0} ft");

        foreach (Sample s in run.Rollout)
        {
            double remainingFt = run.HoldShortFt - s.NoseAlongFt;
            Assert.True(remainingFt >= 0, $"t+{s.Second}s: nose {-remainingFt:F0} ft past the hold-short point at {s.GroundSpeedKts:F1} kt");

            if (remainingFt <= 100.0)
            {
                Assert.True(
                    s.GroundSpeedKts <= 25.0,
                    $"t+{s.Second}s: {s.GroundSpeedKts:F1} kt with only {remainingFt:F0} ft from the nose to the hold-short point"
                );
            }

            if (remainingFt <= 250.0)
            {
                Assert.True(
                    s.GroundSpeedKts <= 32.0,
                    $"t+{s.Second}s: too fast inside 250 ft — {s.GroundSpeedKts:F1} kt with {remainingFt:F0} ft from the nose to the hold-short point"
                );
            }

            // The invariant the rollout ceiling exists to hold: firm braking from here still stops the aircraft
            // before the point. One knot of slack absorbs the integrator's sub-tick lag.
            double stoppableKts = RolloutBraking.MaxEntrySpeedKts(remainingFt / GeoMath.FeetPerNm, RolloutBraking.FirmBrakingRateKtsPerSec);
            Assert.True(
                s.GroundSpeedKts <= (stoppableKts + 1.0),
                $"t+{s.Second}s: {s.GroundSpeedKts:F1} kt with {remainingFt:F0} ft from the nose to the hold-short point — "
                    + $"firm braking only stops from {stoppableKts:F1} kt there"
            );
        }

        for (int i = 1; i < run.Rollout.Count; i++)
        {
            Sample previous = run.Rollout[i - 1];
            Sample current = run.Rollout[i];
            if ((run.HoldShortFt - previous.NoseAlongFt) > 600.0)
            {
                continue;
            }

            Assert.True(
                (current.GroundSpeedKts < previous.GroundSpeedKts) || (current.GroundSpeedKts <= 1.0),
                $"t+{current.Second}s: coast plateau inside 600 ft — {previous.GroundSpeedKts:F1} kt at "
                    + $"{run.HoldShortFt - previous.NoseAlongFt:F0} ft, still {current.GroundSpeedKts:F1} kt at "
                    + $"{run.HoldShortFt - current.NoseAlongFt:F0} ft from the nose to the hold-short point"
            );
        }

        double stopNoseFt = run.Rollout[^1].NoseAlongFt;
        Assert.InRange(stopNoseFt, run.HoldShortFt - 400.0, run.HoldShortFt);

        // The stopping margin is real rather than a lucky sample: the whole aircraft comes to rest clear of the
        // marking, nose included (AIM 2-3-5.a.1).
        Assert.True(stopNoseFt <= (run.HoldShortFt - 25.0), $"nose stopped only {run.HoldShortFt - stopNoseFt:F0} ft short of the hold-short point");

        // StoppedForLahso is what makes PhaseRunner append the hold chain, so the runway hold is its observable half.
        Assert.True(run.StoppedForLahso, "the landing should have completed as a LAHSO stop");
        Assert.True(run.EndedInRunwayHold, $"expected the runway-hold chain, ended in {run.FinalPhase}");
    }

    /// <summary>
    /// AIM 4-3-11.b.6: exiting before the hold-short point is the preferred branch. A light aircraft that can make
    /// a turnoff ahead of the point takes it and the LAHSO hold is cleared — it never rolls up to the line.
    /// </summary>
    [Fact]
    public void LightAircraft_LahsoRollout_ExitsBeforeThePoint_AndClearsTheLahsoTarget()
    {
        LahsoRun? run = RunLahsoLanding("C172", approachSpeedKts: 75, blockedHoldShort: null, exitCommand: null);
        if (run is null)
        {
            return;
        }

        DumpRollout(run, "C172");

        Assert.NotEmpty(run.Rollout);

        foreach (Sample s in run.Rollout)
        {
            if ((s.Phase != typeof(LandingPhase)) && (s.Phase != typeof(RunwayExitPhase)))
            {
                continue;
            }

            double remainingFt = run.HoldShortFt - s.NoseAlongFt;
            Assert.True(remainingFt >= 0, $"t+{s.Second}s: nose {-remainingFt:F0} ft past the hold-short point at {s.GroundSpeedKts:F1} kt");
        }

        Assert.True(run.SawRunwayExit, $"the aircraft should have handed off to the runway exit, ended in {run.FinalPhase}");
        Assert.False(run.SawRunwayHolding, "an aircraft that exits before the point must never enter the LAHSO runway hold");
        Assert.False(run.StoppedForLahso, "an aircraft that exits before the point never stops for LAHSO");
        Assert.True(run.LahsoTargetClearedAtEnd, "the LAHSO target must be cleared once the aircraft commits to an exit");
        Assert.Equal("Holding After Exit", run.FinalPhase);
    }

    /// <summary>
    /// The exit before the point is only worth handing off to if the aircraft can actually take it. With its
    /// hold-short claimed by another aircraft, <c>RunwayExitPhase</c> would drop the commitment and re-search the
    /// centerline with no knowledge of the LAHSO point — so the landing keeps rolling instead and takes the other
    /// branch of AIM 4-3-11.b.6: stop and hold at the hold-short point.
    /// </summary>
    [Fact]
    public void LightAircraft_LahsoRollout_CommittedExitOccupied_StopsShortAndHolds()
    {
        LahsoRun? clearRun = RunLahsoLanding("C172", approachSpeedKts: 75, blockedHoldShort: null, exitCommand: "EXIT N");
        if (clearRun is null)
        {
            return;
        }

        GroundNode? committed = clearRun.CandidateHoldShort;
        Assert.NotNull(committed);
        output.WriteLine($"[C172] unblocked run exits via {clearRun.CandidateExit}, hold-short node {committed.Id}");

        LahsoRun? run = RunLahsoLanding("C172", approachSpeedKts: 75, blockedHoldShort: committed, exitCommand: "EXIT N");
        Assert.NotNull(run);

        DumpRollout(run, "C172 blocked");

        // The blocker has to be visible to the exit planner, or the run proves nothing.
        Assert.Contains(committed.Id, run.OccupiedAfterFirstTick);

        Assert.NotEmpty(run.Rollout);
        foreach (Sample s in run.Rollout)
        {
            double remainingFt = run.HoldShortFt - s.NoseAlongFt;
            Assert.True(remainingFt >= 0, $"t+{s.Second}s: nose {-remainingFt:F0} ft past the hold-short point at {s.GroundSpeedKts:F1} kt");
        }

        Assert.False(run.SawRunwayExit, $"the occupied exit must not be taken, ended in {run.FinalPhase}");
        Assert.True(run.StoppedForLahso, "with no usable exit before the point the aircraft stops for LAHSO");
        Assert.True(run.EndedInRunwayHold, $"expected the runway-hold chain, ended in {run.FinalPhase}");
    }
}
