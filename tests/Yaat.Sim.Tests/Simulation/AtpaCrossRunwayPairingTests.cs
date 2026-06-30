using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the cross-runway ATPA cone bug: a STARS ATPA in-trail cone fired between two arrivals
/// on DIFFERENT finals at KOAK. Recording: S2-OAK-2 (ZOA / NCT) — FDX858 (B763) on final to runway 30
/// and N474TK (C152) on final to runway 28R. The two finals are only ~18 deg apart, and the OAK 30
/// ATPA volume is geometrically wide (MaximumHeadingDeviation 90 deg, ~3 nm left half-width toward the
/// 28R final), so the 28R arrival falls inside BOTH the OAK 28 and OAK 30 volumes and gets paired with
/// the runway-30 arrival inside OAK 30. ATPA in-trail is strictly per-final (7110.65 5-9-5/5-9-6): two
/// aircraft on different runways must never be coned against each other. Each established aircraft must
/// associate to exactly one (best-fit) volume so the cross-runway pair never forms.
/// </summary>
public class AtpaCrossRunwayPairingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/atpa-cross-runway-recording.yaat-bug-report-bundle.zip";
    private const string StudentFacilityId = "NCT";

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
    public void ArrivalsOnDifferentOakFinals_AreNotPairedByAtpa()
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
                output.WriteLine("No ZOA/NCT ATPA volumes in bundle — skipping");
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(776);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=776 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var aircraft = engine.World.GetSnapshot();
            var results = new AtpaProcessor().Process(aircraft, starsConfig.AtpaVolumes, starsConfig);

            foreach (var (callsign, r) in results)
            {
                output.WriteLine(
                    $"{callsign}: state={r.ConeState} target={r.TargetTrackId} allowed={r.AllowedSeparation:F1} actual={r.ActualSeparation:F1}"
                );
            }

            // FDX858 is established on the 30 final, N474TK on the 28R final — different finals. Neither
            // may be paired (in-trail cone) against the other. Before the single-volume-association fix,
            // the wide OAK 30 volume pulled N474TK in and FDX858 was coned against it.
            Assert.False(
                results.TryGetValue("FDX858", out var fdx) && fdx.TargetTrackId == "CALLSIGNN474TK",
                "FDX858 (runway 30) must not be ATPA-paired with N474TK (runway 28R) — different finals"
            );
            Assert.False(
                results.TryGetValue("N474TK", out var ntk) && ntk.TargetTrackId == "CALLSIGNFDX858",
                "N474TK (runway 28R) must not be ATPA-paired with FDX858 (runway 30) — different finals"
            );
        }
    }
}
