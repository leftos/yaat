using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Each Scope window keeps its current state in its own slot of the facility: the primary Scope (slot 1) in
/// <c>current/{facility}.json</c>, an extra window #n in <c>current/{facility}#n.json</c>.
/// </summary>
public class ScopeStateSlotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-slot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TwoSlotsOfOneFacility_SaveAndLoadApart()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveCurrent("ZOA", ScopePrefSetStore.PrimarySlot, Set(30));
        store.SaveCurrent("ZOA", 2, Set(70));

        Assert.Equal(30, NewStore().LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.RangeNm);
        Assert.Equal(70, NewStore().LoadCurrent("ZOA", 2)!.RangeNm);
    }

    [Fact]
    public void AnExtraSlot_IsItsOwnFileBesideThePrimarys()
    {
        NewStore().SaveCurrent("ZOA", 2, Set(70));

        Assert.True(File.Exists(Path.Combine(_root, "current", "ZOA#2.json")));
        Assert.False(File.Exists(Path.Combine(_root, "current", "ZOA.json")));
    }

    [Fact]
    public void AnUnsavedSlot_LoadsNothing()
    {
        NewStore().SaveCurrent("ZOA", ScopePrefSetStore.PrimarySlot, Set(30));

        Assert.Null(NewStore().LoadCurrent("ZOA", 3));
    }

    [Fact]
    public void ASlotBelowOne_IsRefused()
    {
        ScopePrefSetStore store = NewStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.LoadCurrent("ZOA", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveCurrent("ZOA", 0, Set(30)));
    }

    private ScopePrefSetStore NewStore() => new(_root, NullLogger.Instance);

    private static ScopePrefSet Set(double rangeNm) =>
        new()
        {
            Name = null,
            RangeNm = rangeNm,
            CenterLat = 37.5,
            CenterLon = -122.5,
            TemplateId = "stars",
            Db = new ScopePrefDb(true, false, true, 14),
            Kind = "stars",
            Lens = new ScopePrefLens(false, false, false, false),
            MapTools = new ScopePrefMapTools(false, false, null, null, null),
        };
}
