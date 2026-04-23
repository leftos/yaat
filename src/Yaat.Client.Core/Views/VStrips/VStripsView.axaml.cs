using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Code-behind for the vStrips view. Handles user input that can't be expressed
/// cleanly in XAML: bay selection clicks, drag/drop of strips between racks and
/// onto the trash zone, and the keyboard shortcut map from docs/crc/vstrips.md.
///
/// Every user action funnels through <see cref="VStripsViewModel"/> helpers
/// which emit canonical commands — the view never mutates strip state directly.
/// </summary>
public partial class VStripsView : UserControl
{
    // Application-scoped data format for drag/drop. Strip ids fit in a string so
    // the drop target can resolve the view-model through VStripsViewModel.ItemsById
    // without marshalling anything across the drag boundary.
    private static readonly DataFormat<string> StripIdFormat = DataFormat.CreateStringApplicationFormat("yaat.strip-id");

    // Ghost overlay state for the drag preview. Lives for the duration of a
    // single DoDragDropAsync invocation; cleared in the finally block.
    private Control? _dragGhost;
    private StripItemViewModel? _draggingStrip;
    private StripRackViewModel? _draggingFromRack;
    private int _draggingFromIndex = -1;

    // Drag-hover bay-preview state (docs/crc/vstrips.md:217). When the user
    // hovers a drag over a bay header for >500ms without dropping, we
    // temporarily switch SelectedBay to that bay so they can pick a specific
    // rack. Restored on drag-leave / drop / drag-ended.
    private StripBayViewModel? _hoverBay;
    private StripBayViewModel? _preHoverSelectedBay;
    private Avalonia.Threading.DispatcherTimer? _hoverTimer;

    public VStripsView()
    {
        InitializeComponent();

        // Root-level AllowDrop is load-bearing: without it, DragOver/Drop events
        // from deep child targets (inside rack borders, strips) never bubble to
        // any handler. Every concrete drop zone also sets AllowDrop on itself
        // in its Loaded handler — belt-and-suspenders because Avalonia only
        // fires DragOver on the nearest AllowDrop element in the hit path.
        DragDrop.SetAllowDrop(this, true);

        var trash = this.FindControl<Border>("TrashZone");
        if (trash is not null)
        {
            DragDrop.SetAllowDrop(trash, true);
            trash.AddHandler(DragDrop.DragOverEvent, OnTrashDragOver);
            trash.AddHandler(DragDrop.DropEvent, OnTrashDrop);
        }

        // Drag-source wiring at the UserControl level (Tunnel) so pointer
        // presses on any strip — rack or printer — can initiate a drag.
        AddHandler(PointerPressedEvent, OnStripPointerPressed, RoutingStrategies.Tunnel);

        // DragOver + Drop at the root level. DragOver paints the drop-effects
        // cursor + drives the ghost. Drop walks up from e.Source to figure
        // out which zone (rack / trash / bay button) the drop belongs to.
        // A root-level drop handler is the only reliable path — rack Borders
        // are created inside a DataTemplate and their Loaded events don't
        // fire consistently across Avalonia rebuilds, so per-border wiring
        // silently drops events when the Loaded callback misses.
        AddHandler(DragDrop.DragOverEvent, OnGenericDragOver);
        AddHandler(DragDrop.DropEvent, OnRootDrop);
    }

    // ── Bay selection ───────────────────────────────────────────

    private async void OnBayButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StripBayViewModel bay } && DataContext is VStripsViewModel vm)
        {
            await vm.SelectBayAsync(bay);
        }
    }

    private void OnFacilityButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not VStripsViewModel vm || vm.AccessibleFacilities.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach (var facility in vm.AccessibleFacilities)
        {
            var header = facility.IsStudentFacility ? $"{facility.FacilityName} (own)" : facility.FacilityName;
            var item = new MenuItem { Header = header, Tag = facility };
            item.Click += async (_, _) =>
            {
                if (item.Tag is Yaat.Client.Services.AccessibleFacilityDto f)
                {
                    await vm.SwitchFacilityAsync(f.FacilityId);
                }
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    private void OnBayButtonLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            DragDrop.SetAllowDrop(button, true);
            button.AddHandler(DragDrop.DragOverEvent, OnBayButtonDragOver);
            button.AddHandler(DragDrop.DragLeaveEvent, OnBayButtonDragLeave);
            button.AddHandler(DragDrop.DropEvent, OnBayButtonDrop);
        }
    }

    /// <summary>
    /// Starts a 500ms hover timer the first time the drag enters a bay
    /// button. When it elapses without the drag leaving, temporarily switches
    /// the main view to the hovered bay so the user can aim at a specific
    /// rack inside it. Matches CRC docs/crc/vstrips.md:217.
    /// </summary>
    private void OnBayButtonDragOver(object? sender, DragEventArgs e)
    {
        OnGenericDragOver(sender, e);
        if (sender is not Button { Tag: StripBayViewModel bay } || DataContext is not VStripsViewModel vm || bay.IsExternal)
        {
            return;
        }
        if (ReferenceEquals(_hoverBay, bay))
        {
            return;
        }
        _hoverBay = bay;
        _preHoverSelectedBay ??= vm.SelectedBay;
        _hoverTimer?.Stop();
        _hoverTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            if (_hoverBay is not null && DataContext is VStripsViewModel currentVm)
            {
                _ = currentVm.SelectBayAsync(_hoverBay);
            }
        };
        _hoverTimer.Start();
    }

    /// <summary>
    /// Cancels the hover timer if the drag leaves before 500ms elapses. Does
    /// NOT restore the pre-hover bay here — that happens when the whole drag
    /// ends (drop or cancel) via <see cref="HideDragGhost"/>, so the preview
    /// sticks if the user drags back over the bay after a brief detour.
    /// </summary>
    private void OnBayButtonDragLeave(object? sender, DragEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        _hoverBay = null;
    }

    private async void OnBayButtonDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Button { Tag: StripBayViewModel bay } || DataContext is not VStripsViewModel vm || e.DataTransfer is null)
        {
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip))
        {
            return;
        }

        // Dropped on a bay button (push target) without a specific slot —
        // append to the tail of rack 0 (CRC bottom-up first-available).
        await vm.MoveStripAsync(strip, bay, rack: 0, index: null);
        e.Handled = true;
    }

    // ── Rack drop zone ───────────────────────────────────────────

    /// <summary>
    /// Root-level drop handler. Walks up from the hit target to find a rack
    /// Border (marked with <c>Tag = StripRackViewModel</c> by the rack
    /// DataTemplate). When found, computes the drop index inside that rack
    /// and dispatches a move via <see cref="VStripsViewModel.MoveStripAsync"/>.
    /// Drops outside any rack (bay header, trash, printer modal) fall through
    /// — those zones install their own local drop handlers in Loaded hooks.
    /// </summary>
    private async void OnRootDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm || e.DataTransfer is null || e.Source is not Visual hit)
        {
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip) || vm.SelectedBay is null)
        {
            return;
        }

        // Walk up to find the enclosing rack Border.
        Border? rackBorder = null;
        Visual? v = hit;
        while (v is not null)
        {
            if (v is Border b && b.Tag is StripRackViewModel)
            {
                rackBorder = b;
                break;
            }
            v = v.GetVisualParent() as Visual;
        }
        if (rackBorder is null || rackBorder.Tag is not StripRackViewModel rack)
        {
            return;
        }

        var index = ComputeDropIndex(rackBorder, rack, e);
        await vm.MoveStripAsync(strip, vm.SelectedBay, rack.RackIndex, index);
        e.Handled = true;
    }

    /// <summary>No-op — rack-border drop is handled via <see cref="OnRootDrop"/>.</summary>
    private void OnRackBorderLoaded(object? sender, RoutedEventArgs e)
    {
        // Intentionally empty: root-level drop handler in the constructor
        // handles rack drops independent of DataTemplate Loaded timing.
    }

    /// <summary>
    /// Computes the zero-based insertion index for a drop inside a rack. Walks
    /// the rack's strip children, finds which one the pointer is over, and
    /// rounds to before/after based on whether the pointer sits in the top or
    /// bottom half of that strip. Drops below every strip append (index =
    /// rack.Strips.Count).
    /// </summary>
    private static int ComputeDropIndex(Border rackBorder, StripRackViewModel rack, DragEventArgs e)
    {
        var stripsHost = rackBorder.FindDescendantOfType<ItemsControl>();
        if (stripsHost is null || rack.Strips.Count == 0)
        {
            return 0;
        }

        var pos = e.GetPosition(stripsHost);
        var approxStripHeight = stripsHost.Bounds.Height / Math.Max(rack.Strips.Count, 1);
        if (approxStripHeight <= 0)
        {
            return rack.Strips.Count;
        }
        var idx = (int)(pos.Y / approxStripHeight);
        return Math.Clamp(idx, 0, rack.Strips.Count);
    }

    // ── Drag source ─────────────────────────────────────────────

    private async void OnStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        if (e.Source is not Visual hit || DataContext is not VStripsViewModel vm)
        {
            return;
        }

        var stripView = hit.FindAncestorOfType<FlightStripControl>();

        // Right-click: strip → strip context menu; empty rack space → empty-rack
        // menu (add half-strip / separator / blank). Matches CRC's docs/crc/
        // vstrips.md:186 (add separator) and :180 (add half-strip).
        if (props.IsRightButtonPressed)
        {
            if (stripView?.Tag is StripItemViewModel rcStrip)
            {
                ShowStripContextMenu(stripView, rcStrip, vm);
                e.Handled = true;
                return;
            }
            var rackBorder = FindRackBorder(hit);
            if (rackBorder?.Tag is StripRackViewModel rack)
            {
                ShowEmptyRackMenu(rackBorder, rack, vm);
                e.Handled = true;
                return;
            }
            return;
        }

        if (stripView?.Tag is not StripItemViewModel strip)
        {
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        vm.SelectedStrip = strip;

        // Annotation cells handle their own click via the Bubble-phase handler
        // inside FlightStripControl to open the inline editor. Skip drag
        // initiation here so the cell handler can run.
        if (IsAnnotationCellHit(hit))
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await vm.ToggleOffsetAsync(strip);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            await vm.DeleteStripAsync(strip);
            e.Handled = true;
            return;
        }

        // Record the origin rack so MoveStripAsync can skip the no-op when the
        // user drops on the exact same position. Walk the visual tree to find
        // the StripRackViewModel from the source strip's Border ancestor.
        (_draggingFromRack, _draggingFromIndex) = FindStripOrigin(stripView, strip);
        _draggingStrip = strip;

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(StripIdFormat, strip.Id));

        try
        {
            ShowDragGhost(strip, e);
            await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);
        }
        finally
        {
            HideDragGhost();
            _draggingStrip = null;
            _draggingFromRack = null;
            _draggingFromIndex = -1;
        }
    }

    /// <summary>
    /// Finds the rack + index a strip currently lives in by walking up from the
    /// strip's control to the enclosing rack Border (whose Tag is a
    /// <see cref="StripRackViewModel"/>). Returns (null, -1) when the strip is
    /// in the printer queue — printer drags have no origin rack and should
    /// always move.
    /// </summary>
    private static (StripRackViewModel? rack, int index) FindStripOrigin(FlightStripControl stripView, StripItemViewModel strip)
    {
        Visual? v = stripView;
        while (v is not null)
        {
            if (v is Border b && b.Tag is StripRackViewModel r)
            {
                return (r, r.Strips.IndexOf(strip));
            }
            v = v.GetVisualParent() as Visual;
        }
        return (null, -1);
    }

    // ── Drag ghost (preview that follows the cursor) ────────────

    /// <summary>
    /// Creates a semi-transparent clone of the strip's FlightStripControl in
    /// the DragGhostCanvas so the user sees the strip under the cursor during
    /// the drag. Avalonia's built-in DoDragDropAsync renders no preview, so we
    /// roll our own Canvas-overlay ghost. Position is set from the current
    /// pointer press point and updated in OnGenericDragOver.
    /// </summary>
    private void ShowDragGhost(StripItemViewModel strip, PointerEventArgs e)
    {
        var canvas = this.FindControl<Canvas>("DragGhostCanvas");
        if (canvas is null)
        {
            return;
        }
        var ghost = new FlightStripControl
        {
            DataContext = strip,
            Tag = strip,
            Opacity = 0.75,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(ghost);
        _dragGhost = ghost;
        UpdateGhostPosition(e.GetPosition(canvas));
    }

    private void UpdateGhostPosition(Point pointerInCanvas)
    {
        if (_dragGhost is null)
        {
            return;
        }
        // Offset the ghost so the cursor sits inside the strip rather than at
        // the top-left corner — matches CRC's feel where the strip "sticks" to
        // the pointer as if you grabbed it somewhere in the middle.
        Canvas.SetLeft(_dragGhost, pointerInCanvas.X - 24);
        Canvas.SetTop(_dragGhost, pointerInCanvas.Y - 16);
    }

    private void HideDragGhost()
    {
        var canvas = this.FindControl<Canvas>("DragGhostCanvas");
        if (canvas is not null && _dragGhost is not null)
        {
            canvas.Children.Remove(_dragGhost);
        }
        _dragGhost = null;

        // Restore the pre-hover selected bay if the drag ended while a bay
        // preview was active. We only restore if the user DID NOT drop on
        // that bay (in which case SelectBayAsync already ran and the hover
        // bay is now the valid selection).
        _hoverTimer?.Stop();
        _hoverTimer = null;
        _hoverBay = null;
        if (_preHoverSelectedBay is not null && DataContext is VStripsViewModel vm && vm.SelectedBay != _preHoverSelectedBay)
        {
            // Only auto-restore if the user didn't explicitly drop — i.e.
            // the bay switched to a hovered bay and the drag was cancelled.
            // Practical heuristic: if the current bay is the hover target,
            // keep it (user's choice); else restore.
            // For simplicity: always restore. If user intended to switch they
            // can click the bay button — the drag hover is a preview-only.
            _ = vm.SelectBayAsync(_preHoverSelectedBay);
        }
        _preHoverSelectedBay = null;
    }

    // ── Strip context menu ──────────────────────────────────────

    /// <summary>
    /// Full right-click menu for a strip: offset / delete / push-to-bay, plus
    /// type-specific items (slide + edit lines for half-strips, edit label for
    /// separators). Matches the CRC context menu described in
    /// docs/crc/vstrips.md:197 (Offset), :221 (Push), :180 (half-strip slide),
    /// :193 (separator edit).
    /// </summary>
    private void ShowStripContextMenu(Control anchor, StripItemViewModel strip, VStripsViewModel vm)
    {
        var menu = new MenuFlyout();

        var offsetItem = new MenuItem { Header = strip.IsOffset ? "Un-offset" : "Offset" };
        offsetItem.Click += async (_, _) => await vm.ToggleOffsetAsync(strip);
        menu.Items.Add(offsetItem);

        if (strip.IsHalfStrip)
        {
            var slideItem = new MenuItem { Header = "Slide" };
            slideItem.Click += async (_, _) => await vm.SlideHalfStripAsync(strip);
            menu.Items.Add(slideItem);

            var editLines = new MenuItem { Header = "Edit lines" };
            editLines.Click += (_, _) =>
            {
                var editor = this.FindControl<InlineTextEditPopup>("InlineEditor");
                if (editor is null)
                {
                    return;
                }
                var initial = string.Join(" / ", strip.FieldValues.Where(v => !string.IsNullOrEmpty(v)));
                editor.Open(
                    anchor,
                    initial,
                    text =>
                    {
                        var parts = text.Split(" / ", StringSplitOptions.None);
                        _ = vm.AmendHalfStripAsync(strip, parts);
                    }
                );
            };
            menu.Items.Add(editLines);
        }

        if (strip.IsSeparator)
        {
            var editLabel = new MenuItem { Header = "Edit label" };
            editLabel.Click += (_, _) =>
            {
                var editor = this.FindControl<InlineTextEditPopup>("InlineEditor");
                if (editor is null)
                {
                    return;
                }
                var initial = strip.FieldValues.Length > 0 ? strip.FieldValues[0] : "";
                editor.Open(anchor, initial, text => _ = vm.EditSeparatorLabelAsync(strip, text));
            };
            menu.Items.Add(editLabel);
        }

        var pushMenu = new MenuItem { Header = "Push to" };
        foreach (var bay in vm.Bays)
        {
            var baySnapshot = bay;
            var item = new MenuItem { Header = bay.IsExternal ? $"{bay.Name}  ↗" : bay.Name };
            // "Push to <bay>" from the context menu appends to the tail of
            // rack 0 — the new strip takes the first-available bottom slot.
            item.Click += async (_, _) => await vm.MoveStripAsync(strip, baySnapshot, rack: 0, index: null);
            pushMenu.Items.Add(item);
        }
        menu.Items.Add(pushMenu);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += async (_, _) => await vm.DeleteStripAsync(strip);
        menu.Items.Add(deleteItem);

        menu.ShowAt(anchor);
    }

    // ── Empty-rack context menu ─────────────────────────────────

    /// <summary>
    /// Right-click menu for empty rack space: add half-strip / separator /
    /// blank. Runs via the existing <see cref="VStripsViewModel"/> create*
    /// helpers which emit canonical commands. Separators expose a submenu for
    /// the four CRC styles (handwritten / white / red / green) and are hidden
    /// when <see cref="VStripsViewModel.SeparatorsLocked"/> is true per the
    /// ARTCC config (docs/crc/vstrips.md:195).
    /// </summary>
    private static void ShowEmptyRackMenu(Control anchor, StripRackViewModel rack, VStripsViewModel vm)
    {
        if (vm.SelectedBay is null)
        {
            return;
        }
        var selectedBay = vm.SelectedBay;
        var menu = new MenuFlyout();

        var addHalfStrip = new MenuItem { Header = "Add half-strip" };
        addHalfStrip.Click += async (_, _) => await vm.CreateHalfStripAsync(selectedBay, rack.RackIndex, lines: Array.Empty<string>());
        menu.Items.Add(addHalfStrip);

        if (!vm.SeparatorsLocked)
        {
            var addSeparator = new MenuItem { Header = "Add separator" };
            foreach (var style in new[] { SeparatorStyle.Handwritten, SeparatorStyle.White, SeparatorStyle.Red, SeparatorStyle.Green })
            {
                var styleSnapshot = style;
                var item = new MenuItem { Header = style.ToString() };
                item.Click += async (_, _) => await vm.CreateSeparatorAsync(styleSnapshot, selectedBay, rack.RackIndex, index: 0, label: null);
                addSeparator.Items.Add(item);
            }
            menu.Items.Add(addSeparator);
        }
        else
        {
            // Locked facilities still allow handwritten separators
            // (docs/crc/vstrips.md:195).
            var addHandwritten = new MenuItem { Header = "Add handwritten separator" };
            addHandwritten.Click += async (_, _) =>
                await vm.CreateSeparatorAsync(SeparatorStyle.Handwritten, selectedBay, rack.RackIndex, index: 0, label: null);
            menu.Items.Add(addHandwritten);
        }

        var addBlank = new MenuItem { Header = "Add blank strip" };
        addBlank.Click += async (_, _) => await vm.CreateBlankAsync(selectedBay, rack.RackIndex, index: 0);
        menu.Items.Add(addBlank);

        menu.ShowAt(anchor);
    }

    /// <summary>
    /// True when the hit target sits inside a FlightStripControl annotation
    /// cell — Border whose Tag is a numeric string "1".."9". Used to suppress
    /// drag initiation so the cell's own click handler can open the inline
    /// editor in the Bubble phase.
    /// </summary>
    private static bool IsAnnotationCellHit(Visual hit)
    {
        Visual? v = hit;
        while (v is not null && v is not FlightStripControl)
        {
            if (v is Border b && b.Tag is string tag && tag.Length == 1 && tag[0] >= '1' && tag[0] <= '9')
            {
                return true;
            }
            v = v.GetVisualParent() as Visual;
        }
        return false;
    }

    /// <summary>
    /// Walks up the visual tree from a hit target to the enclosing rack Border
    /// (marked with <c>Tag = StripRackViewModel</c> by the rack DataTemplate).
    /// Returns null for clicks outside any rack (header, trash zone, printer
    /// panel), which the caller uses to skip empty-rack menus.
    /// </summary>
    private static Border? FindRackBorder(Visual hit)
    {
        Visual? v = hit;
        while (v is not null)
        {
            if (v is Border b && b.Tag is StripRackViewModel)
            {
                return b;
            }
            v = v.GetVisualParent() as Visual;
        }
        return null;
    }

    // ── Generic drag-over handler ───────────────────────────────

    private void OnGenericDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.Contains(StripIdFormat) == true)
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        // Update the ghost to follow the pointer. DragOver fires continuously
        // over AllowDrop zones, so the ghost tracks smoothly as the user moves
        // the pointer across racks, bay buttons, or the trash zone.
        var canvas = this.FindControl<Canvas>("DragGhostCanvas");
        if (canvas is not null)
        {
            UpdateGhostPosition(e.GetPosition(canvas));
        }
    }

    // ── Trash drop target ───────────────────────────────────────

    private void OnTrashDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(StripIdFormat) == true ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTrashDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm || e.DataTransfer is null)
        {
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip))
        {
            return;
        }

        await vm.DeleteStripAsync(strip);
        e.Handled = true;
    }

    // ── Printer modal actions ───────────────────────────────────

    private void OnPrinterCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            vm.Printer.IsOpen = false;
        }
    }

    private async void OnRequestStripClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm)
        {
            return;
        }
        var box = this.FindControl<TextBox>("RequestStripInput");
        var aircraftId = box?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(aircraftId))
        {
            return;
        }
        await vm.RequestStripAsync(aircraftId);
        if (box is not null)
        {
            box.Text = "";
        }
    }

    private async void OnPrintBlankClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.PrintBlankStripAsync();
        }
    }

    private void OnDeparturePrevClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            vm.Printer.PreviousDeparture();
        }
    }

    private void OnDepartureNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            vm.Printer.NextDeparture();
        }
    }

    private async void OnDepartureMoveToBayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.MoveVisiblePrinterStripToBayAsync(PrinterQueueKind.Departure);
        }
    }

    private async void OnDepartureDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.DeleteVisiblePrinterStripAsync(PrinterQueueKind.Departure);
        }
    }

    private void OnArrivalPrevClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            vm.Printer.PreviousArrival();
        }
    }

    private void OnArrivalNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            vm.Printer.NextArrival();
        }
    }

    private async void OnArrivalMoveToBayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.MoveVisiblePrinterStripToBayAsync(PrinterQueueKind.Arrival);
        }
    }

    private async void OnArrivalDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.DeleteVisiblePrinterStripAsync(PrinterQueueKind.Arrival);
        }
    }

    // ── Keyboard shortcuts ──────────────────────────────────────

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not VStripsViewModel vm)
        {
            base.OnKeyDown(e);
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Bay cycling (docs/crc/vstrips.md:281).
        if (e.Key == Key.PageDown && !ctrl && !alt)
        {
            await vm.NextBayAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp && !ctrl && !alt)
        {
            await vm.PreviousBayAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+H / Ctrl+Shift+S — add half-strip / add separator
        // (docs/crc/vstrips.md:279-280). Target rack = selected strip's rack if
        // a strip is selected, else rack 0.
        if (ctrl && shift && e.Key is Key.H or Key.S)
        {
            if (vm.SelectedBay is not null)
            {
                var targetRack = FindSelectedStripRack(vm) ?? 0;
                if (e.Key == Key.H)
                {
                    await vm.CreateHalfStripAsync(vm.SelectedBay, targetRack, Array.Empty<string>());
                }
                else
                {
                    // Default to handwritten — users can cycle styles via the
                    // separator right-click menu afterwards.
                    await vm.CreateSeparatorAsync(SeparatorStyle.Handwritten, vm.SelectedBay, targetRack, 0, label: null);
                }
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Alt+1..9: push selected strip to bay (if selected) else switch
        // to bay. Ctrl+Alt+←/→: cycle facility.
        if (ctrl && alt)
        {
            if (e.Key is Key.Left or Key.Right && vm.AccessibleFacilities.Count > 0)
            {
                var currentIdx = 0;
                for (var i = 0; i < vm.AccessibleFacilities.Count; i++)
                {
                    if (vm.AccessibleFacilities[i].FacilityId == vm.FacilityId)
                    {
                        currentIdx = i;
                        break;
                    }
                }
                var step = e.Key == Key.Right ? 1 : -1;
                var count = vm.AccessibleFacilities.Count;
                var nextIdx = ((currentIdx + step) % count + count) % count;
                await vm.SwitchFacilityAsync(vm.AccessibleFacilities[nextIdx].FacilityId);
                e.Handled = true;
                return;
            }

            var bayIdx = KeyToDigit(e.Key) - 1;
            if (bayIdx >= 0 && bayIdx < vm.Bays.Count)
            {
                if (vm.SelectedStrip is { } sel)
                {
                    // Keyboard shortcut "move to bay N" appends at the tail.
                    await vm.MoveStripAsync(sel, vm.Bays[bayIdx], rack: 0, index: null);
                }
                else
                {
                    await vm.SelectBayAsync(vm.Bays[bayIdx]);
                }
                e.Handled = true;
                return;
            }
        }

        // Ctrl+1..9 on full strip — open inline editor for annotation box
        // 10..18. Plain number keys map 1→box 1 (rendered as "10") … 9→box 9
        // (rendered as "18"). Supports both main-row digits and Numpad.
        if (ctrl && !alt && !shift && vm.SelectedStrip is { IsFullStrip: true } editStrip)
        {
            var box = KeyToDigit(e.Key);
            if (box >= 1 && box <= 9)
            {
                OpenAnnotationEditorForSelected(vm, editStrip, box);
                e.Handled = true;
                return;
            }
        }

        // Shift+←/→: toggle offset on the selected strip.
        if (shift && !ctrl && !alt && e.Key is Key.Left or Key.Right)
        {
            if (vm.SelectedStrip is { } sel && (sel.IsFullStrip || sel.IsHalfStrip))
            {
                await vm.ToggleOffsetAsync(sel);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Shift+←/→ on half-strip: slide. On separator: cycle style via
        // delete+create (handwritten → white → red → green → handwritten).
        if (ctrl && shift && e.Key is Key.Left or Key.Right)
        {
            if (vm.SelectedStrip is { IsHalfStrip: true } halfSel)
            {
                await vm.SlideHalfStripAsync(halfSel);
                e.Handled = true;
                return;
            }
            if (vm.SelectedStrip is { IsSeparator: true } sepSel)
            {
                await CycleSeparatorStyleAsync(vm, sepSel, e.Key == Key.Right);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+←→↑↓ (no shift/alt) — move selected strip.
        if (ctrl && !shift && !alt && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            if (vm.SelectedStrip is not null)
            {
                await vm.MoveSelectedStripAsync(KeyToDirection(e.Key));
                e.Handled = true;
                return;
            }
        }

        // Plain arrow keys — move selection (if a strip is selected) or pick
        // the first strip (if nothing is selected yet).
        if (!ctrl && !shift && !alt && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            vm.SelectAdjacentStrip(KeyToDirection(e.Key));
            e.Handled = true;
            return;
        }

        // Enter on half-strip → edit lines; on separator → edit label.
        if (!ctrl && !shift && !alt && e.Key == Key.Enter && vm.SelectedStrip is { } enterSel)
        {
            var anchor = this.FindControl<Canvas>("DragGhostCanvas");
            var editor = this.FindControl<InlineTextEditPopup>("InlineEditor");
            if (anchor is null || editor is null)
            {
                return;
            }
            if (enterSel.IsHalfStrip)
            {
                var initial = string.Join(" / ", enterSel.FieldValues.Where(v => !string.IsNullOrEmpty(v)));
                editor.Open(anchor, initial, text => _ = vm.AmendHalfStripAsync(enterSel, text.Split(" / ", StringSplitOptions.None)));
                e.Handled = true;
                return;
            }
            if (enterSel.IsSeparator)
            {
                var initial = enterSel.FieldValues.Length > 0 ? enterSel.FieldValues[0] : "";
                editor.Open(anchor, initial, text => _ = vm.EditSeparatorLabelAsync(enterSel, text));
                e.Handled = true;
                return;
            }
        }

        // Tab — toggle printer panel (docs/crc/vstrips.md:286).
        if (e.Key == Key.Tab)
        {
            vm.Printer.IsOpen = !vm.Printer.IsOpen;
            e.Handled = true;
            return;
        }

        // Esc — deselect if a strip is selected, else toggle printer panel.
        // Docs distinguish Esc as the facility-menu key but the facility menu
        // is our bay switcher flyout, not a modal — fall back to the printer
        // toggle to keep parity with the pre-round-4 behavior.
        if (e.Key == Key.Escape)
        {
            if (vm.SelectedStrip is not null)
            {
                vm.SelectedStrip = null;
            }
            else
            {
                vm.Printer.IsOpen = !vm.Printer.IsOpen;
            }
            e.Handled = true;
            return;
        }

        // Delete / Backspace — delete selected strip.
        if (e.Key is Key.Delete or Key.Back)
        {
            if (vm.SelectedStrip is { } deleteSel)
            {
                await vm.DeleteStripAsync(deleteSel);
                e.Handled = true;
            }
            return;
        }

        base.OnKeyDown(e);
    }

    private static NavDirection KeyToDirection(Key key) =>
        key switch
        {
            Key.Up => NavDirection.Up,
            Key.Down => NavDirection.Down,
            Key.Left => NavDirection.Left,
            _ => NavDirection.Right,
        };

    private static int KeyToDigit(Key key) =>
        key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0,
        };

    private static int? FindSelectedStripRack(VStripsViewModel vm)
    {
        if (vm.SelectedStrip is null || vm.SelectedBay is null)
        {
            return null;
        }
        for (var r = 0; r < vm.SelectedBay.Racks.Count; r++)
        {
            if (vm.SelectedBay.Racks[r].Strips.Contains(vm.SelectedStrip))
            {
                return r;
            }
        }
        return null;
    }

    /// <summary>
    /// Opens the inline editor anchored to the VStripsView for the given
    /// annotation box number on the selected strip. Used by the Ctrl+1..9
    /// keyboard shortcut — the clicked cell handler is the usual path for
    /// mouse-driven edits.
    /// </summary>
    private void OpenAnnotationEditorForSelected(VStripsViewModel vm, StripItemViewModel strip, int box)
    {
        var editor = this.FindControl<InlineTextEditPopup>("InlineEditor");
        if (editor is null || strip.AircraftId is null)
        {
            return;
        }
        var anchor = this.FindControl<Canvas>("DragGhostCanvas") ?? (Control)this;
        var current = box switch
        {
            1 => strip.Annotation10,
            2 => strip.Annotation11,
            3 => strip.Annotation12,
            4 => strip.Annotation13,
            5 => strip.Annotation14,
            6 => strip.Annotation15,
            7 => strip.Annotation16,
            8 => strip.Annotation17,
            9 => strip.Annotation18,
            _ => "",
        };
        editor.Open(anchor, current, text => _ = vm.AnnotateAsync(strip, box, text));
    }

    /// <summary>
    /// Cycles a separator's style by deleting and recreating with the next
    /// (or previous) style from the CRC set {Handwritten, White, Red, Green}.
    /// Skipped when SeparatorsLocked is true unless the target style is also
    /// Handwritten (docs/crc/vstrips.md:195).
    /// </summary>
    private static async Task CycleSeparatorStyleAsync(VStripsViewModel vm, StripItemViewModel strip, bool forward)
    {
        if (vm.SelectedBay is null)
        {
            return;
        }
        var order = new[] { SeparatorStyle.Handwritten, SeparatorStyle.White, SeparatorStyle.Red, SeparatorStyle.Green };
        var cur = strip.Type switch
        {
            StripItemType.WhiteSeparator => SeparatorStyle.White,
            StripItemType.RedSeparator => SeparatorStyle.Red,
            StripItemType.GreenSeparator => SeparatorStyle.Green,
            _ => SeparatorStyle.Handwritten,
        };
        var curIdx = Array.IndexOf(order, cur);
        var step = forward ? 1 : -1;
        var nextIdx = ((curIdx + step) % order.Length + order.Length) % order.Length;
        var nextStyle = order[nextIdx];
        if (vm.SeparatorsLocked && nextStyle != SeparatorStyle.Handwritten)
        {
            return;
        }

        // Replicate the delete+create pattern from EditSeparatorLabelAsync with
        // the same label but new style.
        var rack = -1;
        var index = -1;
        for (var r = 0; r < vm.SelectedBay.Racks.Count; r++)
        {
            var idx = vm.SelectedBay.Racks[r].Strips.IndexOf(strip);
            if (idx >= 0)
            {
                rack = r;
                index = idx;
                break;
            }
        }
        if (rack < 0)
        {
            return;
        }
        var label = strip.FieldValues.Length > 0 ? strip.FieldValues[0] : null;
        var del = VStripsCanonicalBuilder.BuildSeparatorDelete(vm.SelectedBay.Name, rack, label, index);
        var create = VStripsCanonicalBuilder.BuildSeparatorCreate(nextStyle, vm.SelectedBay.Name, rack, index, label);
        // Use the public dispatch through a create-call bounded by the
        // separator lock check we just did; reuse EditSeparatorLabelAsync's
        // _sendCommand path by going through the canonical builders directly.
        await vm.DispatchRawAsync(del);
        await vm.DispatchRawAsync(create);
    }

    internal (StripRackViewModel? rack, int index) CurrentDragOrigin => (_draggingFromRack, _draggingFromIndex);
}
