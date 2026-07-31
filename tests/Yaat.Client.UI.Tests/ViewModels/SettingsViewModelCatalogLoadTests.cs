using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Opening Settings must never block the UI thread on LM-Kit model metadata.
///
/// The recommended Whisper entry is backed by a remote URI, so <c>ModelCard.FileSize</c> resolves its
/// size over the network — and LM-Kit does that sync-over-async. Kicked off from the UI thread it
/// captures the <c>AvaloniaSynchronizationContext</c> and then blocks that same thread waiting for a
/// continuation only that thread can run, so the app freezes permanently rather than recovering on a
/// timeout. A user reported it as "the whole app hangs when I open Settings"; the process dump showed
/// the UI thread parked in <c>ModelCard.get_FileSize</c> → <c>Task.GetResultCore</c> →
/// <c>AvaloniaSynchronizationContext.Wait</c> with no network I/O in flight anywhere in the process.
///
/// These tests run on the headless Avalonia UI thread, so they carry a real
/// <c>AvaloniaSynchronizationContext</c> and reproduce the deadlock exactly as the app hits it. Note
/// that the xUnit <c>Timeout</c> below cannot actually interrupt the wedged dispatcher — against the
/// unfixed code the whole test host hangs until an outer timeout kills it.
/// </summary>
public class SettingsViewModelCatalogLoadTests
{
    // Generous relative to the assertion's intent (these reads should be effectively instant) but far
    // below the "hangs forever" failure mode, so a slow CI box cannot turn this red spuriously.
    private static readonly TimeSpan UiThreadBudget = TimeSpan.FromSeconds(5);

    [AvaloniaFact(Timeout = 60_000)]
    public void Construction_DoesNotResolveModelMetadataOnTheUiThread()
    {
        var elapsed = Stopwatch.StartNew();
        var vm = new SettingsViewModel();
        elapsed.Stop();

        Assert.NotNull(vm);
        Assert.True(
            elapsed.Elapsed < UiThreadBudget,
            $"SettingsViewModel construction took {elapsed.Elapsed.TotalSeconds:F1}s on the UI thread; it must not resolve LM-Kit model metadata inline."
        );
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task LoadModelCatalogsAsync_PopulatesCatalogsWithoutDeadlocking()
    {
        var vm = new SettingsViewModel();

        Assert.Empty(vm.WhisperLmKitModels);

        await vm.LoadModelCatalogsAsync();

        // Predefined LM-Kit cards resolve from local metadata, so these stay populated even when the
        // machine is offline and only the remote-URI entry is skipped.
        Assert.NotEmpty(vm.WhisperLmKitModels);
        Assert.NotEmpty(vm.LlmLmKitModels);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task LoadedEntries_ExposeSnapshottedMetadata_SoBindingsCannotReEnterModelCard()
    {
        var vm = new SettingsViewModel();
        await vm.LoadModelCatalogsAsync();

        // SettingsWindow.axaml binds ApproxSizeMb and IsLocallyAvailable per row, so the UI thread
        // reads them during layout. If either were still a live pass-through to ModelCard, this loop
        // would re-enter the same sync-over-async resolve and hang exactly like construction did.
        var elapsed = Stopwatch.StartNew();
        foreach (var entry in vm.WhisperLmKitModels.Concat(vm.LlmLmKitModels))
        {
            _ = entry.ApproxSizeMb;
            _ = entry.IsLocallyAvailable;
            _ = entry.GpuRecommended;
        }
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < UiThreadBudget,
            $"Reading catalog entry metadata took {elapsed.Elapsed.TotalSeconds:F1}s on the UI thread; it must be snapshotted at load time."
        );
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task GpuSnapshot_ReportsDetectingUntilResolved()
    {
        var vm = new SettingsViewModel();

        Assert.False(vm.LmKitGpuSnapshot.IsResolved);
        Assert.Contains("Detecting", vm.LmKitGpuSnapshot.Summary, StringComparison.Ordinal);

        await vm.LoadModelCatalogsAsync();

        Assert.True(vm.LmKitGpuSnapshot.IsResolved);
        Assert.DoesNotContain("Detecting", vm.LmKitGpuSnapshot.Summary, StringComparison.Ordinal);
    }
}
