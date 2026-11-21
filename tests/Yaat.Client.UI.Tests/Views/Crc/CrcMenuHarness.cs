using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Views.Scope.FieldMenus.Crc;

namespace Yaat.Client.UI.Tests.Views.Crc;

/// <summary>Hosts one CRC-style FDB menu in a headless window and clicks its pick areas through real pointer input.</summary>
internal static class CrcMenuHarness
{
    /// <summary>The height CRC's header-style chip row adds: a 19 px cell overlapping the next row by 1 px.</summary>
    public const int ChipRowHeight = 18;

    public static Window Show(Control menu)
    {
        var canvas = new Canvas();
        canvas.Children.Add(menu);
        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = canvas,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    public static void Click(Window window, CrcFdbMenu menu, string key) => Click(window, menu, key, shift: false);

    public static void Click(Window window, CrcFdbMenu menu, string key, bool shift)
    {
        CrcPickTarget? target = menu.PickTargets.FirstOrDefault(t => t.Key == key);
        Assert.True(target is not null, $"no pick area '{key}' in [{string.Join(", ", menu.PickTargets.Select(t => t.Key))}]");
        // A T column's box starts under its row's altitude, which wins the overlap as in CRC: click its T glyph.
        Point point = key.EndsWith(" T", StringComparison.Ordinal)
            ? new Point(target.Area.X + target.Area.Width - 7, target.Area.Center.Y)
            : target.Area.Center;
        Point? inWindow = menu.TranslatePoint(point, window);
        Assert.NotNull(inWindow);
        RawInputModifiers modifiers = shift ? RawInputModifiers.Shift : RawInputModifiers.None;
        window.MouseDown(inWindow.Value, MouseButton.Left, modifiers);
        window.MouseUp(inWindow.Value, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    public static bool HasPickArea(CrcFdbMenu menu, string key) => menu.PickTargets.Any(t => t.Key == key);
}
