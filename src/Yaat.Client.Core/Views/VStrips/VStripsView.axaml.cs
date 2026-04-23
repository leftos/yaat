using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
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
    private static readonly ILogger Log = AppLog.CreateLogger<VStripsView>();

    // Application-scoped data format for drag/drop. Strip ids fit in a string so
    // the drop target can resolve the view-model through VStripsViewModel.ItemsById
    // without marshalling anything across the drag boundary.
    private static readonly DataFormat<string> StripIdFormat = DataFormat.CreateStringApplicationFormat("yaat.strip-id");

    // Ghost overlay state for the drag preview. Lives for the duration of a
    // single DoDragDropAsync invocation; cleared in the finally block.
    private Control? _dragGhost;
    private TranslateTransform? _dragGhostTransform;
    private StripItemViewModel? _draggingStrip;
    private StripRackViewModel? _draggingFromRack;
    private int _draggingFromIndex = -1;

    // Source ContentPresenter hidden during drag so the dragged strip only
    // appears as the cursor-tracked ghost — the rack's DockPanel collapses
    // the slot, which also keeps ComputeDropIndex from treating the source's
    // own position as a valid drop target.
    private ContentPresenter? _draggingSourcePresenter;

    // Per-rack cache of (ContentPresenter, vm) pairs for the duration of a
    // drag. Windows throttles DragOver events to ~30 Hz, and each event
    // would otherwise walk the full visual subtree of every rack the pointer
    // enters via GetVisualDescendants().OfType<ContentPresenter>() — for a
    // 5-strip rack with nested FlightStripControl children, that's 100+
    // allocations per event. The cache populates on first entry into a rack
    // (after the source hide has settled) and reuses for subsequent events
    // over the same rack. Top-Y positions are still re-read each time (they
    // change as the preview margin shifts) but the lookup is a direct index
    // into the cached list, not a tree walk. Cleared in HideDragGhost.
    private readonly Dictionary<StripRackViewModel, List<(ContentPresenter Presenter, StripItemViewModel Vm)>> _presenterCache = [];

    // Drag-hover bay-preview state (docs/crc/vstrips.md:217). When the user
    // hovers a drag over a bay header for >500ms without dropping, we
    // temporarily switch SelectedBay to that bay so they can pick a specific
    // rack. Restored on drag-leave / drop / drag-ended.
    private StripBayViewModel? _hoverBay;
    private StripBayViewModel? _preHoverSelectedBay;
    private Avalonia.Threading.DispatcherTimer? _hoverTimer;

    // Drop-preview state. While dragging over a rack, we shift the strip at
    // the computed target index up by the dragged strip's height to open a
    // visible gap where the drop will land. For the append case (index ==
    // count) we overlay a yellow insertion line above the topmost strip
    // instead (no strip to shift). Cleared when the rack/index changes,
    // when the pointer leaves all racks, on drop, and on drag cancel.
    private StripRackViewModel? _dropPreviewRack;
    private int _dropPreviewIndex = -1;
    private ContentPresenter? _dropPreviewShiftedPresenter;
    private Thickness _dropPreviewOriginalMargin;
    private Border? _dropPreviewLine;
    private Grid? _dropPreviewLineHost;

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
        // cursor + drives the ghost. Drop hit-tests the pointer position to
        // figure out which zone (rack / trash / bay button) the drop belongs
        // to. Root-level registration keeps the wiring independent of the
        // DataTemplate Loaded timing for rack Borders.
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
        Log.LogInformation("OnRootDrop fired: source={SourceType} hasDataTransfer={HasDt}", e.Source?.GetType().Name, e.DataTransfer is not null);
        if (DataContext is not VStripsViewModel vm || e.DataTransfer is null)
        {
            Log.LogInformation("OnRootDrop: bailing — vm={VmOk} dt={DtOk}", DataContext is VStripsViewModel, e.DataTransfer is not null);
            return;
        }

        var stripId = e.DataTransfer.TryGetValue(StripIdFormat);
        if (stripId is null || !vm.ItemsById.TryGetValue(stripId, out var strip) || vm.SelectedBay is null)
        {
            Log.LogInformation("OnRootDrop: no strip match — stripId={StripId} selectedBay={Bay}", stripId, vm.SelectedBay?.Name);
            return;
        }

        // Prefer an explicit hit-test at the pointer position over walking
        // e.Source — Avalonia can set e.Source to a root-level visual (not
        // the deepest hit target) for some drop events, which would break
        // the rack walk. InputHitTest with the drag-ghost canvas suppressed
        // (IsHitTestVisible=false) returns the underlying rack visual.
        var rootPos = e.GetPosition(this);
        var hit = this.InputHitTest(rootPos) as Visual ?? e.Source as Visual;
        if (hit is null)
        {
            Log.LogInformation("OnRootDrop: no hit target at pointer position {Pos}", rootPos);
            return;
        }

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
            Log.LogInformation("OnRootDrop: no rack border under hit={HitType} at {Pos}", hit.GetType().Name, rootPos);
            return;
        }

        var index = ComputeDropIndex(rackBorder, rack, e);
        Log.LogInformation(
            "OnRootDrop: strip={StripId} → bay={Bay} rack={Rack} index={Index} (from {FromRack}/{FromIdx})",
            stripId,
            vm.SelectedBay.Name,
            rack.RackIndex,
            index,
            _draggingFromRack?.RackIndex,
            _draggingFromIndex
        );
        // Clear the preview before the move so the post-move layout doesn't
        // inherit the lingering Margin.Bottom (it would revisit the wrong
        // presenter after the collection re-orders).
        ClearDropPreview();
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
    /// Computes the zero-based model insertion index for a drop inside a rack.
    /// Queries each rendered strip's actual Y-bounds instead of approximating
    /// via <c>hostHeight / count</c> — the DockPanel stretches vertically past
    /// the strip stack, so an approximate divisor misplaces drops that land in
    /// the empty space above the topmost strip (it would round them to middle
    /// indices instead of "append").
    ///
    /// When a drop preview is active in this rack the shifted presenter and
    /// everything above it are rendered <c>shiftAmount</c> pixels higher than
    /// their natural position. We undo that shift when building the bands so
    /// the pointer-to-index mapping remains stable as the preview moves —
    /// otherwise the pointer would repeatedly cross band boundaries shifted
    /// by the preview itself and the preview would oscillate between indices.
    /// See <see cref="ComputeDropIndexFromBands"/> for the pure index math.
    /// </summary>
    private int ComputeDropIndex(Border rackBorder, StripRackViewModel rack, DragEventArgs e)
    {
        var stripsHost = rackBorder.FindDescendantOfType<ItemsControl>();
        if (stripsHost is null || rack.Strips.Count == 0)
        {
            return 0;
        }

        var visible = GetVisiblePresenters(stripsHost, rack);
        if (visible.Count == 0)
        {
            return 0;
        }

        var pos = e.GetPosition(stripsHost);
        var bands = BuildUnshiftedBands(visible, rack);
        if (bands.Any(b => b.Bottom <= b.Top))
        {
            // Pre-layout — treat as append.
            return visible.Count;
        }
        return ComputeDropIndexFromBands(pos.Y, bands);
    }

    /// <summary>
    /// Returns visible ContentPresenters from the rack's inner ItemsControl
    /// in visual bottom-up order (<c>result[0]</c> = visual-bottom strip,
    /// <c>result[^1]</c> = visual-top), with each entry's current top-Y in
    /// <paramref name="stripsHost"/> coordinates.
    ///
    /// Walking the visual tree for every DragOver is expensive — see
    /// <see cref="_presenterCache"/>. We cache the (Presenter, Vm) pairs on
    /// first entry to a rack during a drag and reuse them for subsequent
    /// events. The cache order matches the rack's Children order (model
    /// order), which for bottom-up DockPanel docking also matches visual
    /// bottom-up order, so no sort is needed. Top-Y is re-read every call
    /// because the preview margin shifts positions as the drag progresses.
    /// </summary>
    private List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> GetVisiblePresenters(
        ItemsControl stripsHost,
        StripRackViewModel rack
    )
    {
        var cache = GetCachedPresenters(stripsHost, rack);
        var result = new List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)>(cache.Count);
        foreach (var (presenter, vm) in cache)
        {
            if (!presenter.IsVisible)
            {
                continue;
            }
            var topPoint = presenter.TranslatePoint(new Point(0, 0), stripsHost);
            if (topPoint is null)
            {
                continue;
            }
            result.Add((presenter, vm, topPoint.Value.Y));
        }
        return result;
    }

    /// <summary>
    /// Returns the cached (Presenter, Vm) list for a rack, populating the
    /// cache on first access. The cache skips the source strip (for
    /// same-rack drags) up front, so downstream callers don't need to
    /// re-check. Populated in <see cref="rack.Strips"/> order, which
    /// matches ItemsControl.Children order and therefore bottom-up visual
    /// order under DockPanel docking.
    /// </summary>
    private List<(ContentPresenter Presenter, StripItemViewModel Vm)> GetCachedPresenters(ItemsControl stripsHost, StripRackViewModel rack)
    {
        if (_presenterCache.TryGetValue(rack, out var cached))
        {
            return cached;
        }
        var sourceStrip = _draggingStrip;
        var sourceRackEqualsThis = ReferenceEquals(_draggingFromRack, rack);
        var list = new List<(ContentPresenter Presenter, StripItemViewModel Vm)>(rack.Strips.Count);
        foreach (var presenter in stripsHost.GetVisualDescendants().OfType<ContentPresenter>())
        {
            if (presenter.Child is not FlightStripControl strip || strip.DataContext is not StripItemViewModel stripVm)
            {
                continue;
            }
            if (sourceRackEqualsThis && ReferenceEquals(stripVm, sourceStrip))
            {
                continue;
            }
            list.Add((presenter, stripVm));
        }
        _presenterCache[rack] = list;
        return list;
    }

    /// <summary>
    /// Builds the Y-bands list for <see cref="ComputeDropIndexFromBands"/>,
    /// undoing any active preview shift so the pointer-to-index mapping
    /// reflects natural strip positions. Without this, a band already
    /// shifted up by the active preview would make the pointer cross a
    /// different mid-point than the user intended, and the preview would
    /// oscillate between indices.
    /// </summary>
    private List<(double Top, double Bottom)> BuildUnshiftedBands(
        List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> visible,
        StripRackViewModel rack
    )
    {
        var shiftedVisualIdx = -1;
        var shiftAmount = 0.0;
        if (_dropPreviewShiftedPresenter is not null && ReferenceEquals(_dropPreviewRack, rack))
        {
            shiftAmount = _dropPreviewShiftedPresenter.Margin.Bottom - _dropPreviewOriginalMargin.Bottom;
            shiftedVisualIdx = visible.FindIndex(r => ReferenceEquals(r.Presenter, _dropPreviewShiftedPresenter));
        }

        var bands = new List<(double Top, double Bottom)>(visible.Count);
        for (var i = 0; i < visible.Count; i++)
        {
            var top = visible[i].Top;
            var bottom = top + visible[i].Presenter.Bounds.Height;
            if (shiftedVisualIdx >= 0 && i >= shiftedVisualIdx && shiftAmount > 0)
            {
                top += shiftAmount;
                bottom += shiftAmount;
            }
            bands.Add((top, bottom));
        }
        return bands;
    }

    /// <summary>
    /// Given the Y-bands (top..bottom) of each strip keyed by model index,
    /// returns the zero-based model insertion index for a drop at pointer
    /// <paramref name="posY"/>. Strips render bottom-up (strip[0] at the
    /// visual bottom) so:
    /// - Inside strip[i]'s band, top half → insert at i+1 (above it); bottom
    ///   half → insert at i (below it).
    /// - Above the entire stack → append (index = count).
    /// - Between bands or anywhere below strip[0] → insert at i.
    /// Empty bands → 0.
    /// </summary>
    internal static int ComputeDropIndexFromBands(double posY, IReadOnlyList<(double Top, double Bottom)> bands)
    {
        if (bands.Count == 0)
        {
            return 0;
        }
        for (var i = 0; i < bands.Count; i++)
        {
            var (top, bottom) = bands[i];
            if (posY >= top && posY <= bottom)
            {
                var mid = (top + bottom) / 2;
                return posY < mid ? i + 1 : i;
            }
        }
        // Not inside any strip band — decide between "above the stack" (append)
        // and "below the stack" (insert at 0) by comparing against the topmost
        // strip's top. Bottom-up render means strip[count-1] has the smallest Top.
        var stackTop = bands[^1].Top;
        return posY < stackTop ? bands.Count : 0;
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

        // Hide the source ContentPresenter (only for rack drags — printer-queue
        // drags keep the strip visible in the carousel) so the rack's DockPanel
        // collapses the slot during the drag. The dragged strip appears only as
        // the cursor ghost, matching the user's "picked up" mental model. Also
        // means ComputeDropIndex won't treat the source's own position as a
        // valid drop target, so dropping the topmost strip back on itself
        // resolves to the source's current idx (caught by IsNoOpMove)
        // instead of count + 1 (which would slip past the no-op guard).
        if (_draggingFromRack is not null)
        {
            _draggingSourcePresenter = stripView.FindAncestorOfType<ContentPresenter>();
            if (_draggingSourcePresenter is not null)
            {
                _draggingSourcePresenter.IsVisible = false;
                // Synchronously apply the initial preview at the source's own
                // slot (visual idx == fromIdx because bottom-up rendering
                // makes visual and model idx coincide). Without this, the
                // user sees the rack collapse under the hide for one layout
                // pass before the first DragOver restores a gap. Applying
                // here queues both changes into the same layout pass so the
                // strip visually "lifts out" into the cursor ghost without
                // the rest of the rack shifting underneath.
                ApplyInitialDropPreview(_draggingFromRack, _draggingFromIndex);
            }
        }

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(StripIdFormat, strip.Id));

        Log.LogInformation(
            "Strip drag start: strip={StripId} fromRack={FromRack} fromIdx={FromIdx}",
            strip.Id,
            _draggingFromRack?.RackIndex,
            _draggingFromIndex
        );
        DragDropEffects effect;
        try
        {
            ShowDragGhost(strip, e);
            effect = await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);
        }
        finally
        {
            HideDragGhost();
            _draggingStrip = null;
            _draggingFromRack = null;
            _draggingFromIndex = -1;
        }
        Log.LogInformation("Strip drag end: strip={StripId} effect={Effect}", strip.Id, effect);
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
        // Park the ghost at Canvas (0,0) once; per-frame movement is handled
        // by mutating a single TranslateTransform below. Canvas.Left/Top
        // would invalidate the Canvas's arrange on every DragOver — with
        // Windows throttling drag events to ~30 Hz, adding layout work per
        // event compounds into visible stutter. RenderTransform skips layout
        // entirely and re-renders in the composition pass.
        var ghostTransform = new TranslateTransform();
        var ghost = new FlightStripControl
        {
            DataContext = strip,
            Tag = strip,
            Opacity = 0.75,
            IsHitTestVisible = false,
            RenderTransform = ghostTransform,
        };
        Canvas.SetLeft(ghost, 0);
        Canvas.SetTop(ghost, 0);
        canvas.Children.Add(ghost);
        _dragGhost = ghost;
        _dragGhostTransform = ghostTransform;
        UpdateGhostPosition(e.GetPosition(canvas));
    }

    private void UpdateGhostPosition(Point pointerInCanvas)
    {
        if (_dragGhostTransform is null)
        {
            return;
        }
        // Offset the ghost so the cursor sits inside the strip rather than at
        // the top-left corner — matches CRC's feel where the strip "sticks" to
        // the pointer as if you grabbed it somewhere in the middle.
        _dragGhostTransform.X = pointerInCanvas.X - 24;
        _dragGhostTransform.Y = pointerInCanvas.Y - 16;
    }

    private void HideDragGhost()
    {
        // Clear any lingering drop-preview before the drag ends so the
        // rack layout snaps back immediately on cancel/drop.
        ClearDropPreview();

        // Restore the source presenter first so any post-drop broadcast
        // that re-renders the rack finds it in its normal visible state.
        if (_draggingSourcePresenter is not null)
        {
            _draggingSourcePresenter.IsVisible = true;
            _draggingSourcePresenter = null;
        }

        var canvas = this.FindControl<Canvas>("DragGhostCanvas");
        if (canvas is not null && _dragGhost is not null)
        {
            canvas.Children.Remove(_dragGhost);
        }
        _dragGhost = null;
        _dragGhostTransform = null;
        _presenterCache.Clear();

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

        UpdateDropPreview(e);
    }

    // ── Drop preview (shifting strips + append line) ────────────

    /// <summary>
    /// Tracks the insertion target during a strip drag and shows a visible
    /// gap where the strip will land. For an insertion between existing
    /// strips, the strip at model-index N is pushed up by the dragged
    /// strip's height (Margin.Bottom), which — because the rack DockPanel
    /// docks bottom-up — cascades the shift to every strip visually above
    /// it. For an append (index == count), draws a thin yellow line just
    /// above the topmost strip. Called from <see cref="OnGenericDragOver"/>
    /// so the preview follows the pointer continuously.
    /// </summary>
    private void UpdateDropPreview(DragEventArgs e)
    {
        if (_draggingStrip is null || e.DataTransfer?.Contains(StripIdFormat) != true)
        {
            ClearDropPreview();
            return;
        }

        var rootPos = e.GetPosition(this);
        var hit = this.InputHitTest(rootPos) as Visual;
        if (hit is null)
        {
            ClearDropPreview();
            return;
        }

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
        if (rackBorder?.Tag is not StripRackViewModel rack)
        {
            ClearDropPreview();
            return;
        }

        var index = ComputeDropIndex(rackBorder, rack, e);
        if (ReferenceEquals(_dropPreviewRack, rack) && _dropPreviewIndex == index)
        {
            return;
        }

        ClearDropPreview();
        ApplyDropPreview(rackBorder, rack, index);
    }

    /// <summary>
    /// Applies the drop preview for visual index <paramref name="visualIdx"/>
    /// in <paramref name="rack"/>. <c>visualIdx &lt; visible.Count</c> shifts
    /// the visible strip at that bottom-up position up by one strip height
    /// (via Margin.Bottom) — the DockPanel cascade pushes every strip above
    /// it up too, opening a gap at the target position. <c>visualIdx ==
    /// visible.Count</c> is "append above the visual top" and draws a thin
    /// yellow line above the topmost visible strip. Uses visual idx (not
    /// model idx) so the logic is uniform across cross-rack drags (visible
    /// = all strips) and same-rack drags (visible = all strips except the
    /// hidden source), and maps 1:1 to the STRIP wire index.
    /// </summary>
    private void ApplyDropPreview(Border rackBorder, StripRackViewModel rack, int visualIdx)
    {
        var rackContent = rackBorder.FindDescendantOfType<Grid>();
        var stripsHost = rackBorder.FindDescendantOfType<ItemsControl>();
        if (rackContent is null || stripsHost is null)
        {
            return;
        }

        var visible = GetVisiblePresenters(stripsHost, rack);
        ApplyDropPreviewToVisible(rackContent, rack, visualIdx, visible);
    }

    private void ApplyDropPreviewToVisible(
        Grid rackContent,
        StripRackViewModel rack,
        int visualIdx,
        List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> visible
    )
    {
        var stripHeight = ResolveDragStripHeight(visible);
        _dropPreviewRack = rack;
        _dropPreviewIndex = visualIdx;

        if (visualIdx < visible.Count)
        {
            var targetPresenter = visible[visualIdx].Presenter;
            _dropPreviewShiftedPresenter = targetPresenter;
            _dropPreviewOriginalMargin = targetPresenter.Margin;
            targetPresenter.Margin = new Thickness(
                targetPresenter.Margin.Left,
                targetPresenter.Margin.Top,
                targetPresenter.Margin.Right,
                targetPresenter.Margin.Bottom + stripHeight
            );
        }
        else if (visible.Count > 0)
        {
            // Append-at-top: overlay a yellow line at the top edge of the
            // visual-topmost strip. visible is sorted by top-Y descending, so
            // visible[^1] is the topmost.
            var topmost = visible[^1].Presenter;
            var topPoint = topmost.TranslatePoint(new Point(0, 0), rackContent);
            if (topPoint is null)
            {
                return;
            }
            var line = new Border
            {
                Height = 2,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, Math.Max(0, topPoint.Value.Y - 1), 0, 0),
                IsHitTestVisible = false,
            };
            rackContent.Children.Add(line);
            _dropPreviewLine = line;
            _dropPreviewLineHost = rackContent;
        }
    }

    /// <summary>
    /// Applies the initial preview synchronously on drag start at the source
    /// strip's own slot (visual idx == <paramref name="fromIdx"/>), before
    /// DoDragDropAsync kicks off the first DragOver. Without this, the
    /// layout pass that processes IsVisible=false on the source runs before
    /// the first DragOver computes a preview — the user briefly sees the
    /// strips above the source fall down to occupy the empty slot, then
    /// pop back up when the gap preview lands. Applying here queues the
    /// shift (or yellow line, for source-at-top) into the same layout pass
    /// as the hide, so the rack lifts out into the ghost smoothly without
    /// the other strips shifting underneath.
    /// </summary>
    private void ApplyInitialDropPreview(StripRackViewModel rack, int fromIdx)
    {
        if (_draggingSourcePresenter is null)
        {
            return;
        }
        Border? rackBorder = null;
        Visual? walk = _draggingSourcePresenter;
        while (walk is not null)
        {
            walk = walk.GetVisualParent() as Visual;
            if (walk is Border b && b.Tag is StripRackViewModel)
            {
                rackBorder = b;
                break;
            }
        }
        if (rackBorder is null)
        {
            return;
        }
        var rackContent = rackBorder.FindDescendantOfType<Grid>();
        var stripsHost = rackBorder.FindDescendantOfType<ItemsControl>();
        if (rackContent is null || stripsHost is null)
        {
            return;
        }

        // GetVisiblePresenters excludes the just-hidden source. It still sorts
        // by current Y (pre-hide), which for strips *above* the source is
        // wrong post-hide — they'll drop by stripHeight — but ApplyDropPreview
        // only reads bounds for the append-line case, and that case only
        // fires when fromIdx == rack.Strips.Count - 1 (source topmost), in
        // which case strips *below* the source are unaffected by the hide.
        var visible = GetVisiblePresenters(stripsHost, rack);
        ApplyDropPreviewToVisible(rackContent, rack, fromIdx, visible);
    }

    /// <summary>
    /// Height to use for the drop-preview gap. Prefers the source presenter
    /// (the strip being dragged — guaranteed correct and available on drag
    /// start before the ghost has been laid out), falls back to any visible
    /// strip's height, then to 69 px (full-strip default).
    /// </summary>
    private double ResolveDragStripHeight(List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> visible)
    {
        if (_draggingSourcePresenter is not null && _draggingSourcePresenter.Bounds.Height > 0)
        {
            return _draggingSourcePresenter.Bounds.Height;
        }
        if (_dragGhost is not null && _dragGhost.Bounds.Height > 0)
        {
            return _dragGhost.Bounds.Height;
        }
        if (visible.Count > 0 && visible[0].Presenter.Bounds.Height > 0)
        {
            return visible[0].Presenter.Bounds.Height;
        }
        return 69;
    }

    /// <summary>
    /// Undoes any active drop preview: restores the shifted presenter's
    /// margin and removes the append-line overlay. Safe to call repeatedly
    /// — a no-op when no preview is active.
    /// </summary>
    private void ClearDropPreview()
    {
        if (_dropPreviewShiftedPresenter is not null)
        {
            _dropPreviewShiftedPresenter.Margin = _dropPreviewOriginalMargin;
            _dropPreviewShiftedPresenter = null;
        }
        if (_dropPreviewLine is not null && _dropPreviewLineHost is not null)
        {
            _dropPreviewLineHost.Children.Remove(_dropPreviewLine);
        }
        _dropPreviewLine = null;
        _dropPreviewLineHost = null;
        _dropPreviewRack = null;
        _dropPreviewIndex = -1;
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
