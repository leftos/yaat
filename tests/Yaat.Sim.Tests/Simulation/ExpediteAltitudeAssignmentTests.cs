using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the <c>EXP &lt;alt&gt;</c> altitude argument (bundle
/// "S2-OAK-5 (1) | Practical Exam Preparation/Advanced Concepts", ZOA). N2BP is a VFR SR22
/// that spawns at t=1950 descending from 3,825 ft toward its assigned 2,000 ft.
///
/// Observed bug: <c>EXP 014</c> at t=2091 was accepted ("Expedite climb/descent through
/// 1,400") but left the 2,000 ft assignment untouched — the argument only armed a queued
/// "resume normal rate at 1,400" block, and 1,400 was past the clearance, so the aircraft
/// levelled at 2,000 with a block that could never fire. Reissuing it at t=2169 was then
/// rejected with "Expedite requires an active altitude assignment", because
/// <c>TargetAltitude</c> self-nulls on level-off even though <c>AssignedAltitude</c> was
/// still 2,000.
///
/// Expected: <c>EXP 014</c> assigns 1,400 and expedites down to it — matching the
/// Intellisense hint ("Expedite climb/descent to altitude") and the speech rule that maps
/// "expedite descent to {alt}" onto this command.
///
/// Replay strategy: hybrid. N2BP is generator-spawned two thirds of the way through the
/// session, and this change alters command semantics before the assertion point, so the
/// snapshot pins the setup. The command is sent out-of-band rather than replayed: the
/// recorded actions include the <c>CM 014</c> workaround at t=2175, which would mask the
/// assertion.
/// </summary>
public class ExpediteAltitudeAssignmentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/exp-altitude-assignment-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N2BP";
    private const int DescendingTime = 2085;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("FlightCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void ExpediteWithAltitude_ReclearsBelowTheCurrentAssignment()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(DescendingTime);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: mid-descent toward 2,000 — the state the controller was looking at.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.Equal(2000, pre.Targets.AssignedAltitude);
            Assert.True(pre.Altitude > 2500, $"expected N2BP above 2,500 at t={DescendingTime}, was {pre.Altitude:F0}");

            var result = engine.SendCommand(Callsign, "EXP 014");
            output.WriteLine($"EXP 014 -> Success={result.Success} Message='{result.Message}'");
            Assert.True(result.Success, result.Message);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.Equal(1400, ac.Targets.AssignedAltitude);
            Assert.True(ac.Procedure.IsExpediting);

            // Physics only — the recorded CM 014 / DCT VPMID must not replay.
            double expeditedVs = 0;
            for (int t = 1; t <= 200; t++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);

                if (t == 10)
                {
                    expeditedVs = ac.VerticalSpeed;
                }

                if (t % 20 == 0)
                {
                    output.WriteLine($"t=+{t} alt={ac.Altitude:F0} vs={ac.VerticalSpeed:F0} aAlt={ac.Targets.AssignedAltitude}");
                }
            }

            // The SR22's 500 fpm descent, doubled and raised to the 1,000 fpm piston expedite floor.
            Assert.InRange(expeditedVs, -1050, -950);
            Assert.Equal(1400, ac.Altitude, 1);
        }
    }
}
