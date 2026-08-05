using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.Find;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

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
    private static readonly ILogger Log = SimLog.CreateLogger("VStripsView");

    // Shared in-view Find (Ctrl+F). Snapshot = the selected bay's racks walked
    // visual-top-to-bottom; scrollTo brings the matched FlightStripControl into view.
    // Its DataContext drives the FindBar overlay.
    private readonly FindController _findController;

    // Tracks the bound VM so a SelectedBay switch can refresh Find against the newly
    // shown bay (and clear highlights left on the previous bay's strips).
    private VStripsViewModel? _trackedVm;

    /// <summary>Test hook: the in-view Find controller backing the FindBar overlay.</summary>
    internal FindController FindController => _findController;

    // Ghost overlay state for the drag preview. Lives for the duration of a
    // single pointer-capture drag; cleared in CleanupDrag. The grab offset
    // pins the pointer to the exact spot of the strip it pressed on; the
    // scale transform renders the ghost at the racks' zoom level (plus the
    // pickup lift) and the base zoom is retained so the drop settle can
    // scale back down.
    private Control? _dragGhost;
    private TranslateTransform? _dragGhostTransform;
    private ScaleTransform? _dragGhostScale;
    private Point _ghostGrabOffset;
    private double _ghostBaseZoom = 1.0;
    private StripItemViewModel? _draggingStrip;
    private StripRackViewModel? _draggingFromRack;
    private int _draggingFromIndex = -1;

    // True while a completed drop's ghost is settling into its slot (a
    // ~130ms window after release). New strip presses are ignored during it
    // so a second drag can't stomp the in-flight ghost or preview state.
    private bool _isSettling;

    // The pointer that owns the active drag (captured on `this`), retained so
    // cancel paths can release capture explicitly, and the last pointer
    // position in `this` coordinates so wheel-scroll during a drag can
    // re-resolve the hover target under a stationary pointer.
    private IPointer? _dragPointer;
    private Point _lastDragRootPos;

    // Resolved once in the constructor — UpdateDrag runs at display rate and
    // must not pay a name-scope lookup per pointer move.
    private Canvas? _dragGhostCanvas;
    private Border? _trashZone;
    private ScrollViewer? _racksScrollViewer;

    // Edge autoscroll: while a drag hovers within AutoscrollEdgeBand px of
    // the racks ScrollViewer's viewport edge, a ~60Hz timer scrolls the
    // offset proportionally to edge proximity and re-resolves the hover
    // target (the content moves under a stationary pointer). Stopped when
    // the pointer leaves the band and in CleanupDrag.
    private const double AutoscrollEdgeBand = 40.0;
    private const double AutoscrollMaxStep = 14.0;
    private Avalonia.Threading.DispatcherTimer? _autoscrollTimer;
    private Vector _autoscrollStep;

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
    // into the cached list, not a tree walk. Cleared in CleanupDrag.
    private readonly Dictionary<StripRackViewModel, List<(ContentPresenter Presenter, StripItemViewModel Vm)>> _presenterCache = [];

    // Drag-hover bay-preview state (docs/crc/vstrips.md:217). When the user
    // hovers a drag over a bay header for >500ms without dropping, we
    // temporarily switch SelectedBay to that bay so they can pick a specific
    // rack. Restored on drag-leave / drop / drag-ended.
    private StripBayViewModel? _hoverBay;
    private StripBayViewModel? _preHoverSelectedBay;
    private Avalonia.Threading.DispatcherTimer? _hoverTimer;

    // Drop-preview state. While dragging over a rack, every visible strip at
    // visual index >= the computed target index carries a TranslateTransform
    // shifted up by the dragged strip's height, opening a visible gap where
    // the drop will land. The transforms animate (DoubleTransition) so the
    // gap *slides* along the rack as the target index changes instead of
    // teleporting — and being render transforms, the animation runs in the
    // composition pass with no layout work per frame. For the append case
    // (index == count) we overlay a yellow insertion line above the topmost
    // strip instead (no strip to shift). Cleared when the pointer leaves all
    // racks, on drop, and on drag cancel.
    private StripRackViewModel? _dropPreviewRack;
    private int _dropPreviewIndex = -1;
    private readonly List<(ContentPresenter Presenter, TranslateTransform Transform)> _dropPreviewShifted = [];
    private Border? _dropPreviewLine;
    private Grid? _dropPreviewLineHost;

    // Gap-slide animation length. Short enough to track a fast-moving
    // pointer, long enough to read as strips physically sliding aside.
    private const int GapAnimationMs = 100;

    // Hysteresis (in strips-host units) the pointer must clear past a band
    // midpoint before the preview index flips — keeps the gap from
    // flickering under hand tremor at band boundaries.
    private const double DropIndexHysteresis = 6.0;

    public VStripsView()
    {
        InitializeComponent();

        _dragGhostCanvas = this.FindControl<Canvas>("DragGhostCanvas");
        _trashZone = this.FindControl<Border>("TrashZone");
        _racksScrollViewer = this.FindControl<ScrollViewer>("RacksScrollViewer");

        // Drag-source wiring at the UserControl level (Tunnel) so pointer
        // presses on any strip — rack or printer — can participate in the
        // click-vs-drag dispatch. Pressed records state; Moved promotes to a
        // pointer-capture drag past the threshold and then drives the ghost +
        // drop preview at display rate; Released completes the drop (or
        // clears state if no drag ran). Tunnel-phase so the handler fires
        // before the TextBox's own bubble handler that would grab focus — we
        // still DON'T set Handled=true, so TextBox focus on short clicks
        // continues to work.
        AddHandler(PointerPressedEvent, OnStripPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnStripPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnStripPointerReleased, RoutingStrategies.Tunnel);

        // While `this` holds pointer capture during a drag, wheel events
        // route along the capture target's ancestor chain — the racks
        // ScrollViewer (a descendant) never sees them. Handle them here so
        // the user can still scroll to an off-screen slot mid-drag.
        AddHandler(PointerWheelChangedEvent, OnDragPointerWheel, RoutingStrategies.Tunnel);

        // In-view Find. The FindBar overlay binds to this controller; the snapshot
        // and scroll callbacks resolve the live selected bay on demand.
        _findController = new FindController(BuildFindSnapshot, ScrollFindMatchIntoView);
        FindBar.DataContext = _findController;
        DataContextChanged += OnFindDataContextChanged;
    }

    // ── In-view Find (Ctrl+F) ───────────────────────────────────

    private void OnFindDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged -= OnTrackedVmPropertyChanged;
        }
        _trackedVm = DataContext as VStripsViewModel;
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnTrackedVmPropertyChanged;
        }
        _findController.Refresh();
    }

    private void OnTrackedVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A bay switch swaps the entire searchable set — re-match and clear any
        // highlights the previous bay's strips still carry.
        if (e.PropertyName == nameof(VStripsViewModel.SelectedBay))
        {
            _findController.Refresh();
            OnSelectedBayChangedDuringDrag();
        }

        // A disconnect mid-drag would leave a ghost tracking a read-only
        // view — cancel so the strip snaps home and no stale move dispatches
        // on release.
        if ((e.PropertyName == nameof(VStripsViewModel.IsConnected)) && (_trackedVm?.IsConnected == false))
        {
            CancelDrag();
        }
    }

    /// <summary>Selected bay's strips in visual order: racks left-to-right, each rack bottom-up.</summary>
    private IReadOnlyList<IFindableItem> BuildFindSnapshot()
    {
        var result = new List<IFindableItem>();
        if (DataContext is VStripsViewModel { SelectedBay: { } bay })
        {
            foreach (var rack in bay.Racks)
            {
                // Racks render bottom-up (index 0 at the visual bottom), so walk the
                // collection in reverse to yield matches top-to-bottom.
                for (var i = rack.Strips.Count - 1; i >= 0; i--)
                {
                    result.Add(rack.Strips[i]);
                }
            }
        }
        return result;
    }

    private void ScrollFindMatchIntoView(IFindableItem item)
    {
        if (item is not StripItemViewModel target)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                var host = this.FindControl<ItemsControl>("RacksHost");
                var strip = host?.GetVisualDescendants().OfType<FlightStripControl>().FirstOrDefault(c => ReferenceEquals(c.Tag, target));
                strip?.BringIntoView();
            },
            Avalonia.Threading.DispatcherPriority.Loaded
        );
    }

    /// <summary>Handles the Find keys (Ctrl+F / F3 / Shift+F3 / Esc); returns true if consumed.</summary>
    private bool HandleFindKeys(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && (e.Key == Key.F))
        {
            _findController.Open();
            FindBar.FocusInput();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.F3)
        {
            if (shift)
            {
                _findController.Previous();
            }
            else
            {
                _findController.Next();
            }
            e.Handled = true;
            return true;
        }
        if ((e.Key == Key.Escape) && _findController.IsVisible)
        {
            _findController.Close();
            Focus();
            e.Handled = true;
            return true;
        }
        return false;
    }

    // ── Sticky-bottom scroll ─────────────────────────────────────

    // Tolerance (device pixels) for "at the bottom" comparisons and for deciding a re-pin is worth applying.
    private const double StickyScrollEpsilon = 1.0;

    private void OnRacksScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }
        var pinned = StickyScroll.PinnedBottomOffset(
            scrollViewer.Offset.Y,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height,
            e.OffsetDelta.Y,
            e.ExtentDelta.Y,
            e.ViewportDelta.Y,
            StickyScrollEpsilon
        );
        if (pinned is { } y)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, y);
        }
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

    // ── Drop-target resolution ──────────────────────────────────

    private enum DropTargetKind
    {
        None,
        Rack,
        Bay,
        Trash,
    }

    /// <summary>
    /// Where a drag currently points: a rack slot (border + rack + insertion
    /// index), a bay button (push target — carries the Button so hover can
    /// highlight it), the trash zone, or nothing. Rack targets also snapshot
    /// the bay they belong to at resolution time so the drop dispatch stays
    /// correct even after <see cref="CleanupDrag"/> restores a pre-hover bay
    /// selection.
    /// </summary>
    private readonly record struct DropTarget(
        DropTargetKind Kind,
        Border? RackBorder,
        StripRackViewModel? Rack,
        int Index,
        StripBayViewModel? Bay,
        Button? BayButton
    );

    /// <summary>
    /// Resolves what sits under <paramref name="rootPos"/> (in this view's
    /// coordinates) during a drag: hit-test at the pointer position, then
    /// ancestor-walk for the trash zone, a bay button, or a rack border.
    /// The explicit hit-test (rather than event source) works under pointer
    /// capture — capture retargets event routing, not hit-testing — and the
    /// two suppressions it relies on are the ghost canvas's
    /// IsHitTestVisible=false and the hidden source presenter.
    /// </summary>
    private DropTarget ResolveDropTarget(Point rootPos)
    {
        if (this.InputHitTest(rootPos) is not Visual hit)
        {
            return new DropTarget(DropTargetKind.None, null, null, -1, null, null);
        }

        Visual? v = hit;
        while (v is not null)
        {
            if (ReferenceEquals(v, _trashZone))
            {
                return new DropTarget(DropTargetKind.Trash, null, null, -1, null, null);
            }
            if (v is Button { Tag: StripBayViewModel bay } bayButton)
            {
                return new DropTarget(DropTargetKind.Bay, null, null, -1, bay, bayButton);
            }
            if (v is Border b && b.Tag is StripRackViewModel rack)
            {
                var index = ComputeDropIndex(b, rack, rootPos);
                return new DropTarget(DropTargetKind.Rack, b, rack, index, (DataContext as VStripsViewModel)?.SelectedBay, null);
            }
            v = v.GetVisualParent() as Visual;
        }
        return new DropTarget(DropTargetKind.None, null, null, -1, null, null);
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
    private int ComputeDropIndex(Border rackBorder, StripRackViewModel rack, Point rootPos)
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

        // Convert from this view's coordinates into the (zoomed) strips-host
        // space — TranslatePoint carries the LayoutTransformControl scale, so
        // the band comparison happens in the same space the presenters were
        // measured in. Null only when the host is detached mid-teardown.
        var posInHost = this.TranslatePoint(rootPos, stripsHost);
        if (posInHost is null)
        {
            return visible.Count;
        }
        var pos = posInHost.Value;
        var bands = BuildUnshiftedBands(visible);
        if (bands.Any(b => b.Bottom <= b.Top))
        {
            // Pre-layout — treat as append.
            return visible.Count;
        }
        // Anchor hysteresis to the active preview index so the gap doesn't
        // flicker while the pointer hovers a band boundary.
        var currentIndex = ReferenceEquals(_dropPreviewRack, rack) ? _dropPreviewIndex : -1;
        return ComputeDropIndexFromBands(pos.Y, bands, currentIndex, DropIndexHysteresis);
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
    /// Builds the Y-bands list for <see cref="ComputeDropIndexFromBands(double, IReadOnlyList{ValueTuple{double, double}})"/>,
    /// undoing any active preview shift so the pointer-to-index mapping
    /// reflects natural strip positions. Without this, a band already
    /// shifted up by the active preview would make the pointer cross a
    /// different mid-point than the user intended, and the preview would
    /// oscillate between indices. Each presenter's measured top includes its
    /// current preview TranslateTransform (TranslatePoint carries render
    /// transforms — mid-animation values included), so subtracting the
    /// transform's current Y restores the natural position exactly.
    /// </summary>
    private static List<(double Top, double Bottom)> BuildUnshiftedBands(
        List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> visible
    )
    {
        var bands = new List<(double Top, double Bottom)>(visible.Count);
        foreach (var (presenter, _, measuredTop) in visible)
        {
            var previewOffset = presenter.RenderTransform is TranslateTransform transform ? transform.Y : 0.0;
            var top = measuredTop - previewOffset;
            bands.Add((top, top + presenter.Bounds.Height));
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

    /// <summary>
    /// Hysteresis-aware variant of
    /// <see cref="ComputeDropIndexFromBands(double, IReadOnlyList{ValueTuple{double, double}})"/>:
    /// when the raw result is adjacent to <paramref name="currentIndex"/>,
    /// the pointer must clear the boundary between the two indices (the
    /// midpoint of the lower band — inside band i, top half → i+1, bottom
    /// half → i) by more than <paramref name="hysteresisPx"/> before the
    /// index flips. Non-adjacent jumps (fast pointer moves) and calls with
    /// no current index (-1) pass through unchanged.
    /// </summary>
    internal static int ComputeDropIndexFromBands(
        double posY,
        IReadOnlyList<(double Top, double Bottom)> bands,
        int currentIndex,
        double hysteresisPx
    )
    {
        var raw = ComputeDropIndexFromBands(posY, bands);
        if ((currentIndex < 0) || (raw == currentIndex) || (Math.Abs(raw - currentIndex) > 1))
        {
            return raw;
        }
        var lower = Math.Min(raw, currentIndex);
        if (lower >= bands.Count)
        {
            return raw;
        }
        var boundary = (bands[lower].Top + bands[lower].Bottom) / 2;
        if (raw > currentIndex)
        {
            // Moving up the stack (smaller Y): flip only once the pointer is
            // clearly above the boundary.
            return posY < (boundary - hysteresisPx) ? raw : currentIndex;
        }
        // Moving down the stack: flip only once clearly below the boundary.
        return posY > (boundary + hysteresisPx) ? raw : currentIndex;
    }

    // ── Drag source (click-vs-drag dispatch) ────────────────────
    //
    // Pressing on a strip records the press position + pending drag target but
    // doesn't start a drag yet. PointerMoved checks distance against
    // DragThresholdSq and promotes to a drag past that point. Until the
    // threshold is crossed, the event bubbles normally: clicking on an
    // annotation TextBox focuses it in place (caret appears), clicking on the
    // strip body just selects the strip. Matches CRC's "short click edits,
    // hold-and-drag moves" model.
    private enum DragState
    {
        Idle,
        Pressed,
        Dragging,
    }

    // 2px: a drag engages almost immediately (Windows' system drag threshold
    // is 4px — user feedback drove this tighter twice), while a steady
    // click-to-edit still stays a click. Any lower and ordinary clicks with
    // slight hand movement start promoting to accidental micro-drags.
    private const double DragThresholdSq = 2.0 * 2.0;
    private Point _pressPos;
    private FlightStripControl? _pressedStripView;
    private DragState _dragState;

    private async void OnStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        if (e.Source is not Visual hit || DataContext is not VStripsViewModel vm)
        {
            return;
        }

        // A completed drop's ghost is still settling into its slot — ignore
        // new strip presses for the ~130ms window so a second drag can't
        // stomp the in-flight ghost or drop-preview state.
        if (_isSettling)
        {
            return;
        }

        if (!vm.IsConnected)
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

        // Record press state so PointerMoved can decide when to promote the
        // gesture to a drag. Do NOT start a drag here — a pure short click
        // should reach the underlying TextBox (if any) and focus it.
        _pressPos = e.GetPosition(this);
        _pressedStripView = stripView;
        _dragState = DragState.Pressed;
        _draggingStrip = strip;
    }

    private void OnStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragState == DragState.Dragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Release swallowed elsewhere (e.g. by another window) —
                // treat as cancel rather than dropping at a stale position.
                CancelDrag();
                return;
            }
            UpdateDrag(e);
            return;
        }

        if (_pressedStripView is null || _dragState != DragState.Pressed || _draggingStrip is not { } strip)
        {
            return;
        }
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
        {
            // Button released without our PointerReleased firing (rare, e.g.
            // capture lost to another window). Treat as click-end: clear state.
            _pressedStripView = null;
            _draggingStrip = null;
            _dragState = DragState.Idle;
            return;
        }

        var pos = e.GetPosition(this);
        var dx = pos.X - _pressPos.X;
        var dy = pos.Y - _pressPos.Y;
        if (dx * dx + dy * dy < DragThresholdSq)
        {
            return;
        }

        StartPointerDrag(e, strip);
    }

    /// <summary>
    /// Promotes the pressed gesture to a pointer-capture drag. Ordering is
    /// load-bearing: capture first (hiding a captured element would release
    /// capture, so the source presenter must not hold it when it hides),
    /// then hide + initial preview in the same layout pass, then the ghost,
    /// then focus so Esc-to-cancel works.
    /// </summary>
    private void StartPointerDrag(PointerEventArgs e, StripItemViewModel strip)
    {
        _dragState = DragState.Dragging;
        var stripView = _pressedStripView!;
        _pressedStripView = null;

        if (DataContext is not VStripsViewModel)
        {
            _dragState = DragState.Idle;
            _draggingStrip = null;
            return;
        }

        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);

        // Record the origin rack so MoveStripAsync can skip the no-op when the
        // user drops on the exact same position. Walk the visual tree to find
        // the StripRackViewModel from the source strip's Border ancestor.
        (_draggingFromRack, _draggingFromIndex) = FindStripOrigin(stripView, strip);

        // Hide the source ContentPresenter (only for rack drags — printer-queue
        // drags keep the strip visible in the carousel) so the rack's DockPanel
        // collapses the slot during the drag. The dragged strip appears only as
        // the cursor ghost, matching the user's "picked up" mental model. Also
        // means ComputeDropIndex won't treat the source's own position as a
        // valid drop target, so dropping the topmost strip back on itself
        // resolves to the source's current idx (caught by IsNoOpMove) instead
        // of count + 1 (which would slip past the no-op guard).
        if (_draggingFromRack is not null)
        {
            _draggingSourcePresenter = stripView.FindAncestorOfType<ContentPresenter>();
            if (_draggingSourcePresenter is not null)
            {
                _draggingSourcePresenter.IsVisible = false;
                // Apply the initial preview synchronously so the rack lifts
                // the source out into the ghost without the other strips
                // flickering into the collapsed-source layout first.
                ApplyInitialDropPreview(_draggingFromRack, _draggingFromIndex);
            }
        }

        ShowDragGhost(strip, stripView, e);
        _lastDragRootPos = e.GetPosition(this);

        // Take keyboard focus so Esc can cancel the drag. Safe for the
        // short-click TextBox-focus contract: we only get here past the
        // drag threshold, never on a plain click.
        Focus();

        Log.LogInformation(
            "Strip drag start: strip={StripId} fromRack={FromRack} fromIdx={FromIdx}",
            strip.Id,
            _draggingFromRack?.RackIndex,
            _draggingFromIndex
        );
    }

    /// <summary>
    /// Per-move drag update, running at display rate (pointer events, not
    /// the throttled OS drag loop this replaced): moves the ghost and
    /// re-resolves the hover target (rack preview / bay hover timer / trash
    /// highlight).
    /// </summary>
    private void UpdateDrag(PointerEventArgs e)
    {
        _lastDragRootPos = e.GetPosition(this);
        if (_dragGhostCanvas is not null)
        {
            UpdateGhostPosition(e.GetPosition(_dragGhostCanvas));
        }
        ResolveHover(_lastDragRootPos);
        UpdateAutoscroll(_lastDragRootPos);
    }

    // ── Edge autoscroll ─────────────────────────────────────────

    /// <summary>
    /// Starts, retargets, or stops the edge-autoscroll timer based on how
    /// close the drag pointer is to the racks ScrollViewer's viewport edges.
    /// Step size ramps linearly with proximity (full speed at the edge, zero
    /// at the band's inner boundary), on both axes — racks overflow
    /// horizontally as well as vertically.
    /// </summary>
    private void UpdateAutoscroll(Point rootPos)
    {
        var scrollViewer = _racksScrollViewer;
        if (scrollViewer is null)
        {
            StopAutoscroll();
            return;
        }
        var posInViewer = this.TranslatePoint(rootPos, scrollViewer);
        if (posInViewer is not { } pos || pos.X < 0 || pos.Y < 0 || pos.X > scrollViewer.Bounds.Width || pos.Y > scrollViewer.Bounds.Height)
        {
            // Pointer is outside the racks area (header, trash, printer
            // modal margins) — never autoscroll from there.
            StopAutoscroll();
            return;
        }

        var stepX = EdgeStep(pos.X, scrollViewer.Bounds.Width);
        var stepY = EdgeStep(pos.Y, scrollViewer.Bounds.Height);
        if ((stepX == 0) && (stepY == 0))
        {
            StopAutoscroll();
            return;
        }

        _autoscrollStep = new Vector(stepX, stepY);
        if (_autoscrollTimer is null)
        {
            _autoscrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _autoscrollTimer.Tick += (_, _) => OnAutoscrollTick();
            _autoscrollTimer.Start();
        }
    }

    /// <summary>
    /// Signed per-tick scroll step for one axis: negative near the low edge,
    /// positive near the high edge, zero outside the band, magnitude ramping
    /// linearly from 0 at the band boundary to <see cref="AutoscrollMaxStep"/>
    /// at the very edge.
    /// </summary>
    private static double EdgeStep(double pos, double extent)
    {
        if (pos < AutoscrollEdgeBand)
        {
            return -AutoscrollMaxStep * ((AutoscrollEdgeBand - pos) / AutoscrollEdgeBand);
        }
        if (pos > (extent - AutoscrollEdgeBand))
        {
            return AutoscrollMaxStep * ((pos - (extent - AutoscrollEdgeBand)) / AutoscrollEdgeBand);
        }
        return 0;
    }

    private void OnAutoscrollTick()
    {
        var scrollViewer = _racksScrollViewer;
        if ((_dragState != DragState.Dragging) || (scrollViewer is null))
        {
            StopAutoscroll();
            return;
        }
        var before = scrollViewer.Offset;
        scrollViewer.Offset = new Vector(before.X + _autoscrollStep.X, before.Y + _autoscrollStep.Y);
        if (scrollViewer.Offset != before)
        {
            // The content moved under a stationary pointer — the hover
            // target (drop preview position) must re-resolve.
            ResolveHover(_lastDragRootPos);
        }
    }

    private void StopAutoscroll()
    {
        _autoscrollTimer?.Stop();
        _autoscrollTimer = null;
        _autoscrollStep = default;
    }

    private void OnStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragState == DragState.Dragging)
        {
            CompleteDrag(e);
            return;
        }

        // No-op release: the separator's inline TextBox handles single-click
        // edit via its own KeyDown / LostFocus path, so there's nothing to
        // do here when the press never promoted to a drag. Just clear state
        // so the next gesture starts fresh.
        if (_dragState == DragState.Pressed)
        {
            _pressedStripView = null;
            _draggingStrip = null;
            _dragState = DragState.Idle;
        }
    }

    /// <summary>
    /// Finishes the drag on pointer release: resolves the drop target at the
    /// release position, restores all drag visuals synchronously, then
    /// dispatches the matching action (rack move / bay push / trash delete /
    /// cancel). The target — including the bay a rack belongs to — is
    /// snapshotted before cleanup because <see cref="CleanupDrag"/> may
    /// restore a pre-hover bay selection.
    /// </summary>
    private async void CompleteDrag(PointerReleasedEventArgs e)
    {
        if (_dragState != DragState.Dragging || _draggingStrip is not { } strip)
        {
            return;
        }

        // Idle before releasing capture: our Capture(null) echoes a
        // PointerCaptureLost, whose handler must see the drag as already over.
        _dragState = DragState.Idle;
        _dragPointer?.Capture(null);
        _dragPointer = null;

        var target = ResolveDropTarget(e.GetPosition(this));
        Log.LogInformation(
            "Strip drag end: strip={StripId} target={Kind} rack={Rack} index={Index} bay={Bay}",
            strip.Id,
            target.Kind,
            target.Rack?.RackIndex,
            target.Index,
            target.Bay?.Name
        );

        // Settle animation: the ghost flies into the open gap (rack drop)
        // or dissolves (bay push / trash) before the cleanup + optimistic
        // reorder land. The gap and hidden source stay as-is during the
        // flight — the ghost visually becomes the strip in its new slot —
        // and _isSettling blocks new presses from stomping the in-flight
        // state. Cleanup in the finally so an animation fault can never
        // leave a hidden presenter behind.
        _isSettling = true;
        try
        {
            switch (target.Kind)
            {
                case DropTargetKind.Rack:
                    if (ComputeSettleDestination(target) is { } destination)
                    {
                        await SettleGhostAsync(destination);
                    }
                    break;
                case DropTargetKind.Bay:
                    await DissolveGhostAsync(durationMs: 80, endScaleFactor: 1.0);
                    break;
                case DropTargetKind.Trash:
                    await DissolveGhostAsync(durationMs: 100, endScaleFactor: 0.6);
                    break;
            }
        }
        finally
        {
            _isSettling = false;
            // Restores visuals; OptimisticallyMove below then mutates the
            // collections in the same frame, so there is no flash of the
            // strip in its old slot and no preview transform surviving
            // into the post-move layout.
            CleanupDrag();
        }

        if (DataContext is not VStripsViewModel vm)
        {
            return;
        }
        switch (target.Kind)
        {
            case DropTargetKind.Rack when target.Bay is not null:
                await vm.MoveStripAsync(strip, target.Bay, target.Rack!.RackIndex, target.Index);
                break;
            case DropTargetKind.Bay:
                // Dropped on a bay button (push target) without a specific
                // slot — append to the tail of rack 0 (CRC bottom-up
                // first-available). External bays are valid push targets.
                await vm.MoveStripAsync(strip, target.Bay!, rack: 0, index: null);
                break;
            case DropTargetKind.Trash:
                await vm.DeleteStripAsync(strip);
                break;
        }
    }

    // ── Drop settle animation ───────────────────────────────────

    private const int SettleAnimationMs = 130;

    /// <summary>
    /// Where the ghost's top-left should land (ghost-canvas coordinates) for
    /// the drop to read as the strip sliding into its slot: the gap the drop
    /// preview opened at the target index, computed from the natural
    /// (unshifted) band positions. Null when the rack visuals can't be
    /// resolved — the caller then skips the flight and snaps.
    /// </summary>
    private Point? ComputeSettleDestination(DropTarget target)
    {
        if (target.RackBorder is null || target.Rack is null || _dragGhostCanvas is null)
        {
            return null;
        }
        var stripsHost = target.RackBorder.FindDescendantOfType<ItemsControl>();
        if (stripsHost is null)
        {
            return null;
        }

        var visible = GetVisiblePresenters(stripsHost, target.Rack);
        var stripHeight = ResolveDragStripHeight(visible);
        double topY;
        double x = 0;
        if (visible.Count == 0)
        {
            topY = stripsHost.Bounds.Height - stripHeight;
        }
        else
        {
            var bands = BuildUnshiftedBands(visible);
            var idx = Math.Clamp(target.Index, 0, bands.Count);
            // Bottom-up stack: the inserted strip's bottom edge is the band
            // below it (or the current bottom strip's bottom edge for idx 0).
            var bottomY = idx == 0 ? bands[0].Bottom : bands[idx - 1].Top;
            topY = bottomY - stripHeight;
            x = visible[0].Presenter.TranslatePoint(new Point(0, 0), stripsHost)?.X ?? 0;
        }
        return stripsHost.TranslatePoint(new Point(x, topY), _dragGhostCanvas);
    }

    /// <summary>
    /// Flies the ghost from its current position into the destination slot
    /// (scale eases back to the racks' zoom so the lifted strip visually
    /// sets down), then waits out the animation. The gap and hidden source
    /// presenter stay untouched during the flight; the caller cleans up and
    /// dispatches the move when this returns.
    /// </summary>
    private async Task SettleGhostAsync(Point destinationTopLeft)
    {
        var translate = _dragGhostTransform;
        if (_dragGhost is null || translate is null)
        {
            return;
        }
        translate.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(SettleAnimationMs),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            },
            new Avalonia.Animation.DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(SettleAnimationMs),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            },
        };
        translate.X = destinationTopLeft.X;
        translate.Y = destinationTopLeft.Y;
        if (_dragGhostScale is { } scale)
        {
            scale.ScaleX = _ghostBaseZoom;
            scale.ScaleY = _ghostBaseZoom;
        }
        if (_dragGhost is Border ghostBorder)
        {
            ghostBorder.Opacity = 1.0;
        }
        await Task.Delay(SettleAnimationMs + 30);
    }

    /// <summary>
    /// Fades the ghost out in place (optionally shrinking it — the trash
    /// drop crumples, the bay push just evaporates), then waits out the
    /// animation before the caller cleans up and dispatches.
    /// </summary>
    private async Task DissolveGhostAsync(int durationMs, double endScaleFactor)
    {
        if (_dragGhost is null)
        {
            return;
        }
        if (_dragGhost is Border ghostBorder)
        {
            ghostBorder.Opacity = 0;
        }
        if ((_dragGhostScale is { } scale) && (endScaleFactor != 1.0))
        {
            scale.ScaleX = _ghostBaseZoom * endScaleFactor;
            scale.ScaleY = _ghostBaseZoom * endScaleFactor;
        }
        await Task.Delay(durationMs + 30);
    }

    /// <summary>
    /// Cancels an active drag (Esc, lost capture, disconnect, missed
    /// release): restores every drag visual and emits nothing. Idempotent —
    /// the Capture(null) → PointerCaptureLost echo re-enters here and hits
    /// the early return.
    /// </summary>
    private void CancelDrag()
    {
        if (_dragState != DragState.Dragging)
        {
            return;
        }
        _dragState = DragState.Idle;
        Log.LogInformation("Strip drag cancelled: strip={StripId}", _draggingStrip?.Id);
        _dragPointer?.Capture(null);
        _dragPointer = null;
        CleanupDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Window deactivation, a popup stealing capture, or the echo of our
        // own Capture(null) — the latter is a no-op via CancelDrag's guard.
        CancelDrag();
    }

    /// <summary>
    /// Wheel handler that keeps the racks ScrollViewer scrollable during a
    /// drag (capture would otherwise starve it), then re-resolves the hover
    /// target from the last pointer position — the content moved under a
    /// stationary pointer. Inert outside a drag.
    /// </summary>
    private void OnDragPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_dragState != DragState.Dragging)
        {
            return;
        }
        var scrollViewer = _racksScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }
        // 50 px per wheel notch matches Avalonia's default line-scroll feel;
        // ScrollViewer clamps the offset to the extent for us.
        const double WheelStep = 50.0;
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X - (e.Delta.X * WheelStep), scrollViewer.Offset.Y - (e.Delta.Y * WheelStep));
        e.Handled = true;
        ResolveHover(_lastDragRootPos);
    }

    // ── Drag hover resolution (preview / bay timer / trash) ─────

    /// <summary>
    /// Applies the hover side effects for the drag position: a rack target
    /// drives the drop-preview gap, a bay button arms the 500ms hover timer
    /// that temporarily switches the view to that bay (CRC
    /// docs/crc/vstrips.md:217), the trash zone lights up, and anything else
    /// clears all three. The pre-hover bay selection deliberately survives
    /// hover-target changes — it is restored only when the whole drag ends
    /// via <see cref="CleanupDrag"/>, so the preview sticks if the user
    /// drags back over the bay after a brief detour.
    /// </summary>
    private void ResolveHover(Point rootPos)
    {
        var target = ResolveDropTarget(rootPos);

        SetTrashHighlight(target.Kind == DropTargetKind.Trash);
        // Light up the hovered bay button (external bays included — they are
        // valid push targets) so "this drop TRANSFERS to another bay" reads
        // differently from repositioning inside the current bay's racks.
        SetBayHighlight(target.Kind == DropTargetKind.Bay ? target.BayButton : null);
        // The ghost usually covers the small header targets completely, so a
        // highlight underneath it would be invisible — recede the ghost to
        // near-transparent over bay buttons and the trash so the lit-up
        // destination shows through. Restores over racks / empty space.
        SetGhostRecessed(target.Kind is DropTargetKind.Bay or DropTargetKind.Trash);

        if (target.Kind == DropTargetKind.Bay && !target.Bay!.IsExternal && DataContext is VStripsViewModel vm)
        {
            if (!ReferenceEquals(_hoverBay, target.Bay))
            {
                _hoverBay = target.Bay;
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
        }
        else if (_hoverBay is not null)
        {
            _hoverTimer?.Stop();
            _hoverTimer = null;
            _hoverBay = null;
        }

        if (target.Kind == DropTargetKind.Rack)
        {
            UpdateRackPreview(target.RackBorder!, target.Rack!, target.Index);
        }
        else
        {
            // Pointer left all racks — slide the gapped strips home.
            ClearDropPreview(animate: true);
        }
    }

    // The bay button currently carrying the drag-over highlight class, so a
    // hover change (or drag end) can remove it from the right control.
    private Button? _hoverBayButton;

    /// <summary>
    /// Fades the drag ghost to near-transparent (animated via its existing
    /// opacity transition) while it hovers a header target it would fully
    /// cover — bay buttons and the trash — and restores it elsewhere. The
    /// destination's highlight is the signal there; the strip visually
    /// gives way to it.
    /// </summary>
    private void SetGhostRecessed(bool recessed)
    {
        if (_dragGhost is not null)
        {
            _dragGhost.Opacity = recessed ? 0.25 : 0.92;
        }
    }

    private void SetBayHighlight(Button? button)
    {
        if (ReferenceEquals(_hoverBayButton, button))
        {
            return;
        }
        _hoverBayButton?.Classes.Remove("drag-over");
        _hoverBayButton = button;
        if (button is not null && !button.Classes.Contains("drag-over"))
        {
            button.Classes.Add("drag-over");
        }
    }

    private void SetTrashHighlight(bool active)
    {
        if (_trashZone is null)
        {
            return;
        }
        if (active)
        {
            if (!_trashZone.Classes.Contains("drag-over"))
            {
                _trashZone.Classes.Add("drag-over");
            }
        }
        else
        {
            _trashZone.Classes.Remove("drag-over");
        }
    }

    /// <summary>
    /// Handles a SelectedBay change while a drag is live — the bay-hover
    /// preview switches bays mid-drag, and every switch discards and
    /// rebuilds the rack containers. The presenter cache and any preview
    /// transforms now point at detached visuals, and if the newly shown bay
    /// contains the dragged strip (the user hovered back to the origin bay),
    /// its fresh container defaults to visible — resurrecting a second copy
    /// of the strip under the ghost. Invalidate the visuals-derived state
    /// and re-hide the strip's new container once it materializes.
    /// </summary>
    private void OnSelectedBayChangedDuringDrag()
    {
        if (_dragState != DragState.Dragging)
        {
            return;
        }
        _presenterCache.Clear();
        ClearDropPreview(animate: false);
        Avalonia.Threading.Dispatcher.UIThread.Post(ReHideDraggingSource, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-applies the source hide after a mid-drag bay switch: finds the
    /// dragged strip's freshly built rack container in the now-shown bay
    /// (null when the shown bay doesn't contain it) and collapses it, so
    /// the strip keeps existing only as the cursor ghost. Also re-targets
    /// <see cref="_draggingSourcePresenter"/> so the drag-end restore
    /// un-hides the container that is actually on screen.
    /// </summary>
    private void ReHideDraggingSource()
    {
        if ((_dragState != DragState.Dragging) || (_draggingStrip is not { } strip))
        {
            return;
        }
        var stripView = this.FindControl<ItemsControl>("RacksHost")
            ?.GetVisualDescendants()
            .OfType<FlightStripControl>()
            .FirstOrDefault(c => ReferenceEquals(c.DataContext, strip));
        var presenter = stripView?.FindAncestorOfType<ContentPresenter>();
        if (presenter is not null)
        {
            presenter.IsVisible = false;
            _draggingSourcePresenter = presenter;
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

    // Pickup scale factor + animation length: a barely-perceptible lift that
    // reads as the strip rising off the bay toward the user's hand.
    private const double GhostPickupScale = 1.03;
    private const int GhostPickupMs = 80;

    /// <summary>
    /// Creates a semi-transparent clone of the strip's FlightStripControl in
    /// the DragGhostCanvas so the user sees the strip under the cursor during
    /// the drag. Position is set from the current pointer position and
    /// updated in <see cref="UpdateDrag"/> on every pointer move. The ghost
    /// keeps the exact grab point — the pointer stays over the same spot of
    /// the strip it pressed on — and "lifts" with a short scale/opacity/
    /// shadow animation on pickup.
    /// </summary>
    private void ShowDragGhost(StripItemViewModel strip, FlightStripControl sourceView, PointerEventArgs e)
    {
        var canvas = _dragGhostCanvas;
        if (canvas is null)
        {
            return;
        }
        // Park the ghost at Canvas (0,0) once; per-frame movement is handled
        // by mutating a single TranslateTransform below. Canvas.Left/Top
        // would invalidate the Canvas's arrange on every pointer move —
        // RenderTransform skips layout entirely and re-renders in the
        // composition pass.
        //
        // The ghost canvas sits OUTSIDE the racks' LayoutTransformControl,
        // so without the ScaleTransform the ghost would render at 1.0 scale
        // while the rack strips render at ZoomScale. Scale is listed before
        // the translate with a top-left origin, so the translate stays in
        // canvas pixels and UpdateGhostPosition's math is scale-agnostic.
        var zoom = (DataContext as VStripsViewModel)?.ZoomScale ?? 1.0;
        _ghostBaseZoom = zoom;

        var pointerInCanvas = e.GetPosition(canvas);
        var stripTopLeft = sourceView.TranslatePoint(new Point(0, 0), canvas);
        _ghostGrabOffset = stripTopLeft is { } topLeft
            ? new Point(Math.Max(0, pointerInCanvas.X - topLeft.X), Math.Max(0, pointerInCanvas.Y - topLeft.Y))
            : new Point(24, 16);

        var ghostTransform = new TranslateTransform();
        var ghostScale = new ScaleTransform(zoom, zoom)
        {
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = ScaleTransform.ScaleXProperty,
                    Duration = TimeSpan.FromMilliseconds(GhostPickupMs),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
                new Avalonia.Animation.DoubleTransition
                {
                    Property = ScaleTransform.ScaleYProperty,
                    Duration = TimeSpan.FromMilliseconds(GhostPickupMs),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            },
        };
        var ghost = new Border
        {
            Child = new FlightStripControl { DataContext = strip, Tag = strip },
            BoxShadow = BoxShadows.Parse("0 4 12 2 #66000000"),
            Opacity = 0.75,
            IsHitTestVisible = false,
            RenderTransform = new TransformGroup { Children = { ghostScale, ghostTransform } },
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(GhostPickupMs),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            },
        };
        Canvas.SetLeft(ghost, 0);
        Canvas.SetTop(ghost, 0);
        canvas.Children.Add(ghost);
        _dragGhost = ghost;
        _dragGhostTransform = ghostTransform;
        _dragGhostScale = ghostScale;
        UpdateGhostPosition(pointerInCanvas);

        // Pickup: nudge the just-created ghost toward its lifted state so the
        // transitions animate scale + opacity from their initial values.
        ghost.Opacity = 0.92;
        ghostScale.ScaleX = zoom * GhostPickupScale;
        ghostScale.ScaleY = zoom * GhostPickupScale;
    }

    private void UpdateGhostPosition(Point pointerInCanvas)
    {
        if (_dragGhostTransform is null)
        {
            return;
        }
        // Keep the recorded grab point under the pointer — the strip sticks
        // to the hand exactly where it was picked up instead of snapping to
        // a fixed corner offset.
        _dragGhostTransform.X = pointerInCanvas.X - _ghostGrabOffset.X;
        _dragGhostTransform.Y = pointerInCanvas.Y - _ghostGrabOffset.Y;
    }

    /// <summary>
    /// Single convergence point for every drag exit path (drop, cancel,
    /// capture lost, disconnect). Restores the source presenter, removes the
    /// ghost, clears the drop preview and presenter cache, restores the
    /// pre-hover bay, and resets all drag state fields. Safe to call
    /// repeatedly — a no-op when nothing is active.
    /// </summary>
    private void CleanupDrag()
    {
        _dragState = DragState.Idle;
        _draggingStrip = null;
        _draggingFromRack = null;
        _draggingFromIndex = -1;
        _pressedStripView = null;
        _dragPointer = null;
        SetTrashHighlight(false);
        SetBayHighlight(null);
        StopAutoscroll();

        // Clear any lingering drop-preview before the drag ends so the
        // rack layout snaps back immediately on cancel/drop — an animated
        // close here would leave transforms mid-flight on presenters the
        // optimistic reorder is about to re-home.
        ClearDropPreview(animate: false);

        // Restore the source presenter first so any post-drop broadcast
        // that re-renders the rack finds it in its normal visible state.
        if (_draggingSourcePresenter is not null)
        {
            _draggingSourcePresenter.IsVisible = true;
            _draggingSourcePresenter = null;
        }

        if (_dragGhostCanvas is not null && _dragGhost is not null)
        {
            _dragGhostCanvas.Children.Remove(_dragGhost);
        }
        _dragGhost = null;
        _dragGhostTransform = null;
        _dragGhostScale = null;
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
        var menu = BuildStripContextMenu(strip, vm, anchor);
        // showAtPointer: true anchors the flyout at the current cursor
        // position rather than the anchor control's top-left, which for
        // a full-width rack Border would land far from where the user
        // right-clicked.
        menu.ShowAt(anchor, showAtPointer: true);
    }

    /// <summary>
    /// Builds the strip context menu's items without showing it — factored out
    /// of <see cref="ShowStripContextMenu"/> so view-level tests can assert
    /// the offered items (Offset, Push to, Delete, plus type-specific
    /// entries) without needing to intercept a Popup in the headless visual
    /// tree. The <paramref name="editorAnchor"/> is passed through to the
    /// inline-editor Open() calls inside the click handlers; pass the same
    /// anchor you intend to ShowAt on for real invocations.
    /// </summary>
    internal MenuFlyout BuildStripContextMenu(StripItemViewModel strip, VStripsViewModel vm, Control? editorAnchor = null)
    {
        var anchor = editorAnchor ?? (Control)this;
        var menu = new MenuFlyout();

        // Every emit dispatches by strip id, so scanned copies
        // (<c>STRIP_{callsign}_{shortGuid}</c> that share a callsign with
        // the original) are now safely addressable from the menu — no
        // separate guard is needed.
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

        // "Push all in rack to" — bulk move every strip in this strip's
        // rack. Hidden when the rack only holds this single strip (then
        // "Push to" already does the same job).
        var (_, sourceRack) = FindRackContaining(vm, strip);
        if (sourceRack is not null && sourceRack.Strips.Count > 1)
        {
            var pushAllMenu = new MenuItem { Header = "Push all in rack to" };
            foreach (var bay in vm.Bays)
            {
                var baySnapshot = bay;
                var rackSnapshot = sourceRack;
                var item = new MenuItem { Header = bay.IsExternal ? $"{bay.Name}  ↗" : bay.Name };
                item.Click += async (_, _) => await PushAllInRackAsync(vm, rackSnapshot, baySnapshot);
                pushAllMenu.Items.Add(item);
            }
            menu.Items.Add(pushAllMenu);
        }

        // "Scan to" — copy a full strip into an external facility's bay
        // while keeping the originator's strip in place (coordination
        // handoff preview). Full-strip-only; hidden when no external
        // bays are accessible from the current position. Listed after
        // "Push to" / "Push all in rack to" so the destructive move
        // affordances stay nearer the top.
        if (strip.IsFullStrip)
        {
            var externalBays = vm.Bays.Where(b => b.IsExternal).ToList();
            if (externalBays.Count > 0)
            {
                var scanMenu = new MenuItem { Header = "Scan to" };
                foreach (var bay in externalBays)
                {
                    var baySnapshot = bay;
                    // Submenu only shows external bays, so the ↗ marker
                    // would be redundant — drop it here.
                    var item = new MenuItem { Header = bay.Name };
                    item.Click += async (_, _) => await vm.ScanStripAsync(strip, baySnapshot, rack: 0, index: null);
                    scanMenu.Items.Add(item);
                }
                menu.Items.Add(scanMenu);
            }
        }

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += async (_, _) => await vm.DeleteStripAsync(strip);
        menu.Items.Add(deleteItem);

        return menu;
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
        var menu = BuildEmptyRackMenu(rack, vm);
        if (menu is null)
        {
            return;
        }
        // The anchor is the rack Border, which is 547 px wide — ShowAt without
        // showAtPointer lands the menu at the Border's top-left, often at the
        // opposite end of the window from where the user right-clicked.
        menu.ShowAt(anchor, showAtPointer: true);
    }

    /// <summary>
    /// Builds the empty-rack context menu's items without showing it —
    /// factored out of <see cref="ShowEmptyRackMenu"/> so view-level tests
    /// can assert the offered items (Add half-strip, Add separator with its
    /// four styles or the locked single-handwritten fallback, Add blank
    /// strip) without needing a visible Popup. Returns null when no bay is
    /// selected (there's nothing to anchor the new strip to).
    /// </summary>
    internal static MenuFlyout? BuildEmptyRackMenu(StripRackViewModel rack, VStripsViewModel vm)
    {
        if (vm.SelectedBay is null)
        {
            return null;
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
                item.Click += async (_, _) => await vm.CreateSeparatorAsync(styleSnapshot, selectedBay, rack.RackIndex, index: null, label: null);
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
                await vm.CreateSeparatorAsync(SeparatorStyle.Handwritten, selectedBay, rack.RackIndex, index: null, label: null);
            menu.Items.Add(addHandwritten);
        }

        // Index null on each adder → server appends at the rack tail (visual
        // top), so a freshly added separator / blank stacks above any
        // existing strips instead of pushing them off the top.
        var addBlank = new MenuItem { Header = "Add blank strip" };
        addBlank.Click += async (_, _) => await vm.CreateBlankAsync(selectedBay, rack.RackIndex, index: null);
        menu.Items.Add(addBlank);

        // "Push all to" — bulk move every strip in this rack to another
        // bay's rack 0. Only meaningful if the rack actually has strips.
        if (rack.Strips.Count > 0)
        {
            var pushAllMenu = new MenuItem { Header = "Push all to" };
            foreach (var bay in vm.Bays)
            {
                var baySnapshot = bay;
                var rackSnapshot = rack;
                var item = new MenuItem { Header = bay.IsExternal ? $"{bay.Name}  ↗" : bay.Name };
                item.Click += async (_, _) => await PushAllInRackAsync(vm, rackSnapshot, baySnapshot);
                pushAllMenu.Items.Add(item);
            }
            menu.Items.Add(pushAllMenu);
        }

        return menu;
    }

    /// <summary>
    /// Walks the bay/rack tree looking for the rack that owns
    /// <paramref name="strip"/>. Used by the "Push all in rack to" submenu so
    /// it can identify the source rack from a right-clicked strip without
    /// touching the visual tree. Returns (null, null) if the strip lives in
    /// the printer queue (no rack ownership).
    /// </summary>
    private static (StripBayViewModel? Bay, StripRackViewModel? Rack) FindRackContaining(VStripsViewModel vm, StripItemViewModel strip)
    {
        foreach (var bay in vm.Bays)
        {
            foreach (var r in bay.Racks)
            {
                if (r.Strips.Contains(strip))
                {
                    return (bay, r);
                }
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Pushes every strip currently in <paramref name="sourceRack"/> to the
    /// tail of rack 0 in <paramref name="destBay"/>, preserving the source's
    /// visual order. Snapshots the rack first because each
    /// <see cref="VStripsViewModel.MoveStripAsync"/> call mutates the
    /// collection as the canonical command echoes back as a server broadcast.
    /// </summary>
    private static async Task PushAllInRackAsync(VStripsViewModel vm, StripRackViewModel sourceRack, StripBayViewModel destBay)
    {
        var snapshot = sourceRack.Strips.ToArray();
        foreach (var strip in snapshot)
        {
            await vm.MoveStripAsync(strip, destBay, rack: 0, index: null);
        }
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

    // ── Drop preview (shifting strips + append line) ────────────

    /// <summary>
    /// Tracks the insertion target during a strip drag and shows a visible
    /// gap where the strip will land. Called from <see cref="ResolveHover"/>
    /// with an already-resolved rack target so the preview follows the
    /// pointer continuously. A rack change slides the previous rack's strips
    /// home; an index change within the rack re-targets the transforms, so
    /// strips entering/leaving the shifted set animate to their new offset
    /// and the gap visibly slides along the rack.
    /// </summary>
    private void UpdateRackPreview(Border rackBorder, StripRackViewModel rack, int index)
    {
        if (_draggingStrip is null)
        {
            ClearDropPreview(animate: true);
            return;
        }
        if (ReferenceEquals(_dropPreviewRack, rack) && _dropPreviewIndex == index)
        {
            return;
        }
        if (_dropPreviewRack is not null && !ReferenceEquals(_dropPreviewRack, rack))
        {
            ClearDropPreview(animate: true);
        }

        ApplyDropPreview(rackBorder, rack, index, animate: true);
    }

    /// <summary>
    /// Applies the drop preview for visual index <paramref name="visualIdx"/>
    /// in <paramref name="rack"/>. <c>visualIdx &lt; visible.Count</c> lifts
    /// every visible strip at that bottom-up position and above by one strip
    /// height (animated TranslateTransform), opening a gap at the target
    /// position. <c>visualIdx == visible.Count</c> is "append above the
    /// visual top" and draws a thin yellow line above the topmost visible
    /// strip. Uses visual idx (not model idx) so the logic is uniform across
    /// cross-rack drags (visible = all strips) and same-rack drags (visible
    /// = all strips except the hidden source), and maps 1:1 to the STRIP
    /// wire index.
    /// </summary>
    private void ApplyDropPreview(Border rackBorder, StripRackViewModel rack, int visualIdx, bool animate)
    {
        var rackContent = rackBorder.FindDescendantOfType<Grid>();
        var stripsHost = rackBorder.FindDescendantOfType<ItemsControl>();
        if (rackContent is null || stripsHost is null)
        {
            return;
        }

        var visible = GetVisiblePresenters(stripsHost, rack);
        ApplyDropPreviewToVisible(rackContent, rack, visualIdx, visible, animate);
    }

    private void ApplyDropPreviewToVisible(
        Grid rackContent,
        StripRackViewModel rack,
        int visualIdx,
        List<(ContentPresenter Presenter, StripItemViewModel Vm, double Top)> visible,
        bool animate
    )
    {
        var stripHeight = ResolveDragStripHeight(visible);
        _dropPreviewRack = rack;
        _dropPreviewIndex = visualIdx;
        RemovePreviewLine();
        _dropPreviewShifted.Clear();

        for (var i = 0; i < visible.Count; i++)
        {
            var shifted = (visualIdx < visible.Count) && (i >= visualIdx);
            var transform = GetPreviewTransform(visible[i].Presenter, animate);
            transform.Y = shifted ? -stripHeight : 0.0;
            if (shifted)
            {
                _dropPreviewShifted.Add((visible[i].Presenter, transform));
            }
        }

        if ((visualIdx >= visible.Count) && (visible.Count > 0))
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
                Opacity = 0,
                Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(80),
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                    },
                },
            };
            rackContent.Children.Add(line);
            line.Opacity = 1;
            _dropPreviewLine = line;
            _dropPreviewLineHost = rackContent;
        }
    }

    /// <summary>
    /// Returns the presenter's preview TranslateTransform, installing one on
    /// first use. <paramref name="animate"/> attaches (or detaches) the
    /// Y-transition so callers can choose between the animated gap slide and
    /// an instant snap — the initial preview on drag start must snap so the
    /// shift lands in the same frame as the source hide, and drop/cancel
    /// cleanup must snap so no transform survives into the post-move layout.
    /// </summary>
    private static TranslateTransform GetPreviewTransform(ContentPresenter presenter, bool animate)
    {
        if (presenter.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            presenter.RenderTransform = transform;
        }
        if (animate)
        {
            transform.Transitions ??= new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(GapAnimationMs),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            };
        }
        else
        {
            transform.Transitions = null;
        }
        return transform;
    }

    /// <summary>
    /// Applies the initial preview synchronously on drag start at the source
    /// strip's own slot (visual idx == <paramref name="fromIdx"/>), before
    /// the first pointer move computes a preview. Without this, the layout
    /// pass that processes IsVisible=false on the source runs first — the
    /// user briefly sees the strips above the source fall down to occupy
    /// the empty slot, then pop back up when the gap preview lands.
    /// Applying here queues the shift (or yellow line, for source-at-top)
    /// into the same layout pass as the hide, so the rack lifts out into
    /// the ghost smoothly without the other strips shifting underneath.
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
        //
        // animate: false is load-bearing — the transforms must land in the
        // same frame as the source hide so the two cancel out visually. An
        // animated shift would let the strips dip into the collapsed slot
        // and slide back up.
        var visible = GetVisiblePresenters(stripsHost, rack);
        ApplyDropPreviewToVisible(rackContent, rack, fromIdx, visible, animate: false);
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
    /// Undoes any active drop preview: returns every shifted presenter to
    /// its natural position and removes the append-line overlay. Safe to
    /// call repeatedly — a no-op when no preview is active.
    /// <paramref name="animate"/> slides the strips home (pointer left the
    /// rack mid-drag); passing false snaps them, which drop and cancel
    /// paths need so no render offset survives into the post-move layout.
    /// </summary>
    private void ClearDropPreview(bool animate)
    {
        foreach (var (_, transform) in _dropPreviewShifted)
        {
            if (!animate)
            {
                transform.Transitions = null;
            }
            transform.Y = 0;
        }
        _dropPreviewShifted.Clear();
        RemovePreviewLine();
        _dropPreviewRack = null;
        _dropPreviewIndex = -1;
    }

    private void RemovePreviewLine()
    {
        if (_dropPreviewLine is not null && _dropPreviewLineHost is not null)
        {
            _dropPreviewLineHost.Children.Remove(_dropPreviewLine);
        }
        _dropPreviewLine = null;
        _dropPreviewLineHost = null;
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

    private async void OnDepartureMoveAllToBayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.MoveAllPrinterStripsToBayAsync(PrinterQueueKind.Departure);
        }
    }

    private async void OnArrivalMoveAllToBayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VStripsViewModel vm)
        {
            await vm.MoveAllPrinterStripsToBayAsync(PrinterQueueKind.Arrival);
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
        // Esc cancels an active drag. First check, before every other guard:
        // cancel is view-local and must work even mid-disconnect, and while
        // dragging Esc must not fall through to the Find-close / deselect /
        // printer-toggle branches below.
        if ((e.Key == Key.Escape) && (_dragState == DragState.Dragging))
        {
            CancelDrag();
            e.Handled = true;
            return;
        }

        if (DataContext is not VStripsViewModel vm)
        {
            base.OnKeyDown(e);
            return;
        }

        // Find keys work regardless of connection state so the bar can open/close
        // over an empty view; they take priority over strip shortcuts.
        if (HandleFindKeys(e))
        {
            return;
        }

        // While the find box owns keyboard focus, its editing keys (arrows, Tab,
        // Backspace, letters…) must not drive strip shortcuts.
        if (_findController.IsVisible && FindBar.IsKeyboardFocusWithin)
        {
            base.OnKeyDown(e);
            return;
        }

        if (!vm.IsConnected)
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
                    // separator right-click menu afterwards. Index null →
                    // append at rack top.
                    await vm.CreateSeparatorAsync(SeparatorStyle.Handwritten, vm.SelectedBay, targetRack, index: null, label: null);
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
        var boxId = box.ToString(System.Globalization.CultureInfo.InvariantCulture);
        editor.Open(anchor, current, text => _ = vm.AnnotateAsync(strip, boxId, text), substituteCheckmark: true);
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
        // the same label but new style. Delete addresses the existing
        // separator by id so two same-label separators in the rack don't
        // collide; the create lays the replacement at the same slot.
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
        var del = VStripsCanonicalBuilder.BuildSeparatorDeleteById(strip.Id);
        var create = VStripsCanonicalBuilder.BuildSeparatorCreate(nextStyle, vm.SelectedBay.FacilityId, vm.SelectedBay.Name, rack, index, label);
        // Use the public dispatch through a create-call bounded by the
        // separator lock check we just did; reuse EditSeparatorLabelAsync's
        // _sendCommand path by going through the canonical builders directly.
        await vm.DispatchRawAsync(del);
        await vm.DispatchRawAsync(create);
    }
}
