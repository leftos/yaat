using Xunit;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the SFO "UAL58 did a loop before taxiing" report.
///
/// Recording: <c>ual58-spot9-reversal-recording.yaat-bug-report-bundle.zip</c> (v4, 231 snapshots,
/// 1833 s, ARTCC ZOA). UAL58 is a B77W off gate G10 that taxis <c>TAXI T9 $9</c> and comes to rest on
/// taxilane T9 with its nose on the spot-9 marking, heading 117.8° — the only T9/A junction sits ~75 ft
/// BEHIND it, so taxiway A is off the tail. At t=1013 the controller issues <c>TAXI A F 28L</c>, whose
/// route leaves the spot on T9 (bearing 297.8°) and then turns onto A (bearing 27.6°).
///
/// The reversal is geometrically unavoidable, but the navigator took it the wrong way round: the
/// entry-alignment slow-turn's direction is picked from the sign of an exactly-180° heading delta — a
/// coin flip — and it went RIGHT, so the following 89.8° right turn onto A compounded with it into one
/// continuous 270° clockwise sweep (t=1015..1045: 117.8 -> 129.1 -> 221.7 -> 312.6 -> 322.6 -> 351.7 ->
/// 21.0 -> 27.7, a 269.8° monotone right rotation). Turning the other way makes the two sweeps cancel:
/// 117.8 --L180--> 297.8 --R90--> 27.6.
///
/// One adjacent defect shows up in the same recording, while UAL58 is still leaving gate G10: a free-space
/// entry-alignment arc aimed at a node only 21 ft away (shorter than the arc itself), which overshoots the
/// node and then swings back to re-acquire the line.
///
/// Strategy: hybrid replay. The fixes change behaviour from t≈930, before the reported moment, so a full
/// replay from t=0 would not reach the reported state the same way (docs/e2e-tdd-issue-debugging.md §5b).
/// There are no corrective commands for UAL58 between t=920 and t=1100 — its next input is <c>CROSS 28L</c>
/// at t=1492 — so a plain <c>ReplayOneSecond()</c> loop over each window is faithful.
/// </summary>
public class Ual58Spot9ReversalTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/ual58-spot9-reversal-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "UAL58";

    /// <summary>
    /// The most net rotation a reversal-plus-corner may add up to over a whole window. A reversal onto a
    /// taxiway behind the aircraft is ~180° and the following corner onto A is ~90°: taken the right way
    /// they cancel to a net ~90°, taken the wrong way they compound to a net ~270°. A net over a half turn
    /// means the aircraft drove the long way round the compass instead of turning around the short way.
    /// Sampling-independent — unlike a monotone run, a net total cannot be split by a one-tick dip.
    /// </summary>
    private const double MaxNetCumulativeDeg = 180.0;

    /// <summary>
    /// The most rotation the aircraft may be away from its window-start heading at any sample. The wrong-way
    /// reversal compounded with the corner onto A peaks past 300°; a single reversal peaks a little over 180°,
    /// not at it: an arc aimed at a node BEHIND the aircraft has to sweep more than a half turn to put its exit
    /// tangent through that node, approaching 180° only as the node recedes, and the adaptive rounding radius
    /// for a 180° deflection clamps to <c>TightTurnFloorRadiusFt</c>. Node 33 at ~81 ft off a 15 ft radius costs
    /// ~201°, which is the floor for this geometry — do not read the headroom here as slack.
    /// </summary>
    private const double MaxPeakCumulativeDeg = 210.0;

    /// <summary>Departure bearing of taxiway A at the T9 junction — the tangent UAL58 must roll out on.</summary>
    private const double TaxiwayABearingDeg = 27.6;

    /// <summary>How close to taxiway A's bearing counts as established on it.</summary>
    private const double EstablishedToleranceDeg = 20.0;

    /// <summary>
    /// What one replay window measured: the signed rotation accumulated over the whole window, the largest
    /// absolute value that running total ever reached, the worst same-sign (monotone) run for the trace, and
    /// two non-vacuity readings — how close the aircraft ever came to taxiway A's bearing, and the phase it
    /// ended the window in.
    /// </summary>
    private readonly record struct WindowResult(
        double NetCumulativeDeg,
        double PeakCumulativeDeg,
        double MaxMonotoneRunDeg,
        double ClosestToTaxiwayADeg,
        string FinalPhaseName
    );

    /// <summary>
    /// The reported bug: after <c>TAXI A F 28L</c> the entry reversal and the corner onto A compound into a
    /// single ~270° clockwise sweep — the "loop" the user saw. Reversing the other way makes the two cancel,
    /// leaving a net ~90° left turn that never swings more than the reversal's own ~180° from where it began.
    /// </summary>
    [Fact]
    public void Ual58_TurnsAroundOnceAtSpot9_DoesNotLoop()
    {
        if (RunWindow(restoreAtSeconds: 1010, replayToSeconds: 1100) is not { } window)
        {
            return;
        }

        Assert.True(
            Math.Abs(window.NetCumulativeDeg) <= MaxNetCumulativeDeg,
            $"UAL58 turned a net {window.NetCumulativeDeg:F1}° after TAXI A F 28L (peak {window.PeakCumulativeDeg:F1}°, longest same-sign "
                + $"run {window.MaxMonotoneRunDeg:F1}°). The reversal off spot 9 and the 89.8° turn onto A compounded instead of cancelling, "
                + $"so it drove the long way round the compass; the limit is {MaxNetCumulativeDeg:F0}°."
        );

        Assert.True(
            window.PeakCumulativeDeg <= MaxPeakCumulativeDeg,
            $"UAL58 swung {window.PeakCumulativeDeg:F1}° away from its spot-9 heading after TAXI A F 28L (net {window.NetCumulativeDeg:F1}°). "
                + $"A single reversal peaks near 180°; the limit is {MaxPeakCumulativeDeg:F0}°."
        );

        Assert.True(
            window.ClosestToTaxiwayADeg <= EstablishedToleranceDeg,
            $"UAL58 never came closer than {window.ClosestToTaxiwayADeg:F1}° to taxiway A's {TaxiwayABearingDeg:F1}° bearing in the window — "
                + $"it never got established on A, so the rotation this window measured proves nothing."
        );
    }

    /// <summary>
    /// The free-space alignment arc leaving gate G10 is aimed at the approach leg's to-node 21 ft ahead,
    /// which the ~72 ft arc overshoots; pure pursuit then re-acquires a line the aircraft has already left,
    /// adding an extra swing on top of the turn out of the ramp.
    /// </summary>
    [Fact]
    public void Ual58_LeavingTheGate_DoesNotOvershootTheRampLeg()
    {
        if (RunWindow(restoreAtSeconds: 920, replayToSeconds: 990) is not { } window)
        {
            return;
        }

        Assert.True(
            window.PeakCumulativeDeg <= MaxPeakCumulativeDeg,
            $"UAL58 swung {window.PeakCumulativeDeg:F1}° away from its gate heading leaving G10 (net {window.NetCumulativeDeg:F1}°, longest "
                + $"same-sign run {window.MaxMonotoneRunDeg:F1}°). The entry-alignment arc overshot the ramp leg it was aimed at and swung "
                + $"back to re-acquire it; the limit is {MaxPeakCumulativeDeg:F0}°."
        );

        Assert.True(
            window.FinalPhaseName == nameof(HoldingInPositionPhase),
            $"UAL58 ended the gate window in {window.FinalPhaseName}, not {nameof(HoldingInPositionPhase)} — it never finished its "
                + $"TAXI T9 $9 route onto spot 9, so the rotation this window measured proves nothing."
        );
    }

    /// <summary>
    /// Restore the bundle's snapshot at <paramref name="restoreAtSeconds"/> and replay second-by-second to
    /// <paramref name="replayToSeconds"/>, sampling UAL58 every second. Accumulates the signed per-second
    /// heading delta into a running total and reports where it ends (net), the largest absolute value it ever
    /// reached (peak), and — for the trace only — the largest same-sign run, the same total reset whenever the
    /// sign flips. Returns null when test data is missing, so the test skips silently.
    /// </summary>
    private WindowResult? RunWindow(int restoreAtSeconds, int replayToSeconds)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var archive = RecordingLoader.OpenArchive(BundlePath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(groundData);
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(restoreAtSeconds);
            if (snapshot is null)
            {
                return null;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int startSeconds = (int)snapshot.ElapsedSeconds;

            var aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                return null;
            }

            output.WriteLine(
                $"restored t={startSeconds} (asked {restoreAtSeconds}) hdg={aircraft.TrueHeading.Degrees:F1} "
                    + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}) type={aircraft.AircraftType}"
            );
            NearestNodeHelper.Log(output, $"t={startSeconds}:", aircraft, layout);

            double prevHeadingDeg = aircraft.TrueHeading.Degrees;
            double runDeg = 0.0;
            double maxRunDeg = 0.0;
            double netDeg = 0.0;
            double peakDeg = 0.0;
            double finalHeadingDeg = prevHeadingDeg;
            double closestToTaxiwayADeg = Math.Abs(GeoMath.SignedBearingDifference(prevHeadingDeg, TaxiwayABearingDeg));
            string finalPhaseName = aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)";

            for (int t = startSeconds + 1; t <= replayToSeconds; t++)
            {
                engine.ReplayOneSecond();
                aircraft = engine.FindAircraft(Callsign);
                if (aircraft is null)
                {
                    break;
                }

                double headingDeg = aircraft.TrueHeading.Degrees;
                double deltaDeg = GeoMath.SignedBearingDifference(prevHeadingDeg, headingDeg);
                if (runDeg != 0.0 && deltaDeg != 0.0 && Math.Sign(deltaDeg) != Math.Sign(runDeg))
                {
                    runDeg = 0.0;
                }
                runDeg += deltaDeg;
                maxRunDeg = Math.Max(maxRunDeg, Math.Abs(runDeg));
                netDeg += deltaDeg;
                peakDeg = Math.Max(peakDeg, Math.Abs(netDeg));
                prevHeadingDeg = headingDeg;
                finalHeadingDeg = headingDeg;
                closestToTaxiwayADeg = Math.Min(closestToTaxiwayADeg, Math.Abs(GeoMath.SignedBearingDifference(headingDeg, TaxiwayABearingDeg)));

                var route = aircraft.Ground.AssignedTaxiRoute;
                int segmentIndex = route?.CurrentSegmentIndex ?? -1;
                int segmentCount = route?.Segments.Count ?? 0;

                finalPhaseName = aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
                output.WriteLine(
                    $"t={t, 4} hdg={headingDeg, 6:F1} d={deltaDeg, 6:F1} net={netDeg, 7:F1} peak={peakDeg, 6:F1} run={runDeg, 7:F1} "
                        + $"maxRun={maxRunDeg, 6:F1} ias={aircraft.IndicatedAirspeed, 5:F1} seg={segmentIndex}/{segmentCount} "
                        + $"phase={finalPhaseName} nearestNodes=[{NearestNodeHelper.Describe(aircraft, layout)}]"
                );
            }

            output.WriteLine(
                $"window [{startSeconds}..{replayToSeconds}]: net={netDeg:F1}° peak={peakDeg:F1}° maxMonotoneRun={maxRunDeg:F1}° "
                    + $"finalHdg={finalHeadingDeg:F1}° closestToTwyA={closestToTaxiwayADeg:F1}° finalPhase={finalPhaseName}"
            );

            return new WindowResult(netDeg, peakDeg, maxRunDeg, closestToTaxiwayADeg, finalPhaseName);
        }
    }
}
