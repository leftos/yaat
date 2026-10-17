using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.ContextMenus;

/// <summary>What a <see cref="FlyoutTab"/> shows beside its label.</summary>
public enum FlyoutTabState
{
    /// <summary>The tab's rows are known; nothing beside the label.</summary>
    Ready,

    /// <summary>The tab's rows are being found; a small spinner beside the label.</summary>
    Searching,

    /// <summary>The tab was searched and has no row; the tab is dimmed and says <c>none</c>.</summary>
    Empty,
}

/// <summary>One tab of a <see cref="FlyoutTabStrip"/>.</summary>
/// <param name="Label">The tab's text: <c>28R</c>.</param>
/// <param name="State">What the tab shows beside the label.</param>
public sealed record FlyoutTab(string Label, FlyoutTabState State);

/// <summary>
/// A row of tabs above a designed flyout's rows, wrapping to further rows in order when they pass the flyout's width. The
/// strip is passive: its tabs never take keyboard focus, so the rows panel owns the keys and calls <see cref="Move"/>; a
/// left press on a tab selects it, and pointing at one only lightens it.
/// </summary>
public sealed class FlyoutTabStrip : Border
{
    /// <summary>The name of a tab's spinner, shown while the tab is <see cref="FlyoutTabState.Searching"/>.</summary>
    public const string SpinnerName = "Spinner";

    private static readonly ILogger Log = AppLog.CreateLogger("FlyoutTabStrip");

    private static readonly IBrush StripLine = Brush.Parse("#3d3d3d");
    private static readonly IBrush TabText = Brush.Parse("#e4e7eb");
    private static readonly IBrush NoneText = Brush.Parse("#9aa3ad");
    private static readonly IBrush TabFill = Brush.Parse("#23272c");
    private static readonly IBrush TabHoverFill = Brush.Parse("#2e343b");
    private static readonly IBrush TabLine = Brush.Parse("#3a414b");
    private static readonly IBrush SelectedFill = Brush.Parse("#24406a");
    private static readonly IBrush SelectedHoverFill = Brush.Parse("#2a4b7a");
    private static readonly IBrush SelectedLine = Brush.Parse("#4a6fa5");
    private static readonly IBrush SpinnerTrack = Brush.Parse("#4a4f57");
    private static readonly IBrush SpinnerHead = Brush.Parse("#8fc1ff");

    private static readonly Animation Spin = new()
    {
        Duration = TimeSpan.FromSeconds(1),
        // Not Infinite: Animation.RunAsync faults an infinite animation at once ("Looping animations must not use the Run method").
        IterationCount = new IterationCount(uint.MaxValue),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
        },
    };

    private readonly WrapPanel _panel = new()
    {
        Orientation = Orientation.Horizontal,
        ItemSpacing = 4,
        LineSpacing = 4,
    };

    private readonly List<TabView> _tabs = [];

    /// <summary>An empty strip; <see cref="SetTabs"/> gives it its tabs.</summary>
    public FlyoutTabStrip()
    {
        Child = _panel;
        Padding = new Thickness(8, 6, 8, 8);
        Margin = new Thickness(0, 0, 0, 2);
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = StripLine;
        Focusable = false;
        IsTabStop = false;
    }

    /// <summary>Raised with the new index when the selection changes by <see cref="Select"/>, <see cref="Move"/> or a press.</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>The selected tab's index; -1 while the strip has no tab.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>The tabs as they show now, each with its current state, in order.</summary>
    public IReadOnlyList<FlyoutTab> Tabs => [.. _tabs.Select(view => view.Tab)];

    /// <summary>
    /// A tab's name for assistive technology: its label, then <c>, searching</c> or <c>, none</c> while it is
    /// <see cref="FlyoutTabState.Searching"/> or <see cref="FlyoutTabState.Empty"/>.
    /// </summary>
    public static string AccessibleName(FlyoutTab tab) =>
        tab.State switch
        {
            FlyoutTabState.Searching => $"{tab.Label}, searching",
            FlyoutTabState.Empty => $"{tab.Label}, none",
            _ => tab.Label,
        };

    /// <summary>
    /// Replaces the tabs, keeping the selected index when it is still in range and selecting the first tab otherwise. Raises no
    /// <see cref="SelectionChanged"/>: the caller that replaces the tabs knows what it shows.
    /// </summary>
    public void SetTabs(IReadOnlyList<FlyoutTab> tabs)
    {
        foreach (TabView old in _tabs)
        {
            old.StopSpinning();
        }

        _tabs.Clear();
        _panel.Children.Clear();
        for (int index = 0; index < tabs.Count; index++)
        {
            int pressed = index;
            var view = new TabView(tabs[index], () => Select(pressed));
            view.Root.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(view.Root).Properties.IsLeftButtonPressed)
                {
                    e.Handled = true;
                    Select(pressed);
                }
            };
            _tabs.Add(view);
            _panel.Children.Add(view.Root);
        }

        SelectedIndex =
            (tabs.Count == 0) ? -1
            : ((SelectedIndex >= 0) && (SelectedIndex < tabs.Count)) ? SelectedIndex
            : 0;
        for (int index = 0; index < _tabs.Count; index++)
        {
            _tabs[index].SetSelected(index == SelectedIndex);
        }
    }

    /// <summary>Shows <paramref name="state"/> on the tab at <paramref name="index"/>, in place.</summary>
    public void SetState(int index, FlyoutTabState state)
    {
        CheckIndex(index);
        _tabs[index].SetState(state);
    }

    /// <summary>Whether the tab at <paramref name="index"/> is running its spinner.</summary>
    public bool IsSpinning(int index)
    {
        CheckIndex(index);
        return _tabs[index].IsSpinning;
    }

    /// <summary>Selects the tab at <paramref name="index"/>, raising <see cref="SelectionChanged"/> when the selection changes.</summary>
    public void Select(int index)
    {
        CheckIndex(index);
        if (index == SelectedIndex)
        {
            return;
        }

        _tabs[SelectedIndex].SetSelected(false);
        _tabs[index].SetSelected(true);
        SelectedIndex = index;
        SelectionChanged?.Invoke(this, index);
    }

    /// <summary>
    /// Selects the previous (<paramref name="delta"/> -1) or next (+1) tab; at either end it does nothing and returns false,
    /// never wrapping round.
    /// </summary>
    public bool Move(int delta)
    {
        if ((delta != -1) && (delta != 1))
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "A tab strip moves one tab at a time: -1 or +1.");
        }

        int target = SelectedIndex + delta;
        if ((SelectedIndex < 0) || (target < 0) || (target >= _tabs.Count))
        {
            return false;
        }

        Select(target);
        return true;
    }

    private void CheckIndex(int index)
    {
        if ((index < 0) || (index >= _tabs.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The strip has {_tabs.Count} tabs.");
        }
    }

    /// <summary>
    /// A tab's rounded box, which UI Automation sees as a tab item named by <see cref="AccessibleName"/>, never keyboard
    /// focusable, whose invoke selects the tab.
    /// </summary>
    private sealed class TabBox(Action select) : Border
    {
        public void Invoke() => select();

        protected override AutomationPeer OnCreateAutomationPeer() => new TabPeer(this);
    }

    private sealed class TabPeer(TabBox owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.Invoke();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;
    }

    /// <summary>One tab's controls: the label, the spinner and the <c>none</c> note in a rounded box.</summary>
    private sealed class TabView
    {
        private readonly Control _spinner;
        private readonly TextBlock _none;
        private FlyoutTabState _state;
        private bool _selected;
        private bool _hovered;
        private bool _onScreen;
        private CancellationTokenSource? _spinning;

        public TabView(FlyoutTab tab, Action select)
        {
            Tab = tab;
            var label = new TextBlock
            {
                Text = tab.Label,
                FontSize = 12,
                Foreground = TabText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.Bind(TextBlock.FontFamilyProperty, label.GetResourceObservable(QuickCommandStrip.MonoFontKey));
            _spinner = SpinnerView();
            _spinner.AttachedToVisualTree += (_, _) => SetOnScreen(true);
            _spinner.DetachedFromVisualTree += (_, _) => SetOnScreen(false);
            _none = new TextBlock
            {
                Text = "none",
                FontSize = 10.5,
                FontStyle = FontStyle.Italic,
                Foreground = NoneText,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Root = new TabBox(select)
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3),
                Focusable = false,
                IsTabStop = false,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, _spinner, _none } },
            };
            Root.PointerEntered += (_, _) => SetHovered(true);
            Root.PointerExited += (_, _) => SetHovered(false);
            SetState(tab.State);
        }

        public Border Root { get; }

        /// <summary>The tab with its current state.</summary>
        public FlyoutTab Tab { get; private set; }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Restyle();
        }

        public void SetState(FlyoutTabState state)
        {
            _state = state;
            Tab = Tab with { State = state };
            AutomationProperties.SetName(Root, AccessibleName(Tab));
            _spinner.IsVisible = (state == FlyoutTabState.Searching);
            _none.IsVisible = (state == FlyoutTabState.Empty);
            Root.Opacity = (state == FlyoutTabState.Empty) ? MenuGlyphRowTemplate.DimmedOpacity : 1;
            Restyle();
            UpdateSpinning();
        }

        public void StopSpinning()
        {
            _spinning?.Cancel();
            _spinning?.Dispose();
            _spinning = null;
        }

        /// <summary>Whether this tab's spinner animation is running.</summary>
        public bool IsSpinning => _spinning is not null;

        private void SetOnScreen(bool onScreen)
        {
            _onScreen = onScreen;
            UpdateSpinning();
        }

        private void SetHovered(bool hovered)
        {
            _hovered = hovered;
            Restyle();
        }

        private void Restyle()
        {
            Root.Background = _selected ? (_hovered ? SelectedHoverFill : SelectedFill) : (_hovered ? TabHoverFill : TabFill);
            Root.BorderBrush = _selected ? SelectedLine : TabLine;
        }

        /// <summary>Turns the spinner only while the tab is searching and on screen, so a closed flyout runs no animation.</summary>
        private void UpdateSpinning()
        {
            bool wanted = (_state == FlyoutTabState.Searching) && _onScreen;
            if (wanted == (_spinning is not null))
            {
                return;
            }

            if (!wanted)
            {
                StopSpinning();
                return;
            }

            _spinning = new CancellationTokenSource();
            Spin.RunAsync(_spinner, _spinning.Token)
                .ContinueWith(
                    run => Log.LogError(run.Exception, "The tab spinner's animation failed"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default
                );
        }

        /// <summary>A 9 px ring with a bright quarter, as the mock's small spinner draws it.</summary>
        private static Control SpinnerView() =>
            new Grid
            {
                Name = SpinnerName,
                Width = 9,
                Height = 9,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(),
                Children =
                {
                    new Ellipse { Stroke = SpinnerTrack, StrokeThickness = 1.5 },
                    new Arc
                    {
                        Stroke = SpinnerHead,
                        StrokeThickness = 1.5,
                        StartAngle = -135,
                        SweepAngle = 90,
                    },
                },
            };
    }
}
