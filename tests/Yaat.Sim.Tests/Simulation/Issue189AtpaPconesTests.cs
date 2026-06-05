using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #189: automatic ATPA ("P") cones never appeared in CRC STARS for
/// arrivals on final at ZHU facilities. Recording: S3-T1-L5 (I90_A_APP) — Houston TRACON (I90)
/// feeding IAH 8L/8R. At ~t=490 there are two in-trail pairs established on final: UCA1538 trailing
/// AAL3787 on 8R, and DAL1238 trailing DAL46 on 8L. The trailing aircraft must each receive an ATPA
/// pairing (cone state + target) from <see cref="AtpaProcessor"/> using the bundle's real ZHU ARTCC
/// config, which adapts the IAH volumes as AlertAndMonitor.
/// </summary>
public class Issue189AtpaPconesTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue189-atpa-pcones-recording.zip";
    private const string StudentFacilityId = "I90";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("AtpaProcessor", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void TrailingArrivalsOnIahFinal_ReceiveAtpaCones()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var starsConfig = archive.DeserializeArtccConfig()?.GetStarsConfigForFacility(StudentFacilityId);
            if (starsConfig is null || starsConfig.AtpaVolumes.Count == 0)
            {
                output.WriteLine("No ZHU/I90 ATPA volumes in bundle — skipping");
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(490);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=490 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var aircraft = engine.World.GetSnapshot();
            var results = new AtpaProcessor().Process(aircraft, starsConfig.AtpaVolumes, starsConfig);

            foreach (var (callsign, r) in results)
            {
                output.WriteLine(
                    $"{callsign}: state={r.ConeState} target={r.TargetTrackId} allowed={r.AllowedSeparation:F1} actual={r.ActualSeparation:F1} mon=[{string.Join(",", r.AtpaMonitorTcps)}] alert=[{string.Join(",", r.AtpaAlertTcps)}]"
                );
            }

            // Each trailing arrival receives an automatic cone pointing to the aircraft ahead on its OWN
            // runway: UCA1538 follows AAL3787 on 8R, DAL1238 follows DAL46 on 8L. The volumes must keep
            // the parallel approaches separate (no cross-runway pairing).
            Assert.True(results.ContainsKey("UCA1538"), "UCA1538 (8R trailing) should receive an ATPA cone");
            Assert.Equal("CALLSIGNAAL3787", results["UCA1538"].TargetTrackId);

            Assert.True(results.ContainsKey("DAL1238"), "DAL1238 (8L trailing) should receive an ATPA cone");
            Assert.Equal("CALLSIGNDAL46", results["DAL1238"].TargetTrackId);
        }
    }
}
