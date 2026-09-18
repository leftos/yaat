using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The three track-automation spine steps on the bare engine: delayed handoffs fire in pre-physics, auto-accept and
/// the point-out timeout run post-physics. They decide from engine state alone — the scenario's queue and delay,
/// the recorded CRC attendance, the consolidation hierarchy — so a plain <see cref="SimulationEngine.TickOneSecond"/>
/// over the real OAK data reaches every gate the live room reaches. Real NCT hierarchy from the ZOA config: <c>4U</c>
/// is the parent of <c>4Q</c>, and the student works <c>2B</c>.
/// </summary>
public class TrackAutomationStepTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TrackAutomationStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;

        // The scenario's ATC positions are what resolves a handoff target to a TCP, and therefore what lets the
        // attendance and consolidation gates see the target at all.
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

    /// <summary>The lines the room would print, captured as the engine emits them.</summary>
    private static List<string> CaptureTerminal(SimulationEngine engine)
    {
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry => lines.Add(entry.Message);
        return lines;
    }

    private static AircraftState Owned(SimulationEngine engine, TrackOwner owner)
    {
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        aircraft.Track.Owner = owner;
        return aircraft;
    }

    [Fact]
    public void DelayedHandoffFiresAtItsSecond()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);
        scenario.DelayedHandoffQueue.Add(
            new DelayedHandoff
            {
                Callsign = AiTestFixture.Callsign,
                Target = Nct4U,
                FireAtSeconds = 3,
            }
        );

        AiTestFixture.Tick(engine, 2);

        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.Single(scenario.DelayedHandoffQueue);

        AiTestFixture.Tick(engine, 1);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.HandoffPeer!.MatchesPosition(Nct4U));
        Assert.Equal(3, aircraft.Track.HandoffInitiatedAt);
        Assert.Empty(scenario.DelayedHandoffQueue);
        Assert.Contains(lines, line => line.Contains("[AutoTrack] Delayed handoff initiated", StringComparison.Ordinal));
    }

    [Fact]
    public void DelayedHandoffToATargetConsolidatedUnderAnAttendedTcpIsHeld()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        AircraftState aircraft = Owned(engine, Student);
        AttendanceTestSupport.Attend(engine, "4U");
        scenario.DelayedHandoffQueue.Add(
            new DelayedHandoff
            {
                Callsign = AiTestFixture.Callsign,
                Target = Nct4Q,
                FireAtSeconds = 3,
            }
        );

        AiTestFixture.Tick(engine, 6);

        // 4Q is unattended, so its airspace is worked by 4U — handing it off would be a handoff to the controller
        // who already owns it.
        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.Single(scenario.DelayedHandoffQueue);

        AttendanceTestSupport.Attend(engine, "4U", "4Q");
        AiTestFixture.Tick(engine, 1);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.HandoffPeer!.MatchesPosition(Nct4Q));
        Assert.Empty(scenario.DelayedHandoffQueue);
    }

    [Fact]
    public void PendingHandoffToAnUnattendedPositionAutoAcceptsAfterTheDelay()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 4);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));

        AiTestFixture.Tick(engine, 1);

        Assert.True(aircraft.Track.Owner!.MatchesPosition(Nct4U));
        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.Null(aircraft.Track.HandoffInitiatedAt);
        Assert.Null(aircraft.Track.HandoffRedirectedBy);
        Assert.True(aircraft.Track.HandoffAccepted);
        Assert.NotNull(aircraft.Eram.RecentHandoffPreviousOwner);
        Assert.True(aircraft.Eram.RecentHandoffPreviousOwner!.MatchesPosition(Student));
        Assert.Contains(lines, line => line.Contains("[AutoAccept] Handoff accepted", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingHandoffToAnAttendedPositionIsLeftForTheHuman()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AttendanceTestSupport.Attend(engine, "4U");
        AircraftState aircraft = Owned(engine, Student);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void SoloModeLeavesTheStudentsOwnHandoffPendingAndFloorsTheDelayAtThreeSeconds()
    {
        if (Engine() is not { } toStudent)
        {
            return;
        }

        SimScenarioState studentScenario = toStudent.Scenario!;
        studentScenario.SoloTrainingMode = true;
        studentScenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState handedToStudent = Owned(toStudent, Nct4U);
        handedToStudent.Track.HandoffPeer = Student;
        handedToStudent.Track.HandoffInitiatedAt = studentScenario.ElapsedSeconds;

        AiTestFixture.Tick(toStudent, 10);

        Assert.NotNull(handedToStudent.Track.HandoffPeer);
        Assert.True(handedToStudent.Track.Owner!.MatchesPosition(Nct4U));

        SimulationEngine toAi = Engine()!;
        SimScenarioState aiScenario = toAi.Scenario!;
        aiScenario.SoloTrainingMode = true;
        aiScenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState handedToAi = Owned(toAi, Student);
        handedToAi.Track.HandoffPeer = Nct4U;
        handedToAi.Track.HandoffInitiatedAt = aiScenario.ElapsedSeconds;

        AiTestFixture.Tick(toAi, 2);

        Assert.NotNull(handedToAi.Track.HandoffPeer);

        AiTestFixture.Tick(toAi, 1);

        Assert.Null(handedToAi.Track.HandoffPeer);
        Assert.True(handedToAi.Track.Owner!.MatchesPosition(Nct4U));
    }

    [Fact]
    public void AutoAcceptDisabledOutsideSoloDoesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState aircraft = Owned(engine, Student);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void LiveFeedOwnerIsNeverAutoAccepted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        aircraft.Track.SetOwnerFromLiveFeed(Student);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    /// <summary>
    /// Nobody answers for an absent controller: after the no-action interval the point-out is withdrawn and the
    /// sender is told to coordinate verbally (7110.65 §5-4-7.a.1.(a)), never acknowledged on the recipient's behalf.
    /// </summary>
    [Fact]
    public void PendingPointoutToAnUnattendedRecipientIsWithdrawnAfterThirtySeconds()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);
        Tcp recipient = TrackResolver.FindTcpByCode(scenario, "4U")!;
        aircraft.Track.Pointout = new StarsPointout(recipient, scenario.StudentTcp!) { InitiatedAt = scenario.ElapsedSeconds };

        AiTestFixture.Tick(engine, 29);

        Assert.True(aircraft.Track.Pointout!.IsPending);

        AiTestFixture.Tick(engine, 1);

        Assert.Null(aircraft.Track.Pointout);
        Assert.Contains(lines, line => line.Contains("[Pointout] ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("point-out withdrawn, coordinate verbally", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("[AutoAck]", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingPointoutToAnAttendedRecipientIsLeftForTheHuman()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AttendanceTestSupport.Attend(engine, "4U");
        AircraftState aircraft = Owned(engine, Student);
        Tcp recipient = TrackResolver.FindTcpByCode(scenario, "4U")!;
        aircraft.Track.Pointout = new StarsPointout(recipient, scenario.StudentTcp!) { InitiatedAt = scenario.ElapsedSeconds };

        AiTestFixture.Tick(engine, 40);

        Assert.True(aircraft.Track.Pointout!.IsPending);
    }

    [Fact]
    public void SoloModeLeavesAPointoutToTheStudentPending()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        // The student is the receiving controller of §5-4-7.b: they answer a point-out addressed to their own sector
        // by hand, exactly as they accept their own handoff, and nothing withdraws it out from under them.
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState aircraft = Owned(engine, Nct4U);
        aircraft.Track.Pointout = new StarsPointout(scenario.StudentTcp!, TrackResolver.FindTcpByCode(scenario, "4U")!)
        {
            InitiatedAt = scenario.ElapsedSeconds,
        };

        AiTestFixture.Tick(engine, 40);

        Assert.True(aircraft.Track.Pointout!.IsPending);
    }

    /// <summary>
    /// A room whose <see cref="SimScenarioState.StudentTcp"/> never resolved still knows the student's position: the
    /// recipient resolves back to it, so the student's own point-out stays pending for them.
    /// </summary>
    [Fact]
    public void SoloModeLeavesThePointoutPendingWhenOnlyTheStudentPositionResolves()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        Tcp studentTcp = scenario.StudentTcp!;
        Tcp sender = TrackResolver.FindTcpByCode(scenario, "4U")!;
        scenario.StudentTcp = null;
        AircraftState aircraft = Owned(engine, Nct4U);
        aircraft.Track.Pointout = new StarsPointout(studentTcp, sender) { InitiatedAt = scenario.ElapsedSeconds };

        AiTestFixture.Tick(engine, 40);

        Assert.True(aircraft.Track.Pointout!.IsPending);
    }

    [Fact]
    public void SoloModeWithdrawsAPointoutToAnAiPositionAfterThirtySeconds()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState aircraft = Owned(engine, Student);
        aircraft.Track.Pointout = new StarsPointout(TrackResolver.FindTcpByCode(scenario, "4U")!, scenario.StudentTcp!)
        {
            InitiatedAt = scenario.ElapsedSeconds,
        };

        AiTestFixture.Tick(engine, 29);

        Assert.True(aircraft.Track.Pointout!.IsPending);

        AiTestFixture.Tick(engine, 1);

        Assert.Null(aircraft.Track.Pointout);
    }

    /// <summary>
    /// The enable gate is unchanged by the timeout: outside solo mode a room that disabled auto-accept keeps its
    /// point-outs flashing forever, the way a real STARS display does.
    /// </summary>
    [Fact]
    public void AutoAcceptDisabledOutsideSoloLeavesPointoutsPendingForever()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState aircraft = Owned(engine, Student);
        aircraft.Track.Pointout = new StarsPointout(TrackResolver.FindTcpByCode(scenario, "4U")!, scenario.StudentTcp!)
        {
            InitiatedAt = scenario.ElapsedSeconds,
        };

        AiTestFixture.Tick(engine, 60);

        Assert.True(aircraft.Track.Pointout!.IsPending);
    }
}
