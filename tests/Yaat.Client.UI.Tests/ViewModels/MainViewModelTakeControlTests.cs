using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Tests for GitHub issue #173: "Take Control" during recording playback must warn before
/// ending the replay. The destructive <see cref="MainViewModel.TakeControl"/> path routes
/// through the view-supplied <see cref="MainViewModel.TakeControlConfirmation"/> hook so the
/// gate can be exercised in a unit test without showing a real dialog.
///
/// A unit-constructed VM has no live server connection, so the actual
/// <c>_connection.TakeControlAsync()</c> call throws "Not connected." and is swallowed by
/// <c>TakeControl</c>'s catch. That makes <see cref="MainViewModel.StatusText"/> the
/// distinguishing observable: it stays at the sentinel when the gate short-circuits on cancel,
/// and changes (to the error) when the destructive path is attempted.
/// </summary>
public class MainViewModelTakeControlTests
{
    [AvaloniaFact]
    public async Task TakeControl_WhenConfirmationCancelled_ShortCircuitsAndPreservesPlayback()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsPlaybackMode = true;
        vm.PlaybackTapeEnd = 120;
        vm.StatusText = "sentinel";

        var prompted = false;
        vm.TakeControlConfirmation = () =>
        {
            prompted = true;
            return Task.FromResult(false);
        };

        await vm.TakeControlCommand.ExecuteAsync(null);

        Assert.True(prompted);
        Assert.Equal("sentinel", vm.StatusText); // destructive server call never attempted
        Assert.True(vm.IsPlaybackMode);
        Assert.Equal(120, vm.PlaybackTapeEnd);
    }

    [AvaloniaFact]
    public async Task TakeControl_WhenConfirmed_ProceedsPastTheGate()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsPlaybackMode = true;
        vm.StatusText = "sentinel";

        var prompted = false;
        vm.TakeControlConfirmation = () =>
        {
            prompted = true;
            return Task.FromResult(true);
        };

        await vm.TakeControlCommand.ExecuteAsync(null);

        Assert.True(prompted);
        Assert.NotEqual("sentinel", vm.StatusText); // destructive path was attempted past the gate
    }

    [AvaloniaFact]
    public async Task TakeControl_WhenNotInPlayback_DoesNotPrompt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.IsPlaybackMode = false;

        var prompted = false;
        vm.TakeControlConfirmation = () =>
        {
            prompted = true;
            return Task.FromResult(true);
        };

        await vm.TakeControlCommand.ExecuteAsync(null);

        Assert.False(prompted);
    }
}
