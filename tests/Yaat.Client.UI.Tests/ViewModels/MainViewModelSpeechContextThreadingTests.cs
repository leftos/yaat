using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Regression test for the threading contract of <see cref="MainViewModel.BuildSpeechContext"/>.
///
/// The speech pipeline pulls the context provider from a <c>Task.Run</c> background thread
/// (<c>SpeechRecognitionService.ProcessPipelineAsync</c>), but <c>BuildSpeechContext</c> reads the
/// UI-thread-only <c>Aircraft</c> collection (plus <c>SelectedAircraft</c> / <c>Ground.DomainLayout</c>).
/// A concurrent UI-thread spawn/delete bumps the collection version mid-enumeration and throws
/// <see cref="System.InvalidOperationException"/>. The fix marshals the build onto the UI thread.
///
/// A parallel-stress test can't gate a marshaling fix (it deadlocks once the read serializes onto the
/// UI thread). Instead this asserts the contract directly: an off-thread call must marshal onto the UI
/// thread, so it cannot complete until the UI thread pumps its dispatcher queue. The test waits on the
/// queued Send job rather than a time window, then pumps once.
/// </summary>
public class MainViewModelSpeechContextThreadingTests
{
    // A hang guard, never a deadline: a passing run leaves each wait as soon as the background call queues its build or returns.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [AvaloniaFact]
    public void BuildSpeechContext_InvokedOffUiThread_MarshalsOntoUiThread()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        for (int i = 0; i < 40; i++)
        {
            vm.Aircraft.Add(new AircraftModel { Callsign = $"UAL{i:000}", Destination = "KOAK" });
        }

        // Mirror the speech pipeline: pull the context from a Task.Run background thread.
        Task<SpeechContext> bg = Task.Run(() => vm.BuildSpeechContext());

        // This thread does not pump while it waits, so the background call either queues its build onto the UI thread
        // (Dispatcher.Invoke, Send priority) and blocks there, or, unmarshalled, runs to completion off the UI thread.
        // "A Send job is queued" reads as our Invoke: nothing else in src posts at Send today, and a future
        // Send poster could only make this test pass falsely, never fail a correct product.
        var waited = Stopwatch.StartNew();
        while (!bg.IsCompleted && !Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.Send))
        {
            Assert.True(waited.Elapsed < HangGuard, "BuildSpeechContext neither queued its build on the UI thread nor returned");
            Thread.Sleep(1);
        }
        Assert.False(bg.IsCompleted, "BuildSpeechContext ran off the UI thread instead of marshaling");

        // The queued build runs here, and the background call returns once it has.
        Dispatcher.UIThread.RunJobs();
        Assert.True(bg.Wait(HangGuard), "BuildSpeechContext did not return after its UI-thread build ran");
        Assert.Equal(40, bg.Result.ActiveCallsigns.Count);
    }
}
