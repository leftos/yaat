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
/// thread, so it cannot complete until the UI thread pumps its dispatcher queue.
/// </summary>
public class MainViewModelSpeechContextThreadingTests
{
    [AvaloniaFact]
    public void BuildSpeechContext_InvokedOffUiThread_MarshalsOntoUiThread()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        for (var i = 0; i < 40; i++)
        {
            vm.Aircraft.Add(new AircraftModel { Callsign = $"UAL{i:000}", Destination = "KOAK" });
        }

        SpeechContext? result = null;
        // Mirror the speech pipeline: pull the context from a Task.Run background thread.
        var bg = Task.Run(() => result = vm.BuildSpeechContext());

        // With the guard, the build is queued onto the UI thread and bg cannot complete until this
        // (UI) thread pumps. On unfixed code it enumerates Aircraft directly off-thread and completes
        // immediately, failing this assertion.
        Assert.False(bg.Wait(250), "BuildSpeechContext ran off the UI thread instead of marshaling");

        Dispatcher.UIThread.RunJobs();
        Assert.True(bg.Wait(2000));
        Assert.NotNull(result);
        Assert.Equal(40, result!.ActiveCallsigns.Count);
    }
}
