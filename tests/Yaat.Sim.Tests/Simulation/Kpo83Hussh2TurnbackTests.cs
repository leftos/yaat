using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression for the KPO83 turn-back (KOAK HUSSH2 departure). Filed route
/// "HUSSH2 OAK SYRAH Q128 ...": the departure route builder inserted the on-field OAK VOR between
/// NIITE and SYRAH, reversing the aircraft ~140 degrees back over the airport. HUSSH2 is a fixed-path
/// RNAV SID; the correct route applies its published SYRAH enroute transition
/// (NIITE -> REBAS -> TAMMM -> SYRAH) and drops the redundant co-located OAK.
///
/// Hybrid replay: restore the recorded snapshot just before CTO (t=2339), replay through it so the
/// departure route is rebuilt by current code, then tick until the DepartureProcedure phase loads the
/// NavigationRoute. Ticking after replay avoids the instructor's corrective DCT SYRAH at t=2563.
/// Recording: S2-OAK-P Practical Exam, ARTCC ZOA, KOAK.
/// </summary>
public class Kpo83Hussh2TurnbackTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/kpo83-hussh2-turnback-recording.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void KPO83_AfterCto_DoesNotTurnBackThroughOakVor()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null || BuildEngine() is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            Assert.NotNull(engine);

            engine.Replay(recording, 0);
            var snap = archive.ReadSnapshotAt(2335); // just before CTO at t=2339
            Assert.NotNull(snap);

            engine.RestoreFromSnapshot(snap.State);
            int t = (int)snap.ElapsedSeconds;
            engine.FastForwardTo(t + 1, recording.Actions);
            t += 1;

            // Replay through CTO (t=2339) so the departure route is rebuilt by current code. Once past
            // the recorded commands, switch to plain ticking so the NavigationRoute can load without
            // applying the later corrective DCT SYRAH / DEL actions.
            IReadOnlyList<NavigationTarget> route = [];
            for (; t < 2460; t++)
            {
                if (t < 2385)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }

                var acLoop = engine.FindAircraft("KPO83");
                if (acLoop is not null && acLoop.Targets.NavigationRoute.Count > 0)
                {
                    route = acLoop.Targets.NavigationRoute;
                    break;
                }
            }

            var names = route.Select(f => f.Name).ToList();
            output.WriteLine($"KPO83 route @t={t}: [{string.Join(", ", names)}]");
            Assert.NotEmpty(names);

            // The on-field OAK VOR must never appear — routing through it is the turn-back over the field.
            Assert.DoesNotContain("OAK", names);

            // The published HUSSH2 SYRAH enroute transition must be flown.
            Assert.Contains("REBAS", names);
            Assert.Contains("TAMMM", names);
            Assert.Contains("SYRAH", names);
        }
    }
}
