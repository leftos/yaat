using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public sealed class TdlsCommandParserTests
{
    private static ParsedCommand Parse(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"parse failed for '{input}': {result.Reason}");
        return result.Value!;
    }

    [Fact]
    public void TDLSOPS_ParsesFacilityAndConfig()
    {
        var cmd = Assert.IsType<TdlsOpsConfigCommand>(Parse("TDLSOPS OAK OAKE"));
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("OAKE", cmd.Config);
    }

    [Fact]
    public void TDLSOPS_KeepsSpacesInTheConfigName()
    {
        // Facility Engineers name configurations freely — BOS ships "Logan Sid" — so the config
        // token has to run to end of line rather than stopping at the first space.
        var cmd = Assert.IsType<TdlsOpsConfigCommand>(Parse("TDLSOPS BOS Logan Sid"));
        Assert.Equal("BOS", cmd.FacilityId);
        Assert.Equal("Logan Sid", cmd.Config);
    }

    [Fact]
    public void TDLSOPS_UppercasesTheFacilityButNotTheConfig()
    {
        // The facility is an identifier; the config may be matched by name, and names are
        // displayed verbatim in the footer, so its casing is preserved for the error message.
        var cmd = Assert.IsType<TdlsOpsConfigCommand>(Parse("tdlsops oak Logan Sid"));
        Assert.Equal("OAK", cmd.FacilityId);
        Assert.Equal("Logan Sid", cmd.Config);
    }

    [Theory]
    [InlineData("TDLSOPS")]
    [InlineData("TDLSOPS OAK")]
    public void TDLSOPS_RequiresBothArguments(string input)
    {
        Assert.False(CommandParser.Parse(input).IsSuccess);
    }

    [Fact]
    public void TDLSQ_NoArgs_ParsesAsQueueCommand()
    {
        Assert.IsType<TdlsQueueCommand>(Parse("TDLSQ"));
    }

    [Fact]
    public void TDLSW_NoArgs_ParsesAsWilcoCommand()
    {
        Assert.IsType<TdlsWilcoCommand>(Parse("TDLSW"));
    }

    [Fact]
    public void TDLSDUMP_NoArgs_ParsesAsDumpCommand()
    {
        Assert.IsType<TdlsDumpCommand>(Parse("TDLSDUMP"));
    }

    [Fact]
    public void TDLSD_Alias_ParsesAsDumpCommand()
    {
        Assert.IsType<TdlsDumpCommand>(Parse("TDLSD"));
    }

    [Fact]
    public void TDLSS_NineFields_ParsesAsSendWithAllFieldsPositional()
    {
        var send = Assert.IsType<TdlsSendCommand>(Parse("TDLSS 10 MIN AFT DP|OAKLAND4|ALTAM||CLIMB VIA SID|5000||120.9|ADV ATIS AND LOCATION"));

        Assert.Equal(9, send.Fields.Count);
        Assert.Equal("10 MIN AFT DP", send.Fields[0]); // Expect
        Assert.Equal("OAKLAND4", send.Fields[1]); // Sid
        Assert.Equal("ALTAM", send.Fields[2]); // Transition
        Assert.Equal("", send.Fields[3]); // Climbout (empty)
        Assert.Equal("CLIMB VIA SID", send.Fields[4]); // Climbvia
        Assert.Equal("5000", send.Fields[5]); // InitialAlt
        Assert.Equal("", send.Fields[6]); // ContactInfo (empty)
        Assert.Equal("120.9", send.Fields[7]); // DepFreq
        Assert.Equal("ADV ATIS AND LOCATION", send.Fields[8]); // LocalInfo
    }

    [Fact]
    public void TDLSS_AllEmpty_ParsesAsNineEmptyFields()
    {
        var send = Assert.IsType<TdlsSendCommand>(Parse("TDLSS ||||||||"));
        Assert.Equal(9, send.Fields.Count);
        Assert.All(send.Fields, f => Assert.Equal("", f));
    }

    [Fact]
    public void TDLSS_FewerThanNineFields_Fails()
    {
        var result = CommandParser.Parse("TDLSS a|b|c");
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Reason);
        Assert.Contains("nine", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TDLSS_MoreThanNineFields_Fails()
    {
        var result = CommandParser.Parse("TDLSS a|b|c|d|e|f|g|h|i|j");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TdlsCommands_RoundTripThroughDescriber()
    {
        AssertRoundTrip(new TdlsQueueCommand(), "TDLSQ");
        AssertRoundTrip(new TdlsWilcoCommand(), "TDLSW");
        AssertRoundTrip(new TdlsDumpCommand(), "TDLSDUMP");
        AssertRoundTrip(
            new TdlsSendCommand(["10 MIN", "OAKLAND4", "ALTAM", "", "CLIMB VIA SID", "5000", "", "120.9", "ADV ATIS"]),
            "TDLSS 10 MIN|OAKLAND4|ALTAM||CLIMB VIA SID|5000||120.9|ADV ATIS"
        );
    }

    [Fact]
    public void TdlsCommands_AreClassifiedAsTdlsByTrackEngine()
    {
        Assert.True(TrackEngine.IsTdlsCommand(new TdlsQueueCommand()));
        Assert.True(TrackEngine.IsTdlsCommand(new TdlsSendCommand([])));
        Assert.True(TrackEngine.IsTdlsCommand(new TdlsWilcoCommand()));
        Assert.True(TrackEngine.IsTdlsCommand(new TdlsDumpCommand()));
    }

    [Fact]
    public void TdlsCommands_AreCanonicalTypeMapped()
    {
        Assert.Equal(CanonicalCommandType.TdlsQueue, CommandDescriber.ToCanonicalType(new TdlsQueueCommand()));
        Assert.Equal(CanonicalCommandType.TdlsSend, CommandDescriber.ToCanonicalType(new TdlsSendCommand([])));
        Assert.Equal(CanonicalCommandType.TdlsWilco, CommandDescriber.ToCanonicalType(new TdlsWilcoCommand()));
        Assert.Equal(CanonicalCommandType.TdlsDump, CommandDescriber.ToCanonicalType(new TdlsDumpCommand()));
    }

    private static void AssertRoundTrip(ParsedCommand cmd, string expectedCanonical)
    {
        var canonical = CommandDescriber.DescribeCommand(cmd);
        Assert.Equal(expectedCanonical, canonical);
    }
}
