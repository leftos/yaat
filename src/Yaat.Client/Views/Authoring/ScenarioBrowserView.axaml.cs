using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Scenario Editor's browser: the Local and Server tabs over a <see cref="ScenarioBrowserViewModel"/>, each row with
/// its name, id and badge icons, an empty state per list, and the browser's commands on right-click. A double-click on a
/// row, or Enter on the selected row, opens it in the editor; a single click only selects. A UserControl so
/// the editor window hosts it beside its content area.
/// </summary>
public partial class ScenarioBrowserView : UserControl
{
    /// <summary>The tab index of a <see cref="ScenarioBrowserTab"/>, both ways.</summary>
    public static readonly IValueConverter TabIndexOf = new TabIndexConverter();

    /// <summary>A row's badges as icons with their tooltips.</summary>
    public static readonly FuncValueConverter<ScenarioBrowserRowViewModel?, IReadOnlyList<ScenarioBadgeLook>> BadgeLooks = new(row =>
        row is null ? [] : [.. row.Badges.Select(b => ScenarioBadgeLook.For(b, row.Problem))]
    );

    /// <summary>A row's name colour: dimmed when its scenario is gone from the server.</summary>
    public static readonly FuncValueConverter<ScenarioBrowserRowViewModel?, IBrush> NameBrush = new(row =>
        ((row is not null) && row.Badges.Contains(ScenarioBadge.GoneFromServer)) ? ScenarioEditorIcons.Dim : ScenarioEditorIcons.Text
    );

    /// <summary>A row's background: tinted red when its file is corrupt.</summary>
    public static readonly FuncValueConverter<bool, IBrush> RowBackground = new(corrupt =>
        corrupt ? ScenarioEditorIcons.CorruptTint : ScenarioEditorIcons.None
    );

    /// <summary>Whether a list's empty state shows: there is an ARTCC and the list (its count second) is empty.</summary>
    public static readonly FuncMultiValueConverter<object?, bool> EmptyListVisible = new(values =>
    {
        object?[] v = [.. values];
        return (v.Length == 2) && (v[0] is true) && (v[1] is 0);
    });

    public ScenarioBrowserView()
    {
        InitializeComponent();
        foreach (ListBox list in new[] { LocalList, ServerList })
        {
            list.AddHandler(DoubleTappedEvent, OnRowDoubleTapped, handledEventsToo: true);
            list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    // A double-click on a row opens it; one on the list's empty space opens nothing.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender is ListBox list) && (e.Source is Visual source) && (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item))
        {
            OpenRow(list, item.DataContext as ScenarioBrowserRowViewModel);
        }
    }

    // Enter on the list opens its selected row.
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Enter) && (sender is ListBox list))
        {
            OpenRow(list, list.SelectedItem as ScenarioBrowserRowViewModel);
        }
    }

    // Selects the row and runs the browser's Open when it can run; a single click stays selection only.
    private void OpenRow(ListBox list, ScenarioBrowserRowViewModel? row)
    {
        if ((row is null) || (DataContext is not ScenarioBrowserViewModel browser))
        {
            return;
        }

        list.SelectedItem = row;
        if (browser.OpenCommand.CanExecute(null))
        {
            browser.OpenCommand.Execute(null);
        }
    }

    // SelectedTab <-> TabControl.SelectedIndex.
    private sealed class TabIndexConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is ScenarioBrowserTab tab ? (int)tab : 0;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ((value is int index) && Enum.IsDefined((ScenarioBrowserTab)index)) ? (ScenarioBrowserTab)index : BindingOperations.DoNothing;
    }
}
