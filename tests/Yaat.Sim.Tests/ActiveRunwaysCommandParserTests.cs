using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// <c>ARWY [airport] {runway}…</c> at parse time: the parser only splits the airport from the runway text and
/// recognises the show and <c>NONE</c> forms. Whether the runways exist is the arm's question, asked at fire time.
/// </summary>
public class ActiveRunwaysCommandParserTests
{
    private static ActiveRunwaysCommand ParseArwy(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        Assert.True(cmd.IsSuccess, cmd.Reason);
        return Assert.IsType<ActiveRunwaysCommand>(cmd.Value);
    }

    [Fact]
    public void Arwy_AirportAndRunways()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY OAK 28L 28R");

        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("28L 28R", arwy.RunwayText);
    }

    [Fact]
    public void Arwy_RunwaysOnly_LeavesTheAirportToThePrimary()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY 28L 28R");

        Assert.Null(arwy.AirportId);
        Assert.Equal("28L 28R", arwy.RunwayText);
    }

    [Theory]
    [InlineData("ARWY OAK 28L,28R")]
    [InlineData("ARWY OAK 28L, 28R")]
    public void Arwy_CommaForms_ReachArwyWhole(string input)
    {
        ActiveRunwaysCommand single = ParseArwy(input);
        Assert.Equal("OAK", single.AirportId);
        Assert.Equal("28L 28R", single.RunwayText);

        ParseResult<CompoundCommand> compound = CommandParser.ParseCompound(input);
        Assert.True(compound.IsSuccess, compound.Reason);
        ParsedCommand only = Assert.Single(Assert.Single(compound.Value!.Blocks).Commands);
        ActiveRunwaysCommand fromCompound = Assert.IsType<ActiveRunwaysCommand>(only);
        Assert.Equal("28L 28R", fromCompound.RunwayText);

        CompoundParseResult? scheme = CommandSchemeParser.ParseCompound(input, CommandScheme.Default());
        Assert.NotNull(scheme);
        Assert.Equal(input, scheme.CanonicalString);

        Assert.Null(CompoundPolicy.FindNonCompoundableInChain(input));
    }

    [Fact]
    public void Arwy_UsePrefixes_KeptInTheText()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY OAK D28L A28R 30");

        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("D28L A28R 30", arwy.RunwayText);
    }

    [Fact]
    public void Arwy_Lowercase_Uppercased()
    {
        ActiveRunwaysCommand arwy = ParseArwy("arwy oak d28l a28r");

        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("D28L A28R", arwy.RunwayText);
    }

    [Fact]
    public void Arwy_BareAirport_IsTheShowForm()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY OAK");

        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("", arwy.RunwayText);
    }

    [Fact]
    public void Arwy_None_AtANamedAirport()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY OAK NONE");

        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("NONE", arwy.RunwayText);
    }

    [Fact]
    public void Arwy_None_AloneIsNotAnAirport()
    {
        ActiveRunwaysCommand arwy = ParseArwy("ARWY NONE");

        Assert.Null(arwy.AirportId);
        Assert.Equal("NONE", arwy.RunwayText);
    }

    [Theory]
    [InlineData("ARWY", null, "")]
    [InlineData("ARWY +33", "+33", "")]
    [InlineData("ARWY OAK +33", "OAK", "+33")]
    [InlineData("ARWY OAK -28L", "OAK", "-28L")]
    public void Arwy_TokenShape_NeverFailsTheParse(string input, string? airport, string runwayText)
    {
        // The arm answers a malformed ARWY with its own text; the parser only splits airport from runways.
        ActiveRunwaysCommand arwy = ParseArwy(input);

        Assert.Equal(airport, arwy.AirportId);
        Assert.Equal(runwayText, arwy.RunwayText);
    }

    [Fact]
    public void Arwy_ChainedWithAnotherCommand_IsRejected()
    {
        Assert.True(CompoundPolicy.IsNonCompoundable(ParseArwy("ARWY OAK 28L")));

        ParsedCommand? rejected = CompoundPolicy.FindNonCompoundableInChain("ARWY OAK 28L; CM 50");

        ActiveRunwaysCommand arwy = Assert.IsType<ActiveRunwaysCommand>(rejected);
        Assert.Equal("OAK", arwy.AirportId);
        Assert.Equal("28L", arwy.RunwayText);
    }

    [Fact]
    public void Rwy_TaxiAliasUnchanged()
    {
        ParseResult<ParsedCommand> assign = CommandParser.Parse("RWY 30");
        Assert.Equal("30", Assert.IsType<AssignRunwayCommand>(assign.Value).RunwayId);

        ParseResult<ParsedCommand> taxi = CommandParser.Parse("RWY 30 TAXI");
        Assert.True(taxi.IsSuccess, taxi.Reason);
        Assert.IsNotType<ActiveRunwaysCommand>(taxi.Value);
    }
}
