using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// A Scope pref-set store on disk for a test whose Scopes save and read their state: a fresh root under the temp folder,
/// the <see cref="MainViewModel.ScopeStoreFactory"/> pointed at it, and a viewer on ZOA's sector 44 so a Scope has a
/// facility to save under.
/// </summary>
internal static class ScopeStoreFixture
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    /// <summary>A fresh store root; nothing is written there until a Scope saves.</summary>
    public static string NewStoreRoot() => Path.Combine(Path.GetTempPath(), "yaat-scope-store-tests", Guid.NewGuid().ToString("N"));

    public static ScopePrefSetStore Store(string root) => new(root, AppLog.CreateLogger<ScopePrefSetStore>());

    /// <summary>Points <see cref="MainViewModel.ScopeStoreFactory"/> at a store under <paramref name="root"/> until disposed.</summary>
    public static IDisposable UseStore(string root)
    {
        Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous = MainViewModel.ScopeStoreFactory;
        MainViewModel.ScopeStoreFactory = () => (Store(root), null);
        return new Restore(previous);
    }

    public static void DeleteStoreRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A Scope's state is saved per ERAM facility, so it needs a viewer on one: ZOA's sector 44.</summary>
    public static async Task OnSector44Async(MainViewModel vm)
    {
        await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        vm.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        Assert.Equal("ZOA", vm.PrimaryScope.Viewer.EramFacilityId);
    }

    /// <summary>
    /// Saves <paramref name="rangeNm"/> as extra Scope #<paramref name="ordinal"/>'s state on ZOA, the way closing that
    /// window does: opens it, sets the range and closes it.
    /// </summary>
    public static void SeedExtraState(MainViewModel vm, string root, int ordinal, double rangeNm)
    {
        List<ScopeViewInstance> opened = [];
        while (opened.Count == 0 || opened[^1].Ordinal < ordinal)
        {
            opened.Add(vm.OpenExtraScopeView());
        }

        opened[^1].Vm.RangeNm = rangeNm;
        foreach (ScopeViewInstance instance in opened)
        {
            vm.CloseExtraScopeView(instance);
        }

        Assert.Equal(rangeNm, Store(root).LoadCurrent("ZOA", ordinal)!.RangeNm);
    }

    private sealed class Restore(Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous) : IDisposable
    {
        public void Dispose() => MainViewModel.ScopeStoreFactory = previous;
    }
}
