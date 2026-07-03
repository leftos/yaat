using Xunit;
using Yaat.Client.Find;

namespace Yaat.Client.Tests.Find;

/// <summary>
/// Fake row for exercising the shared Find core without any view-model or UI.
/// </summary>
file sealed class FakeItem(string text) : IFindableItem
{
    public string GetFindText() => text;

    public bool IsFindMatch { get; set; }
    public bool IsCurrentFindMatch { get; set; }

    public override string ToString() => text;
}

public class FindMatcherTests
{
    private static IReadOnlyList<IFindableItem> Items(params string[] texts) => texts.Select(t => (IFindableItem)new FakeItem(t)).ToList();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankQueryMatchesNothing(string query)
    {
        var matches = FindMatcher.ComputeMatches(Items("UAL123", "AAL456"), query);
        Assert.Empty(matches);
    }

    [Fact]
    public void SingleTokenIsCaseInsensitiveSubstring()
    {
        var items = Items("UAL123", "AAL456", "ual789");
        var matches = FindMatcher.ComputeMatches(items, "ual");
        Assert.Equal(2, matches.Count);
        Assert.Same(items[0], matches[0]);
        Assert.Same(items[2], matches[1]);
    }

    [Fact]
    public void MultipleTokensAreAndedAcrossTheText()
    {
        var items = Items("UAL123 KOAK KSFO", "UAL999 KOAK KLAX");
        Assert.Single(FindMatcher.ComputeMatches(items, "ual ksfo"));
        Assert.Equal(2, FindMatcher.ComputeMatches(items, "ual koak").Count);
        Assert.Empty(FindMatcher.ComputeMatches(items, "ual kjfk"));
    }

    [Fact]
    public void PreservesInputOrder()
    {
        var items = Items("b UAL", "a UAL", "c UAL");
        var matches = FindMatcher.ComputeMatches(items, "ual");
        Assert.Equal(new[] { items[0], items[1], items[2] }, matches);
    }
}

public class FindControllerTests
{
    private static (FindController Ctrl, List<IFindableItem> Snapshot, List<IFindableItem> Scrolled) Build(params string[] texts)
    {
        var snapshot = texts.Select(t => (IFindableItem)new FakeItem(t)).ToList();
        var scrolled = new List<IFindableItem>();
        var ctrl = new FindController(() => snapshot, scrolled.Add);
        return (ctrl, snapshot, scrolled);
    }

    [Fact]
    public void OpeningAndTypingFlagsMatchesAndSelectsFirst()
    {
        var (ctrl, snap, scrolled) = Build("UAL123", "AAL456", "UAL789");

        ctrl.Open();
        ctrl.Query = "UAL";

        Assert.Equal("1/2", ctrl.MatchSummary);
        Assert.True(snap[0].IsFindMatch);
        Assert.False(snap[1].IsFindMatch);
        Assert.True(snap[2].IsFindMatch);
        Assert.True(snap[0].IsCurrentFindMatch);
        Assert.False(snap[2].IsCurrentFindMatch);
        Assert.Same(snap[0], scrolled[^1]);
    }

    [Fact]
    public void NextAndPreviousWrapAround()
    {
        var (ctrl, snap, _) = Build("UAL1", "AAL", "UAL2");
        ctrl.Open();
        ctrl.Query = "UAL";

        ctrl.Next();
        Assert.Equal("2/2", ctrl.MatchSummary);
        Assert.True(snap[2].IsCurrentFindMatch);
        Assert.False(snap[0].IsCurrentFindMatch);

        ctrl.Next();
        Assert.Equal("1/2", ctrl.MatchSummary);
        Assert.True(snap[0].IsCurrentFindMatch);

        ctrl.Previous();
        Assert.Equal("2/2", ctrl.MatchSummary);
        Assert.True(snap[2].IsCurrentFindMatch);
    }

    [Fact]
    public void NoMatchesClearsFlagsAndReportsNoMatches()
    {
        var (ctrl, snap, _) = Build("UAL1", "UAL2");
        ctrl.Open();
        ctrl.Query = "ZZZ";

        Assert.Equal("No matches", ctrl.MatchSummary);
        Assert.All(snap, i => Assert.False(i.IsFindMatch));
        Assert.All(snap, i => Assert.False(i.IsCurrentFindMatch));
    }

    [Fact]
    public void ClosingClearsEveryFlagAndSummary()
    {
        var (ctrl, snap, _) = Build("UAL1", "UAL2");
        ctrl.Open();
        ctrl.Query = "UAL";

        ctrl.Close();

        Assert.False(ctrl.IsVisible);
        Assert.Equal("", ctrl.MatchSummary);
        Assert.All(snap, i => Assert.False(i.IsFindMatch));
        Assert.All(snap, i => Assert.False(i.IsCurrentFindMatch));
    }

    [Fact]
    public void BlankQueryShowsNoHighlightAndEmptySummary()
    {
        var (ctrl, snap, _) = Build("UAL1", "UAL2");
        ctrl.Open();
        ctrl.Query = "   ";

        Assert.Equal("", ctrl.MatchSummary);
        Assert.All(snap, i => Assert.False(i.IsFindMatch));
    }

    [Fact]
    public void RefreshClearsFlagsOnItemsThatLeftTheSnapshot()
    {
        var (ctrl, snap, _) = Build("UAL1", "UAL2");
        ctrl.Open();
        ctrl.Query = "UAL";
        var left = snap[1];
        Assert.True(left.IsFindMatch);

        snap.RemoveAt(1);
        ctrl.Refresh();

        Assert.False(left.IsFindMatch);
        Assert.False(left.IsCurrentFindMatch);
        Assert.Equal("1/1", ctrl.MatchSummary);
    }

    [Fact]
    public void RefreshPreservesCurrentMatchWhenStillPresent()
    {
        var (ctrl, snap, _) = Build("UAL1", "UAL2", "UAL3");
        ctrl.Open();
        ctrl.Query = "UAL";
        ctrl.Next(); // current -> snap[1]
        Assert.True(snap[1].IsCurrentFindMatch);

        ctrl.Refresh();

        Assert.True(snap[1].IsCurrentFindMatch);
        Assert.Equal("2/3", ctrl.MatchSummary);
    }

    [Fact]
    public void NextDoesNothingWhileHidden()
    {
        var (ctrl, _, scrolled) = Build("UAL1", "UAL2");
        ctrl.Query = "UAL"; // set while hidden

        ctrl.Next();

        Assert.Empty(scrolled);
    }

    [Fact]
    public void ReopeningRestoresTheLastQuery()
    {
        var (ctrl, snap, _) = Build("UAL1", "AAL", "UAL2");
        ctrl.Open();
        ctrl.Query = "UAL";
        ctrl.Close();
        Assert.All(snap, i => Assert.False(i.IsFindMatch));

        ctrl.Open();

        Assert.Equal("1/2", ctrl.MatchSummary);
        Assert.True(snap[0].IsFindMatch);
        Assert.True(snap[2].IsFindMatch);
    }
}
