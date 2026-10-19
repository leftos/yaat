using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// The process-wide default slot behind <see cref="NavigationDatabase.Instance"/>: a registered test loader fills an empty
/// default once, however many threads read it at the same moment, and an installed instance still replaces it. Each
/// test builds its own slot, so the process-wide default the rest of the suite reads is never touched.
/// </summary>
public sealed class NavigationDatabaseTestDefaultLoaderTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task EmptySlotWithLoader_LoadsOnce_AcrossConcurrentReads()
    {
        var slot = new NavigationDatabase.DefaultSlot();
        var loadedDb = NavigationDatabase.ForTesting();
        int loads = 0;
        slot.SetLoader(() =>
        {
            Interlocked.Increment(ref loads);
            return loadedDb;
        });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<NavigationDatabase?>[] reads =
        [
            .. Enumerable
                .Range(0, 16)
                .Select(_ =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        return slot.GetOrLoad();
                    })
                ),
        ];
        start.SetResult();
        NavigationDatabase?[] seen = await Task.WhenAll(reads).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.All(seen, db => Assert.Same(loadedDb, db));
        Assert.Equal(1, loads);
    }

    [Fact]
    public void InstalledInstance_AfterALoad_ReplacesTheLoadedDefault()
    {
        var slot = new NavigationDatabase.DefaultSlot();
        slot.SetLoader(() => NavigationDatabase.ForTesting());
        _ = slot.GetOrLoad();
        var installed = NavigationDatabase.ForTesting();

        slot.Set(installed);

        Assert.Same(installed, slot.GetOrLoad());
    }

    [Fact]
    public void InstalledInstance_BeforeAnyRead_IsReadWithoutRunningTheLoader()
    {
        var slot = new NavigationDatabase.DefaultSlot();
        var installed = NavigationDatabase.ForTesting();
        slot.SetLoader(() => throw new InvalidOperationException("The loader ran although an instance was installed."));

        slot.Set(installed);

        Assert.Same(installed, slot.GetOrLoad());
    }

    [Fact]
    public void EmptySlotWithoutLoader_ReadsNull_SoInstanceStillThrows()
    {
        var slot = new NavigationDatabase.DefaultSlot();

        Assert.Null(slot.GetOrLoad());
    }
}
