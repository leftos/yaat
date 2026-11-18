using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The overlay over the scope canvas that holds the toolbar buttons torn off onto the scope (<see cref="ScopeViewModel.Tearoffs"/>)
/// and the views floating on it (<see cref="ScopeViewModel.FloatingViews"/>), each anchored to its corner by alignment and
/// margin. It has no background, so a click on its empty area reaches the canvas. A strip press on a button
/// (<see cref="ScopeButtonControl.TearoffRequestedEvent"/>) or a title press on a view card
/// (<see cref="ScopeViewCard.MoveRequestedEvent"/>) starts a move: a follower (a button copy, or a card's outline) tracks
/// the pointer over a capture surface, a left press drops it through <c>.scope tearoff</c> or <c>.view {name} tear</c>, and
/// Esc or a press outside the layer cancels the move (Esc otherwise leaves DEL TEAROFF's mode), as does closing the moved
/// view or turning DEL TEAROFF's mode on.
/// </summary>
public sealed class ScopeTearoffLayer : Panel
{
    private static readonly IBrush OutlineBrush = new SolidColorBrush(Color.Parse("#E0B84A"));

    // The committed copies; a panel of its own so a rebuild leaves the surface and the follower alone.
    private readonly Panel _copies = new();

    // The floating views' cards, likewise.
    private readonly Panel _views = new();

    // The floating views whose place changes the layer follows.
    private readonly List<ScopeDockViewModel> _watchedViews = [];

    // Shown only while a move runs: takes every pointer event, so no click reaches the canvas.
    private readonly Border _surface = new() { Background = Brushes.Transparent, IsVisible = false };

    private ScopeViewModel? _viewModel;
    private TopLevel? _topLevel;

    // The move in progress: the follower under the pointer, the control it moves (hidden meanwhile; null for a new
    // tear-off or a docked card), the drop's command for a placement, the grab offset and the moved thing's size.
    private Control? _follower;
    private Control? _moving;
    private Func<ScopePlacement, string>? _dropCommand;

    // The moved view's name, or null while no view moves: closing it ends the move.
    private string? _movingView;
    private Point _grab;
    private Size _size;

    // The follower's top-left in the layer.
    private Point _followerAt;

    public ScopeTearoffLayer()
    {
        // Button copies above view cards: a small copy is never hidden under a card.
        Children.Add(_views);
        Children.Add(_copies);
        Children.Add(_surface);
        _surface.PointerMoved += OnSurfacePointerMoved;
        _surface.PointerPressed += OnSurfacePointerPressed;
        _surface.PointerReleased += (_, e) => e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is { } old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.Tearoffs.CollectionChanged -= OnTearoffsChanged;
            old.FloatingViews.CollectionChanged -= OnFloatingViewsChanged;
        }

        _viewModel = DataContext as ScopeViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.Tearoffs.CollectionChanged += OnTearoffsChanged;
            vm.FloatingViews.CollectionChanged += OnFloatingViewsChanged;
        }

        Rebuild();
        RebuildViews();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(ScopeButtonControl.TearoffRequestedEvent, OnTearoffRequested);
        _topLevel?.AddHandler(ScopeViewCard.MoveRequestedEvent, OnViewMoveRequested);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndMove();
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _topLevel?.RemoveHandler(ScopeButtonControl.TearoffRequestedEvent, OnTearoffRequested);
        _topLevel?.RemoveHandler(ScopeViewCard.MoveRequestedEvent, OnViewMoveRequested);
        _topLevel = null;
    }

    private void OnTearoffsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnFloatingViewsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildViews();

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScopeDockViewModel.Place))
        {
            RebuildViews();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A map toggle's copy takes its label from the Scope's map toggles.
        if (e.PropertyName == nameof(ScopeViewModel.MapToggles))
        {
            Rebuild();
        }
        else if (ScopeToolbar.IsDrawnState(e.PropertyName))
        {
            CancelStaleMove(e.PropertyName);
            RefreshCopies();
        }
    }

    /// <summary>
    /// Ends a move whose source went stale: DEL TEAROFF's mode turned on (a new copy goes away and a moved one shows again),
    /// or the moved view closed.
    /// </summary>
    private void CancelStaleMove(string? propertyName)
    {
        bool deleting = (propertyName == nameof(ScopeViewModel.IsDeletingTearoffs)) && (_viewModel?.IsDeletingTearoffs == true);
        bool closed = (propertyName == nameof(ScopeViewModel.OpenViews)) && (_movingView is { } name) && (_viewModel?.IsViewOpen(name) == false);
        if (deleting || closed)
        {
            EndMove();
        }
    }

    // A press outside the layer (on the dock column or the toolbar) while a move runs cancels the move, and passes on to
    // what it was pressed on.
    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((_follower is not null) && !new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            EndMove();
        }
    }

    private void RefreshCopies()
    {
        foreach (ScopeButtonControl copy in _copies.Children.OfType<ScopeButtonControl>())
        {
            copy.Refresh();
        }
    }

    /// <summary>Builds one copy per committed tear-off, at its place; a move in progress is cancelled.</summary>
    private void Rebuild()
    {
        EndMove();
        _copies.Children.Clear();
        if (_viewModel is not { } vm)
        {
            return;
        }

        // A copy keeps the shape its button has where it lives (ScopeToolbar.IsHalfTile), as its follower had.
        foreach (ScopeTearoff tearoff in vm.Tearoffs)
        {
            ScopeButton model = vm.ButtonFor(tearoff.Key);
            var copy = new ScopeButtonControl(model, isTearoff: true)
            {
                IsHalf = ScopeToolbar.IsHalfTile(vm, model),
                ViewModel = vm,
                HorizontalAlignment = tearoff.Place.HorizontalAlignment,
                VerticalAlignment = tearoff.Place.VerticalAlignment,
                Margin = tearoff.Place.ToMargin(),
            };
            copy.Refresh();
            _copies.Children.Add(copy);
        }
    }

    /// <summary>Builds one card per floating view, at its place; a move in progress is cancelled.</summary>
    private void RebuildViews()
    {
        EndMove();
        foreach (ScopeDockViewModel watched in _watchedViews)
        {
            watched.PropertyChanged -= OnViewPropertyChanged;
        }

        _watchedViews.Clear();
        _views.Children.Clear();
        foreach (ScopeDockViewModel view in _viewModel?.FloatingViews ?? [])
        {
            view.PropertyChanged += OnViewPropertyChanged;
            _watchedViews.Add(view);

            // A view docked a moment before the floating list catches up has no place: it shows in the dock alone.
            if (view.Place is not { } place)
            {
                continue;
            }

            _views.Children.Add(
                new ScopeViewCard
                {
                    DataContext = view,
                    IsFloating = true,
                    HorizontalAlignment = place.HorizontalAlignment,
                    VerticalAlignment = place.VerticalAlignment,
                    Margin = place.ToMargin(),
                }
            );
        }
    }

    /// <summary>
    /// A strip press: a bar button not yet torn off starts a new tear-off; a copy starts a move of itself. Nothing starts
    /// while a move runs or DEL TEAROFF's mode is on.
    /// </summary>
    private void OnTearoffRequested(object? sender, ScopeTearoffRequestedEventArgs e)
    {
        ScopeButtonControl source = e.Control;
        if (
            (_viewModel is not { } vm)
            || (_follower is not null)
            || vm.IsDeletingTearoffs
            || (!source.IsTearoff && vm.IsTornOff(source.Key))
            || (source.TranslatePoint(default, this) is not { } topLeft)
        )
        {
            return;
        }

        e.Handled = true;
        _grab = e.Press.GetPosition(this) - topLeft;
        _size = source.Bounds.Size;
        _followerAt = topLeft;
        // The follower has the shape its copy will have once dropped, at its own content width.
        var follower = new ScopeButtonControl(source.Model, isTearoff: true)
        {
            IsHalf = ScopeToolbar.IsHalfTile(vm, source.Model),
            ViewModel = vm,
            IsHitTestVisible = false,
        };
        follower.Refresh();
        ScopeButtonKey key = source.Key;
        BeginMove(follower, source.IsTearoff ? source : null, e.Press, place => ScopeToolbarButtons.TearoffCommand(key, place));
    }

    /// <summary>
    /// A title press on a view card, docked or floating, starts a move of the view: its outline follows the pointer, a
    /// floating card hides meanwhile, a docked one stays in the dock until the drop. Nothing starts while a move runs or
    /// DEL TEAROFF's mode is on.
    /// </summary>
    private void OnViewMoveRequested(object? sender, ScopeViewMoveRequestedEventArgs e)
    {
        ScopeViewCard card = e.Card;
        if (
            (_viewModel is not { } vm)
            || (_follower is not null)
            || vm.IsDeletingTearoffs
            || (card.ViewName is not { } name)
            || (card.TranslatePoint(default, this) is not { } topLeft)
        )
        {
            return;
        }

        e.Handled = true;
        bool floating = _views.Children.Contains(card);
        _grab = e.Press.GetPosition(this) - topLeft;
        _size = floating ? card.Bounds.Size : FloatingSizeOf(card);
        _followerAt = topLeft;
        _movingView = name;
        var outline = new Border
        {
            Width = _size.Width,
            Height = _size.Height,
            BorderBrush = OutlineBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        BeginMove(outline, floating ? card : null, e.Press, place => ScopeToolbarButtons.ViewTearCommand(name, place));
    }

    /// <summary>
    /// The content size docked <paramref name="card"/> takes once floating. The docked card is stretched to the dock
    /// column and hides its dock button, so neither its bounds nor its own unconstrained measure is that size: an unseen
    /// floating probe of the same view is measured unconstrained in the layer, then removed, leaving the dock's layout alone.
    /// </summary>
    private Size FloatingSizeOf(ScopeViewCard card)
    {
        var probe = new ScopeViewCard
        {
            DataContext = card.DataContext,
            IsFloating = true,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _views.Children.Add(probe);
        probe.Measure(Size.Infinity);
        Size size = probe.DesiredSize;
        _views.Children.Remove(probe);
        return size;
    }

    /// <summary>
    /// Shows <paramref name="follower"/> at the follower's top-left, hides <paramref name="moving"/>, and captures the
    /// pointer of <paramref name="press"/> on the surface until the drop, which runs <paramref name="dropCommand"/>'s command.
    /// </summary>
    private void BeginMove(Control follower, Control? moving, PointerPressedEventArgs press, Func<ScopePlacement, string> dropCommand)
    {
        follower.HorizontalAlignment = HorizontalAlignment.Left;
        follower.VerticalAlignment = VerticalAlignment.Top;

        // A render offset, not a margin: arrange never squeezes the follower at the layer's right or bottom edge.
        follower.RenderTransform = new TranslateTransform(_followerAt.X, _followerAt.Y);
        _follower = follower;
        _moving = moving;
        _moving?.IsVisible = false;
        _dropCommand = dropCommand;
        Children.Add(follower);
        _surface.IsVisible = true;
        press.Pointer.Capture(_surface);
    }

    private void OnSurfacePointerMoved(object? sender, PointerEventArgs e) => Follow(e.GetPosition(this));

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if ((_follower is null) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Follow(e.GetPosition(this));
        e.Pointer.Capture(null);
        Drop();
    }

    private void Follow(Point pointer)
    {
        _followerAt = new Point(pointer.X - _grab.X, pointer.Y - _grab.Y);
        if (_follower?.RenderTransform is TranslateTransform offset)
        {
            offset.X = _followerAt.X;
            offset.Y = _followerAt.Y;
        }
    }

    /// <summary>Places the moved button or view at the corner nearest the follower, through the move's command.</summary>
    private void Drop()
    {
        if ((_follower is null) || (_dropCommand is not { } dropCommand) || (_viewModel is not { } vm))
        {
            return;
        }

        // The follower's bounds may lag at the drop, so they are no measure of where it was dropped. A button's follower
        // is drawn at its own content size, which can differ from its source's (a half tile stretched to its pair's width),
        // so it is measured here; a view's outline is drawn at the size taken at the press.
        Size size = _size;
        if (_follower is ScopeButtonControl button)
        {
            button.Measure(Size.Infinity);
            size = button.DesiredSize;
        }

        var rect = new Rect(_followerAt, size);
        string command = dropCommand(ScopePlacement.Nearest(rect, Bounds.Size));
        EndMove();
        vm.RunCommand(command);
    }

    /// <summary>Ends a move, if one runs: the follower goes away and a moved copy or card shows again where it was.</summary>
    private void EndMove()
    {
        if (_follower is { } follower)
        {
            Children.Remove(follower);
            _follower = null;
        }

        _moving?.IsVisible = true;
        _moving = null;
        _dropCommand = null;
        _movingView = null;
        _surface.IsVisible = false;
    }

    /// <summary>Whether a tear-off or view is being moved, which Esc cancels before anything else on the Scope.</summary>
    internal bool IsMoving => _follower is not null;

    // Esc cancels a move, else leaves DEL TEAROFF's mode while the Scope has the keyboard focus; any other key, or Esc with
    // neither, passes on.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_follower is not null)
        {
            EndMove();
            e.Handled = true;
        }
        else if ((_viewModel is { IsDeletingTearoffs: true } vm) && HasScopeFocus)
        {
            vm.IsDeletingTearoffs = false;
            e.Handled = true;
        }
    }

    // Whether the Scope has the keyboard focus: in a window of its own always, and docked in the main window only while the
    // focus is on the Scope (ScopeView.IsKeyFocusInside), so an Esc typed in the command box there stays the box's.
    private bool HasScopeFocus => this.FindAncestorOfType<ScopeView>()?.IsKeyFocusInside() == true;
}
