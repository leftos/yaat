using System.Text;
using Avalonia.Controls;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Renders a context menu as plain text, one line per node, depth-first in <c>Items</c> order with two spaces of
/// indent per level: a <see cref="MenuItem"/> as its header text followed by <c>[disabled]</c>, <c>[checked]</c> and
/// its <see cref="MenuPickerDescriptor"/> (<c>picker:list [a, b]</c>, <c>picker:filteredList [a, b]</c>,
/// <c>picker:input</c>, <c>picker:richList [FL410, …, MVA 5,000 here (sector SCK_M), …]</c>, or a grouped picker on one
/// line with its groups and no children,
/// <c>picker:grouped [Runway 30 · assigned: I30, L30 | Other runways: 10L, 12]</c>); the quick-command strip
/// (<see cref="QuickCommandStrip"/>) as one node naming its buttons' catalog ids in order, <c>strip: [id, id, …]</c>; a
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
            case MenuItem menuItem:
                text.Append(Describe(menuItem)).Append('\n');
                if (menuItem.Tag is not MenuPickerDescriptor { Kind: MenuPickerDescriptor.Grouped })
                {
                    AppendItems(text, menuItem.Items, depth + 1);
                }

                break;
            default:
                text.Append('<').Append(item?.GetType().Name ?? "null").Append(">\n");
                break;
        }
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

        if (menuItem.Tag is MenuPickerDescriptor picker)
        {
            parts.Add(DescribePicker(picker));
        }

        return string.Join(' ', parts);
    }

    private static string DescribePicker(MenuPickerDescriptor picker) =>
        picker.Kind switch
        {
            MenuPickerDescriptor.Input => "picker:input",
            MenuPickerDescriptor.Grouped => $"picker:grouped [{string.Join(" | ", picker.Items)}]",
            _ => $"picker:{picker.Kind} [{string.Join(", ", picker.Items)}]",
        };
}
