using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Runway-occupancy characterization over the <c>sfo-gc-arrival-goarounds</c> bundle — how long an arrival
/// takes to get off the landing runway once <see cref="RunwayExitPhase"/> owns it.
///
/// <para>Both arms watch a high-speed exit: SKW3398 (E75L) off 28R at T, and UAL2627 off 28L. A ≤45° exit is
/// taken at <c>CategoryPerformance.ExitTurnOffSpeed</c> (30 kt for a jet) and the exit route's ceiling is
/// <c>min(coastSpeed, TaxiSpeed)</c> — also 30 kt — so the ~1,050 ft from the branch node to the hold-short
/// plus half a fuselage is a ~21 s leg. In the recorded run SKW3398 entered the exit at t≈370 at 37 kt and was
/// not clear until t≈407-410: ~37-40 s at 18-27 kt. The bundle's mean vacate across 14 landings was 16.6 s, so
/// these two legs are where the runway occupancy goes — and occupancy is what forces the arrival behind to go
/// around (§3-10-3.a.1).</para>
///
/// <para>Hybrid replay (docs/e2e-tdd-issue-debugging.md §5b), the same shape
/// <c>SfoGroundControlArrivalGoAroundTests.Observe</c> uses: restore the recorded snapshot at T, replay recorded
/// actions to a cutoff, then <c>TickOneSecond</c> so phases and physics alone drive the rollout and the exit.
/// Each arm also drops a <c>TickRecorder</c> JSON under <c>.tmp/</c> for
/// <c>Yaat.LayoutInspector --ticks --tick-table</c>.</para>
/// </summary>
public class Sfo28rArrivalOccupancyTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-gc-arrival-goarounds-recording.yaat-bug-report-bundle.zip";

    /// <summary>
    /// The longest vacate the 28R arm accepts. SKW3398's remainder from the exit-phase entry to the virtual
    /// target half a fuselage past hold-short node 834 is ~1,286 ft. The exit route's ceiling is
    /// <c>min(coastSpeed, TaxiSpeed)</c> = 30 kt and the navigator brakes to the stop at
    /// <c>CategoryPerformance.TaxiDecelRate</c> = 5 kt/s, so the profile is ~1,134 ft held at 30 kt (~22 s)
    /// plus the 152 ft / 6 s stop: ~28 s, and 30 s is that plus a margin.
    /// </summary>
    private const int SkwMaxVacateSeconds = 30;

    /// <summary>
    /// The mean ground speed the 28R arm requires over the vacate. The old one-tick overshoot backstop fired
    /// on every pass-through node of the 16-chord taxiway-T polyline and ratcheted the aircraft down to 16-19
    /// kt for the last 500 ft (mean 20.2 kt); a run that holds the 30-kt ceiling to the braking point and only
    /// then brakes averages ~26 kt. 24 kt is the floor that separates the two.
    /// </summary>
    private const double SkwMinMeanGroundSpeedKts = 24.0;

    /// <summary>
    /// How far <c>SameRunwayArrivalProtection</c>'s vacate prediction may sit from the vacate the sim then flies.
    /// The prediction is what the arrival-spacing pass and <c>OccupiedRunwayGoAround.WillBeClearOfRunway</c> act on,
    /// so a model that does not match the phase sends an arrival around behind a leader that will be clear (or the
    /// reverse). It measures the straight line to the hold-short rather than the curved exit path, which is the
    /// standing optimism the class documents, so a few seconds either way is the model, not a defect.
    /// </summary>
    private const double MaxPredictionErrorSeconds = 4.0;

    /// <summary>
    /// Regression pin, not a target: UAL2627's E exit off 28L is a 70° standard-angle turn, and the fillet's
    /// own <c>GroundArc.MaxSafeSpeedKts</c> (12.1 kt through that turn) is what sets its budget — the aircraft
    /// is back to only 17.7 kt at the 1.0 kt/s taxi accel rate before it brakes for the stop 566 ft later. The
    /// pass-through-node fix does not apply to an arc-bound leg. This pins the 26 s it measures from drifting.
    /// </summary>
    private const int UalMaxVacateSeconds = 27;

    /// <summary>One observed second: what phase owned the aircraft, how fast it was going, and what the ground navigator was steering to.</summary>
    private sealed record Second(
        int T,
        string Phase,
        double Ias,
        double Gs,
        double? TargetSpeed,
        string Twy,
        int? HoldShortNodeId,
        double HoldShortDistFt,
        string Nav
    );

    /// <summary>
    /// One arm's measurement: the second the aircraft first entered <c>LandingPhase</c> / <see cref="RunwayExitPhase"/>,
    /// the second the exit phase handed off (the sim's "clear of the runway" moment) and to what, the taxiway it used,
    /// and every observed second.
    /// </summary>
    private sealed record VacateTrace(
        string Callsign,
        string Runway,
        int? LandingSecond,
        int? ExitSecond,
        int? ClearSecond,
        string? PhaseAfterExit,
        string? ExitTaxiway,
        int? PredictedAtSecond,
        double? PredictedClearSeconds,
        List<Second> Seconds
    );

    /// <summary>
    /// Restores the snapshot at <paramref name="restoreAt"/>, replays recorded actions through
    /// <paramref name="replayUntil"/>, then ticks physics and phases alone through <paramref name="endAt"/>.
    /// Returns null when the fixture or navdata is unavailable (silent skip).
    /// </summary>
    private VacateTrace? Observe(string callsign, string runway, int restoreAt, int replayUntil, int endAt)
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            TestVnasData.EnsureInitialized();
            if (TestVnasData.NavigationDb is null)
            {
                return null;
            }

            // Add .EnableCategory("GroundNavigator", LogLevel.Debug) here to get the navigator's per-sub-tick
            // speed-cap attribution ([Nav] lines). It is ~46k lines over these two windows, so it stays off by
            // default and is switched on for an investigation run.
            SimLogBuilder.CreateForTest(output).EnableCategory("RunwayExitPhase", LogLevel.Debug).InitializeSimLog();

            SessionRecording recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(new TestAirportGroundData());
            engine.Replay(recording, 0);

            TimedSnapshot? snapshot = archive.ReadSnapshotAt(restoreAt);
            if (snapshot is null)
            {
                return null;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            string tickJson = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", $"{callsign.ToLowerInvariant()}-exit-ticks.json");
            using IDisposable _ = TickRecorder.Attach(engine, tickJson, callsign);

            var seconds = new List<Second>();
            int? landingSecond = null;
            int? exitSecond = null;
            int? clearSecond = null;
            string? phaseAfterExit = null;
            string? exitTaxiway = null;
            int? predictedAtSecond = null;
            double? predictedClearSeconds = null;

            for (int t = restoreAt + 1; t <= endAt; t++)
            {
                if (t <= replayUntil)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }

                AircraftState? ac = engine.FindAircraft(callsign);
                if (ac?.Phases?.CurrentPhase is not { } phase)
                {
                    if (ac is null)
                    {
                        output.WriteLine($"t={t}: {callsign} is no longer in the world");
                        break;
                    }
                    continue;
                }

                string name = phase.GetType().Name;
                var exitPhase = phase as RunwayExitPhase;
                GroundNode? holdShort = exitPhase?.TargetHoldShortNode;
                double holdShortDistFt = holdShort is null ? double.NaN : GeoMath.DistanceNm(ac.Position, holdShort.Position) * GeoMath.FeetPerNm;

                string nav = ac.Ground.LastNavDiag is { } d
                    ? $"node={d.TargetNodeId} dist={d.DistToTargetNm * GeoMath.FeetPerNm:F0}ft hdgErr={d.AngleDiffDeg:F0} navTgt={d.TargetSpeedKts:F1} nodeReq={d.NodeRequiredSpeedKts:F1} arc={d.OnArc}"
                    : "(no nav diag)";

                seconds.Add(
                    new Second(
                        t,
                        name,
                        ac.IndicatedAirspeed,
                        ac.GroundSpeed,
                        ac.Targets.TargetSpeed,
                        ac.Ground.CurrentTaxiway ?? "",
                        exitPhase?.TargetHoldShortNodeId,
                        holdShortDistFt,
                        nav
                    )
                );

                if ((landingSecond is null) && (name == nameof(Yaat.Sim.Phases.Tower.LandingPhase)))
                {
                    landingSecond = t;
                }

                if (exitPhase is not null)
                {
                    exitSecond ??= t;
                    if (!string.IsNullOrEmpty(ac.Ground.CurrentTaxiway))
                    {
                        exitTaxiway = ac.Ground.CurrentTaxiway;
                    }

                    // What the spacing pass and the occupied-runway go-around believe about this aircraft, taken
                    // the first second its exit is resolved enough to answer (zero elapsed = a plain time-from-now).
                    if ((predictedClearSeconds is null) && (SameRunwayArrivalProtection.TryBuildRollout(ac, 0.0) is { } rollout))
                    {
                        predictedClearSeconds = SameRunwayArrivalProtection.SecondsToRunwayClear(rollout);
                        predictedAtSecond = t;
                    }
                }
                else if ((exitSecond is not null) && (clearSecond is null))
                {
                    clearSecond = t;
                    phaseAfterExit = name;
                }
            }

            return new VacateTrace(
                callsign,
                runway,
                landingSecond,
                exitSecond,
                clearSecond,
                phaseAfterExit,
                exitTaxiway,
                predictedAtSecond,
                predictedClearSeconds,
                seconds
            );
        }
    }

    /// <summary>What the vacate measured: its duration in seconds and the ground speeds observed over it.</summary>
    private sealed record VacateStats(int Vacate, double MeanGs, double MinGs);

    /// <summary>
    /// Prints the measured durations and the per-second vacate table, asserts the exit taxiway and the vacate
    /// budget, and returns the measurement so an arm can assert more about it.
    /// </summary>
    private VacateStats AssertVacatesWithinTheExitLeg(VacateTrace trace, string[] acceptedExits, int maxVacateSeconds)
    {
        output.WriteLine(
            $"{trace.Callsign} ({trace.Runway}): landing={trace.LandingSecond?.ToString() ?? "(never)"} "
                + $"exit={trace.ExitSecond?.ToString() ?? "(never)"} clear={trace.ClearSecond?.ToString() ?? "(never)"} "
                + $"→ {trace.PhaseAfterExit ?? "(nothing)"} via {trace.ExitTaxiway ?? "(no taxiway)"}"
        );

        Assert.True(
            trace.ExitSecond is not null,
            $"{trace.Callsign} never entered RunwayExitPhase in the observed window — the arm's window is wrong, not the sim. "
                + $"Phases seen: {string.Join(", ", trace.Seconds.Select(s => s.Phase).Distinct())}."
        );
        Assert.True(
            trace.ClearSecond is not null,
            $"{trace.Callsign} entered RunwayExitPhase at t={trace.ExitSecond} and was still in it at the end of the window — "
                + $"it never got clear of {trace.Runway} at all."
        );

        int exitAt = trace.ExitSecond!.Value;
        int clearAt = trace.ClearSecond!.Value;
        int vacate = clearAt - exitAt;
        int rollout = trace.LandingSecond is { } landAt ? exitAt - landAt : -1;

        var vacateSeconds = trace.Seconds.Where(s => (s.T >= exitAt) && (s.T <= clearAt)).ToList();
        double minGs = vacateSeconds.Count == 0 ? 0 : vacateSeconds.Min(s => s.Gs);
        double meanGs = vacateSeconds.Count == 0 ? 0 : vacateSeconds.Average(s => s.Gs);

        output.WriteLine($"--- {trace.Callsign} vacate table (t, phase, ias, gs, targetSpeed, twy, holdShort) ---");
        foreach (Second? s in trace.Seconds.Where(s => (s.T >= exitAt - 3) && (s.T <= clearAt + 2)))
        {
            string hs = s.HoldShortNodeId is { } id ? $"hs={id} @{s.HoldShortDistFt:F0}ft" : "hs=(none)";
            output.WriteLine($"t={s.T} {s.Phase} ias={s.Ias:F1} gs={s.Gs:F1} tgt={s.TargetSpeed:F1} twy={s.Twy} {hs} {s.Nav}");
        }

        output.WriteLine($"{trace.Callsign}: rollout(landing→exit)={rollout}s vacate(exit→clear)={vacate}s minGs={minGs:F1}kt meanGs={meanGs:F1}kt");

        Assert.True(
            acceptedExits.Contains(trace.ExitTaxiway, StringComparer.OrdinalIgnoreCase),
            $"{trace.Callsign} vacated {trace.Runway} via '{trace.ExitTaxiway ?? "(none)"}', not one of {string.Join("/", acceptedExits)} — "
                + $"the arm is measuring a different exit than the bundle flew, so the duration below is not comparable."
        );

        Assert.True(
            vacate <= maxVacateSeconds,
            $"{trace.Callsign} took {vacate}s to get clear of {trace.Runway} via {trace.ExitTaxiway} (t={exitAt}→{clearAt}), "
                + $"averaging {meanGs:F1} kt and dropping to {minGs:F1} kt. The exit route's ceiling is the 30-kt taxi speed and "
                + $"the navigator brakes to the hold-short stop at 5 kt/s, so the remainder is one steady leg plus a ~6 s stop; "
                + $"anything past {maxVacateSeconds}s is the navigator holding the aircraft below its ceiling down a taxiway, and "
                + $"that time is runway occupancy the arrival behind pays for."
        );

        return new VacateStats(vacate, meanGs, minGs);
    }

    /// <summary>
    /// SKW3398 (E75L) lands 28R and takes T. Restores at t=300 and replays to t=410 — the recording carries no
    /// command to SKW3398 in that window — then ticks on so the exit completes under phases and physics alone.
    /// </summary>
    [Fact]
    public void SKW3398_VacatesTAt28R_WithinTheExitLegAtTurnOffSpeed()
    {
        VacateTrace? trace = Observe("SKW3398", "28R", restoreAt: 300, replayUntil: 410, endAt: 460);
        if (trace is null)
        {
            return;
        }

        VacateStats stats = AssertVacatesWithinTheExitLeg(trace, ["T", "E"], SkwMaxVacateSeconds);

        Assert.True(
            stats.MeanGs >= SkwMinMeanGroundSpeedKts,
            $"SKW3398 averaged {stats.MeanGs:F1} kt over its {stats.Vacate}s vacate (low {stats.MinGs:F1} kt). Taxiway T is a 33° "
                + $"high-speed exit and the route's ceiling is 30 kt, so the only thing that should hold it below that is the "
                + $"braking curve onto the hold-short stop — a mean under {SkwMinMeanGroundSpeedKts:F0} kt means it is being held "
                + $"down along the leg, not just at the end of it."
        );

        Assert.True(
            trace.PredictedClearSeconds is not null,
            "SKW3398's exit never produced a rollout, so SameRunwayArrivalProtection had nothing to predict from — "
                + "the spacing pass and the go-around would both be reading the runway as occupied by an unknown."
        );

        double remainingWhenPredicted = trace.ClearSecond!.Value - trace.PredictedAtSecond!.Value;
        double predictionError = Math.Abs(trace.PredictedClearSeconds!.Value - remainingWhenPredicted);
        output.WriteLine(
            $"SKW3398: predicted clear at t={trace.PredictedAtSecond} in {trace.PredictedClearSeconds:F1}s, "
                + $"actually took {remainingWhenPredicted:F0}s (error {predictionError:F1}s)"
        );

        Assert.True(
            predictionError <= MaxPredictionErrorSeconds,
            $"SameRunwayArrivalProtection predicted SKW3398 clear of 28R in {trace.PredictedClearSeconds:F1}s from t="
                + $"{trace.PredictedAtSecond}, and it took {remainingWhenPredicted:F0}s. A prediction that far off the profile "
                + $"the phase flies is what the arrival behind is spaced and sent around on."
        );
    }

    /// <summary>
    /// UAL2627 lands 28L ahead of SKW5536. Restores at t=640 and replays to t=725 — the instructor deletes
    /// SKW5536 at t=728 — then ticks on through the rollout and the exit.
    /// </summary>
    [Fact]
    public void UAL2627_VacatesAt28L_WithinTheExitLeg()
    {
        VacateTrace? trace = Observe("UAL2627", "28L", restoreAt: 640, replayUntil: 725, endAt: 820);
        if (trace is null)
        {
            return;
        }

        AssertVacatesWithinTheExitLeg(trace, ["E", "T"], UalMaxVacateSeconds);
    }
}
