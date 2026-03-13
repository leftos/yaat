using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class OnHandoffConditionTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private static readonly IFixLookup Fixes = new PermissiveFixes();

    // --- CommandSchemeParser: canonical form ---

    [Theory]
    [InlineData("ONHO CAPP", "ONHO CAPP")]
    [InlineData("ONHO CM 360", "ONHO CM 360")]
    [InlineData("ONHO DEL", "ONHO DEL")]
    [InlineData("ONHO DM 060", "ONHO DM 060")]
    [InlineData("ONHO SPD 180", "ONHO SPD 180")]
    public void SchemeParser_OnhoSimpleCommand(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);
        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    [Theory]
    [InlineData("ONHO AT LIVVY DEL", "ONHO; AT LIVVY DEL")]
    [InlineData("ONHO AT MIIDY DEL", "ONHO; AT MIIDY DEL")]
    [InlineData("ONHO AT KERRK DEL", "ONHO; AT KERRK DEL")]
    [InlineData("ONHO AT CAMRN DEL", "ONHO; AT CAMRN DEL")]
    [InlineData("ONHO AT EYESS DM 060", "ONHO; AT EYESS DM 060")]
    [InlineData("ONHO AT FELTY DM 025", "ONHO; AT FELTY DM 025")]
    [InlineData("ONHO AT CAVDI DCT KATRN", "ONHO; AT CAVDI DCT KATRN")]
    public void SchemeParser_OnhoWithAtCondition_UnpacksToSequentialBlocks(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);
        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    [Theory]
    [InlineData("ONHO AT 3000 WAIT 2 AT SCHOO CAPP I5L")]
    [InlineData("ONHO AT 9500 DEL")]
    public void SchemeParser_OnhoWithAtAltitude_Parses(string input)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("ONHO WAIT 30 CM 360", "ONHO WAIT 30; CM 360")]
    [InlineData("ONHO WAIT 60 SCRATCHPAD MTV", "ONHO WAIT 60; SP1 MTV")]
    [InlineData("ONHO WAIT 20 CM 230", "ONHO WAIT 20; CM 230")]
    public void SchemeParser_OnhoWithWait_ExpandsWait(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);
        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    // --- CommandParser: parsed block structure ---

    [Fact]
    public void CommandParser_OnhoSimple_ProducesSingleBlockWithCondition()
    {
        var result = CommandParser.ParseCompound("ONHO CAPP", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<OnHandoffCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
    }

    [Fact]
    public void CommandParser_OnhoAtFix_ProducesTwoBlocks()
    {
        var result = CommandParser.ParseCompound("ONHO AT LIVVY DEL", Fixes);
        Assert.NotNull(result);
        Assert.Equal(2, result.Blocks.Count);

        // Block 1: ONHO gate (no commands)
        Assert.IsType<OnHandoffCondition>(result.Blocks[0].Condition);
        Assert.Empty(result.Blocks[0].Commands);

        // Block 2: AT LIVVY DEL
        Assert.IsType<AtFixCondition>(result.Blocks[1].Condition);
        var atCond = (AtFixCondition)result.Blocks[1].Condition!;
        Assert.Equal("LIVVY", atCond.FixName);
        Assert.Single(result.Blocks[1].Commands);
        Assert.IsType<DeleteCommand>(result.Blocks[1].Commands[0]);
    }

    [Fact]
    public void CommandParser_OnhoWaitCm_ProducesTwoBlocks()
    {
        var result = CommandParser.ParseCompound("ONHO WAIT 30 CM 360", Fixes);
        Assert.NotNull(result);
        Assert.Equal(2, result.Blocks.Count);

        // Block 1: ONHO + WAIT 30
        Assert.IsType<OnHandoffCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
        Assert.IsType<WaitCommand>(result.Blocks[0].Commands[0]);

        // Block 2: CM 360
        Assert.Null(result.Blocks[1].Condition);
        Assert.Single(result.Blocks[1].Commands);
        Assert.IsType<ClimbMaintainCommand>(result.Blocks[1].Commands[0]);
    }

    [Fact]
    public void CommandParser_OnhoDm_ProducesSingleBlock()
    {
        var result = CommandParser.ParseCompound("ONHO DM 060", Fixes);
        Assert.NotNull(result);
        Assert.Single(result.Blocks);
        Assert.IsType<OnHandoffCondition>(result.Blocks[0].Condition);
        Assert.Single(result.Blocks[0].Commands);
        Assert.IsType<DescendMaintainCommand>(result.Blocks[0].Commands[0]);
    }

    // --- Bare HO (no args) ---

    [Fact]
    public void SchemeParser_BareHo_Parses()
    {
        var result = CommandSchemeParser.ParseCompound("HO", Scheme);
        Assert.NotNull(result);
        Assert.Equal("HO", result.CanonicalString);
    }

    [Fact]
    public void SchemeParser_HoWithPosition_Parses()
    {
        var result = CommandSchemeParser.ParseCompound("HO 3O", Scheme);
        Assert.NotNull(result);
        Assert.Equal("HO 3O", result.CanonicalString);
    }

    [Theory]
    [InlineData("WAIT 10 HO", "WAIT 10; HO")]
    [InlineData("AT 16000 HO", "AT 16000 HO")]
    [InlineData("DELAY 5 HO", "WAIT 5; HO")]
    public void SchemeParser_ConditionedHo_Parses(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);
        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    [Fact]
    public void CommandParser_WaitHo_ProducesTwoBlocks()
    {
        var result = CommandParser.ParseCompound("WAIT 10 HO", Fixes);
        Assert.NotNull(result);
        Assert.Equal(2, result.Blocks.Count);
        Assert.IsType<WaitCommand>(result.Blocks[0].Commands[0]);
        Assert.IsType<InitiateHandoffCommand>(result.Blocks[1].Commands[0]);
    }

    // --- TrackOwner.IsTcp ---

    [Fact]
    public void TrackOwner_IsTcp_MatchesSubsetAndSector()
    {
        var owner = TrackOwner.CreateStars("OAK_GND", "ZOA", 3, "O");
        var tcp = new Tcp(3, "O", "some-id", null);
        Assert.True(owner.IsTcp(tcp));
    }

    [Fact]
    public void TrackOwner_IsTcp_DoesNotMatchDifferentTcp()
    {
        var owner = TrackOwner.CreateStars("OAK_GND", "ZOA", 3, "O");
        var tcp = new Tcp(1, "A", "other-id", null);
        Assert.False(owner.IsTcp(tcp));
    }

    [Fact]
    public void TrackOwner_IsTcp_NonNasPositionDoesNotMatch()
    {
        var owner = TrackOwner.CreateNonNas("KSFO_TWR");
        var tcp = new Tcp(3, "O", "some-id", null);
        Assert.False(owner.IsTcp(tcp));
    }

    // --- HandoffAccepted flag + StudentTcp filtering ---

    [Fact]
    public void HandleAccept_SetsHandoffAccepted()
    {
        var ac = new AircraftState { Callsign = "AAL123", AircraftType = "B738" };
        var acceptor = TrackOwner.CreateStars("NOR_APP", "ZOA", 1, "R");
        ac.Owner = TrackOwner.CreateStars("OAK_GND", "ZOA", 3, "O");
        ac.HandoffPeer = acceptor;

        var result = TrackEngine.HandleAccept(ac, acceptor);

        Assert.True(result.Success);
        Assert.True(ac.HandoffAccepted);
        Assert.Equal(acceptor, ac.Owner);
        Assert.Null(ac.HandoffPeer);
    }

    [Fact]
    public void SimulationWorld_ClearsHandoffAccepted_ForStudentTcp()
    {
        var world = new SimulationWorld();
        var studentTcp = new Tcp(3, "O", "student-tcp-id", null);
        world.StudentTcp = studentTcp;

        var ac = new AircraftState
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            Owner = TrackOwner.CreateStars("OAK_GND", "ZOA", 3, "O"),
            HandoffAccepted = true,
        };
        world.AddAircraft(ac);

        world.Tick(1.0);

        Assert.False(ac.HandoffAccepted);
    }

    [Fact]
    public void SimulationWorld_PreservesHandoffAccepted_ForNonStudentTcp()
    {
        var world = new SimulationWorld();
        var studentTcp = new Tcp(3, "O", "student-tcp-id", null);
        world.StudentTcp = studentTcp;

        var ac = new AircraftState
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            Owner = TrackOwner.CreateStars("NOR_APP", "ZOA", 1, "R"),
            HandoffAccepted = true,
        };
        world.AddAircraft(ac);

        // HandoffAccepted should survive the tick (different TCP than student)
        // The tick itself will consume it via trigger evaluation if there's a matching ONHO block,
        // but with no commands queued it stays true through the tick
        world.Tick(1.0);

        Assert.True(ac.HandoffAccepted);
    }

    private sealed class PermissiveFixes : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string fixName) => (37.0, -122.0);

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
