using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Tdls.Views.VTdls;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > vTDLS. The connected main window with the OAK
// clearances scenario loaded and the student facility's vTDLS tab selected.
// The scene selects the tab holding the VTdlsView of the first vTDLS entry.
internal sealed class VtdlsTabScene : ScenarioSceneBase
{
    public override string Name => "vtdls-tab";

    // The base sets this before the dynamic vTDLS tab exists; the real
    // selection happens in OnSceneReadyAsync.
    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await SceneActions.WaitUntilAsync(() => vm.TdlsEntries.Count >= 1, TimeSpan.FromSeconds(10), "TdlsEntries to populate");

        vm.SelectedTabIndex = TdlsTabIndex(window, vm.TdlsEntries[0]);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private int TdlsTabIndex(Window window, VTdlsDockEntryViewModel entry)
    {
        TabControl tabs =
            window.FindControl<TabControl>("MainTabControl")
            ?? throw new InvalidOperationException($"Scene '{Name}': the main window has no MainTabControl.");
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            if (tabs.Items[i] is TabItem { Content: VTdlsView view } && ReferenceEquals(view.DataContext, entry.Vm))
            {
                return i;
            }
        }
        throw new InvalidOperationException($"Scene '{Name}': no tab holds the vTDLS view for entry '{entry.TabTitle}'.");
    }
}
