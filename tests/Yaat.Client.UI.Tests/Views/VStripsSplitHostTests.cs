using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins <see cref="VStripsSplitHost"/>'s layout contract: a single strips
/// view when the entry is unsplit, two views around a GridSplitter when
/// split, and back to one after unsplit — with the same primary view
/// instance surviving every transition so pane state isn't rebuilt.
/// </summary>
public class VStripsSplitHostTests
{
    private static (MainViewModel Vm, VStripsDockEntryViewModel Entry, VStripsSplitHost Host, Window Window) NewSplitHost()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var entry = vm.StripsEntries[0];
        var host = new VStripsSplitHost { DataContext = entry };
        var window = new Window { Content = host };
        window.Show();
        return (vm, entry, host, window);
    }

    private static List<VStripsView> StripsViewsOf(VStripsSplitHost host) => host.GetVisualDescendants().OfType<VStripsView>().ToList();

    [AvaloniaFact]
    public async Task SplitHost_RendersOneThenTwoThenOnePane()
    {
        var (vm, entry, host, window) = NewSplitHost();
        try
        {
            window.UpdateLayout();
            var single = StripsViewsOf(host);
            Assert.Single(single);
            var primaryView = single[0];
            Assert.Empty(host.GetVisualDescendants().OfType<GridSplitter>());

            await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);
            window.UpdateLayout();

            var splitViews = StripsViewsOf(host);
            Assert.Equal(2, splitViews.Count);
            Assert.Contains(primaryView, splitViews);
            var splitter = Assert.Single(host.GetVisualDescendants().OfType<GridSplitter>());
            Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);

            await vm.SplitStripsEntryAsync(entry, StripsSplitMode.Stacked);
            window.UpdateLayout();
            splitter = Assert.Single(host.GetVisualDescendants().OfType<GridSplitter>());
            Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
            Assert.Equal(2, StripsViewsOf(host).Count);

            vm.UnsplitStripsEntry(entry);
            window.UpdateLayout();

            var afterUnsplit = Assert.Single(StripsViewsOf(host));
            Assert.Same(primaryView, afterUnsplit);
            Assert.Empty(host.GetVisualDescendants().OfType<GridSplitter>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SplitHost_PanesBindPrimaryAndSecondaryVms()
    {
        var (vm, entry, host, window) = NewSplitHost();
        try
        {
            await vm.SplitStripsEntryAsync(entry, StripsSplitMode.SideBySide);
            window.UpdateLayout();

            var contexts = StripsViewsOf(host).Select(v => v.DataContext).ToList();
            Assert.Contains(entry.Vm, contexts);
            Assert.Contains(entry.SecondaryVm, contexts);
        }
        finally
        {
            window.Close();
        }
    }
}
