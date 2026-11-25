using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The three track-automation spine steps on the bare engine: delayed handoffs fire in pre-physics, auto-accept and
/// the solo point-out approval run post-physics. They decide from engine state alone — the scenario's queue and delay,
/// the recorded CRC attendance, the consolidation hierarchy — so a plain <see cref="SimulationEngine.TickOneSecond"/>
/// over the real OAK data reaches every gate the live room reaches. Real NCT hierarchy from the ZOA config: <c>4U</c>
/// is the parent of <c>4Q</c>, and the student works <c>2B</c>.
/// </summary>
public class TrackAutomationStepTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");
    private static readonly TrackOwner Zoa14 = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");

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

    /// <summary>
    /// An airborne track near OAK, held at <paramref name="feetAboveField"/> above the field elevation the ERAM coverage
    /// check resolves for it, owned by <paramref name="owner"/>.
    /// </summary>
    private static AircraftState AirborneOwned(SimulationEngine engine, TrackOwner owner, double feetAboveField)
    {
        AircraftState aircraft = AiTestFixture.Airborne("AAL1", 37.75, -122.25, 0);
        aircraft.Altitude = FieldElevationResolver.Resolve(aircraft, NavigationDatabase.Instance) + feetAboveField;
        aircraft.Targets.TargetAltitude = aircraft.Altitude;
        aircraft.Track.Owner = owner;
        engine.World.AddAircraft(aircraft);
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
        AircraftState aircraft = AirborneOwned(engine, Student, 5000);
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
        AircraftState aircraft = AirborneOwned(engine, Student, 5000);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void SoloModeLeavesTheStudentsOwnHandoffPendingAndFloorsTheDelayAtFiveSeconds()
    {
        if (Engine() is not { } toStudent)
        {
            return;
        }

        SimScenarioState studentScenario = toStudent.Scenario!;
        studentScenario.SoloTrainingMode = true;
        studentScenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState handedToStudent = AirborneOwned(toStudent, Nct4U, 5000);
        handedToStudent.Track.HandoffPeer = Student;
        handedToStudent.Track.HandoffInitiatedAt = studentScenario.ElapsedSeconds;

        AiTestFixture.Tick(toStudent, 10);

        Assert.NotNull(handedToStudent.Track.HandoffPeer);
        Assert.True(handedToStudent.Track.Owner!.MatchesPosition(Nct4U));

        SimulationEngine toAi = Engine()!;
        SimScenarioState aiScenario = toAi.Scenario!;
        aiScenario.SoloTrainingMode = true;
        aiScenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState handedToAi = AirborneOwned(toAi, Student, 5000);
        handedToAi.Track.HandoffPeer = Nct4U;
        handedToAi.Track.HandoffInitiatedAt = aiScenario.ElapsedSeconds;

        AiTestFixture.Tick(toAi, 4);

        Assert.NotNull(handedToAi.Track.HandoffPeer);

        AiTestFixture.Tick(toAi, 1);

        Assert.Null(handedToAi.Track.HandoffPeer);
        Assert.True(handedToAi.Track.Owner!.MatchesPosition(Nct4U));
    }

    [Fact]
    public void OnGroundHandoffIsNeverAutoAccepted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.True(aircraft.IsOnGround);
        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void HandoffBelowEramCoverageFloorIsNeverAutoAccepted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = AirborneOwned(engine, Student, 1000);
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void HandoffAboveEramCoverageFloorAutoAccepts()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = AirborneOwned(engine, Student, 2000);
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 5);

        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Zoa14));
    }

    [Fact]
    public void QtCoastTrackHandoffIsNeverAutoAccepted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = AirborneOwned(engine, Student, 5000);
        AircraftEramState eram = aircraft.Eram;
        eram.IsCoastTrack = true;
        eram.CoastLat = aircraft.Position.Lat;
        eram.CoastLon = aircraft.Position.Lon;
        eram.CoastStartSeconds = scenario.ElapsedSeconds;
        eram.CoastAltitude = (int)(aircraft.Altitude / 100);
        eram.CoastSpeed = 250;
        eram.CoastTrueCourse = 90;
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.True(aircraft.Eram.IsCoastTrack);
        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void GuardAppliesToStarsHandoffs()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = AirborneOwned(engine, Student, 1000);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
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
        AircraftState aircraft = AirborneOwned(engine, Student, 5000);
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
        AircraftState aircraft = AirborneOwned(engine, Student, 5000);
        aircraft.Track.SetOwnerFromLiveFeed(Student);
        aircraft.Track.HandoffPeer = Nct4U;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 10);

        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    /// <summary>
    /// A point-out to a position nobody is working stays pending with no timeout, as on a real STARS display: the RPO
    /// answers it by hand (<c>OK</c> / <c>UN</c>), and nothing withdraws it or approves it on the recipient's behalf.
    /// </summary>
    [Fact]
    public void PendingPointoutToAnUnattendedRecipient_StaysPendingWithNoTimeout()
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

        AiTestFixture.Tick(engine, 120);

        Assert.NotNull(aircraft.Track.Pointout);
        Assert.True(aircraft.Track.Pointout.IsPending);
        Assert.Equal("4U", aircraft.Track.Pointout.Recipient.ToString());
        Assert.DoesNotContain(lines, line => line.Contains("withdrawn", StringComparison.Ordinal));
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

    /// <summary>
    /// Solo mode has no RPO: a point-out to a simulated position is approved after the auto-accept delay (the 5 s solo
    /// floor), where a handoff to the same position is auto-accepted.
    /// </summary>
    [Fact]
    public void SoloMode_ApprovesAPointoutToASimulatedPositionAfterTheAutoAcceptDelay()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.AutoAcceptDelay = TimeSpan.Zero;
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);
        aircraft.Track.Pointout = new StarsPointout(TrackResolver.FindTcpByCode(scenario, "4U")!, scenario.StudentTcp!)
        {
            InitiatedAt = scenario.ElapsedSeconds,
        };

        AiTestFixture.Tick(engine, 4);

        Assert.True(aircraft.Track.Pointout!.IsPending);

        AiTestFixture.Tick(engine, 1);

        Assert.NotNull(aircraft.Track.Pointout);
        Assert.True(aircraft.Track.Pointout.IsAccepted);
        string approval = Assert.Single(lines, line => line.Contains("[AutoAck]", StringComparison.Ordinal));
        Assert.Equal("[AutoAck] Pointout approved by 4U", approval);
    }

    /// <summary>
    /// Point-outs follow STARS consolidation: a point-out addressed to a TCP consolidated to the attended student
    /// position flashes to the student's position, the one working the combined airspace. The server's
    /// <c>PointoutConsolidationRedirectTests</c> covers the config's automatic hierarchy through the CRC seam; this
    /// drives the engine's own command arm with a manual override and the engine's attendance.
    /// </summary>
    [Fact]
    public void PointoutToAConsolidatedTcp_GoesToThePositionItIsConsolidatedTo()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        AircraftState aircraft = Owned(engine, Nct4Q);
        Tcp studentTcp = scenario.StudentTcp!;
        Tcp fourU = TrackResolver.FindTcpByCode(scenario, "4U")!;
        Assert.True(engine.ConsolidationState.Consolidate(studentTcp, fourU, basic: true));
        AttendanceTestSupport.Attend(engine, "2B");

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));

        Assert.Equal("2B", aircraft.Track.Pointout!.Recipient.ToString());
    }

    /// <summary>
    /// A non-solo room's point-out to a position nobody works tells the instructor it is waiting for an answer — once,
    /// when it is filed — since the RPO works that position and nothing else announces the point-out.
    /// </summary>
    [Fact]
    public void PointoutToAnUnattendedPosition_AnnouncesItAwaitsAnAnswer()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));

        string announcement = Assert.Single(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
        Assert.Equal($"[Pointout] 2B → 4U: {aircraft.Callsign} awaits OK or UN (nobody works 4U)", announcement);
    }

    /// <summary>
    /// The announcement is only for a position nobody works: a CRC-attended recipient's controller answers the
    /// point-out on their own display.
    /// </summary>
    [Fact]
    public void PointoutToAnAttendedPosition_AnnouncesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        AttendanceTestSupport.Attend(engine, "4U");
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));

        Assert.DoesNotContain(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
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

    /// <summary>
    /// A point-out filed by a triggered queue block — the path into the track table a chained or conditioned command
    /// takes — announces like one filed directly: exactly one line, and only once it actually files.
    /// </summary>
    [Fact]
    public void PointoutFiledByATriggeredBlock_AnnouncesOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "WAIT 1 PO 4U", "conn-1", "XX", Baked: null));

        Assert.DoesNotContain(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));

        AiTestFixture.Tick(engine, 3);

        Assert.Equal("4U", aircraft.Track.Pointout!.Recipient.ToString());
        string announcement = Assert.Single(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
        Assert.Equal($"[Pointout] 2B → 4U: {aircraft.Callsign} awaits OK or UN (nobody works 4U)", announcement);
    }

    /// <summary>
    /// A point-out chained behind a flight command on one transmission announces exactly once: the compound routes its
    /// point-out unit through the same track table, not a second creation site.
    /// </summary>
    [Fact]
    public void PointoutInACompoundTransmission_AnnouncesOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U; FH 090", "conn-1", "XX", Baked: null));

        Assert.Equal("4U", aircraft.Track.Pointout!.Recipient.ToString());
        Assert.Single(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nobody works a point-out addressed to the student's own position: the student answers it, so no instructor line.
    /// </summary>
    [Fact]
    public void PointoutToTheStudent_AnnouncesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Nct4U);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 2B", "conn-1", "XX", Baked: null));

        Assert.Equal("2B", aircraft.Track.Pointout!.Recipient.ToString());
        Assert.DoesNotContain(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
    }

    /// <summary>
    /// A solo room approves its own point-outs instead of asking an instructor, so it announces none.
    /// </summary>
    [Fact]
    public void SoloModePointout_AnnouncesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));

        Assert.Equal("4U", aircraft.Track.Pointout!.Recipient.ToString());
        Assert.DoesNotContain(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
    }

    /// <summary>
    /// Re-issuing a point-out while one is pending is refused and announces nothing: one line per filed point-out.
    /// </summary>
    [Fact]
    public void ReissuedPointoutWhilePending_AnnouncesOnlyOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Owned(engine, Student);
        List<string> lines = CaptureTerminal(engine);

        engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));
        ActionOutcome second = engine.Actions.Issue(new ActionInput(aircraft.Callsign, "PO 4U", "conn-1", "XX", Baked: null));

        Assert.False(second.Result.Success);
        Assert.Single(lines, line => line.Contains("awaits OK or UN", StringComparison.Ordinal));
    }
}
