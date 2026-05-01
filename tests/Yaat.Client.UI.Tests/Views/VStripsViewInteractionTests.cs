using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;

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
    public void StripsInRack_RenderInBottomUpVisualOrder()
    {
        // Regression guard for the entire drop-preview math, which depends
        // on DockPanel bottom-up docking (rack.Strips[0] at the visual
        // bottom, Strips[^1] at the visual top). If the docking direction
        // or the ItemsControl.Styles DockPanel.Dock="Bottom" selector ever
        // flips, strips would render top-down and every ComputeDropIndex
        // branch would silently target the wrong visual slot.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1", "S2", "S3"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strips = view.GetVisualDescendants().OfType<FlightStripControl>().Where(c => c.DataContext is StripItemViewModel).ToList();
        // The rack-mounted controls + the drag-ghost slot (not used here) and
        // printer carousels (not used here) share the FlightStripControl type,
        // so filter by bay parent to only see the rack-rendered ones.
        var rackStrips = strips.Where(s => s.GetVisualAncestors().OfType<Border>().Any(b => b.Tag is StripRackViewModel)).ToList();
        Assert.Equal(3, rackStrips.Count);

        var positions = rackStrips
            .Select(s => new { Id = ((StripItemViewModel)s.DataContext!).Id, Y = s.TranslatePoint(new Point(0, 0), view)?.Y ?? double.NaN })
            .ToDictionary(p => p.Id, p => p.Y);
        // Bottom-up: S1 (model idx 0) should have the largest Y, S3 the smallest.
        Assert.True(positions["S1"] > positions["S2"], $"S1 Y={positions["S1"]} should be > S2 Y={positions["S2"]}");
        Assert.True(positions["S2"] > positions["S3"], $"S2 Y={positions["S2"]} should be > S3 Y={positions["S3"]}");
    }

    [AvaloniaFact]
    public void ZoomButtons_StepScaleAndClamp()
    {
        // Covers the zoom +/- buttons added in Round 1.5. Default is 0.8;
        // minus steps by 0.1 down to 0.5, plus steps up to 1.5.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (_, view) = BootView(vm);

        Assert.Equal(0.8, vm.ZoomScale, 3);

        var zoomOut = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string s && s == "−");
        var zoomIn = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string s && s == "+");

        zoomOut.Command?.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.7, vm.ZoomScale, 3);

        zoomIn.Command?.Execute(null);
        zoomIn.Command?.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.9, vm.ZoomScale, 3);

        // Clamp at 0.5 — step down 8 times from 0.9.
        for (var i = 0; i < 8; i++)
        {
            zoomOut.Command?.Execute(null);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, vm.ZoomScale, 3);

        // Clamp at 1.5 — step up 15 times from 0.5.
        for (var i = 0; i < 15; i++)
        {
            zoomIn.Command?.Execute(null);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.5, vm.ZoomScale, 3);
    }

    [AvaloniaFact]
    public void StripContextMenu_FullStrip_HasOffsetDeletePush()
    {
        // Right-click on a full strip shows: Offset, Push-to-{bays}, Delete.
        // Half-strip and separator items are absent for full strips.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.Contains("Offset", headers);
        Assert.Contains("Push to", headers);
        Assert.Contains("Delete", headers);
        Assert.DoesNotContain("Slide", headers);
        Assert.DoesNotContain("Edit lines", headers);
        Assert.DoesNotContain("Edit label", headers);

        // "Push to" sub-menu has one item per bay.
        var pushItem = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Push to");
        var pushTargets = pushItem.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
        Assert.Equal(2, pushTargets.Count);
        Assert.Contains("GROUND", pushTargets);
        Assert.Contains("LOCAL", pushTargets);
    }

    [AvaloniaFact]
    public void StripContextMenu_RackWithMultipleStrips_HasPushAllInRackToBays()
    {
        // Right-click on a strip whose rack holds more than one strip exposes a
        // "Push all in rack to" submenu with one item per bay. Hidden when the
        // rack has only one strip (then "Push to" already does the same job).
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1", "S2", "S3"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.Contains("Push all in rack to", headers);
        var pushAll = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Push all in rack to");
        var targets = pushAll.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Contains("GROUND", targets);
        Assert.Contains("LOCAL", targets);
    }

    [AvaloniaFact]
    public void StripContextMenu_RackWithSingleStrip_HidesPushAllInRack()
    {
        // With only one strip in the rack, "Push all in rack to" is redundant
        // with "Push to" and is suppressed.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.DoesNotContain("Push all in rack to", headers);
    }

    [AvaloniaFact]
    public async Task StripContextMenu_PushAllInRack_EmitsOneStripCommandPerStrip()
    {
        // Clicking "Push all in rack to LOCAL" with three strips in rack 0 of
        // bay-gnd emits three STRIP canonicals — one per strip — in the
        // source's visual-bottom-to-top order so the destination preserves
        // the same order at its visual bottom.
        var (vm, captured) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1", "S2", "S3"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var pushAll = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Push all in rack to");
        var localItem = pushAll.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "LOCAL");

        // Click handlers are async void Click handlers; await the underlying
        // PushAllInRackAsync indirectly by invoking the same path the click
        // would invoke. We simulate the click via Click handlers RaiseEvent —
        // that fires the registered async lambda, which we then drain.
        localItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 4 && captured.Count < 3; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(3, captured.Count);
        Assert.All(captured, c => Assert.StartsWith("STRIP LOCAL", c.Command));
        Assert.Equal(["S1", "S2", "S3"], captured.Select(c => c.Callsign).ToArray());
    }

    [AvaloniaFact]
    public void StripContextMenu_FullStrip_NoExternalBays_OmitsScanTo()
    {
        // SimpleConfig has no external bays. The "Scan to" submenu only shows
        // when at least one external bay is accessible — without one, scanning
        // has no destination, so the entry is suppressed.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.DoesNotContain("Scan to", headers);
    }

    [AvaloniaFact]
    public void StripContextMenu_FullStrip_WithExternalBays_HasScanToSubmenu()
    {
        // When external bays are accessible, the strip context menu surfaces
        // "Scan to" — listing only those external bays as targets. Internal
        // bays stay in the Push-to / Push-all-to submenus where they belong;
        // mixing them under Scan-to would invite a wrong-destination click
        // (server would reject anyway, but the UX should keep the surface
        // semantically clean).
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithExternalBay());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.Contains("Scan to", headers);
        var scanItem = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Scan to");
        var targets = scanItem.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();

        // Only external bays should appear; internal bays would create a
        // confusing "scan to your own facility" affordance.
        Assert.Single(targets);
        Assert.Contains("NCT", targets);
        Assert.DoesNotContain("GROUND", targets);
        Assert.DoesNotContain("LOCAL", targets);
    }

    [AvaloniaFact]
    public async Task StripContextMenu_ScanToBay_EmitsScanCanonical()
    {
        // Clicking "Scan to NCT" emits "SCAN NCT/1" — the bay-only short form,
        // append-to-tail of rack 1 (CRC bottom-up first-available). The
        // dispatched callsign matches the strip's AircraftId (full-strip
        // aircraft-scoped behavior).
        var (vm, captured) = MakeVm();
        SeedBays(vm, ConfigWithExternalBay());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1"],
                [],
            ]
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["S1"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var scanItem = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Scan to");
        var nctItem = scanItem.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "NCT");

        nctItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 4 && captured.Count < 1; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        var emitted = Assert.Single(captured);
        Assert.Equal("S1", emitted.Callsign);
        Assert.Equal("SCAN NCT/1", emitted.Command);
    }

    [AvaloniaFact]
    public void StripContextMenu_ScannedCopy_HidesCallsignKeyedItems()
    {
        // A scanned copy is a full strip with id "STRIP_{callsign}_{guid}" —
        // distinguishable from the canonical "STRIP_{callsign}" form. The
        // Offset / Push to / Push all in rack to / Delete items all dispatch
        // callsign-keyed canonicals (STRIPO / STRIP / STRIPD) which would
        // hit the **originator's** strip, not this copy. Hide them on copies
        // so the receiver can't accidentally clobber the source.
        var (vm, _) = MakeVm();
        SeedBays(vm, ConfigWithExternalBay());

        // Seed a "scanned copy" — id has STRIP_ prefix but doesn't match
        // STRIP_{AircraftId}, so the gate fires.
        var copy = new StripItemDto(
            "STRIP_UAL123_abcdef01",
            "UAL123",
            IsDisconnected: false,
            StripItemType.DepartureStrip,
            IsOffset: false,
            FieldValues: ["UAL123", "", "B738/L"]
        );
        vm.ReconcileItems([copy]);
        vm.ReconcileFullState(
            new FlightStripsStateDto(
                PrinterItems: [],
                BayItems:
                [
                    new StripBayContentsDto(
                        "bay-ext",
                        [
                            ["STRIP_UAL123_abcdef01"],
                        ]
                    ),
                ],
                NewItemInPrinter: false,
                NewItemInArrivalPrinter: false,
                NewItemInBayId: null,
                ItemMovedOrCreatedBySessionId: null
            )
        );
        var (_, view) = BootView(vm);

        var strip = vm.ItemsByIdForTests["STRIP_UAL123_abcdef01"];
        var menu = view.BuildStripContextMenu(strip, vm);
        var headers = ExtractHeaders(menu);

        Assert.DoesNotContain("Offset", headers);
        Assert.DoesNotContain("Push to", headers);
        Assert.DoesNotContain("Push all in rack to", headers);
        Assert.DoesNotContain("Scan to", headers);
        Assert.DoesNotContain("Delete", headers);
    }

    [AvaloniaFact]
    public void EmptyRackContextMenu_RackWithStrips_HasPushAllToBays()
    {
        // Right-click on rack space below populated strips exposes "Push all
        // to" with one item per bay. Behavior matches the strip context menu.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            rackStrips:
            [
                ["S1", "S2"],
                [],
            ]
        );
        var (_, _) = BootView(vm);

        var rack = vm.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        var menu = VStripsView.BuildEmptyRackMenu(rack, vm);
        Assert.NotNull(menu);
        var headers = ExtractHeaders(menu!);

        Assert.Contains("Push all to", headers);
        var pushAll = menu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Push all to");
        var targets = pushAll.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Contains("GROUND", targets);
        Assert.Contains("LOCAL", targets);
    }

    [AvaloniaFact]
    public void EmptyRackContextMenu_EmptyRack_HidesPushAllTo()
    {
        // No strips in the rack → no "Push all to" submenu.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (_, _) = BootView(vm);

        var rack = vm.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        var menu = VStripsView.BuildEmptyRackMenu(rack, vm);
        Assert.NotNull(menu);
        var headers = ExtractHeaders(menu!);

        Assert.DoesNotContain("Push all to", headers);
    }

    [AvaloniaFact]
    public void EmptyRackContextMenu_Unlocked_OffersAllSeparatorStyles()
    {
        // Right-click on empty rack space in an unlocked facility exposes Add
        // Half-Strip, Add Separator (4 styles: Handwritten, White, Red,
        // Green), and Add Blank Strip. Matches docs/crc/vstrips.md:180-195.
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (_, _) = BootView(vm);

        var rack = vm.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        var menu = VStripsView.BuildEmptyRackMenu(rack, vm);
        Assert.NotNull(menu);
        var headers = ExtractHeaders(menu!);

        Assert.Contains("Add half-strip", headers);
        Assert.Contains("Add separator", headers);
        Assert.Contains("Add blank strip", headers);
        Assert.DoesNotContain("Add handwritten separator", headers);

        var sepItem = menu!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Add separator");
        var styles = sepItem.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
        Assert.Equal(4, styles.Count);
        Assert.Contains("Handwritten", styles);
        Assert.Contains("White", styles);
        Assert.Contains("Red", styles);
        Assert.Contains("Green", styles);
    }

    [AvaloniaFact]
    public void EmptyRackContextMenu_Locked_OnlyHandwrittenSeparator()
    {
        // Locked facilities collapse the separator styles to Handwritten only
        // (docs/crc/vstrips.md:195).
        var (vm, _) = MakeVm();
        SeedBays(
            vm,
            new FlightStripsConfigDto(
                FacilityId: "FAC1",
                FacilityName: "Fresno ATCT",
                Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
                HasTwoPrinters: false,
                SeparatorsLocked: true
            )
        );
        var (_, _) = BootView(vm);

        var rack = vm.Bays.Single(b => b.BayId == "bay-gnd").Racks[0];
        var menu = VStripsView.BuildEmptyRackMenu(rack, vm);
        Assert.NotNull(menu);
        var headers = ExtractHeaders(menu!);

        Assert.Contains("Add half-strip", headers);
        Assert.Contains("Add handwritten separator", headers);
        Assert.Contains("Add blank strip", headers);
        Assert.DoesNotContain("Add separator", headers);
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
        var localButton = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Tag is StripBayViewModel bay && bay.Name == "LOCAL");
        Assert.NotNull(localButton);

        localButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedBay);
        Assert.Equal("LOCAL", vm.SelectedBay!.Name);
        Assert.Empty(captured); // selecting an own bay does not send a canonical
    }

    [AvaloniaFact]
    public void Disconnect_ClearsRackStripsAndPrinterButKeepsBayLayout()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                ["S3"],
            ]
        );
        vm.SetConnected(true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsConnected);
        Assert.Equal(2, vm.Bays.Count);
        Assert.Equal(2, vm.Bays[0].Racks[0].Strips.Count);
        Assert.Single(vm.Bays[0].Racks[1].Strips);

        vm.SetConnected(false);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsConnected);
        Assert.Equal(2, vm.Bays.Count); // bay shells stay
        Assert.Empty(vm.Bays[0].Racks[0].Strips);
        Assert.Empty(vm.Bays[0].Racks[1].Strips);
        Assert.Empty(vm.Bays[1].Racks[0].Strips);
        Assert.Null(vm.SelectedStrip);
    }

    [AvaloniaFact]
    public void Disconnect_BannerVisibleWhenOfflineAndHidesOnReconnect()
    {
        var (vm, _) = MakeVm();
        SeedBays(vm, SimpleConfig());
        var (_, view) = BootView(vm);

        // Default state is disconnected; the red banner should be visible.
        Assert.False(vm.IsConnected);
        var banner = view.FindControl<Border>("DisconnectedBanner");
        Assert.NotNull(banner);
        Assert.True(banner!.IsVisible);

        vm.SetConnected(true);
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);
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
            getUserInitials: null
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

    /// <summary>
    /// Two own bays plus one external bay (NCT) — used to exercise the
    /// "Scan to" submenu and the scanned-copy footgun gate. External bays
    /// flow through to <c>StripBayViewModel.IsExternal</c>, which is what
    /// the context menu filters on.
    /// </summary>
    private static FlightStripsConfigDto ConfigWithExternalBay() =>
        new(
            FacilityId: "FAC1",
            FacilityName: "Fresno ATCT",
            Bays:
            [
                new StripBayConfigDto("bay-gnd", "GROUND", 2),
                new StripBayConfigDto("bay-loc", "LOCAL", 2),
                new StripBayConfigDto("bay-ext", "NCT", 5, IsExternal: true),
            ],
            HasTwoPrinters: false,
            SeparatorsLocked: false
        );

    private static StripItemDto FullStrip(string id) =>
        new(id, id, IsDisconnected: false, StripItemType.DepartureStrip, IsOffset: false, FieldValues: [id, "", "B738/L"]);

    // Seeds a set of full strips into bayId's racks at the positions given by
    // rackStrips (outer = rack index, inner = strip ids in model order bottom-up).
    private static void SeedStripsInBay(VStripsViewModel vm, string bayId, string[][] rackStrips)
    {
        var flatIds = rackStrips.SelectMany(r => r).ToArray();
        vm.ReconcileItems(flatIds.Select(FullStrip).ToArray());
        vm.ReconcileFullState(
            new FlightStripsStateDto(
                PrinterItems: [],
                BayItems: [new StripBayContentsDto(bayId, rackStrips)],
                NewItemInPrinter: false,
                NewItemInArrivalPrinter: false,
                NewItemInBayId: null,
                ItemMovedOrCreatedBySessionId: null
            )
        );
    }

    private static List<string?> ExtractHeaders(MenuFlyout menu) => menu.Items.OfType<MenuItem>().Select(i => (string?)i.Header).ToList();

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
