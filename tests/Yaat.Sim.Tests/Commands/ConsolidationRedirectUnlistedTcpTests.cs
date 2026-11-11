using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-2042 (#1369): a STARS handoff or point-out to a TCP that is unstaffed, and that the scenario does not list as an
/// ATC position, still lands on the attended TCP its airspace consolidates into. Real NCT hierarchy with automatic
/// consolidation on: <c>1T</c>'s parent is <c>1M</c>. The student works <c>2B</c>, a second CRC client works <c>1M</c>,
/// and neither <c>1T</c> nor <c>1M</c> is a scenario ATC position (as in a real room, where the scenario's ATC list
/// names only the positions it staffs).
/// </summary>
public class ConsolidationRedirectUnlistedTcpTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ConsolidationRedirectUnlistedTcpTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? EngineWithStudentAndOneMAttended()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;

        // The facts the scenario is built on: 1T folds into 1M, automatic consolidation is on, and neither is listed.
        Tcp oneT = TrackResolver.FindTcpByCode(scenario, "1T")!;
        Tcp oneM = TrackResolver.FindTcpByCode(scenario, "1M")!;
        Assert.Equal(oneM.Id, oneT.ParentTcpId);
        Assert.True(_zoa.IsAutoConsolidation("NCT"));
        Assert.DoesNotContain(scenario.AtcPositions, p => (p.Tcp is not null) && ((p.Tcp.Id == oneT.Id) || (p.Tcp.Id == oneM.Id)));

        AttendanceTestSupport.Attend(engine, "2B", "1M");
        Assert.True(engine.Attendance.IsTcpAttended(oneM));
        Assert.False(engine.Attendance.IsTcpAttended(oneT));
        Assert.Equal(oneM.Id, engine.Attendance.ConsolidationOwnerOf(oneT, scenario, engine.ConsolidationState)!.Id);
        return engine;
    }

    private static RecordedCommand Recorded(string command) => new(0, AiTestFixture.Callsign, command, "XX", "conn-1");

    private static string Code(TrackOwner? owner) => $"{owner?.Subset}{owner?.SectorId}";

    [Fact]
    public void Handoff_ToUnstaffedUnlistedTcp_GoesToTheAttendedTcpItConsolidatesInto()
    {
        if (EngineWithStudentAndOneMAttended() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        ActionOutcome outcome = engine.Actions.Apply(Recorded("HO 1T"), new AttendanceActionHost());

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("1M", Code(ac.Track.HandoffPeer));
        Assert.Equal("1T", Code(ac.Track.HandoffRedirectedBy));
        Assert.Equal($"Handoff {AiTestFixture.Callsign} to 1T (redirected to 1M)", outcome.Result.Message);
    }

    [Fact]
    public void PointOut_ToUnstaffedUnlistedTcp_GoesToTheAttendedTcpItConsolidatesInto()
    {
        if (EngineWithStudentAndOneMAttended() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        ActionOutcome outcome = engine.Actions.Apply(Recorded("PO 1T"), new AttendanceActionHost());

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("1M", ac.Track.Pointout!.Recipient.ToString());
    }

    [Fact]
    public void AutoAccept_LeavesAHandoffToAnUnlistedCrcControlledTcpForItsController()
    {
        if (EngineWithStudentAndOneMAttended() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        TrackOwner oneM = TrackResolver.ResolveTcpToOwner(scenario, "1M", facilityHint: null)!;
        TrackOwner oneT = TrackResolver.ResolveTcpToOwner(scenario, "1T", facilityHint: null)!;

        // A handoff flashing at 1M (attended), or at 1T (combined into attended 1M), is the CRC controller's to take.
        Assert.True(TrackEngine.IsHandoffPeerControlledByCrc(oneM, scenario, engine.Attendance, engine.ConsolidationState));
        Assert.True(TrackEngine.IsHandoffPeerControlledByCrc(oneT, scenario, engine.Attendance, engine.ConsolidationState));
    }
}
