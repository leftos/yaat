using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>click</c>'s paths over a real named pipe: Avalonia's own click through the automation peers (toggles, flyouts, the
/// <c>Click</c> event), menu items, item selection, the TextBlock stand-in, the focus a click moves, the pointer path for a
/// non-plain click, and the refusals.
/// </summary>
public sealed class AutomationClickTests : AutomationHostFixture
{
    private async Task<JsonElement> Click(object parameters)
    {
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.Click, parameters);
    }

    [AvaloniaFact]
    public async Task Click_ThreeStateCheckBox_CyclesInAvaloniaOrder()
    {
        var check = new CheckBox
        {
            Name = "Check",
            Content = "Check",
            IsThreeState = true,
            IsChecked = false,
        };
        ShowWindow("CheckWindow", check, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        List<bool?> states = [];

        for (int click = 0; click < 3; click++)
        {
            JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector = "#Check" }));
            Assert.Equal("toggle", result.GetProperty("action").GetString());
            states.Add(check.IsChecked);
        }

        Assert.Equal([true, null, false], states);
    }

    [AvaloniaFact]
    public async Task Click_RadioButton_ChecksItAndStaysChecked()
    {
        var radio = new RadioButton { Name = "Radio", Content = "Radio" };
        ShowWindow("RadioWindow", radio, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Result(await Send(client, ProtocolMethods.Click, new { selector = "#Radio" }));
        Result(await Send(client, ProtocolMethods.Click, new { selector = "#Radio" }));

        Assert.True(radio.IsChecked);
    }

    [AvaloniaFact]
    public async Task Click_CheckBoxMenuItem_TogglesAndKeepsItsOneWayBinding()
    {
        var source = new FlagSource();
        var item = new MenuItem
        {
            Name = "Toggle",
            Header = "Toggle",
            ToggleType = MenuItemToggleType.CheckBox,
        };
        item.Bind(MenuItem.IsCheckedProperty, new ReflectionBinding(nameof(FlagSource.Flag)) { Mode = BindingMode.OneWay, Source = source });
        ShowWindow("MenuWindow", new Menu { Items = { item } }, null);

        JsonElement result = Result(await Click(new { selector = "#Toggle" }));

        Assert.Equal("menu_item", result.GetProperty("action").GetString());
        Assert.True(item.IsChecked);
        // The source still drives the item after the click.
        source.Flag = true;
        source.Flag = false;
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public async Task Click_ItemContainers_SelectThem()
    {
        var list = new ListBox { ItemsSource = new[] { "A", "B" } };
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "One" },
                new TabItem { Header = "Two" },
            },
        };
        var second = new TreeViewItem { Header = "Second" };
        var tree = new TreeView
        {
            Items =
            {
                new TreeViewItem { Header = "First" },
                second,
            },
        };
        ShowWindow("SelectWindow", new StackPanel { Children = { list, tabs, tree } }, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        foreach (string selector in new[] { "ListBoxItem:nth(1)", "TabItem:nth(1)", "TreeViewItem:nth(1)" })
        {
            JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector }));
            Assert.Equal("select", result.GetProperty("action").GetString());
        }

        Assert.Equal(1, list.SelectedIndex);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.Same(second, tree.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Click_ComboBoxItem_SelectsItAndClosesTheDropDown()
    {
        var combo = new ComboBox { ItemsSource = new[] { "A", "B" } };
        ShowWindow("ComboWindow", combo, null);
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        JsonElement result = Result(await Click(new { selector = "ComboBoxItem:nth(1)" }));

        Assert.Equal("select", result.GetProperty("action").GetString());
        Assert.Equal(1, combo.SelectedIndex);
        Assert.False(combo.IsDropDownOpen);
    }

    [AvaloniaFact]
    public async Task Click_ButtonWithFlyout_OpensIt()
    {
        var flyout = new Flyout { Content = new TextBlock { Text = "Inside" } };
        ShowWindow(
            "FlyoutWindow",
            new Button
            {
                Name = "Open",
                Content = "Open",
                Flyout = flyout,
            },
            null
        );

        JsonElement result = Result(await Click(new { selector = "#Open" }));

        Assert.Equal("flyout", result.GetProperty("action").GetString());
        Assert.True(flyout.IsOpen);
    }

    [AvaloniaFact]
    public async Task Click_TextBlockInsideButton_ClicksTheButton()
    {
        int clicks = 0;
        var button = new Button
        {
            Content = new TextBlock { Name = "Label", Text = "Go" },
        };
        button.Click += (_, _) => clicks++;
        ShowWindow("LabelWindow", button, null);

        JsonElement result = Result(await Click(new { selector = "#Label" }));

        Assert.Equal("click_event", result.GetProperty("action").GetString());
        Assert.Equal(1, clicks);
    }

    [AvaloniaFact]
    public async Task Click_PlainLeftClick_MovesFocusBeforeTheAction()
    {
        List<string> order = [];
        var box = new TextBox { Name = "Box" };
        box.LostFocus += (_, _) => order.Add("lost focus");
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            Command = new RelayCommand(() => order.Add("command")),
        };
        ShowWindow("FocusWindow", new StackPanel { Children = { box, button } }, null);
        box.Focus();

        Result(await Click(new { selector = "#Run" }));

        Assert.Equal(["lost focus", "command"], order);
        Assert.True(button.IsFocused);
    }

    [AvaloniaFact]
    public async Task Click_RightClickOnButton_TakesThePointerPath()
    {
        int executed = 0;
        int presses = 0;
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            Command = new RelayCommand(() => executed++),
        };
        button.AddHandler(InputElement.PointerPressedEvent, (_, _) => presses++, RoutingStrategies.Bubble, true);
        ShowWindow("ButtonWindow", button, null);

        JsonElement result = Result(await Click(new { selector = "#Run", button = "right" }));

        Assert.Equal("pointer", result.GetProperty("action").GetString());
        Assert.Equal(1, presses);
        Assert.Equal(0, executed);
    }

    [AvaloniaFact]
    public async Task Click_HiddenElement_ReturnsElementDisabled()
    {
        int clicks = 0;
        var hidden = new Button
        {
            Name = "Hidden",
            Content = "Hidden",
            IsVisible = false,
        };
        hidden.Click += (_, _) => clicks++;
        ShowWindow("HiddenWindow", new StackPanel { Children = { hidden } }, null);

        JsonElement error = Error(await Click(new { selector = "#Hidden" }), AutomationErrorCodes.ElementDisabled);

        Assert.Contains("not visible", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, clicks);
    }

    [AvaloniaFact]
    public async Task Click_CommandThatCannotExecute_ReturnsElementDisabled()
    {
        int executed = 0;
        ShowWindow("ButtonWindow", new Button { Name = "Run", Command = new RelayCommand(() => executed++, () => false) }, null);

        Error(await Click(new { selector = "#Run" }), AutomationErrorCodes.ElementDisabled);

        Assert.Equal(0, executed);
    }

    [AvaloniaFact]
    public async Task Click_UnknownNodeId_ReturnsStaleNode()
    {
        ShowWindow("PadWindow", Pad(), null);

        JsonElement error = Error(await Click(new { nodeId = 987654 }), AutomationErrorCodes.StaleNode);

        Assert.Equal(987654, error.GetProperty("details").GetProperty("nodeId").GetInt32());
    }

    /// <summary>A one-way binding source.</summary>
    private sealed class FlagSource : ObservableObject
    {
        private bool _flag;

        public bool Flag
        {
            get => _flag;
            set => SetProperty(ref _flag, value);
        }
    }
}
