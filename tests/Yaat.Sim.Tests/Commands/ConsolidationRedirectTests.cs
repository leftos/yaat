using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The consolidation redirect inside the Sim track table: a handoff or point-out addressed to an unattended TCP lands on
/// the attended position whose airspace has absorbed it, and a handoff recipient who re-addresses an inbound handoff
/// re-points it. Attendance is the host's answer, so these run the <c>Track</c> arm with a host that controls it.
/// Real NCT hierarchy: <c>4Q</c> under <c>4U</c> under <c>2B</c>.
/// </summary>
public class ConsolidationRedirectTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ConsolidationRedirectTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc(),
                Owner = Nct4U,
                Tcp = TrackResolver.FindTcpByCode(scenario, "4U")!,
            }
        );
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc(),
                Owner = Nct4Q,
                Tcp = TrackResolver.FindTcpByCode(scenario, "4Q")!,
            }
        );
        return engine;
    }

    private static RecordedCommand Recorded(string command) => new(0, AiTestFixture.Callsign, command, "XX", "conn-1");

    /// <summary>4Q's airspace is combined into 4U by a manual override; whether 4U is attended is the host's answer.</summary>
    private static AttendanceActionHost CombineFourQIntoFourU(SimulationEngine engine, bool fourUAttended)
    {
        var scenario = engine.Scenario!;
        var fourU = TrackResolver.FindTcpByCode(scenario, "4U")!;
        var fourQ = TrackResolver.FindTcpByCode(scenario, "4Q")!;
        Assert.True(engine.ConsolidationState.Consolidate(fourU, fourQ, basic: true));
        var host = new AttendanceActionHost();
        if (fourUAttended)
        {
            host.AttendedTcpIds.Add(fourU.Id);
        }

        return host;
    }

    [Fact]
    public void Handoff_ToAnUnattendedTcp_RedirectsToItsAttendedConsolidationOwner()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = CombineFourQIntoFourU(engine, fourUAttended: true);
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        var outcome = engine.Actions.Apply(Recorded("HO 4Q"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal($"Handoff {AiTestFixture.Callsign} to 4Q (redirected to 4U)", outcome.Result.Message);
        Assert.True(ac.Track.HandoffPeer!.MatchesPosition(Nct4U));
        Assert.True(ac.Track.HandoffRedirectedBy!.MatchesPosition(Nct4Q));
        Assert.Equal(engine.Scenario!.ElapsedSeconds, ac.Track.HandoffInitiatedAt);
    }

    [Fact]
    public void Handoff_WhenNobodyIsAttended_IsNotRedirected()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = CombineFourQIntoFourU(engine, fourUAttended: false);
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        var outcome = engine.Actions.Apply(Recorded("HO 4Q"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal($"Handoff {AiTestFixture.Callsign} to 4Q", outcome.Result.Message);
        Assert.True(ac.Track.HandoffPeer!.MatchesPosition(Nct4Q));
        Assert.Null(ac.Track.HandoffRedirectedBy);
    }

    [Fact]
    public void Handoff_ReaddressedByTheRecipient_RepointsTheInboundHandoff()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4Q;
        ac.Track.HandoffPeer = Student;

        var outcome = engine.Actions.Apply(Recorded("HO 4U"), new AttendanceActionHost());

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal($"Redirected handoff {AiTestFixture.Callsign} to 4U", outcome.Result.Message);
        Assert.True(ac.Track.HandoffPeer!.MatchesPosition(Nct4U));
        Assert.True(ac.Track.HandoffRedirectedBy!.MatchesPosition(Student));
        Assert.True(ac.Track.Owner!.MatchesPosition(Nct4Q));
    }

    [Fact]
    public void PointOut_ToAnUnattendedTcp_LandsOnItsAttendedConsolidationOwner()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = CombineFourQIntoFourU(engine, fourUAttended: true);
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        var outcome = engine.Actions.Apply(Recorded("PO 4Q"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("4U", ac.Track.Pointout!.Recipient.ToString());
        Assert.Equal("2B", ac.Track.Pointout.Sender.ToString());
    }
}
