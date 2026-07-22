using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Parse-level coverage for the <c>BM</c> timeline-bookmark verb. The sub-verbs shadow a bare
/// name, so the "is this a name or a sub-verb" boundary is the interesting part.
/// </summary>
public class BookmarkCommandTests
{
    private static BookmarkCommand Parse(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"'{input}' failed to parse: {result.Reason}");
        return Assert.IsType<BookmarkCommand>(result.Value);
    }

    [Fact]
    public void BareBm_AddsUnnamedBookmark()
    {
        var cmd = Parse("BM");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Null(cmd.Name);
        Assert.Null(cmd.Id);
    }

    [Fact]
    public void FreeText_BecomesTheName()
    {
        var cmd = Parse("BM Go-around 28R");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Equal("Go-around 28R", cmd.Name);
    }

    [Fact]
    public void NameKeepsCommas()
    {
        // BM is registered as a free-text verb, so ParseCommandList must not split on ','.
        var cmd = Parse("BM Go-around, then vectors");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Equal("Go-around, then vectors", cmd.Name);
    }

    [Fact]
    public void CompoundParse_KeepsCommasInTheName()
    {
        var result = CommandParser.ParseCompound("BM Go-around, then vectors");
        Assert.True(result.IsSuccess, result.Reason);
        var block = Assert.Single(result.Value!.Blocks);
        var cmd = Assert.IsType<BookmarkCommand>(Assert.Single(block.Commands));
        Assert.Equal("Go-around, then vectors", cmd.Name);
    }

    [Fact]
    public void AddEscapeHatch_ForcesAReservedWordToBeAName()
    {
        var cmd = Parse("BM ADD LIST");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Equal("LIST", cmd.Name);
    }

    [Fact]
    public void BareAdd_IsStillUnnamed()
    {
        var cmd = Parse("BM ADD");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Null(cmd.Name);
    }

    [Theory]
    [InlineData("BM LIST", BookmarkAction.List)]
    [InlineData("BM list", BookmarkAction.List)]
    [InlineData("BM NEXT", BookmarkAction.Next)]
    [InlineData("BM PREV", BookmarkAction.Prev)]
    [InlineData("BM DEL ALL", BookmarkAction.DeleteAll)]
    [InlineData("BM DELETE all", BookmarkAction.DeleteAll)]
    public void SubVerbsWithoutArguments(string input, BookmarkAction expected)
    {
        Assert.Equal(expected, Parse(input).Action);
    }

    [Theory]
    [InlineData("BM DEL 3")]
    [InlineData("BM DEL bm-3")]
    [InlineData("BM DELETE BM-3")]
    public void DeleteAcceptsBareOrPrefixedId(string input)
    {
        var cmd = Parse(input);
        Assert.Equal(BookmarkAction.Delete, cmd.Action);
        Assert.Equal("bm-3", cmd.Id);
    }

    [Fact]
    public void RenameSplitsIdFromName()
    {
        var cmd = Parse("BM REN 3 Better name here");
        Assert.Equal(BookmarkAction.Rename, cmd.Action);
        Assert.Equal("bm-3", cmd.Id);
        Assert.Equal("Better name here", cmd.Name);
    }

    [Fact]
    public void RenameWithoutAName_ClearsIt()
    {
        var cmd = Parse("BM RENAME bm-7");
        Assert.Equal(BookmarkAction.Rename, cmd.Action);
        Assert.Equal("bm-7", cmd.Id);
        Assert.Null(cmd.Name);
    }

    [Theory]
    [InlineData("BM GO 2")]
    [InlineData("BM GOTO bm-2")]
    public void GotoNormalizesTheId(string input)
    {
        var cmd = Parse(input);
        Assert.Equal(BookmarkAction.Goto, cmd.Action);
        Assert.Equal("bm-2", cmd.Id);
    }

    [Theory]
    [InlineData("BM DEL", "BM DEL requires a bookmark id or ALL")]
    [InlineData("BM DEL xyz", "BM DEL requires a bookmark id or ALL")]
    [InlineData("BM REN", "BM REN requires a bookmark id")]
    [InlineData("BM GO", "BM GO requires a bookmark id")]
    [InlineData("BM GOTO nope", "BM GO requires a bookmark id")]
    public void MalformedSubVerbsFailWithAUsableMessage(string input, string expectedReason)
    {
        var result = CommandParser.Parse(input);
        Assert.False(result.IsSuccess);
        Assert.Contains(expectedReason, result.Reason);
    }

    [Fact]
    public void BookmarkAliasParsesToo()
    {
        var cmd = Parse("BOOKMARK Conflict here");
        Assert.Equal(BookmarkAction.Add, cmd.Action);
        Assert.Equal("Conflict here", cmd.Name);
    }

    [Theory]
    [InlineData("3", "bm-3")]
    [InlineData("bm-3", "bm-3")]
    [InlineData("BM-12", "bm-12")]
    [InlineData(" 0 ", "bm-0")]
    public void TryNormalizeId_AcceptsBothShapes(string token, string expected)
    {
        Assert.True(TimelineBookmark.TryNormalizeId(token, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("bm-")]
    [InlineData("3.5")]
    public void TryNormalizeId_RejectsGarbage(string token)
    {
        Assert.False(TimelineBookmark.TryNormalizeId(token, out _));
    }

    [Fact]
    public void CanonicalRoundTrip()
    {
        // Every form the describer emits must parse back to the same command.
        string[] inputs =
        [
            "BM",
            "BM ADD Go-around",
            "BM LIST",
            "BM REN bm-3 New name",
            "BM REN bm-3",
            "BM DEL bm-3",
            "BM DEL ALL",
            "BM GO bm-3",
            "BM NEXT",
            "BM PREV",
        ];
        foreach (var input in inputs)
        {
            var parsed = Parse(input);
            var canonical = CommandDescriber.DescribeCommand(parsed);
            Assert.Equal(parsed, Parse(canonical));
        }
    }
}
