using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #236: SFO "A F1 B" taxi weirdness.
///
/// <para>
/// At SFO, taxiways A and B are parallel and ~236 ft apart, joined by the
/// near-perpendicular short connector F1 (junction A/F1 = raw node #66,
/// F1/B = raw node #152). A clearance <c>TAXI A F1 B M1 1L</c> is a lane
/// change (S-turn): the aircraft should flow smoothly from A across to B.
/// </para>
///
/// <para>
/// Before the fix, the fillet graph models it as two independent ~90° corner
/// arcs with an ~86 ft straight F1 centerline between them, and the navigator
/// treats each corner in isolation. UAL1390 turns onto F1, fully aligns to
/// F1's centerline (heading ~94–102°) and <b>accelerates to ~20.6 kt</b> on the
/// straight middle, then makes a second ~84° turn onto B, braking back to ~5 kt
/// — the "slow down, full turn to align, then another huge turn" the reporter
/// described.
/// </para>
///
/// <para>
/// The fix (navigator short-connector transit) holds a steady low speed across
/// the connector instead of surging: peak IAS on F1 must stay well below the
/// ~20 kt spike. Replay is full-from-t0 (<see cref="SimulationEngine.Replay"/>
/// to just before the corner, then <see cref="SimulationEngine.ReplayOneSecond"/>
/// per tick) — the fix only changes behavior at the corner, but the aircraft's
/// route was assigned at t=1509, so a mid-session snapshot restore doesn't
/// resume the in-progress taxi; replaying every recorded action from t=0
/// faithfully drives UAL1390 down A behind traffic, onto F1, and reproduces the
/// surge.
/// </para>
/// </summary>
public class Issue236SfoAF1BConnectorTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue236-sfo-a-f1-b-recording.zip";
    private const string Callsign = "UAL1390";

    /// <summary>
    /// Replay to well before UAL1390 nears F1 (it is still on A behind traffic ~t=1600), then tick forward.
    /// Starting early makes the capture robust to the fix's global timing shift: the speed change ripples
    /// through the whole High-Intensity traffic flow, so UAL1390 reaches F1 at a slightly different time with
    /// vs without the fix. The loop captures the F1 transit whenever it happens rather than at a fixed tick.
    /// </summary>
    private const int ReplayToSeconds = 1600;

    /// <summary>End of the capture window — generous margin so the F1 transit is caught in either timeline.</summary>
    private const int AssertAtSeconds = 1730;

    /// <summary>
    /// Peak IAS ceiling on the F1 connector. The pre-fix surge peaks at ~20.6 kt;
    /// a steady low-speed flow-through stays near the ~5–8 kt corner speed. 10 kt
    /// leaves margin over the corner speed while still failing hard on the surge.
    /// </summary>
    private const double ConnectorPeakIasCeilingKts = 10.0;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void UAL1390_DoesNotSurgeAcrossF1Connector()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("SKIP: recording, navdata, or SFO layout not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            // Full replay from t=0 to just before the F1 corner, then tick per second.
            engine.Replay(recording, ReplayToSeconds);

            double peakIasOnF1 = 0.0;
            int f1Ticks = 0;
            bool wasOnF1 = false;
            bool reachedBravoAfterF1 = false;

            for (int t = ReplayToSeconds; t < AssertAtSeconds; t++)
            {
                engine.ReplayOneSecond();
                var ac = engine.FindAircraft(Callsign);
                if (ac is null)
                {
                    break;
                }

                var route = ac.Ground?.AssignedTaxiRoute;
                var curSeg = route?.CurrentSegment;
                string? twy = curSeg?.TaxiwayName;
                double ias = ac.IndicatedAirspeed;
                double hdg = ac.TrueHeading.Degrees;
                int segIdx = route?.CurrentSegmentIndex ?? -1;

                if (twy == "F1")
                {
                    wasOnF1 = true;
                    f1Ticks++;
                    peakIasOnF1 = System.Math.Max(peakIasOnF1, ias);
                }
                if (twy == "B" && wasOnF1)
                {
                    reachedBravoAfterF1 = true;
                }

                if (twy is "F1" or "B" || (segIdx >= 0 && ias > 0.1))
                {
                    output.WriteLine(
                        $"t={t + 1} seg={segIdx} twy={twy ?? "-"} ias={ias:F1} hdg={hdg:F0} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
                    );
                }

                if (reachedBravoAfterF1 && twy != "F1")
                {
                    break; // captured the whole F1 transit; stop early
                }
            }

            output.WriteLine("");
            output.WriteLine($"summary: f1Ticks={f1Ticks} peakIasOnF1={peakIasOnF1:F1}kt reachedBravoAfterF1={reachedBravoAfterF1}");

            Assert.True(f1Ticks > 0, "UAL1390 should transit the F1 connector within the window");
            Assert.True(reachedBravoAfterF1, "UAL1390 should complete A→F1→B and reach taxiway B (not stall on F1) within the window");
            Assert.True(
                peakIasOnF1 <= ConnectorPeakIasCeilingKts,
                $"UAL1390 should flow through the short F1 connector at a steady low speed, not surge. "
                    + $"Peak IAS on F1 was {peakIasOnF1:F1} kt (ceiling {ConnectorPeakIasCeilingKts:F1} kt). "
                    + "Pre-fix, the isolated per-corner speed profile lets it accelerate to ~20 kt on the ~86 ft "
                    + "straight middle of F1 and brake back down."
            );
        }
    }
}
