using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the extended-downwind-lead sequencing bug.
///
/// Recording: S2-OAK-3 "VFR Sequencing" (ZOA), OAK left traffic for 28R, two GA
/// aircraft. N346G is told to <c>FOLLOW</c> N436MS at t=179. N436MS enters
/// Downwind at t=315 and is given <c>EXT</c> at t=323 → <c>DownwindPhase.IsExtended
/// = true</c>; it then holds the downwind (never turns base) through the end of the
/// recording. N346G enters its own Downwind at t=390, still following N436MS.
///
/// Observed bug: at t=440 N346G turned Base at its fixed base-turn point while
/// N436MS was still extending its downwind ahead of it — so N346G would roll out on
/// final ahead of the very aircraft it was sequencing behind. Root cause:
/// <see cref="AirborneFollowHelper.ShouldHoldForLeadSequencing"/> is gated on
/// <c>IsLeadPatternFlowAhead</c>, which only returned true for a strictly later leg.
/// Both aircraft are on Downwind (same leg index), so the sequencing hold never
/// engaged and the follower turned base on schedule. (The runaway watchdog then
/// began accumulating and would have cancelled the follow too.)
///
/// Expected after fix: an extended-downwind lead counts as pattern-flow-ahead, so
/// N346G holds (extends) its downwind to stay behind N436MS rather than turning
/// base. Within the recording window N346G remains on Downwind the whole time.
/// </summary>
public class VfrFollowExtendedLeadBaseTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/vfr-follow-extended-lead-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N346G";
    private const string Leader = "N436MS";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Follower_HoldsDownwind_WhileLeadExtends()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Full replay from t=0: the fix only changes behavior from N346G's
        // base-turn decision onward (t>=440); the earlier trajectory (FOLLOW at
        // t=179, pattern join, Downwind entry at t=390) is unaffected and has no
        // WAIT presets, so the pre-bug state is reached faithfully.
        engine.Replay(recording, 430);

        var lead0 = engine.FindAircraft(Leader);
        var foll0 = engine.FindAircraft(Follower);
        Assert.NotNull(lead0);
        Assert.NotNull(foll0);
        output.WriteLine(
            $"t=430 lead={lead0.Phases?.CurrentPhase?.GetType().Name} "
                + $"ext={(lead0.Phases?.CurrentPhase as DownwindPhase)?.IsExtended} | "
                + $"foll={foll0.Phases?.CurrentPhase?.GetType().Name} "
                + $"follow={foll0.Approach.FollowingCallsign ?? "(null)"}"
        );

        // Sanity: full replay reached the expected pre-bug geometry.
        Assert.True(
            lead0.Phases?.CurrentPhase is DownwindPhase { IsExtended: true },
            $"Setup: N436MS should be on extended Downwind at t=430, was {lead0.Phases?.CurrentPhase?.GetType().Name}"
        );
        Assert.True(
            foll0.Phases?.CurrentPhase is DownwindPhase,
            $"Setup: N346G should be on Downwind at t=430, was {foll0.Phases?.CurrentPhase?.GetType().Name}"
        );

        // Step second-by-second through the buggy base-turn point (t=440) to the
        // end of the recording. While N436MS is still extending its downwind ahead,
        // N346G must remain on Downwind (holding to sequence behind it) and keep
        // following. On current code N346G turns Base at t=440 -> this fails.
        bool assertedAtLeastOnce = false;
        for (int now = 431; now <= 465; now++)
        {
            engine.ReplayOneSecond();
            var lead = engine.FindAircraft(Leader);
            var foll = engine.FindAircraft(Follower);
            Assert.NotNull(lead);
            Assert.NotNull(foll);

            var leadPhase = lead.Phases?.CurrentPhase;
            var follPhase = foll.Phases?.CurrentPhase;
            output.WriteLine(
                $"t={now} lead={leadPhase?.GetType().Name} "
                    + $"ext={(leadPhase as DownwindPhase)?.IsExtended} | "
                    + $"foll={follPhase?.GetType().Name} "
                    + $"follow={foll.Approach.FollowingCallsign ?? "(null)"}"
            );

            // Only assert while the lead is genuinely still extending its downwind.
            if (leadPhase is not DownwindPhase { IsExtended: true })
            {
                continue;
            }

            assertedAtLeastOnce = true;
            Assert.True(
                follPhase is DownwindPhase,
                $"t={now}: N346G turned {follPhase?.GetType().Name} while its lead N436MS is still "
                    + $"extending downwind — it should hold/extend behind it. "
                    + $"following={foll.Approach.FollowingCallsign ?? "(null)"}"
            );
            Assert.Equal(Leader, foll.Approach.FollowingCallsign);
        }

        Assert.True(assertedAtLeastOnce, "Lead never observed on extended Downwind in the window — recording/replay drift.");
    }
}
