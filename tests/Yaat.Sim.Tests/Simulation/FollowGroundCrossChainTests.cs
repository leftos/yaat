using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end cover for the <c>FOLLOWG X; CROSS &lt;rwy&gt;</c> idiom that
/// <see cref="Yaat.Sim.Commands.CommandDescriber.InstallsIndefiniteHoldPhase"/> deliberately does not warn about
/// (pinned at the predicate level by <c>IndefiniteHoldMarkerTests</c>): <see cref="FollowingPhase"/> self-completes
/// when the follower reaches a runway hold-short, dropping it into <see cref="HoldingShortPhase"/> — an
/// <c>IsIdleAwaitingCommands</c> phase where the queued <c>CROSS</c> block fires. Until then the block must sit in
/// the queue and the follower must not enter the runway.
///
/// Recording: S2-OAK-P (the FOLLOWG-from-parking fixture). At t=205 FTH399 is parked at KAI7 and KPO83 is taxiing
/// its <c>TAXI C B W HS 28R RWY 30</c> clearance, which holds it short of 28R with the crossing un-cleared.
/// </summary>
public class FollowGroundCrossChainTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/followg-from-parking-recording.zip";
    private const string Follower = "FTH399";
    private const string Lead = "KPO83";
    private const int ReplayTime = 205;
    private const int TickBudgetSeconds = 300;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void FollowGroundThenCross_FiresTheCrossOnlyWhenTheFollowerReachesTheHoldShort()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayTime);

        var follower = engine.FindAircraft(Follower);
        var lead = engine.FindAircraft(Lead);
        Assert.NotNull(follower);
        Assert.NotNull(lead);
        Assert.IsType<AtParkingPhase>(follower.Phases?.CurrentPhase);
        Assert.IsType<TaxiingPhase>(lead.Phases?.CurrentPhase);

        // (1) The chain is accepted.
        var result = engine.SendCommand(Follower, "FOLLOWG KPO83; CROSS 28R");
        output.WriteLine($"FOLLOWG KPO83; CROSS 28R -> success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"chain should be accepted but got: {result.Message}");

        follower = engine.FindAircraft(Follower);
        Assert.NotNull(follower);
        Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);

        var blocks = follower.Queue.Blocks;
        output.WriteLine($"queue blocks={blocks.Count} index={follower.Queue.CurrentBlockIndex}");
        for (int i = 0; i < blocks.Count; i++)
        {
            output.WriteLine($"  block[{i}] applied={blocks[i].IsApplied} cmds={string.Join(",", blocks[i].Commands.Select(c => c.Type))}");
        }

        // The FOLLOWG applies at issue time and installs the phase; only the CROSS remains queued behind it.
        Assert.Single(blocks);
        var crossBlock = blocks[0];
        Assert.False(crossBlock.IsApplied, "the CROSS block must be queued, not fired, while the follower is still following");

        var runway28R = RunwayOccupancy.AirportRunways("OAK").FirstOrDefault(r => r.Id.Contains("28R"));
        Assert.NotNull(runway28R);

        var layout = follower.Ground.Layout ?? lead.Ground.Layout;
        Assert.NotNull(layout);
        var holdShorts28R = layout.GetRunwayHoldShortNodes("28R");
        Assert.NotEmpty(holdShorts28R);

        // The lead's own crossing of 28R is un-cleared, so it stops at the bar rather than taxiing through.
        var leadRoute = lead.Ground.AssignedTaxiRoute;
        Assert.NotNull(leadRoute);
        var lead28R = leadRoute.HoldShortPoints.FirstOrDefault(h => (h.TargetName is { } n) && RunwayIdentifier.Parse(n).Contains("28R"));
        Assert.NotNull(lead28R);
        Assert.False(lead28R.IsCleared, "the lead's 28R crossing must be un-cleared so it holds short");

        int crossAppliedAt = -1;
        int holdShortSeenAt = -1;
        int crossingPhaseAt = -1;
        bool onPavementWhileFollowing = false;
        bool leadCrossed = false;
        double followerSpeedAtCross = double.NaN;
        double followerDistanceToBarAtCross = double.NaN;

        for (int t = 1; t <= TickBudgetSeconds; t++)
        {
            engine.TickOneSecond();

            follower = engine.FindAircraft(Follower);
            lead = engine.FindAircraft(Lead);
            Assert.NotNull(follower);
            Assert.NotNull(lead);

            var phase = follower.Phases?.CurrentPhase;

            // (4) While still following, the follower must not be on the runway it has not been cleared across.
            if ((phase is FollowingPhase) && RunwayOccupancy.IsOnPavement(follower, runway28R))
            {
                onPavementWhileFollowing = true;
            }

            // (2) The CROSS block stays queued for as long as the follower is following the lead.
            if ((phase is FollowingPhase) && crossBlock.IsApplied && crossAppliedAt < 0)
            {
                Assert.Fail($"t={t}: CROSS fired while the follower was still in FollowingPhase");
            }

            // Diagnostic only: the hold-short is entered and cleared inside one simulated second, so a
            // per-second sample usually misses HoldingShortPhase entirely (reported as -1 below).
            if ((holdShortSeenAt < 0) && (phase is HoldingShortPhase))
            {
                holdShortSeenAt = t;
            }

            if (lead.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                leadCrossed = true;
            }

            if ((crossAppliedAt < 0) && crossBlock.IsApplied)
            {
                crossAppliedAt = t;
                followerSpeedAtCross = follower.GroundSpeed;
                followerDistanceToBarAtCross = holdShorts28R.Min(n =>
                    GeoMath.DistanceNm(follower.Position.Lat, follower.Position.Lon, n.Position.Lat, n.Position.Lon)
                );
            }

            if ((crossingPhaseAt < 0) && (phase is CrossingRunwayPhase))
            {
                crossingPhaseAt = t;
            }

            if ((t % 10 == 0) || (crossAppliedAt == t) || (crossingPhaseAt == t) || (holdShortSeenAt == t))
            {
                double gap = GeoMath.DistanceNm(follower.Position.Lat, follower.Position.Lon, lead.Position.Lat, lead.Position.Lon);
                output.WriteLine(
                    $"t={t}: follower={phase?.Name ?? "null"} gs={follower.GroundSpeed:F1} gapToLead={gap:F3}nm "
                        + $"| lead={lead.Phases?.CurrentPhase?.Name ?? "null"} gs={lead.GroundSpeed:F1} "
                        + $"| crossApplied={crossBlock.IsApplied} queueIndex={follower.Queue.CurrentBlockIndex}"
                );
            }

            if (crossingPhaseAt > 0)
            {
                break;
            }
        }

        output.WriteLine(
            $"holdShortSeenAt={holdShortSeenAt} crossAppliedAt={crossAppliedAt} crossingPhaseAt={crossingPhaseAt} "
                + $"gsAtCross={followerSpeedAtCross:F1} distToBarAtCross={followerDistanceToBarAtCross:F4}nm"
        );

        // (4) Neither aircraft entered 28R before a crossing clearance fired.
        Assert.False(onPavementWhileFollowing, "the follower entered runway 28R while still following, before any crossing clearance fired");
        Assert.False(leadCrossed, "the lead crossed 28R although its crossing was never cleared");

        // (3) The CROSS fired at the 28R hold-short, with the follower stopped behind the lead, and put it into the crossing.
        Assert.True(crossAppliedAt > 0, $"the queued CROSS never fired within {TickBudgetSeconds}s");
        Assert.True(crossingPhaseAt > 0, $"the follower never started crossing within {TickBudgetSeconds}s");
        // ~180 ft: the follower stops at the bar it triggered on, so anything larger means it fired somewhere
        // other than a 28R hold-short (mid-taxi, or at another runway's bar).
        Assert.True(
            followerDistanceToBarAtCross < 0.03,
            $"the CROSS fired {followerDistanceToBarAtCross:F4} nm from the nearest 28R hold-short, not at the bar"
        );
        // The block fires the second the follower reaches the bar, which can be the last second of its decel: 0.0 kt on
        // Windows, 1.4 kt on Linux CI for the same replay. Pin "slowed to the bar", not "already stationary".
        Assert.True(followerSpeedAtCross < 5.0, $"the follower was still taxiing ({followerSpeedAtCross:F1} kt) when the CROSS fired");
    }
}
