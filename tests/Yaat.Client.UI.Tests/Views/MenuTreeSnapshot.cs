using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Renders a context menu as plain text, one line per node, depth-first in <c>Items</c> order with two spaces of
/// indent per level: a <see cref="MenuItem"/> as its header text followed by <c>[disabled]</c>, <c>[checked]</c>,
/// <c>[bold]</c> (a command row whose header template draws text bold: <see cref="MenuCommandRow.IsHighlighted"/>) and
/// its <see cref="MenuPickerDescriptor"/> (<c>picker:list [a, b]</c>, <c>picker:filteredList [a, b]</c>,
/// or <c>picker:input</c>); the quick-command strip
/// (<see cref="QuickCommandStrip"/>) as one node naming its buttons' catalog ids in order, <c>strip: [id, id, …]</c>; the
/// Taxi to runway panel (<see cref="TaxiToRunwayPanel"/>) as its tabs, <c>tabs: [30 assigned [selected], 12 [none], …]</c>,
/// then the selected tab's lines at the same depth; a
/// <see cref="Separator"/> as <c>---</c>; anything else as its type name in angle brackets.
/// Commands, tooltips, gestures and icons are left out. Every line ends with <c>\n</c>.
/// </summary>
internal static class MenuTreeSnapshot
{
    private const string Indent = "  ";

    /// <summary>The whole menu as text, each line terminated by a line feed.</summary>
    public static string Render(ContextMenu menu)
    {
        var text = new StringBuilder();
        AppendItems(text, menu.Items, depth: 0);
        return text.ToString();
    }

    /// <summary>One item and everything under it as text, in the same format.</summary>
    public static string Render(MenuItem item)
    {
        var text = new StringBuilder();
        AppendNode(text, item, depth: 0);
        return text.ToString();
    }

    private static void AppendItems(StringBuilder text, ItemCollection items, int depth)
    {
        foreach (object? item in items)
        {
            AppendNode(text, item, depth);
        }
    }

    private static void AppendNode(StringBuilder text, object? item, int depth)
    {
        for (int i = 0; i < depth; i++)
        {
            text.Append(Indent);
        }

        switch (item)
        {
            case Separator:
                text.Append("---\n");
                break;
            case MenuItem strip when QuickCommandStrip.IsStrip(strip):
                text.Append("strip: [").AppendJoin(", ", QuickCommandStrip.Buttons(strip).Select(button => button.Tag as string)).Append("]\n");
                break;
            case MenuItem { Header: TaxiToRunwayPanel panel }:
                AppendPanel(text, panel, depth);
                break;
            case MenuItem menuItem:
                text.Append(Describe(menuItem)).Append('\n');
                AppendItems(text, menuItem.Items, depth + 1);
                break;
            default:
                text.Append('<').Append(item?.GetType().Name ?? "null").Append(">\n");
                break;
        }
    }

    /// <summary>
    /// The Taxi to runway panel, its first line already indented: its tabs on one line when it has any, then the selected
    /// tab's lines (<see cref="TaxiToRunwayPanel.Lines"/>) at the same depth.
    /// </summary>
    private static void AppendPanel(StringBuilder text, TaxiToRunwayPanel panel, int depth)
    {
        List<string> lines = [.. panel.Lines];
        if (panel.Strip.Tabs.Count > 0)
        {
            lines.Insert(
                0,
                $"tabs: [{string.Join(", ", panel.Strip.Tabs.Select((tab, index) => TabText(tab, index == panel.Strip.SelectedIndex)))}]"
            );
        }

        text.Append(lines[0]).Append('\n');
        foreach (string line in lines.Skip(1))
        {
            for (int i = 0; i < depth; i++)
            {
                text.Append(Indent);
            }

            text.Append(line).Append('\n');
        }
    }

    /// <summary>A tab's label, then <c>[selected]</c> and <c>[searching]</c> or <c>[none]</c> where they apply.</summary>
    private static string TabText(FlyoutTab tab, bool selected)
    {
        string state = tab.State switch
        {
            FlyoutTabState.Searching => " [searching]",
            FlyoutTabState.Empty => " [none]",
            _ => "",
        };
        return $"{tab.Label}{(selected ? " [selected]" : "")}{state}";
    }

    /// <summary>The header text, then the state flags and picker descriptor, space-separated.</summary>
    private static string Describe(MenuItem menuItem)
    {
        var parts = new List<string> { menuItem.Header?.ToString() ?? "" };
        if (!menuItem.IsEnabled)
        {
            parts.Add("[disabled]");
        }

        if (menuItem.IsChecked)
        {
            parts.Add("[checked]");
        }

        if (IsDrawnBold(menuItem))
        {
            parts.Add("[bold]");
        }

        if (menuItem.Tag is MenuPickerDescriptor picker)
        {
            parts.Add(DescribePicker(picker));
        }

        return string.Join(' ', parts);
    }

    /// <summary>Whether <paramref name="menuItem"/>'s command row, as its header template builds it, draws any text bold.</summary>
    private static bool IsDrawnBold(MenuItem menuItem) =>
        (menuItem.Header is MenuCommandRow)
        && (menuItem.HeaderTemplate?.Build(menuItem.Header) is { } view)
        && view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.FontWeight == FontWeight.Bold);

    private static string DescribePicker(MenuPickerDescriptor picker) =>
        picker.Kind switch
        {
            MenuPickerDescriptor.Input => "picker:input",
            _ => $"picker:{picker.Kind} [{string.Join(", ", picker.Items)}]",
        };
}
