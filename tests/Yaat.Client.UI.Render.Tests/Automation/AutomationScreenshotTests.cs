using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Render.Tests.Automation;

/// <summary>
/// <c>screenshot</c> over a real named pipe: a window (overlay popups included) and one element rendered to PNG at the
/// render scale, and its coded errors.
/// </summary>
public sealed class AutomationScreenshotTests : AutomationHostFixture
{
    private static readonly SKColor Red = new(255, 0, 0, 255);
    private static readonly SKColor Blue = new(0, 0, 255, 255);

    /// <summary>A red border at (40, 30) from its parent's top-left corner, so a capture offset by its position shows white.</summary>
    private static Border Swatch(double width, double height) =>
        new()
        {
            Name = "Swatch",
            Width = width,
            Height = height,
            Margin = new Thickness(40, 30, 0, 0),
            Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

    private static Panel OnWhite(params Control[] children)
    {
        var grid = new Grid { Background = Brushes.White };
        grid.Children.AddRange(children);
        return grid;
    }

    private static SKBitmap DecodePng(JsonElement result)
    {
        byte[] png = Convert.FromBase64String(result.GetProperty("pngBase64").GetString()!);
        var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        return bitmap;
    }

    private async Task<JsonElement> Screenshot(object parameters)
    {
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.Screenshot, parameters);
    }

    [AvaloniaFact]
    public async Task Screenshot_Window_IsAPngOfTheClientAreaAtTheRenderScale()
    {
        Window window = ShowWindow("ShotWindow", OnWhite(Swatch(120, 80)), null);

        JsonElement result = Result(await Screenshot(new { windowSelector = "#ShotWindow" }));

        double scale = result.GetProperty("scale").GetDouble();
        Assert.Equal(window.RenderScaling, scale);
        using SKBitmap bitmap = DecodePng(result);
        Assert.Equal((int)Math.Ceiling(window.ClientSize.Width * scale), bitmap.Width);
        Assert.Equal((int)Math.Ceiling(window.ClientSize.Height * scale), bitmap.Height);
        Assert.Equal(bitmap.Width, result.GetProperty("width").GetInt32());
        Assert.Equal(bitmap.Height, result.GetProperty("height").GetInt32());
        Assert.True(bitmap.Pixels.Distinct().Count() >= 2, "The window screenshot has a single colour.");
    }

    [AvaloniaFact]
    public async Task Screenshot_Element_HasItsSizeAndItsColourFromCornerToCorner()
    {
        Window window = ShowWindow("ShotWindow", OnWhite(Swatch(120, 80)), null);
        double scale = window.RenderScaling;

        JsonElement result = Result(await Screenshot(new { selector = "#Swatch" }));

        using SKBitmap bitmap = DecodePng(result);
        Assert.Equal((int)Math.Ceiling(120 * scale), bitmap.Width);
        Assert.Equal((int)Math.Ceiling(80 * scale), bitmap.Height);
        Assert.Equal(Red, bitmap.GetPixel(0, 0));
        Assert.Equal(Red, bitmap.GetPixel(bitmap.Width - 1, 0));
        Assert.Equal(Red, bitmap.GetPixel(0, bitmap.Height - 1));
        Assert.Equal(Red, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1));
        Assert.Equal(Red, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
    }

    [AvaloniaFact]
    public async Task Screenshot_WindowWithAnOpenOverlayPopup_ContainsThePopup()
    {
        Border anchor = Swatch(120, 80);
        var popup = new Popup
        {
            ShouldUseOverlayLayer = true,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Child = new Border
            {
                Width = 60,
                Height = 40,
                Background = Brushes.Blue,
            },
        };
        ShowWindow("ShotWindow", OnWhite(anchor, popup), null);
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Assert.True(popup.IsOpen);

        JsonElement result = Result(await Screenshot(new { windowSelector = "#ShotWindow" }));

        using SKBitmap bitmap = DecodePng(result);
        Assert.Contains(Blue, bitmap.Pixels);
    }

    [AvaloniaFact]
    public async Task Screenshot_ZeroSizeElement_ReturnsOutOfBounds()
    {
        ShowWindow("ShotWindow", OnWhite(Swatch(0, 0)), null);

        JsonElement error = Error(await Screenshot(new { selector = "#Swatch" }), AutomationErrorCodes.OutOfBounds);

        Assert.Equal(0, error.GetProperty("details").GetProperty("width").GetDouble());
    }

    [AvaloniaFact]
    public async Task Screenshot_StaleNodeId_ReturnsStaleNode()
    {
        Border swatch = Swatch(120, 80);
        Panel canvas = OnWhite(swatch);
        ShowWindow("ShotWindow", canvas, null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();
        JsonElement tree = Result(await Send(client, ProtocolMethods.GetTree, new { selector = "#Swatch", depth = 0 }));
        int swatchId = Assert.Single(tree.EnumerateArray()).GetProperty("nodeId").GetInt32();
        canvas.Children.Remove(swatch);

        JsonElement error = Error(await Send(client, ProtocolMethods.Screenshot, new { nodeId = swatchId }), AutomationErrorCodes.StaleNode);

        Assert.Equal(swatchId, error.GetProperty("details").GetProperty("nodeId").GetInt32());
    }

    [AvaloniaTheory]
    [InlineData("{\"windowSelector\":\"#ShotWindow\",\"selector\":\"#Swatch\"}", "windowSelector", "'windowSelector'", "'selector'")]
    [InlineData("{\"windowNodeId\":1,\"nodeId\":2}", "windowNodeId", "'windowNodeId'", "'nodeId'")]
    [InlineData("{}", "selector", "'windowSelector'", "'nodeId'")]
    public async Task Screenshot_NotExactlyOneTarget_ReturnsInvalidParamNamingTheParams(
        string paramsJson,
        string param,
        string firstNamed,
        string secondNamed
    )
    {
        ShowWindow("ShotWindow", OnWhite(Swatch(120, 80)), null);
        using AutomationHost host = StartHost(() => Windows);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement error = Error(await SendRawParams(client, ProtocolMethods.Screenshot, paramsJson), AutomationErrorCodes.InvalidParam);

        Assert.Equal(param, error.GetProperty("details").GetProperty("param").GetString());
        string message = error.GetProperty("message").GetString()!;
        Assert.Contains(firstNamed, message, StringComparison.Ordinal);
        Assert.Contains(secondNamed, message, StringComparison.Ordinal);
    }
}
