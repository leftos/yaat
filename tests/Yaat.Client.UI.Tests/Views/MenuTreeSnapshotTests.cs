using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Pins the menu golden format on a hand-built menu, independently of any view: a change to the walker shows up here
// as one readable diff instead of as a rewrite of every golden file.
public class MenuTreeSnapshotTests
{
    [AvaloniaFact]
    public void Render_PrintsEveryNodeKindInTheGoldenFormat()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "AAL123 - B738", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Show route", IsChecked = true });
        menu.Items.Add(
            new MenuItem
            {
                Header = "Locked",
                IsEnabled = false,
                IsChecked = true,
            }
        );

        var heading = new MenuItem { Header = "Heading" };
        heading.Items.Add(new MenuItem { Header = "Fly heading", Tag = new MenuPickerDescriptor(MenuPickerDescriptor.List, ["5", "10", "15"]) });
        heading.Items.Add(new MenuItem { Header = "Approach", Tag = new MenuPickerDescriptor(MenuPickerDescriptor.FilteredList, ["I30", "R30"]) });
        heading.Items.Add(new MenuItem { Header = "Heading...", Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Input, []) });
        heading.Items.Add(new MenuItem { Header = "Empty list", Tag = new MenuPickerDescriptor(MenuPickerDescriptor.List, []) });
        var nested = new MenuItem { Header = "Nested" };
        nested.Items.Add(new MenuItem { Header = "Leaf" });
        heading.Items.Add(nested);
        menu.Items.Add(heading);

        menu.Items.Add(new TextBox());
        menu.Items.Add(new MenuItem { Header = 42 });

        string expected = string.Join(
            "",
            "AAL123 - B738 [disabled]\n",
            "---\n",
            "Show route [checked]\n",
            "Locked [disabled] [checked]\n",
            "Heading\n",
            "  Fly heading picker:list [5, 10, 15]\n",
            "  Approach picker:filteredList [I30, R30]\n",
            "  Heading... picker:input\n",
            "  Empty list picker:list []\n",
            "  Nested\n",
            "    Leaf\n",
            "<TextBox>\n",
            "42\n"
        );
        Assert.Equal(expected, MenuTreeSnapshot.Render(menu));
    }
}
