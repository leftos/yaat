using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests;

public class AdctParserTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    public AdctParserTests()
    {
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
    }

    [Fact]
    public void Parse_AdctWithFix_ReturnsAppendDirectTo()
    {
        ParsedInput? result = CommandSchemeParser.Parse("ADCT SUNOL", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.AppendDirectTo, result.Type);
        Assert.Equal("SUNOL", result.Argument);
    }

    [Fact]
    public void Parse_AdctLowercase_ReturnsAppendDirectTo()
    {
        ParsedInput? result = CommandSchemeParser.Parse("adct sunol", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.AppendDirectTo, result.Type);
        Assert.Equal("SUNOL", result.Argument);
    }

    [Fact]
    public void Parse_AdctMultipleFixes_ReturnsFullArgument()
    {
        ParsedInput? result = CommandSchemeParser.Parse("ADCT SUNOL MODESTO", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.AppendDirectTo, result.Type);
        Assert.Equal("SUNOL MODESTO", result.Argument);
    }

    [Fact]
    public void Parse_AdctNoArg_ReturnsNull()
    {
        ParsedInput? result = CommandSchemeParser.Parse("ADCT", Scheme);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_AdctConcatenated_DoesNotMatch()
    {
        // ADCTSUNOL should NOT be parsed (concatenation excluded)
        ParsedInput? result = CommandSchemeParser.Parse("ADCTSUNOL", Scheme);

        Assert.Null(result);
    }

    [Fact]
    public void ParseCompound_AdctInSequence_Succeeds()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CM 150; ADCT SUNOL", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CM 150; ADCT SUNOL", result.CanonicalString);
    }

    [Fact]
    public void ToCanonical_AppendDirectTo_ProducesAdct()
    {
        string canonical = CommandSchemeParser.ToCanonical(CanonicalCommandType.AppendDirectTo, "SUNOL");

        Assert.Equal("ADCT SUNOL", canonical);
    }
}
