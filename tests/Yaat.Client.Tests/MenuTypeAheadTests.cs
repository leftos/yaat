using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// The rich-row picker's type to jump: the typed buffer resolves as an altitude argument does (<c>35</c> is 3,500 ft,
/// <c>350</c> 35,000 ft, <c>FL350</c> 35,000 ft) to the nearest row, Backspace edits it, and it empties after 1.5 s
/// without a key. Key times are passed in, so no clock is involved.
/// </summary>
public class MenuTypeAheadTests
{
    /// <summary>The rows' values top to bottom; the null is a line typing never lands on.</summary>
    private static readonly int?[] Values = [35500, 35000, 34500, 4000, 3500, null, 3000, 400, 300, 200];

    [Theory]
    [InlineData("35", 4)]
    [InlineData("350", 1)]
    [InlineData("FL350", 1)]
    [InlineData("fl350", 1)]
    [InlineData("3", 8)]
    [InlineData("31", 6)]
    public void TypeAhead_ResolvesShorthandToTheNearestRow(string typed, int expectedIndex)
    {
        var typeAhead = new MenuTypeAhead(Values);

        Assert.Equal(expectedIndex, TypeAll(typeAhead, typed, 0));
        Assert.Equal(typed.ToUpperInvariant(), typeAhead.Buffer);
    }

    [Fact]
    public void TypeAhead_BackspaceEditsTheBuffer()
    {
        var typeAhead = new MenuTypeAhead(Values);
        Assert.Equal(1, TypeAll(typeAhead, "350", 0));

        Assert.Equal(4, typeAhead.Backspace(500));
        Assert.Equal("35", typeAhead.Buffer);
        Assert.Equal(8, typeAhead.Backspace(600));
        Assert.Equal("3", typeAhead.Buffer);
        Assert.Null(typeAhead.Backspace(700));
        Assert.Equal("", typeAhead.Buffer);
        Assert.Null(typeAhead.Backspace(800));
        Assert.Equal("", typeAhead.Buffer);
    }

    [Fact]
    public void TypeAhead_ResetsAfterOneAndAHalfSeconds()
    {
        var kept = new MenuTypeAhead(Values);
        Assert.Equal(8, kept.Type('3', 10_000));
        Assert.Equal(4, kept.Type('5', 11_499));
        Assert.Equal("35", kept.Buffer);
        Assert.Equal(1, kept.Type('0', 12_998));
        Assert.Equal("350", kept.Buffer);

        var reset = new MenuTypeAhead(Values);
        Assert.Equal(8, reset.Type('3', 10_000));
        Assert.Equal(7, reset.Type('5', 11_500));
        Assert.Equal("5", reset.Buffer);
    }

    [Fact]
    public void TypeAhead_UnparseableKeepsTheSelection()
    {
        var typeAhead = new MenuTypeAhead(Values);

        Assert.Null(typeAhead.Type('F', 0));
        Assert.Null(typeAhead.Type('L', 100));
        Assert.Null(typeAhead.Type('X', 200));
        Assert.Equal("FLX", typeAhead.Buffer);
        Assert.Null(new MenuTypeAhead(Values).Type('0', 0));
        Assert.Null(new MenuTypeAhead([null]).Type('3', 0));
    }

    /// <summary>Types <paramref name="text"/> a key every 100 ms from <paramref name="start"/> and returns the last key's answer.</summary>
    private static int? TypeAll(MenuTypeAhead typeAhead, string text, long start)
    {
        int? index = null;
        for (int i = 0; i < text.Length; i++)
        {
            index = typeAhead.Type(text[i], start + (i * 100));
        }

        return index;
    }
}
