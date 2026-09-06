using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// A bare <c>AS</c> selects by any name the resolver knows — a TCP, a position callsign, or <c>callsign@tcp</c> — and the
/// host is told the selected position's real TCP code, not the argument, because the host keys the position's display
/// config on it. Uses the real ZOA config: <c>OAK_GND</c> shares TCP 3O with <c>OAK_TWR</c>, and four <c>NCT_APP</c>
/// positions sit on different TCPs.
/// </summary>
public class PositionSelectionArmTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PositionSelectionArmTests()
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
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "3O")!;
        return engine;
    }

    [Theory]
    [InlineData("AS OAK_GND", "OAK_GND", "O", "3O")]
    [InlineData("AS NCT_APP@1M", "NCT_APP", "M", "1M")]
    [InlineData("AS 4U", "", "U", "4U")] // the scenario's own 4U position wins over the config's SFO_DEP
    public void SelectByName_TellsTheHostTheRealTcpCode(string command, string callsign, string sector, string hostCode)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var outcome = engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.True(engine.PositionSelections.TryGet("conn-1", out var selected));
        if (callsign.Length > 0)
        {
            Assert.Equal(callsign, selected.Callsign);
        }

        Assert.Equal(sector, selected.SectorId);
        var told = Assert.Single(host.SelectedPositions);
        Assert.Equal(("conn-1", selected, hostCode), told);
    }
}
