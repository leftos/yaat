using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Bookmarks;

/// <summary>
/// The timeline-bookmark bodies on the bare engine: the four mutating <c>BM</c> verbs (<c>ADD</c>, <c>REN</c>,
/// <c>DEL</c>, <c>DEL ALL</c>) write <see cref="SimScenarioState.Bookmarks"/> from engine state alone — the scenario's
/// clock and the issuing initials — so a run with no server behind it keeps the same list a live room does, and the
/// host is only told the set changed.
///
/// <para>
/// Bookmarks are <see cref="RecordingPolicy.Never"/>: they are timeline-global metadata the rewind paths carry over
/// verbatim, so a <c>BM</c> read back from a recording must stay inert or a rewind would duplicate every bookmark.
/// </para>
/// </summary>
public class BookmarkStepTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public BookmarkStepTests()
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
        engine.Scenario!.ElapsedSeconds = 42;
        return engine;
    }

    private static CommandResult Issue(SimulationEngine engine, IActionHost host, string command) =>
        engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null), host).Result;

    private static List<TimelineBookmark> Bookmarks(SimulationEngine engine) => engine.Scenario!.Bookmarks;

    [Fact]
    public void BmAdd_AddsOneBookmarkAtTheCurrentSecond_TaggedWithTheIssuersInitials()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var result = Issue(engine, new AttendanceActionHost(), "BM ADD Go-around 28R");

        Assert.True(result.Success, result.Message);
        var bookmark = Assert.Single(Bookmarks(engine));
        Assert.Equal("bm-0", bookmark.Id);
        Assert.Equal("Go-around 28R", bookmark.Name);
        Assert.Equal(42, bookmark.TimeSeconds);
        Assert.Equal("XX", bookmark.CreatorInitials);
        Assert.Equal("Bookmark bm-0 added at 00:42", result.Message);
    }

    [Fact]
    public void BmRename_ChangesTheLabelOfTheNamedBookmark()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(Issue(engine, host, "BM ADD first").Success);

        var result = Issue(engine, host, "BM REN bm-0 Conflict");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Conflict", Assert.Single(Bookmarks(engine)).Name);
    }

    [Fact]
    public void BmDelete_RemovesThatBookmarkOnly()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(Issue(engine, host, "BM ADD first").Success);
        Assert.True(Issue(engine, host, "BM ADD second").Success);

        var result = Issue(engine, host, "BM DEL bm-0");

        Assert.True(result.Success, result.Message);
        Assert.Equal("second", Assert.Single(Bookmarks(engine)).Name);
    }

    [Fact]
    public void BmDeleteAll_ClearsTheList_AndCountsWhatItRemoved()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(Issue(engine, host, "BM ADD first").Success);
        Assert.True(Issue(engine, host, "BM ADD second").Success);

        var result = Issue(engine, host, "BM DEL ALL");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Deleted 2 bookmark(s)", result.Message);
        Assert.Empty(Bookmarks(engine));
    }

    /// <summary>
    /// The broadcast seam: every verb that changed the set hands the host exactly one notification through the
    /// router's drain, and one the bodies refused hands it none — an unknown id and a <c>DEL ALL</c> over an empty
    /// list both leave the room with nothing to re-send.
    /// </summary>
    [Fact]
    public void EachMutatingVerb_RaisesOneBookmarkChange_AndARefusedOneRaisesNone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(Issue(engine, host, "BM ADD first").Success);
        Assert.Equal(1, host.BookmarkChanges);

        Assert.True(Issue(engine, host, "BM REN bm-0 Conflict").Success);
        Assert.Equal(2, host.BookmarkChanges);

        Assert.False(Issue(engine, host, "BM DEL bm-999").Success);
        Assert.Equal(2, host.BookmarkChanges);

        Assert.True(Issue(engine, host, "BM DEL bm-0").Success);
        Assert.Equal(3, host.BookmarkChanges);

        Assert.True(Issue(engine, host, "BM ADD second").Success);
        Assert.True(Issue(engine, host, "BM DEL ALL").Success);
        Assert.Equal(5, host.BookmarkChanges);

        Assert.False(Issue(engine, host, "BM DEL ALL").Success);
        Assert.Equal(5, host.BookmarkChanges);
    }

    /// <summary>
    /// The Never policy, from the router: a <c>BM</c> in an older recording is refused before any body runs, so a
    /// rewind through it cannot re-add the bookmark the live run already carries over.
    /// </summary>
    [Fact]
    public void ABookmarkReadBackFromARecord_IsInert()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var outcome = engine.Actions.Apply(new RecordedCommand(0, "", "BM ADD test", "XX", "conn-1"), new AttendanceActionHost());

        Assert.False(outcome.Result.Success);
        Assert.Empty(Bookmarks(engine));
    }
}
