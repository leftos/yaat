// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>screenshot</c>: renders a window (<c>windowNodeId</c> or <c>windowSelector</c>, its client area) or one element
/// (<c>nodeId</c> or <c>selector</c>) to a PNG with <see cref="RenderTargetBitmap"/>, at its window's
/// <see cref="TopLevel.RenderScaling"/>: the pixel size is the DIP size times the scale, rounded up, at 96 × scale DPI.
/// A hidden target is <c>ELEMENT_DISABLED</c>, as for <c>click</c>; one with no area is <c>OUT_OF_BOUNDS</c>.
/// </summary>
public sealed class ScreenshotHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    private const double BaseDpi = 96;

    private sealed record ScreenshotTarget(ElementTarget Target, bool IsWindow);

    /// <summary>A target rendered on the UI thread, waiting to be encoded off it.</summary>
    private sealed record RenderedTarget(RenderTargetBitmap Bitmap, PixelSize PixelSize, double Scale);

    public string Method => ProtocolMethods.Screenshot;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not ScreenshotTarget target)
        {
            return parsed;
        }

        object captured = await Dispatcher.UIThread.InvokeAsync(() => Capture(target)).GetTask().ConfigureAwait(false);
        return (captured is RenderedTarget rendered) ? Encode(rendered) : captured;
    }

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget window, HandlerErrorResult? windowError) = InputParams.ReadTarget(element, "windowNodeId", "windowSelector");
        (ElementTarget item, HandlerErrorResult? itemError) = InputParams.ReadTarget(element, "nodeId", "selector");
        HandlerErrorResult? error = windowError ?? itemError;
        if (error is not null)
        {
            return error;
        }

        if (window.IsGiven && item.IsGiven)
        {
            return HandlerResult.InvalidParam(
                window.ParamName,
                $"Give either a window ('{window.ParamName}') or one element ('{item.ParamName}'), not both."
            );
        }

        if (!window.IsGiven && !item.IsGiven)
        {
            return HandlerResult.InvalidParam(
                item.SelectorParam,
                "Give the window as 'windowNodeId' or 'windowSelector', or one element as 'nodeId' or 'selector'."
            );
        }

        return window.IsGiven ? new ScreenshotTarget(window, IsWindow: true) : new ScreenshotTarget(item, IsWindow: false);
    }

    private object Capture(ScreenshotTarget target)
    {
        if (!targets.TryResolve(target.Target, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        if (target.IsWindow && (visual is not TopLevel))
        {
            return HandlerResult.InvalidParam(
                target.Target.ParamName,
                $"'{target.Target.ParamName}' must name a window, not a {visual.GetType().Name}."
            );
        }

        int nodeId = registry.GetOrRegister(visual);
        string elementType = visual.GetType().Name;
        if (!visual.IsEffectivelyVisible)
        {
            return HandlerResult.ElementDisabled(nodeId, elementType, "It is not visible.");
        }

        if (TopLevel.GetTopLevel(visual) is not { } topLevel)
        {
            return HandlerResult.StaleNode(nodeId, $"Node {nodeId} is not in a window. Call get_tree or list_windows for fresh node ids.");
        }

        Size size = (visual is TopLevel window) ? window.ClientSize : visual.Bounds.Size;
        if ((size.Width <= 0) || (size.Height <= 0))
        {
            return HandlerResult.ZeroSize(nodeId, elementType, size.Width, size.Height);
        }

        return Render(visual, size, topLevel.RenderScaling);
    }

    private static RenderedTarget Render(Visual visual, Size size, double scale)
    {
        var pixelSize = new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
        var bitmap = new RenderTargetBitmap(pixelSize, new Vector(BaseDpi * scale, BaseDpi * scale));
        try
        {
            bitmap.Render(visual);
            return new RenderedTarget(bitmap, pixelSize, scale);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encodes a rendered target off the UI thread. Avalonia's Skia render-target bitmap is a CPU bitmap whose
    /// <c>Save</c> snapshots it under its own lock and encodes with SkiaSharp, with no dispatcher check.
    /// </summary>
    private static ScreenshotResult Encode(RenderedTarget rendered)
    {
        using RenderTargetBitmap bitmap = rendered.Bitmap;
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return new ScreenshotResult(rendered.PixelSize.Width, rendered.PixelSize.Height, rendered.Scale, Convert.ToBase64String(stream.ToArray()));
    }
}
