using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>
/// The Scope window's views dock: the open views' cards (<see cref="ScopeViewModel.DockViews"/>) stacked top to bottom
/// in dock order. Each view's body is a data template picked by its view model's type.
/// </summary>
public partial class ScopeDock : UserControl
{
    public ScopeDock()
    {
        InitializeComponent();
    }
}

/// <summary>
/// One view's card: a header strip with the view's title and a close button that runs <c>.view {name} off</c>, over the
/// view's body. The card is bound only to its <see cref="ScopeDockViewModel"/> (its data context), so any host can show it.
/// </summary>
public sealed class ScopeViewCard : UserControl
{
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#22313b"));

    public ScopeViewCard()
    {
        var title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#c9d4dc")),
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.TextProperty] = new Binding(nameof(ScopeDockViewModel.Title)),
        };
        var close = new Button
        {
            Content = "–",
            Padding = new Avalonia.Thickness(6, 0),
            MinWidth = 0,
            CornerRadius = new Avalonia.CornerRadius(0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#c9d4dc")),
        };
        close.Click += (_, _) => (DataContext as ScopeDockViewModel)?.CloseCommand.Execute(null);
        AutomationProperties.SetName(close, "Close");
        Grid.SetColumn(close, 1);
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#10181e")),
            Padding = new Avalonia.Thickness(8, 2, 2, 2),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, close } },
        };
        var body = new ContentControl { [!ContentControl.ContentProperty] = new Binding() };
        Content = new Border
        {
            BorderBrush = CardBorder,
            BorderThickness = new Avalonia.Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#0a1015")),
            Child = new StackPanel { Children = { header, body } },
        };
    }

    /// <summary>The name of the view the card shows (<c>ssa</c>, <c>ca</c>), or null before it is bound.</summary>
    public string? ViewName => (DataContext as ScopeDockViewModel)?.Name;
}
