using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #286: scenario preset <c>CFIX TTE 140; AT TTE WAIT 170 DM 110; RNS</c> must hold N32BR at
/// 14,000 across TTE, wait 170 s, THEN descend to 11,000 — not descend the instant it crosses TTE.
///
/// Root cause: <c>InjectCfixImplicitAtCondition</c> stamped the CFIX's <c>AT TTE</c> trigger onto the
/// WAIT's <c>DM 110</c> payload, so WAIT/DM/RNS all fired together at TTE and the delay was dropped.
///
/// Recording: S3-FAT-3 (A) | Approaches 1 — N32BR (C68A) arriving FAT via TTE. The controller manually
/// re-issued <c>DM 110</c> at t=596, so we replay to just before TTE and then <see cref="SimulationEngine.TickOneSecond"/>
/// (pure sim, no recorded actions) to observe the preset's automatic behavior in isolation.
/// </summary>
public class Issue286WaitDescentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue286-cfix-wait-descent-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N32BR_HoldsAt14000ForWaitBeforeDescending()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // N32BR spawns ~t=275 with `CFIX TTE 140; AT TTE WAIT 170 DM 110; RNS`; it sequences TTE ~t=490.
        // Replay to just before that, then TickOneSecond so the controller's later manual DM 110 (t=596)
        // is NOT replayed and cannot mask the preset's automatic descent.
        engine.Replay(recording, 480);

        var initial = engine.FindAircraft("N32BR");
        Assert.NotNull(initial);
        Assert.Equal(14000, initial!.Targets.AssignedAltitude);
        bool tteInRoute = initial.Targets.NavigationRoute.Any(f => f.Name == "TTE");
        Assert.True(tteInRoute, "Expected TTE still in N32BR's route at t=480");

        int? tteSequencedAt = null;
        int? descendedAt = null;
        double? altAtTtePlus60 = null;

        for (int t = 481; t <= 700; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft("N32BR");
            if (ac is null)
            {
                continue;
            }

            bool inRoute = ac.Targets.NavigationRoute.Any(f => f.Name == "TTE");
            if (tteInRoute && !inRoute && tteSequencedAt is null)
            {
                tteSequencedAt = t;
                output.WriteLine($"TTE sequenced at t={t}: AssignedAltitude={ac.Targets.AssignedAltitude}");
            }
            tteInRoute = inRoute;

            if (tteSequencedAt is null)
            {
                continue;
            }

            int since = t - tteSequencedAt.Value;
            if (since == 60)
            {
                altAtTtePlus60 = ac.Targets.AssignedAltitude;
            }

            if (descendedAt is null && ac.Targets.AssignedAltitude == 11000)
            {
                descendedAt = t;
                output.WriteLine($"Descent to 11000 at t={t} ({since}s after TTE)");
                break;
            }
        }

        Assert.NotNull(tteSequencedAt);

        // Regression: 60 s past TTE is well inside the 170 s wait — the aircraft must NOT have descended
        // yet. Buggy code descends the instant TTE is crossed, so this is 11000 without the fix.
        Assert.Equal(14000, altAtTtePlus60);

        // The delayed descent must fire, and only after the ~170 s wait has elapsed.
        Assert.NotNull(descendedAt);
        int waited = descendedAt!.Value - tteSequencedAt!.Value;
        Assert.True(waited is >= 160 and <= 185, $"Descent fired {waited}s after TTE; expected ~170s (WAIT 170).");
    }
}
