using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;
using Xunit;

namespace Yaat.Client.UI.Tests.Views;

// View-layer coverage for VStripsView interactions that can't be reached from
// pure VStripsViewModel tests (see tests/Yaat.Client.Tests/VStripsViewModelTests.cs).
// Every user action funnels through VStripsViewModel helpers and emits a canonical
// command string, which tests capture via the sendCommand delegate.
public class VStripsViewInteractionTests
{
    [AvaloniaFact]
    public void VStripsViewWindow_BootsWithSeededBays()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (window, view) = BootView(vm);

        Assert.True(window.IsVisible);
        Assert.Equal("Fresno ATCT", vm.FacilityName);
        Assert.Equal(2, vm.Bays.Count);

        // ItemsControl realizes a Button per bay — find at least one to prove
        // the DataTemplate and Tag binding survive the headless layout pass.
        var bayButtons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is StripBayViewModel).ToList();
        Assert.Equal(2, bayButtons.Count);
    }

    [AvaloniaFact]
    public void BayButtonClick_SelectsBayViaViewModel()
    {
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (_, view) = BootView(vm);

        // Click the LOCAL bay (second one). SelectBayAsync on an own-bay is a
        // local-only selection — no server command emitted. We verify the
        // observable property flipped to prove the click handler ran.
        var localButton = view.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Tag is StripBayViewModel bay && bay.Name == "LOCAL");
        Assert.NotNull(localButton);

        localButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedBay);
        Assert.Equal("LOCAL", vm.SelectedBay!.Name);
        Assert.Empty(captured); // selecting an own bay does not send a canonical
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static (VStripsViewModel Vm, List<(string Callsign, string Command)> Captured) MakeVm()
    {
        var captured = new List<(string, string)>();
        var vm = new VStripsViewModel(
            new ServerConnection(),
            sendCommand: (cs, cmd, _) =>
            {
                captured.Add((cs, cmd));
                return Task.CompletedTask;
            },
            preferences: null
        );
        return (vm, captured);
    }

    private static FlightStripsConfigDto SimpleConfig() =>
        new(
            FacilityId: "FAC1",
            FacilityName: "Fresno ATCT",
            Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
            HasTwoPrinters: false,
            SeparatorsLocked: false
        );

    // ApplyBayConfig posts to the dispatcher. Under an Avalonia.Headless test
    // that's fine — RunJobs flushes. The reflection path in the unit-test MakeVm
    // exists for non-Avalonia tests and is not needed here.
    private static void SeedBays(VStripsViewModel vm, FlightStripsConfigDto config)
    {
        vm.ApplyBayConfig(config);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window window, VStripsView view) BootView(VStripsViewModel vm)
    {
        var view = new VStripsView { DataContext = vm };
        var window = new Window
        {
            Width = 1000,
            Height = 400,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // Realize the bay ItemsControl — without this the DataTemplate containers
        // may not be instantiated yet, and GetVisualDescendants misses them.
        foreach (var itemsControl in view.GetVisualDescendants().OfType<ItemsControl>())
        {
            itemsControl.ApplyTemplate();
            if (itemsControl.Presenter is ItemsPresenter presenter)
            {
                presenter.ApplyTemplate();
            }
        }
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
