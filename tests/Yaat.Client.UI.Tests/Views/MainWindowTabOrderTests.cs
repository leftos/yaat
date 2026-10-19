using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Tdls.Views.VTdls;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins the main TabControl's materialized order against the view-model's positional
/// index model (<c>MainViewModel.IsTabVisible</c> / <c>FindNextVisibleTabIndex</c> /
/// <c>EnsureSelectedTabVisible</c>). The VM models the dynamic tabs as one contiguous
/// Strips block followed by one contiguous vTDLS block; the View must keep the physical
/// tab order in step, or a pop-out moves the selection off the wrong tab.
/// </summary>
public class MainWindowTabOrderTests
{
    private static int IndexOfContent<T>(TabControl tabControl) => tabControl.Items.OfType<TabItem>().ToList().FindIndex(t => t.Content is T);

    [AvaloniaFact]
    public async Task PoppingOutAStripsTabOpenedAfterTheTdlsTab_LeavesSelectionOnADockedTab()
    {
        (MainWindow window, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            await vm.NavDataInitialization;
            TabControl tabControl = window.FindControl<TabControl>("MainTabControl")!;

            // The student Strips[0] and vTDLS[0] entries are created in the ctor and
            // materialized adjacent at boot.
            Assert.Single(vm.StripsEntries);
            Assert.Single(vm.TdlsEntries);

            // Open a second strips tab. The RPC the command issues is offline in the test,
            // but the entry — and its TabItem — are added synchronously before the first
            // await, so the materialization we care about has already happened.
            try
            {
                await vm.OpenStripsEntryForFacilityAsync("ZZZ");
            }
            catch
            {
                // Offline connection: SwitchFacilityAsync throws, but StripsEntries.Add ran.
            }
            MainWindowHost.Pump();

            Assert.Equal(2, vm.StripsEntries.Count);

            // Locate the (single, docked) student vTDLS tab by its content.
            int tdlsPos = IndexOfContent<VTdlsView>(tabControl);
            Assert.True(tdlsPos >= 0);

            // Simulate the user viewing the (docked) vTDLS tab, then popping out the extra
            // strips tab.
            vm.SelectedTabIndex = tdlsPos;
            vm.StripsEntries[1].IsPoppedOut = true;
            MainWindowHost.Pump();

            // EnsureSelectedTabVisible's whole job is: if the current selection points at a
            // popped-out tab, advance to the next docked one. The invariant is therefore that
            // the tab the control actually shows at SelectedTabIndex is still docked/visible.
            TabItem selected = tabControl.Items.OfType<TabItem>().ElementAt(vm.SelectedTabIndex);
            Assert.True(
                selected.IsVisible,
                $"SelectedTabIndex={vm.SelectedTabIndex} points at a popped-out (hidden) tab — the VM index model disagrees with the physical tab order"
            );
        }
        finally
        {
            if (vm.StripsEntries.Count > 1)
            {
                vm.CloseStripsEntry(vm.StripsEntries[1]);
            }
            vm.StripsEntries[0].IsPoppedOut = false;
            Dispatcher.UIThread.RunJobs();
            MainWindowHost.CloseAll(window);
        }
    }
}
