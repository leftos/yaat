using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests;

// Canary for the headless lifetime: if Avalonia can't bootstrap our App (themes,
// fonts, etc.) this file fails first, before any of the richer tests run.
public class SmokeTests
{
    [AvaloniaFact]
    public void HeadlessLifetime_BootsWindow()
    {
        var probe = new TextBlock { Text = "hello" };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = probe,
        };

        window.ShowAndRunLayout();

        Assert.True(window.IsVisible);
        Assert.Equal("hello", probe.Text);
        Assert.True(probe.Bounds.Width > 0, "layout pass should have run");
    }
}
