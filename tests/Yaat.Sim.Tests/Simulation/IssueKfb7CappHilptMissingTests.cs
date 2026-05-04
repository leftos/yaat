using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the KFB7 hold-in-lieu regression.
///
/// Recording: S3-NCTC-3 | Area C Complete — KFB7 (CL60), KVNY → KMOD after a go-around
/// from KTCY. At t=1183 the controller issues "CAPP I28R" while the aircraft is on a
/// "DCT MOD" leg, ~3 nm NW of MOD VOR at 3000 ft.
///
/// Expected: enter the I28R via the MOD transition, which terminates with an HF leg
/// (hold-in-lieu of procedure turn) at ZELAT (the IAF for that transition). The
/// aircraft should fly MOD → ZELAT, execute one HILPT circuit, then continue inbound
/// on the FAC (288° true) to RW28R (MAP).
///
/// Actual (broken): the engine merged the MOD transition with the I28R common legs
/// without trimming the common-leg DLRAY (IAF for the FRA/PXN feeders, beyond ZELAT
/// on the FAC). Resulting NavigationRoute was
/// [MOD, ZELAT(TF), ZELAT(HF), DLRAY, ZELAT(FAF)] flown as a plain sequence with no
/// HoldingPatternPhase — the aircraft sailed past ZELAT to DLRAY (~13 nm beyond the
/// IAF) before doubling back. This produces a broken triangle in place of the
/// published HILPT.
///
/// Two compounding root causes addressed by these tests:
///   1. BuildApproachFixesWithTransition only deduplicated the boundary fix at
///      common-legs index 0; the MOD transition ends at ZELAT (the FAF in common
///      legs at index 1, after DLRAY) so dedup missed.
///   2. TryClearedApproach never inserted a HoldingPatternPhase even when
///      procedure.HasHoldInLieu was true (only TryJoinApproach did).
///
/// All tests use HYBRID REPLAY (snapshot restore) instead of replay-from-t=0. The
/// recording's track commands (AS 3Y ACCEPT) are skipped during ReplayCommand, which
/// cascades into divergent state by the time CAPP I28R fires at t=1183 (aircraft ends
/// up at the wrong position with the wrong NavRoute). See
/// docs/plans/open-issues/replay-divergence-from-t0.md.
/// </summary>
public class IssueKfb7CappHilptMissingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/b143fc615682.zip";

    /// <summary>
    /// Hybrid replay setup: load the scenario shell (engine.Replay(recording, 0)),
    /// then restore the snapshot at the requested time. Returns the engine + snapshot.
    /// Returns null if NavData or the recording is unavailable.
    /// </summary>
    private (SimulationEngine Engine, TimedSnapshot Snapshot, SessionRecording Recording, RecordingArchive Archive)? RestoreAt(double targetSeconds)
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        var snapshot = archive.ReadSnapshotAt(targetSeconds);
        if (snapshot is null)
        {
            archive.Dispose();
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(groundData);
        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0); // load scenario + weather, no actions
        engine.RestoreFromSnapshot(snapshot.State);

        return (engine, snapshot, recording, archive);
    }

    /// <summary>
    /// Sanity check: with KFB7 heading DCT MOD (NavigationRoute = [MOD]), the I28R
    /// transition selector must pick the MOD transition. MOD is in the route and is a
    /// transition-only fix (not a common-leg fix), so the existing nav-route check at
    /// SelectBestTransition.cs:1037-1067 should match it. Should pass against current code.
    /// </summary>
    [Fact]
    public void SelectBestTransition_KFB7_ReturnsModTransition()
    {
        var ctx = RestoreAt(1180);
        if (ctx is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }
        using (ctx.Value.Archive)
        {
            var aircraft = ctx.Value.Engine.FindAircraft("KFB7");
            Assert.NotNull(aircraft);

            output.WriteLine($"NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");

            var resolved = ApproachCommandHandler.ResolveApproach("I28R", "MOD", aircraft);
            Assert.True(resolved.Success, $"Should resolve I28R at KMOD. Got: {resolved.Error}");

            var procedure = resolved.Procedure!;
            var selected = ApproachCommandHandler.SelectBestTransition(procedure, aircraft);
            output.WriteLine($"Selected transition: {selected?.Name ?? "(none)"}");

            Assert.NotNull(selected);
            Assert.Equal("MOD", selected.Name);
        }
    }

    /// <summary>
    /// Core regression: after CAPP I28R the aircraft's downstream nav fixes must not
    /// include DLRAY. DLRAY is the IAF/IF for the FRA/PXN feeders (beyond ZELAT on
    /// the FAC); arriving via the MOD transition's HILPT puts the aircraft at ZELAT
    /// inbound, so the DLRAY → ZELAT common-leg segment is the wrong-IAF entry path
    /// and must be trimmed.
    ///
    /// Today the deferred path takes effect (Phases stays null, PendingClearance set,
    /// NavRoute mutated to [MOD, ZELAT(TF), ZELAT(HF), DLRAY, ZELAT(FAF)]). After the
    /// fix, the common-leg dedup drops DLRAY and the trailing ZELAT(FAF) regardless of
    /// whether deferred or immediate activation is used.
    /// </summary>
    [Fact]
    public void CappI28R_KFB7_DownstreamFixesExcludeDlray()
    {
        var ctx = RestoreAt(1180);
        if (ctx is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }
        using (ctx.Value.Archive)
        {
            var engine = ctx.Value.Engine;
            var aircraft = engine.FindAircraft("KFB7");
            Assert.NotNull(aircraft);

            output.WriteLine($"Pre-CAPP NavRoute: {string.Join(" → ", aircraft.Targets.NavigationRoute.Select(n => n.Name))}");

            var result = engine.SendCommand("KFB7", "CAPP I28R");
            output.WriteLine($"CAPP result: Success={result.Success} Message={result.Message}");
            Assert.True(result.Success, $"CAPP I28R should succeed. Got: {result.Message}");

            // Collect all downstream fix names regardless of whether the deferred path
            // (NavRoute fixes) or immediate path (ApproachNavigationPhase fixes) is taken.
            var navRouteFixes = aircraft.Targets.NavigationRoute.Select(n => n.Name).ToList();
            var phaseFixes = aircraft.Phases?.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault()?.Fixes.Select(f => f.Name).ToList() ?? new();

            output.WriteLine($"Post-CAPP NavRoute: {string.Join(" → ", navRouteFixes)}");
            output.WriteLine($"Post-CAPP ApproachNavigationPhase fixes: {string.Join(" → ", phaseFixes)}");

            Assert.DoesNotContain("DLRAY", navRouteFixes);
            Assert.DoesNotContain("DLRAY", phaseFixes);
        }
    }

    /// <summary>
    /// CAPP must insert a HoldingPatternPhase that mirrors JAPP's existing HILPT
    /// behaviour: one circuit at the HILPT fix, with the published direction and
    /// inbound course taken from procedure.HoldInLieuLeg. The KMOD I28R published
    /// HILPT is at ZELAT, left turns, course 288°.
    /// </summary>
    [Fact]
    public void CappI28R_KFB7_InsertsHoldingPatternPhaseAtZelat()
    {
        var ctx = RestoreAt(1180);
        if (ctx is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }
        using (ctx.Value.Archive)
        {
            var engine = ctx.Value.Engine;
            var aircraft = engine.FindAircraft("KFB7");
            Assert.NotNull(aircraft);

            var result = engine.SendCommand("KFB7", "CAPP I28R");
            Assert.True(result.Success, $"CAPP I28R should succeed. Got: {result.Message}");

            Assert.NotNull(aircraft.Phases);
            foreach (var phase in aircraft.Phases.Phases)
            {
                output.WriteLine($"Phase: {phase.GetType().Name}");
            }

            var holdPhase = aircraft.Phases.Phases.OfType<HoldingPatternPhase>().FirstOrDefault();
            Assert.NotNull(holdPhase);

            Assert.Equal("ZELAT", holdPhase.FixName);
            Assert.Equal(1, holdPhase.MaxCircuits);
            Assert.Equal(TurnDirection.Left, holdPhase.Direction);

            // Inbound course must use the same convention as JAPP's existing HILPT block:
            // (HoldInLieuLeg.OutboundCourse + 180) % 360, falling back to the FAC.
            var procedure = ApproachCommandHandler.ResolveApproach("I28R", "MOD", aircraft).Procedure!;
            var holdLeg = procedure.HoldInLieuLeg!;
            int expectedInboundCourse = holdLeg.OutboundCourse.HasValue ? (int)((holdLeg.OutboundCourse.Value + 180) % 360) : holdPhase.InboundCourse;
            Assert.Equal(expectedInboundCourse, holdPhase.InboundCourse);
        }
    }

    /// <summary>
    /// End-to-end: restore at t=1180, replay the recording's CAPP I28R action and
    /// downstream actions through t=2000, and confirm the aircraft never reaches
    /// DLRAY's vicinity. With the broken code the aircraft sails past ZELAT to DLRAY
    /// (closest approach ~3 nm in the actual recording at t=1830). After the fix the
    /// HILPT keeps it within ~6 nm of ZELAT until inbound on FAC.
    /// </summary>
    [Fact]
    public void CappI28R_KFB7_AircraftDoesNotFlyPastZelatToDlray()
    {
        var ctx = RestoreAt(1180);
        if (ctx is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }
        using (ctx.Value.Archive)
        {
            var engine = ctx.Value.Engine;
            int startSeconds = (int)ctx.Value.Snapshot.ElapsedSeconds;

            const double dlrayLat = 37.463075;
            const double dlrayLon = -120.65214166666667;

            double minDistanceToDlray = double.MaxValue;
            int minAt = 0;

            // ReplayRange one-second-at-a-time so we can sample position; the snapshot's
            // start time is ~t=1180, recording's CAPP I28R fires at t=1183. Run through
            // ~t=2000 (820 seconds, ~14 minutes) to cover both broken and fixed flight
            // paths.
            for (int t = startSeconds + 1; t <= startSeconds + 820; t++)
            {
                engine.ReplayRange(t - 1, t, ctx.Value.Recording.Actions);

                var ac = engine.FindAircraft("KFB7");
                if (ac is null)
                {
                    break;
                }

                double dist = GeoMath.DistanceNm(ac.Position, new LatLon(dlrayLat, dlrayLon));
                if (dist < minDistanceToDlray)
                {
                    minDistanceToDlray = dist;
                    minAt = t;
                }

                if ((t - startSeconds) % 60 == 0)
                {
                    output.WriteLine(
                        $"t={t} pos=({ac.Position.Lat:F4},{ac.Position.Lon:F4}) "
                            + $"alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} dlray={dist:F1}nm "
                            + $"phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}"
                    );
                }
            }

            output.WriteLine($"Closest approach to DLRAY: {minDistanceToDlray:F1} nm at t={minAt}");

            // Threshold: the broken behaviour gets within ~3 nm of DLRAY. The HILPT keeps
            // the aircraft at least ~5 nm clear (DLRAY is ~13 nm beyond ZELAT on the FAC).
            // 6 nm leaves margin for both fixed (~big margin) and broken (~3 nm fail).
            Assert.True(minDistanceToDlray > 6.0, $"Aircraft flew within {minDistanceToDlray:F1} nm of DLRAY at t={minAt} — HILPT regression");
        }
    }
}
