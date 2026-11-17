using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>
/// One view's card, in the dock or floating on the scope: a header with the view's title, a dock button (shown while the
/// view floats) that runs <c>.view {name} dock</c> and a close button that runs <c>.view {name} off</c>, over the view's
/// body, a data template the card carries. The card is bound only to its <see cref="ScopeDockViewModel"/> (its data
/// context), so any host can show it. A left press on the title area asks to move the card (<see cref="MoveRequestedEvent"/>),
/// except while DEL TEAROFF's mode is on.
/// </summary>
public sealed partial class ScopeViewCard : UserControl
{
    /// <summary>Raised, bubbling, when a left press on the title area asks to move the card onto the scope.</summary>
    public static readonly RoutedEvent<ScopeViewMoveRequestedEventArgs> MoveRequestedEvent = RoutedEvent.Register<
        ScopeViewCard,
        ScopeViewMoveRequestedEventArgs
    >("MoveRequested", RoutingStrategies.Bubble);

    /// <summary>Defines the <see cref="IsFloating"/> property.</summary>
    public static readonly StyledProperty<bool> IsFloatingProperty = AvaloniaProperty.Register<ScopeViewCard, bool>(nameof(IsFloating));

    /// <summary>The card's border.</summary>
    public static readonly IBrush CardBorderBrush = new SolidColorBrush(Color.Parse("#22313b"));

    /// <summary>The header strip's background.</summary>
    public static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#10181e"));

    /// <summary>The body's background.</summary>
    public static readonly IBrush BodyBrush = new SolidColorBrush(Color.Parse("#0a1015"));

    /// <summary>The title's and the header buttons' text.</summary>
    public static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#c9d4dc"));

    /// <summary>A view's text.</summary>
    public static readonly IBrush ViewTextBrush = Brushes.LimeGreen;

    /// <summary>A view's alert text, such as the SSA's status outside a room.</summary>
    public static readonly IBrush AlertBrush = new SolidColorBrush(Color.Parse("#ff4040"));

    /// <summary>A card for the view in its data context.</summary>
    public ScopeViewCard()
    {
        InitializeComponent();
        PickArea.PointerPressed += OnPickAreaPressed;
    }

    /// <summary>The name of the view the card shows (<c>ssa</c>, <c>ca</c>, <c>rpoq</c>, <c>crr</c>), or null before it is bound.</summary>
    public string? ViewName => (DataContext as ScopeDockViewModel)?.Name;

    /// <summary>
    /// Whether the card floats on the scope, which its host sets: a floating card shows its dock button. A floating view's
    /// place is never null, and a docked view's always is.
    /// </summary>
    public bool IsFloating
    {
        get => GetValue(IsFloatingProperty);
        set => SetValue(IsFloatingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFloatingProperty)
        {
            DockButton.IsVisible = IsFloating;
        }
    }

    private void OnPickAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (DataContext is not ScopeDockViewModel view) || view.Scope.IsDeletingTearoffs)
        {
            return;
        }

        e.Handled = true;
        RaiseEvent(new ScopeViewMoveRequestedEventArgs(MoveRequestedEvent, this, e));
    }
}

/// <summary>A left press on a view card's title area: the card and the press.</summary>
public sealed class ScopeViewMoveRequestedEventArgs(RoutedEvent routedEvent, ScopeViewCard card, PointerPressedEventArgs press)
    : RoutedEventArgs(routedEvent, card)
{
    /// <summary>The card whose title area was pressed.</summary>
    public ScopeViewCard Card { get; } = card;

    /// <summary>The press, for its position and its pointer.</summary>
    public PointerPressedEventArgs Press { get; } = press;
}
