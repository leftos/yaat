using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The YAAT-577 replay staging the <see cref="Oak577FollowBehindDepartingLeadReplayTests"/> classes share.
/// </summary>
public abstract class Oak577FollowBehindDepartingLeadReplayTestBase(ITestOutputHelper output)
{
    protected ITestOutputHelper Output { get; } = output;

    private protected const string RecordingPath = "TestData/oak576-push-te-parked-neighbour-recording.yaat-bug-report-bundle.zip";
    private protected const string Follower = "NKS2904";
    private protected const string Lead = "UPS947";
    private protected const string LeadRunway = "30";

    /// <summary>A second while UPS947 holds short of 30 at W1 on its own taxi route, before the CTO at t=876.</summary>
    private protected const double LeadHeldSecond = 870;

    /// <summary>One second before NKS2904 took its hold at the W1 bar in the recording (t=885).</summary>
    private protected const double BeforeHoldSecond = 884;

    /// <summary>A second while the follower still taxis up to the W1 bar, about 340 ft short of it, behind the lead lining up.</summary>
    private protected const double ShortOfTheBarSecond = 890;

    /// <summary>The last second the follower is given to take its hold.</summary>
    private protected const int HoldBudgetSecond = 960;

    /// <summary>Height (ft) above the field the lead is put at when it is staged as departed.</summary>
    private protected const double DepartedLeadHeightFt = 1500;

    private protected static void AssertDepartureHoldAtTheLeadsBar(HoldingShortPhase hold, int leadBarNodeId)
    {
        Assert.Equal(leadBarNodeId, hold.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal(LeadRunway, hold.HoldShort.TargetName);
    }

    /// <summary>Puts the lead in the air above the field with nothing left to fly: it has left the ground by departing.</summary>
    private protected void StageLeadDeparted(SimulationEngine engine)
    {
        AircraftState lead = Assert.IsType<AircraftState>(engine.FindAircraft(Lead));
        Output.WriteLine($"{Lead} staged departed from {lead.Phases?.CurrentPhase?.Name}");
        lead.Phases = new PhaseList();
        lead.IsOnGround = false;
        lead.Altitude += DepartedLeadHeightFt;
    }

    /// <summary>
    /// Loads the recording and replays to <see cref="LeadHeldSecond"/>, where the lead's taxi route names its departure bar, in a
    /// probe engine: a fresh engine, the recording, the OAK layout and that bar's node. Null on the repo's silent-skip path.
    /// </summary>
    private protected (SimulationEngine Engine, SessionRecording Recording, AirportGroundLayout Layout, int LeadBarNodeId)? Start()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("FOLLOWG behind a lead departing 30 from W1");
        if ((recording is null) || (TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        var probe = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        probe.Replay(recording, LeadHeldSecond);
        AircraftState lead = Assert.IsType<AircraftState>(probe.FindAircraft(Lead));
        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        HoldShortPoint departure = Assert.Single(leadRoute.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        Output.WriteLine(
            $"{Lead} at t={LeadHeldSecond}: {lead.Phases?.CurrentPhase?.Name}, route {leadRoute.ToSummary()}, departs node {departure.NodeId}"
        );

        return (new SimulationEngine(groundData, PushPlanStore.CreateShipped()), recording, layout, departure.NodeId);
    }

    /// <summary>
    /// Replays a second at a time from <paramref name="fromSecond"/> (or, with <paramref name="live"/> false, ticks a restored
    /// engine) until the follower holds short, to <see cref="HoldBudgetSecond"/>; its hold.
    /// </summary>
    private protected HoldingShortPhase TickUntilFollowerHolds(SimulationEngine engine, GroundNode leadBar, int fromSecond, bool live)
    {
        for (int t = fromSecond + 1; t <= HoldBudgetSecond; t++)
        {
            if (live)
            {
                engine.ReplayOneSecond();
            }
            else
            {
                engine.TickOneSecond();
            }

            AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
            AircraftState? lead = engine.FindAircraft(Lead);
            Output.WriteLine(
                $"t={t} {Follower} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                    + $"bar {GeoMath.DistanceNm(follower.Position, leadBar.Position) * GeoMath.FeetPerNm:F0} ft "
                    + $"{Lead} {lead?.Phases?.CurrentPhase?.Name} onGround={lead?.IsOnGround}"
            );
            if (follower.Phases?.CurrentPhase is HoldingShortPhase hold)
            {
                return hold;
            }
        }

        Assert.Fail($"{Follower} never held short by t={HoldBudgetSecond}");
        return null!;
    }
}
