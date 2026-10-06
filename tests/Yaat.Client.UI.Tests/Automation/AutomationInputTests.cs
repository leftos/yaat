using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// The input methods end to end over a real named pipe: <c>click</c> (the semantic action or a synthetic pointer click,
/// never both), <c>click_point</c>, <c>send_keys</c> (text at the caret, routed key chords, the push-to-talk refusal),
/// <c>set_text</c> and <c>focus</c>, with their coded errors.
/// </summary>
public sealed class AutomationInputTests : AutomationHostFixture
{
    [AvaloniaFact]
    public async Task SendKeys_TypingIntoCommandInput_ShowsSuggestions()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Aircraft.Add(new AircraftModel { Callsign = "UAL123" });
        ShowWindow("MainTestWindow", new CommandInputView { DataContext = vm }, vm);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Result(await Send(client, ProtocolMethods.Focus, new { selector = "#MainTestWindow #CommandInput" }));
        JsonElement typed = Result(await Send(client, ProtocolMethods.SendKeys, new { keys = "UA" }));

        Assert.Equal(2, typed.GetProperty("strokes").GetInt32());
        Assert.Equal("UA", vm.CommandText);
        Assert.True(vm.CommandInput.IsSuggestionsVisible);
        Assert.Contains(vm.CommandInput.Suggestions, suggestion => suggestion.Text.Contains("UAL123", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task SendKeys_WindowShortcut_Fires()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        ShowWindow("HotkeyWindow", new TextBox { Name = "Box" }, vm);
        bool dcbBefore = vm.Radar.IsDcbVisible;
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Result(await Send(client, ProtocolMethods.SendKeys, new { selector = "#Box", keys = "^{F8}" }));

        Assert.NotEqual(dcbBefore, vm.Radar.IsDcbVisible);
    }

    [AvaloniaFact]
    public async Task ClickPoint_OnRadar_ReachesTheCanvas()
    {
        var canvas = new RadarCanvas();
        int picked = 0;
        canvas.MeasurePointPicked += _ => picked++;
        ShowWindow("RadarWindow", canvas, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        // Alt+click anchors a measurement: the press records the anchor, and only the release, carrying the pressed
        // left button, reports the pick.
        JsonElement result = Result(
            await Send(
                client,
                ProtocolMethods.ClickPoint,
                new
                {
                    windowSelector = "#RadarWindow",
                    x = 200,
                    y = 150,
                    modifiers = "alt",
                }
            )
        );

        Assert.Equal(nameof(RadarCanvas), result.GetProperty("elementType").GetString());
        Assert.Equal(1, picked);
    }

    [AvaloniaFact]
    public async Task ClickPoint_OutsideWindow_ReturnsOutOfBounds()
    {
        ShowWindow("RadarWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(
            await Send(
                client,
                ProtocolMethods.ClickPoint,
                new
                {
                    windowSelector = "#RadarWindow",
                    x = 450,
                    y = 10,
                }
            ),
            AutomationErrorCodes.OutOfBounds
        );

        Assert.Equal(400, error.GetProperty("details").GetProperty("width").GetDouble());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("suggested").GetString()));
    }

    [AvaloniaFact]
    public async Task Click_ButtonWithClickHandlerAndCommand_FiresEachOnce()
    {
        int executed = 0;
        int pointerPresses = 0;
        int clickEvents = 0;
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            Command = new RelayCommand(() => executed++),
        };
        button.AddHandler(InputElement.PointerPressedEvent, (_, _) => pointerPresses++, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        button.Click += (_, _) => clickEvents++;
        ShowWindow("ButtonWindow", button, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector = "#Run" }));

        Assert.Equal("command", result.GetProperty("action").GetString());
        Assert.Equal(1, executed);
        Assert.Equal(1, clickEvents);
        Assert.Equal(0, pointerPresses);
    }

    [AvaloniaFact]
    public async Task Click_ControlWithNoAction_SendsPointerClick()
    {
        Border pad = Pad();
        List<Point> pressedAt = [];
        List<MouseButton> released = [];
        pad.PointerPressed += (_, e) => pressedAt.Add(e.GetPosition(pad));
        pad.PointerReleased += (_, e) => released.Add(e.InitialPressMouseButton);
        ShowWindow("PadWindow", pad, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector = "#Pad" }));

        Assert.Equal("pointer", result.GetProperty("action").GetString());
        Assert.Equal([new Point(60, 40)], pressedAt);
        Assert.Equal([MouseButton.Left], released);
    }

    [AvaloniaFact]
    public async Task Click_Disabled_ReturnsElementDisabled()
    {
        int executed = 0;
        var button = new Button
        {
            Name = "Run",
            Content = "Run",
            IsEnabled = false,
            Command = new RelayCommand(() => executed++),
        };
        ShowWindow("ButtonWindow", button, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await Send(client, ProtocolMethods.Click, new { selector = "#Run" }), AutomationErrorCodes.ElementDisabled);

        Assert.Equal(nameof(Button), error.GetProperty("details").GetProperty("elementType").GetString());
        Assert.Equal(0, executed);
    }

    [AvaloniaFact]
    public async Task Click_DoubleClick_RaisesDoubleTapped()
    {
        Border pad = Pad();
        int doubleTapped = 0;
        int pressed = 0;
        pad.DoubleTapped += (_, _) => doubleTapped++;
        pad.PointerPressed += (_, _) => pressed++;
        ShowWindow("PadWindow", pad, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.Click, new { selector = "#Pad", clickCount = 2 }));

        Assert.Equal("pointer", result.GetProperty("action").GetString());
        Assert.Equal(2, pressed);
        Assert.Equal(1, doubleTapped);
    }

    [AvaloniaFact]
    public async Task SendKeys_PushToTalkChord_IsRefused()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        UserPreferences prefs = vm.Preferences;
        string savedPtt = prefs.PttKey;
        prefs.SetPttKey("F9");
        try
        {
            var box = new TextBox { Name = "Box" };
            ShowWindow("PttWindow", box, vm);
            using AutomationHost host = StartHost(() => Windows);
            await using AutomationPipeTestClient client = await Connect();

            JsonElement error = Error(
                await Send(client, ProtocolMethods.SendKeys, new { selector = "#Box", keys = "a{F9}" }),
                AutomationErrorCodes.UnsupportedOperation
            );

            Assert.Contains("automation mode", error.GetProperty("suggested").GetString(), StringComparison.Ordinal);
            Assert.True(string.IsNullOrEmpty(box.Text), "a refused string sends no stroke at all");
            Assert.False(box.IsFocused, "a refused string moves no focus");
        }
        finally
        {
            prefs.SetPttKey(savedPtt);
        }
    }

    [AvaloniaFact]
    public async Task SendKeys_Malformed_ReturnsInvalidParam()
    {
        var box = new TextBox { Name = "Box" };
        ShowWindow("KeysWindow", box, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(
            await Send(client, ProtocolMethods.SendKeys, new { selector = "#Box", keys = "ab{ENTER" }),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal(2, error.GetProperty("details").GetProperty("position").GetInt32());
        Assert.Contains("position 2", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(box.Text), "a malformed string sends no stroke at all");
    }

    [AvaloniaFact]
    public async Task SendKeys_ShiftPrefixOnLetter_TypesTheCapital()
    {
        var box = new TextBox { Name = "Box" };
        ShowWindow("KeysWindow", box, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Result(await Send(client, ProtocolMethods.SendKeys, new { selector = "#Box", keys = "+ab" }));

        Assert.Equal("Ab", box.Text);
    }

    [AvaloniaFact]
    public async Task SetText_ReplacesTextAndMovesCaretToEnd()
    {
        var box = new TextBox { Name = "Box", Text = "old" };
        ShowWindow("TextWindow", new StackPanel { Children = { box } }, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await Send(client, ProtocolMethods.SetText, new { selector = "#TextWindow StackPanel", text = "new text" }));

        Assert.Equal("new text", result.GetProperty("text").GetString());
        Assert.Equal("new text", box.Text);
        Assert.Equal(8, box.CaretIndex);
    }

    [AvaloniaFact]
    public async Task Focus_FocusableElement_TakesFocus()
    {
        var box = new TextBox { Name = "Box" };
        ShowWindow(
            "FocusWindow",
            new StackPanel
            {
                Children =
                {
                    new Button { Content = "Other" },
                    box,
                },
            },
            null
        );
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        Result(await Send(client, ProtocolMethods.Focus, new { selector = "#Box" }));

        Assert.True(box.IsFocused);
    }

    [AvaloniaFact]
    public async Task Focus_NotFocusable_ReturnsNotFocusable()
    {
        ShowWindow("FocusWindow", new TextBlock { Name = "Label", Text = "Ready" }, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await Send(client, ProtocolMethods.Focus, new { selector = "#Label" }), AutomationErrorCodes.NotFocusable);

        Assert.Equal(nameof(TextBlock), error.GetProperty("details").GetProperty("elementType").GetString());
    }
}
