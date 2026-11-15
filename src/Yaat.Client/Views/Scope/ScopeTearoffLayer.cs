using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The overlay over the scope canvas that holds the toolbar buttons torn off onto the scope (<see cref="ScopeViewModel.Tearoffs"/>),
/// each anchored to its corner by alignment and margin. It has no background, so a click on its empty area reaches the
/// canvas. A strip press on a button (<see cref="ScopeButtonControl.TearoffRequestedEvent"/>) starts a move: a follower
/// tracks the pointer over a capture surface, a left press drops it through <c>.scope tearoff</c>, and Esc cancels the
/// move, or leaves DEL TEAROFF's mode.
/// </summary>
public sealed class ScopeTearoffLayer : Panel
{
    // The committed copies; a panel of its own so a rebuild leaves the surface and the follower alone.
    private readonly Panel _copies = new();

    // Shown only while a move runs: takes every pointer event, so no click reaches the canvas.
    private readonly Border _surface = new() { Background = Brushes.Transparent, IsVisible = false };

    private ScopeViewModel? _viewModel;
    private TopLevel? _topLevel;

    // The move in progress: the follower under the pointer, the copy it moves (null for a new tear-off), and the grab offset.
    private ScopeButtonControl? _follower;
    private ScopeButtonControl? _moving;
    private Point _grab;
    private Size _size;

    // The follower's top-left in the layer.
    private Point _followerAt;

    public ScopeTearoffLayer()
    {
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
        }

        _viewModel = DataContext as ScopeViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.Tearoffs.CollectionChanged += OnTearoffsChanged;
        }

        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        _topLevel?.AddHandler(ScopeButtonControl.TearoffRequestedEvent, OnTearoffRequested);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndMove();
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel?.RemoveHandler(ScopeButtonControl.TearoffRequestedEvent, OnTearoffRequested);
        _topLevel = null;
    }

    private void OnTearoffsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A map toggle's copy takes its label from the Scope's map toggles.
        if (e.PropertyName == nameof(ScopeViewModel.MapToggles))
        {
            Rebuild();
        }
        else if (ScopeToolbar.IsDrawnState(e.PropertyName))
        {
            // DEL TEAROFF's mode cancels a move: a new copy goes away and a moved one shows again.
            if ((e.PropertyName == nameof(ScopeViewModel.IsDeletingTearoffs)) && (_viewModel?.IsDeletingTearoffs == true))
            {
                EndMove();
            }

            RefreshCopies();
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

        foreach (ScopeTearoff tearoff in vm.Tearoffs)
        {
            var copy = new ScopeButtonControl(vm.ButtonFor(tearoff.Key), isTearoff: true)
            {
                ViewModel = vm,
                HorizontalAlignment = tearoff.Place.HorizontalAlignment,
                VerticalAlignment = tearoff.Place.VerticalAlignment,
                Margin = tearoff.Place.ToMargin(),
            };
            copy.Refresh();
            _copies.Children.Add(copy);
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
        _follower = new ScopeButtonControl(source.Model, isTearoff: true)
        {
            ViewModel = vm,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // A render offset, not a margin: arrange never squeezes the follower at the layer's right or bottom edge.
            RenderTransform = new TranslateTransform(topLeft.X, topLeft.Y),
        };
        _follower.Refresh();
        _moving = source.IsTearoff ? source : null;
        _moving?.IsVisible = false;
        Children.Add(_follower);
        _surface.IsVisible = true;
        e.Press.Pointer.Capture(_surface);
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

    /// <summary>Places the follower's key at the corner nearest the follower, through <c>.scope tearoff</c>.</summary>
    private void Drop()
    {
        if ((_follower is not { } follower) || (_viewModel is not { } vm))
        {
            return;
        }

        // The source control's measured size: the follower's own bounds are no measure of where it was dropped.
        var rect = new Rect(_followerAt, _size);
        var place = ScopePlacement.Nearest(rect, Bounds.Size);
        ScopeButtonKey key = follower.Key;
        EndMove();
        vm.RunCommand(ScopeToolbarButtons.TearoffCommand(key, place));
    }

    /// <summary>Ends a move, if one runs: the follower goes away and a moved copy shows again where it was.</summary>
    private void EndMove()
    {
        if (_follower is { } follower)
        {
            Children.Remove(follower);
            _follower = null;
        }

        _moving?.IsVisible = true;
        _moving = null;
        _surface.IsVisible = false;
    }

    // Esc cancels a move, else leaves DEL TEAROFF's mode; any other key, or Esc with neither, passes on.
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
        else if (_viewModel is { IsDeletingTearoffs: true } vm)
        {
            vm.IsDeletingTearoffs = false;
            e.Handled = true;
        }
    }
}
