using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The overlay over the scope canvas that holds the views floating on it (<see cref="ScopeViewModel.FloatingViews"/>), each
/// anchored to its corner by alignment and margin. It has no background, so a click on its empty area reaches the canvas.
/// A title press on a view card (<see cref="ScopeViewCard.MoveRequestedEvent"/>) starts a move: the card's outline tracks
/// the pointer over a capture surface, a left press drops it through <c>.view {name} tear</c>, and Esc or a press outside
/// the layer cancels the move, as does closing the moved view.
/// </summary>
public sealed class ScopeTearoffLayer : Panel
{
    private static readonly IBrush OutlineBrush = new SolidColorBrush(Color.Parse("#E0B84A"));

    // The floating views' cards; a panel of its own so a rebuild leaves the surface and the follower alone.
    private readonly Panel _views = new();

    // The floating views whose place changes the layer follows.
    private readonly List<ScopeDockViewModel> _watchedViews = [];

    // Shown only while a move runs: takes every pointer event, so no click reaches the canvas.
    private readonly Border _surface = new() { Background = Brushes.Transparent, IsVisible = false };

    private ScopeViewModel? _viewModel;
    private TopLevel? _topLevel;

    // The move in progress: the follower under the pointer, the card it moves (hidden meanwhile; null for a docked card),
    // the drop's command for a placement, the grab offset and the moved card's size.
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
        Children.Add(_views);
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
            old.FloatingViews.CollectionChanged -= OnFloatingViewsChanged;
        }

        _viewModel = DataContext as ScopeViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.FloatingViews.CollectionChanged += OnFloatingViewsChanged;
        }

        RebuildViews();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(ScopeViewCard.MoveRequestedEvent, OnViewMoveRequested);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndMove();
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _topLevel?.RemoveHandler(ScopeViewCard.MoveRequestedEvent, OnViewMoveRequested);
        _topLevel = null;
    }

    private void OnFloatingViewsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildViews();

    private void OnViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScopeDockViewModel.Place))
        {
            RebuildViews();
        }
    }

    // Closing the moved view ends its move.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        bool closed = (e.PropertyName == nameof(ScopeViewModel.OpenViews)) && (_movingView is { } name) && (_viewModel?.IsViewOpen(name) == false);
        if (closed)
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
    /// A title press on a view card, docked or floating, starts a move of the view: its outline follows the pointer, a
    /// floating card hides meanwhile, a docked one stays in the dock until the drop. Nothing starts while a move runs.
    /// </summary>
    private void OnViewMoveRequested(object? sender, ScopeViewMoveRequestedEventArgs e)
    {
        ScopeViewCard card = e.Card;
        if (
            (_viewModel is not { } vm)
            || (_follower is not null)
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

    /// <summary>Places the moved view at the corner nearest the follower, through the move's command.</summary>
    private void Drop()
    {
        if ((_follower is null) || (_dropCommand is not { } dropCommand) || (_viewModel is not { } vm))
        {
            return;
        }

        // The follower's bounds may lag at the drop, so they are no measure of where it was dropped; the outline is drawn
        // at the size taken at the press.
        var rect = new Rect(_followerAt, _size);
        string command = dropCommand(ScopePlacement.Nearest(rect, Bounds.Size));
        EndMove();
        vm.RunCommand(command);
    }

    /// <summary>Ends a move, if one runs: the follower goes away and a moved card shows again where it was.</summary>
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

    /// <summary>Whether a view is being moved, which Esc cancels before anything else on the Scope.</summary>
    internal bool IsMoving => _follower is not null;

    // Esc cancels a move; any other key, or Esc with no move, passes on.
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
    }
}
