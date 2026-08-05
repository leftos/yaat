using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.UI.Tests.Views;

// End-to-end coverage of the pointer-capture strip drag in VStripsView,
// driven through the real headless mouse device (window.MouseDown/Move/Up).
// The device path is required: hand-raised PointerPressed/Released routed
// events do not reproduce pointer-capture semantics, and the drag promotes,
// captures, and completes entirely from captured pointer events.
//
// Every test closes its window in a finally so a leaked pointer capture
// can't bleed into another test's input.
public class VStripsDragGestureTests
{
    [AvaloniaFact]
    public void DragStrip_CrossRack_EmitsMoveAndOptimisticallyRelocates()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S1");
            var rack1 = RackBorder(view, rackIndex: 1);
            // Near the visual bottom of the empty rack — empty racks resolve
            // to insertion index 0 regardless of position.
            var target = rack1.TranslatePoint(new Point(50, rack1.Bounds.Height - 20), window)!.Value;

            window.MouseDrag(start, target);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            // Wire is slash-compound 1-based: rack 1 → "2", index 0 → "1".
            Assert.Equal("STRIP S1 FAC1/GROUND/2/1", entry.Command);
            // Optimistic move relocated the strip before any server echo.
            Assert.DoesNotContain(vm.Bays[0].Racks[0].Strips, s => s.Id == "S1");
            Assert.Contains(vm.Bays[0].Racks[1].Strips, s => s.Id == "S1");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DragStrip_SameRackToTop_EmitsAppendIndexMove()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2", "S3"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            // S1 renders at the visual bottom (model index 0); drag it above
            // the topmost strip (S3) → append index. With the source hidden
            // during the drag, the visible set is {S2, S3}, so append = 2.
            var start = StripPoint(window, view, "S1");
            var s3Top = StripPoint(window, view, "S3");
            var target = new Point(s3Top.X, s3Top.Y - 60);

            window.MouseDrag(start, target, steps: 6);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            // rack 0 → "1", index 2 → "3".
            Assert.Equal("STRIP S1 FAC1/GROUND/1/3", entry.Command);
            Assert.Equal(["S2", "S3", "S1"], vm.Bays[0].Racks[0].Strips.Select(s => s.Id).ToArray());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DragStrip_ReleaseOnOwnSlot_EmitsNothing()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2", "S3"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            // Promote to a drag, wander a few pixels, release in the BOTTOM
            // half of the source's own (now vacated) slot — that resolves to
            // the source's original index and IsNoOpMove suppresses the
            // canonical. (The top half of the vacated slot legitimately
            // means "insert above the strip that fell into my slot".) The
            // release lands deep in the bottom half, beyond the drop-index
            // hysteresis band, so the resolution is index 0 regardless of
            // which side of a band midpoint the interpolated drag path
            // grazed — releasing near the midpoint made this flake on
            // sub-pixel layout differences between runs.
            var start = StripPoint(window, view, "S1");
            window.MouseDrag(start, new Point(start.X + 12, start.Y + 22));
            WaitOutSettle();

            Assert.Empty(captured);
            Assert.Equal(["S1", "S2", "S3"], vm.Bays[0].Racks[0].Strips.Select(s => s.Id).ToArray());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ShortClick_BelowThreshold_SelectsWithoutEmitting()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S2");
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 1, start.Y), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(start.X + 1, start.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("S2", vm.SelectedStrip?.Id);
            Assert.Empty(captured);
            Assert.Equal(["S1", "S2"], vm.Bays[0].Racks[0].Strips.Select(s => s.Id).ToArray());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void EscDuringDrag_CancelsAndRestoresSource()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S1");
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 20, start.Y - 20), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            var sourcePresenter = StripControl(view, "S1").FindAncestorOfType<ContentPresenter>();
            Assert.NotNull(sourcePresenter);
            Assert.False(sourcePresenter.IsVisible);

            window.DispatchKey(Key.Escape);

            Assert.True(sourcePresenter.IsVisible);
            Assert.Empty(captured);

            // Releasing after the cancel is inert — no late drop dispatch.
            window.MouseUp(new Point(start.X + 20, start.Y - 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(captured);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DisconnectDuringDrag_CancelsAndRestoresSource()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S1");
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 20, start.Y - 20), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            var sourcePresenter = StripControl(view, "S1").FindAncestorOfType<ContentPresenter>();
            Assert.NotNull(sourcePresenter);
            Assert.False(sourcePresenter.IsVisible);

            vm.SetConnected(false);
            Dispatcher.UIThread.RunJobs();

            Assert.True(sourcePresenter.IsVisible);
            window.MouseUp(new Point(start.X + 20, start.Y - 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(captured);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DragToTrash_DeletesById()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S1");
            var trash = view.FindControl<Border>("TrashZone")!;
            var target = trash.TranslatePoint(new Point(trash.Bounds.Width / 2, trash.Bounds.Height / 2), window)!.Value;

            window.MouseDrag(start, target, steps: 6);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            Assert.Equal("STRIPD S1", entry.Command);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DragToBayButton_AppendsToBayRackZero()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var localButton = view.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is StripBayViewModel { Name: "LOCAL" });
            var start = StripPoint(window, view, "S1");
            var target = localButton.TranslatePoint(new Point(localButton.Bounds.Width / 2, localButton.Bounds.Height / 2), window)!.Value;

            // Drive the drag manually so the mid-drag hover state is
            // observable: the hovered bay button carries the drag-over
            // highlight (the "this drop transfers to another bay" cue).
            window.MouseDown(start, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            for (var i = 1; i <= 6; i++)
            {
                var t = (double)i / 6;
                window.MouseMove(
                    new Point(start.X + ((target.X - start.X) * t), start.Y + ((target.Y - start.Y) * t)),
                    RawInputModifiers.LeftMouseButton
                );
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Contains("drag-over", localButton.Classes);

            window.MouseUp(target, MouseButton.Left);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            // Bay-button drop appends: no index token on the wire.
            Assert.Equal("STRIP S1 FAC1/LOCAL/1", entry.Command);
            // Highlight clears once the drag ends.
            Assert.DoesNotContain("drag-over", localButton.Classes);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DragFromPrinterCarousel_IntoRack_EmitsMove()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        vm.SetConnected(true);
        vm.ReconcileItems([VStripsViewInteractionTests.FullStrip("P1")]);
        vm.ReconcileFullState(
            new FlightStripsStateDto(
                PrinterItems: ["P1"],
                BayItems: [],
                NewItemInPrinter: false,
                NewItemInArrivalPrinter: false,
                NewItemInBayId: null,
                ItemMovedOrCreatedBySessionId: null
            )
        );
        vm.Printer.IsOpen = true;
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            VStripsViewInteractionTests.RealizeContainers(view);
            // The carousel-rendered strip has no rack Border ancestor.
            var carouselStrip = view.GetVisualDescendants()
                .OfType<FlightStripControl>()
                .Single(c =>
                    ((c.DataContext as StripItemViewModel)?.Id == "P1")
                    && !c.GetVisualAncestors().OfType<Border>().Any(b => b.Tag is StripRackViewModel)
                );
            var start = carouselStrip.TranslatePoint(new Point(30, 20), window)!.Value;
            var rack0 = RackBorder(view, rackIndex: 0);
            // Bottom-left corner of rack 0, clear of the centered printer modal.
            var target = rack0.TranslatePoint(new Point(30, rack0.Bounds.Height - 15), window)!.Value;

            window.MouseDrag(start, target, steps: 6);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            Assert.Equal("STRIP P1 FAC1/GROUND/1/1", entry.Command);
            // Printer drags never hide their source — the carousel strip
            // stays visible for the duration of the drag.
            Assert.True(carouselStrip.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void WheelDuringDrag_ScrollsRacksAndKeepsDragAlive()
    {
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        // Enough strips that the rack stack overflows the 400px-tall window,
        // giving the ScrollViewer a scrollable extent.
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9", "S10"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var scrollViewer = view.FindControl<ScrollViewer>("RacksScrollViewer")!;
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height, "test setup: rack must overflow the viewport");

            var start = StripPoint(window, view, "S2");
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 20, start.Y - 20), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            // The sticky-bottom behavior boots the rack pinned to the bottom
            // (offset at max), so scroll UP — the only direction with room.
            var offsetBefore = scrollViewer.Offset.Y;
            window.MouseWheel(new Point(start.X + 20, start.Y - 20), new Vector(0, 1), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                scrollViewer.Offset.Y < offsetBefore,
                $"wheel during drag should scroll (before={offsetBefore}, after={scrollViewer.Offset.Y})"
            );

            // The drag survives the scroll: releasing over a rack still
            // moves. The release point must be inside the ScrollViewer's
            // visible viewport (rack 1's bottom is now scrolled out of view
            // and would hit-test to nothing), so combine rack 1's x with the
            // viewport's vertical center.
            var rack1 = RackBorder(view, rackIndex: 1);
            var rackX = rack1.TranslatePoint(new Point(50, 0), window)!.Value.X;
            var viewportCenterY = scrollViewer.TranslatePoint(new Point(0, scrollViewer.Viewport.Height / 2), window)!.Value.Y;
            var target = new Point(rackX, viewportCenterY);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(target, MouseButton.Left);
            WaitForCaptured(captured, 1);

            var entry = Assert.Single(captured);
            Assert.Equal("STRIP S2 FAC1/GROUND/2/1", entry.Command);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task BayRoundTripDuringDrag_KeepsSourceHiddenUntilDragEnds()
    {
        // The 500ms bay-hover preview switches SelectedBay mid-drag, which
        // rebuilds every rack container. Returning to the origin bay used to
        // resurrect the dragged strip: its fresh container defaulted to
        // visible while the ghost was still in hand — two copies on screen.
        var (vm, captured) = VStripsViewInteractionTests.MakeVm();
        VStripsViewInteractionTests.SeedBays(vm, VStripsViewInteractionTests.SimpleConfig());
        VStripsViewInteractionTests.SeedStripsInBay(
            vm,
            "bay-gnd",
            [
                ["S1", "S2"],
                [],
            ]
        );
        vm.SetConnected(true);
        var (window, view) = VStripsViewInteractionTests.BootView(vm);
        try
        {
            var start = StripPoint(window, view, "S1");
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 20, start.Y - 20), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            // Simulate what the bay-hover timer does: switch away, then back.
            var ground = vm.Bays.Single(b => b.Name == "GROUND");
            var local = vm.Bays.Single(b => b.Name == "LOCAL");
            await vm.SelectBayAsync(local);
            VStripsViewInteractionTests.RealizeContainers(view);
            await vm.SelectBayAsync(ground);
            VStripsViewInteractionTests.RealizeContainers(view);

            // The dragged strip must exist only as the cursor ghost — every
            // rack-mounted rendering of it stays hidden while the drag lives.
            var rackMountedS1 = view.GetVisualDescendants()
                .OfType<FlightStripControl>()
                .Where(c =>
                    ((c.DataContext as StripItemViewModel)?.Id == "S1")
                    && c.GetVisualAncestors().OfType<Border>().Any(b => b.Tag is StripRackViewModel)
                )
                .ToList();
            Assert.NotEmpty(rackMountedS1);
            Assert.All(rackMountedS1, c => Assert.False(c.IsEffectivelyVisible, "dragged strip's rack copy resurrected after bay round-trip"));

            // Cancelling restores exactly one visible copy.
            window.DispatchKey(Key.Escape);
            VStripsViewInteractionTests.RealizeContainers(view);
            var visibleAfterCancel = view.GetVisualDescendants()
                .OfType<FlightStripControl>()
                .Count(c =>
                    ((c.DataContext as StripItemViewModel)?.Id == "S1")
                    && c.GetVisualAncestors().OfType<Border>().Any(b => b.Tag is StripRackViewModel)
                    && c.IsEffectivelyVisible
                );
            Assert.Equal(1, visibleAfterCancel);
            Assert.Empty(captured);

            window.MouseUp(new Point(start.X + 20, start.Y - 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Pumps the dispatcher until <paramref name="captured"/> reaches
    /// <paramref name="count"/> entries (or 2s elapse). Drop dispatches run
    /// after the ~130ms ghost-settle animation, so canonicals arrive on a
    /// posted continuation rather than synchronously on MouseUp.
    /// </summary>
    private static void WaitForCaptured(List<(string Callsign, string Command)> captured, int count)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while ((captured.Count < count) && (stopwatch.ElapsedMilliseconds < 2000))
        {
            System.Threading.Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Pumps the dispatcher past the longest settle window so a test can
    /// assert that a drop deliberately emitted NOTHING (no-op moves,
    /// cancelled drags) without racing the delayed dispatch.
    /// </summary>
    private static void WaitOutSettle()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 400)
        {
            System.Threading.Thread.Sleep(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static FlightStripControl StripControl(VStripsView view, string stripId) =>
        view.GetVisualDescendants()
            .OfType<FlightStripControl>()
            .Single(c =>
                ((c.DataContext as StripItemViewModel)?.Id == stripId)
                && c.GetVisualAncestors().OfType<Border>().Any(b => b.Tag is StripRackViewModel)
            );

    /// <summary>A point inside the rack-rendered strip, in window coordinates.</summary>
    private static Point StripPoint(Window window, VStripsView view, string stripId)
    {
        var control = StripControl(view, stripId);
        var point = control.TranslatePoint(new Point(30, 20), window);
        Assert.NotNull(point);
        return point.Value;
    }

    private static Border RackBorder(VStripsView view, int rackIndex) =>
        view.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is StripRackViewModel r && r.RackIndex == rackIndex);
}
