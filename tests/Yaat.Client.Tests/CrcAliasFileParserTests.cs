using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CrcAliasFileParserTests
{
    private static List<CrcAlias> Parse(params string[] lines) => CrcAliasFileParser.Parse(lines, "test.txt");

    [Fact]
    public void AliasLine_IsParsedIntoNameAndBody()
    {
        var aliases = Parse(".C172 .echo DESIGNATOR: C172 | RECAT: I");

        var alias = Assert.Single(aliases);
        Assert.Equal(".C172", alias.Name);
        Assert.Equal(".echo DESIGNATOR: C172 | RECAT: I", alias.ReplacementText);
        Assert.Equal("test.txt", alias.SourceFile);
        Assert.Equal(1, alias.LineNumber);
    }

    /// <summary>
    /// ARTCC alias files open with a free-text header block and personal files use <c>#</c> comments.
    /// CRC has no comment syntax — both survive only because non-alias lines are skipped.
    /// </summary>
    [Fact]
    public void HeaderTextCommentsAndBlankLines_AreSkipped()
    {
        var aliases = Parse(
            "BASIC OAKLAND ARTCC ON VATSIM CONTROLLERS ALIAS LIST",
            "Amendments by several authors",
            "AIRAC 2604",
            "",
            "# Aircraft Name Aliases",
            ".ACON .echo live"
        );

        Assert.Equal([".ACON"], aliases.Select(a => a.Name));
    }

    [Theory]
    [InlineData(".ab")] // shorter than CRC's four-character minimum
    [InlineData(".noBodyJustAName")]
    [InlineData("no leading dot")]
    [InlineData(".has-hyphen body")] // name must be \w+
    public void MalformedLines_AreSkipped(string line)
    {
        Assert.Empty(Parse(line));
    }

    /// <summary>
    /// CRC drops an indented definition because it tests its regex against the untrimmed line.
    /// We match the trimmed line, so the alias the author obviously meant to write loads.
    /// </summary>
    [Fact]
    public void IndentedAlias_IsLoaded()
    {
        var alias = Assert.Single(Parse("   .REF .openurl https://reference.oakartcc.org"));
        Assert.Equal(".REF", alias.Name);
    }

    [Fact]
    public void ArgumentCount_CountsConsecutiveSlotsFromOne()
    {
        Assert.Equal(0, Assert.Single(Parse(".REF .openurl https://example.test")).ArgumentCount);
        Assert.Equal(1, Assert.Single(Parse(".CH .openurl https://example.test/charts/$1")).ArgumentCount);
        Assert.Equal(2, Assert.Single(Parse(".RM2 .openurl https://example.test?dep=$1&dest=$2")).ArgumentCount);
    }

    /// <summary>A gap stops the count, so <c>$3</c> is never substituted — matching CRC.</summary>
    [Fact]
    public void ArgumentCount_StopsAtFirstGap()
    {
        Assert.Equal(1, Assert.Single(Parse(".GAP .echo $1 and $3")).ArgumentCount);
    }

    [Fact]
    public void LaterDefinitions_AreReturnedInFileOrder()
    {
        var aliases = Parse(".A .echo first", ".B .echo second", ".A .echo third");

        Assert.Equal([".A", ".B", ".A"], aliases.Select(a => a.Name));
        Assert.Equal([1, 2, 3], aliases.Select(a => a.LineNumber));
    }

    [Fact]
    public void Tokenize_CollapsesRunsOfSpaces()
    {
        Assert.Equal([".echo", "a", "b"], CrcAliasFileParser.Tokenize("  .echo   a  b "));
    }
}
