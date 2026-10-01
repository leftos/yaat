using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Automation;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// The shared setup of the automation pipe tests: each test gets its own pipe name and discovery directory, the windows
/// it shows are closed and the directory removed when it ends, and the response helpers read a result or a coded error.
/// </summary>
public abstract class AutomationHostFixture : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    protected string PipeName { get; } = $"yaat-automation-test-{Guid.NewGuid():N}";

    protected string DiscoveryDirectory { get; } = Path.Combine(Path.GetTempPath(), $"yaat-automation-test-{Guid.NewGuid():N}");

    /// <summary>The windows the test showed, in the order it showed them.</summary>
    protected List<Window> Windows { get; } = [];

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        foreach (Window window in Windows)
        {
            window.Close();
        }

        if (Directory.Exists(DiscoveryDirectory))
        {
            Directory.Delete(DiscoveryDirectory, recursive: true);
        }
    }

    protected AutomationHost StartHost(Func<IEnumerable<TopLevel>> rootsProvider)
    {
        var host = new AutomationHost(PipeName, DiscoveryDirectory, rootsProvider);
        host.Start();
        return host;
    }

    protected Task<AutomationPipeTestClient> Connect() => AutomationPipeTestClient.ConnectAsync(PipeName, ConnectTimeout);

    /// <summary>Shows <paramref name="window"/>, owned by <paramref name="owner"/> when one is given, and tracks it for closing.</summary>
    protected Window Show(Window window, Window? owner)
    {
        Windows.Add(window);
        if (owner is null)
        {
            window.Show();
        }
        else
        {
            window.Show(owner);
        }

        return window;
    }

    /// <summary>Shows a 400 x 300 window named <paramref name="name"/> holding <paramref name="content"/>, laid out and rendered.</summary>
    protected Window ShowWindow(string name, Control content, object? dataContext)
    {
        Window window = Show(
            new Window
            {
                Name = name,
                Title = name,
                Width = 400,
                Height = 300,
                Content = content,
                DataContext = dataContext,
            },
            null
        );
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // Hit testing reads the last rendered frame, and headless frames are drawn only on a render-timer tick.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>A 120 x 80 hit-testable border named "Pad", with no action of its own.</summary>
    protected static Border Pad() =>
        new()
        {
            Name = "Pad",
            Width = 120,
            Height = 80,
            Background = Brushes.Transparent,
        };

    /// <summary>Sends a request whose params are the raw JSON <paramref name="paramsJson"/>, which may be any JSON value.</summary>
    protected static Task<JsonElement> SendRawParams(AutomationPipeTestClient client, string method, string paramsJson) =>
        client.SendRawAsync($"{{\"id\":\"{method}\",\"method\":\"{method}\",\"params\":{paramsJson}}}");

    /// <summary>Sends <paramref name="method"/> with <paramref name="parameters"/> serialised as its params object.</summary>
    protected static Task<JsonElement> Send(AutomationPipeTestClient client, string method, object parameters) =>
        client.SendRawAsync(
            JsonSerializer.Serialize(
                new
                {
                    id = method,
                    method,
                    @params = parameters,
                }
            )
        );

    protected static JsonElement Result(JsonElement response)
    {
        Assert.False(response.TryGetProperty("errorInfo", out JsonElement error), $"Unexpected error: {error}");
        return response.GetProperty("result");
    }

    protected static JsonElement Error(JsonElement response, string expectedCode)
    {
        Assert.False(response.TryGetProperty("result", out JsonElement result), $"Unexpected result: {result}");
        JsonElement error = response.GetProperty("errorInfo");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        return error;
    }
}
