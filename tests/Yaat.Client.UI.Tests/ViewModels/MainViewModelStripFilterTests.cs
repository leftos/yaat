using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

public class MainViewModelStripFilterTests
{
    [AvaloniaFact]
    public void StripChannel_Visibility_TracksToggle()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        // Set the toggle explicitly rather than relying on the persisted default — the UI-test
        // suite shares one preferences.json per process, so another test may have persisted a
        // hidden Strip channel.
        vm.ShowStripEntries = true;
        Assert.True(vm.IsEntryVisible(TerminalEntryKind.Strip));

        vm.ShowStripEntries = false;
        Assert.False(vm.IsEntryVisible(TerminalEntryKind.Strip));
    }

    [AvaloniaFact]
    public void ShiftClickSoloStrip_IsolatesStrip_ThenRestoresOnUndo()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        // Establish a known baseline so the solo snapshot is deterministic regardless of any
        // filter state persisted by other tests in the shared preferences.json.
        vm.ShowStripEntries = true;
        vm.ShowCommandEntries = true;

        // Enter solo on the Strip channel: only Strip stays visible.
        vm.OnTerminalCategoryShiftClicked(TerminalEntryKind.Strip);
        Assert.True(vm.IsEntryVisible(TerminalEntryKind.Strip));
        Assert.False(vm.IsEntryVisible(TerminalEntryKind.Command));

        // Undo solo: the pre-solo visibility (Strip and Command both on) is restored.
        vm.OnTerminalCategoryShiftClicked(TerminalEntryKind.Strip);
        Assert.True(vm.IsEntryVisible(TerminalEntryKind.Strip));
        Assert.True(vm.IsEntryVisible(TerminalEntryKind.Command));
    }
}
