using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope canvas's palette: the user's assignment tints and selected-symbol colour, parsed from the preferences once per
/// value rather than once per frame, and a colour that does not parse warned about once and then drawn as the default.
/// </summary>
public class ScopeCanvasPaletteTests
{
    private const string Malformed = "#zzzzzz";
    private const string BadTintWarning = "AssignmentTintColor colour #zzzzzz does not parse; using the default";

    // Five frames with one bad value: parsed once and warned about once, and the tint it cannot give is left off.
    [AvaloniaFact]
    public async Task AMalformedTintColour_LogsOneWarning_AcrossManyFrames()
    {
        (_, ScopeView view, Window window) = await OpenAsync();
        try
        {
            UserPreferences prefs = Assert.IsType<UserPreferences>(view.Canvas.Preferences);
            prefs.SetAssignmentTint(true, Malformed);
            long mark = LogMark();

            for (int i = 0; i < 5; i++)
            {
                view.Canvas.DropCachedFrame();
                Assert.NotNull(view.Canvas.CurrentFrame);
                Assert.Null(view.Canvas.RenderSnapshotForTesting().Palette.AssignedToMe);
            }

            Assert.Equal(1, WarningCount(mark));
        }
        finally
        {
            Close(window);
        }
    }

    // A good value between two bad ones evicts the parsed value, so the bad one parses again; it was warned about already
    // and stays quiet.
    [AvaloniaFact]
    public async Task ARepeatedBadColour_WarnsOnce_AfterAGoodValueBetween()
    {
        (_, ScopeView view, Window window) = await OpenAsync();
        try
        {
            UserPreferences prefs = Assert.IsType<UserPreferences>(view.Canvas.Preferences);
            long mark = LogMark();

            prefs.SetAssignmentTint(true, Malformed);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().Palette.AssignedToMe);
            prefs.SetAssignmentTint(true, "#00FF00");
            Assert.NotNull(view.Canvas.RenderSnapshotForTesting().Palette.AssignedToMe);
            prefs.SetAssignmentTint(true, Malformed);
            Assert.Null(view.Canvas.RenderSnapshotForTesting().Palette.AssignedToMe);

            Assert.Equal(1, WarningCount(mark));
        }
        finally
        {
            Close(window);
        }
    }

    // A disposed canvas builds nothing and draws nothing: a frame already queued on the render thread must not reach the
    // Skia objects Dispose freed.
    [AvaloniaFact]
    public async Task ADisposedCanvas_BuildsNoSnapshot_AndDrawsNothing()
    {
        (_, ScopeView view, Window window) = await OpenAsync();
        try
        {
            ScopeCanvas canvas = view.Canvas;
            ScopeRenderSnapshot snapshot = canvas.RenderSnapshotForTesting();
            using var surface = SKSurface.Create(new SKImageInfo(200, 200));
            Assert.True(canvas.DrawSnapshotForTesting(surface.Canvas, canvas.Viewport, snapshot));

            canvas.Dispose();

            Assert.True(canvas.IsDisposed);
            Assert.Null(canvas.TryRenderSnapshotForTesting());
            Assert.False(canvas.DrawSnapshotForTesting(surface.Canvas, canvas.Viewport, snapshot));
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>A Scope view in its window, laid out and rendered once.</summary>
    private static async Task<(ScopeViewModel Scope, ScopeView View, Window Window)> OpenAsync()
    {
        (_, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (scope, view, window);
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>How many log lines since byte <paramref name="offset"/> name the bad tint colour.</summary>
    private static int WarningCount(long offset)
    {
        string[] lines = ReadLogSince(offset).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Count(line => line.Contains(BadTintWarning, StringComparison.Ordinal));
    }

    /// <summary>The current length of the run's log file, the offset a test reads from.</summary>
    private static long LogMark() => new FileInfo(AppLog.LogPath).Length;

    /// <summary>The log lines written since byte <paramref name="offset"/> of the run's log file.</summary>
    private static string ReadLogSince(long offset)
    {
        using var stream = new FileStream(AppLog.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
