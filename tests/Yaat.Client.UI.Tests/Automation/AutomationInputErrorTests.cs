using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>The input methods' coded errors over a real named pipe: bad params, bad targets, bounds and refused elements.</summary>
public sealed class AutomationInputErrorTests : AutomationHostFixture
{
    private async Task<JsonElement> SendOne(string method, object parameters)
    {
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, method, parameters);
    }

    [AvaloniaTheory]
    [InlineData(ProtocolMethods.Click, "5", "params")]
    [InlineData(ProtocolMethods.Click, "{\"nodeId\":1,\"selector\":\"#Pad\"}", "selector")]
    [InlineData(ProtocolMethods.Click, "{}", "selector")]
    [InlineData(ProtocolMethods.Click, "{\"selector\":\"#Pad\",\"button\":\"left2\"}", "button")]
    [InlineData(ProtocolMethods.Click, "{\"selector\":\"#Pad\",\"modifiers\":\"ctrl+hyper\"}", "modifiers")]
    [InlineData(ProtocolMethods.Click, "{\"selector\":\"#Pad\",\"clickCount\":3}", "clickCount")]
    [InlineData(ProtocolMethods.ClickPoint, "{\"windowSelector\":\"#Pad\",\"x\":1,\"y\":1}", "windowSelector")]
    [InlineData(ProtocolMethods.ClickPoint, "{\"windowSelector\":\"#ErrorWindow\",\"y\":1}", "x")]
    [InlineData(ProtocolMethods.ClickPoint, "{\"windowSelector\":\"#ErrorWindow\",\"x\":1}", "y")]
    [InlineData(ProtocolMethods.SetText, "{\"selector\":\"#Pad\",\"text\":\"x\"}", "selector")]
    [InlineData(ProtocolMethods.SendKeys, "{\"keys\":\"\"}", "keys")]
    [InlineData(ProtocolMethods.SendKeys, "{\"keys\":\"a\"}", "selector")]
    public async Task BadParams_ReturnInvalidParamNamingTheParam(string method, string paramsJson, string param)
    {
        // Nothing in the window holds the focus, so a send_keys without a target has nowhere to go.
        ShowWindow("ErrorWindow", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await SendRawParams(client, method, paramsJson), AutomationErrorCodes.InvalidParam);

        Assert.Equal(param, error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaTheory]
    [InlineData(-1, 10)]
    [InlineData(10, -0.5)]
    [InlineData(400, 10)]
    [InlineData(10, 300)]
    public async Task ClickPoint_OffTheClientArea_ReturnsOutOfBounds(double x, double y)
    {
        ShowWindow("ErrorWindow", Pad(), null);

        JsonElement error = Error(
            await SendOne(
                ProtocolMethods.ClickPoint,
                new
                {
                    windowSelector = "#ErrorWindow",
                    x,
                    y,
                }
            ),
            AutomationErrorCodes.OutOfBounds
        );

        Assert.Equal(x, error.GetProperty("details").GetProperty("x").GetDouble());
    }

    [AvaloniaFact]
    public async Task ClickPoint_DisabledWindow_ReturnsElementDisabled()
    {
        int presses = 0;
        Border pad = Pad();
        pad.PointerPressed += (_, _) => presses++;
        Window window = ShowWindow("ErrorWindow", pad, null);
        window.IsEnabled = false;

        Error(
            await SendOne(
                ProtocolMethods.ClickPoint,
                new
                {
                    windowSelector = "#ErrorWindow",
                    x = 10,
                    y = 10,
                }
            ),
            AutomationErrorCodes.ElementDisabled
        );

        Assert.Equal(0, presses);
    }

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SetText_ReadOnlyOrDisabledBox_ReturnsElementDisabled(bool readOnly, bool enabled)
    {
        var box = new TextBox
        {
            Name = "Box",
            Text = "old",
            IsReadOnly = readOnly,
            IsEnabled = enabled,
        };
        ShowWindow("ErrorWindow", box, null);

        Error(await SendOne(ProtocolMethods.SetText, new { selector = "#Box", text = "new" }), AutomationErrorCodes.ElementDisabled);

        Assert.Equal("old", box.Text);
    }

    [AvaloniaFact]
    public async Task SendKeys_DisabledTarget_ReturnsElementDisabled()
    {
        var box = new TextBox { Name = "Box", IsEnabled = false };
        ShowWindow("ErrorWindow", box, null);

        Error(await SendOne(ProtocolMethods.SendKeys, new { selector = "#Box", keys = "ab" }), AutomationErrorCodes.ElementDisabled);

        Assert.True(string.IsNullOrEmpty(box.Text));
    }
}
