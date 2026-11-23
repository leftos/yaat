using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The parking picker window with no room, over the real KOAK vNAS map served by a stub HTTP handler: it shows the
/// stands with the current one selected and returns the clicked stand's name on OK; Esc and Cancel return null; a 404
/// and an unreachable vNAS with nothing cached say "No vNAS map for OAK"; a loader that fails (or cancels on its own)
/// says why; the loader runs
/// off the UI thread; and picking and closing leave the vNAS cache folder as the load wrote it (only that folder is
/// checked: closing also saves the window's geometry to the preferences).
/// </summary>
public sealed class ParkingPickerWindowTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly UserPreferences Preferences = new();

    private readonly string _root = YaatPaths.Combine("parking-picker-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OpensWithAStubMapAndNoRoom_ReturnsTheClickedStandsName()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        string current = OakParkingLayout.FirstParkingName;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("KOAK", current.ToLowerInvariant(), Preferences, downloader.GetLayoutAsync);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            Button ok = picker.FindControl<Button>("OkButton")!;

            Assert.NotEmpty(canvas.Scene.Stands);
            Assert.Equal(current, canvas.Selected);
            Assert.True(ok.IsEnabled);
            Assert.Equal("KOAK", picker.FindControl<TextBlock>("AirportText")!.Text);

            (ParkingPickerStand stand, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport, notNamed: current);
            Click(picker, canvas, at);
            Assert.Equal(stand.Name, canvas.Selected);
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(stand.Name, await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EscOrCancel_ReturnsNull(bool escape)
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            (_, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport);
            Click(picker, canvas, at);
            Assert.NotNull(canvas.Selected);

            if (escape)
            {
                picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            }
            else
            {
                picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Dispatcher.UIThread.RunJobs();

            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMap_OnA404_OrUnreachableWithNoCache_SaysNoVnasMap(bool unreachable)
    {
        AirportLayoutDownloader downloader = Downloader(_ =>
            unreachable ? throw new HttpRequestException("offline") : new HttpResponseMessage(HttpStatusCode.NotFound)
        );
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", "A1", Preferences, downloader.GetLayoutAsync);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("No vNAS map for OAK", picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Empty(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Scene.Stands);
            Assert.False(picker.FindControl<Button>("OkButton")!.IsEnabled);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task PickingAndClosing_LeaveTheVnasCacheAsTheLoadWroteIt()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        string cachePath = downloader.GetCachePath("KOAK");
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("KOAK", null, Preferences, downloader.GetLayoutAsync);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            byte[] bytes = await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken);
            DateTime lastWrite = File.GetLastWriteTimeUtc(cachePath);
            string[] entries = Directory.GetFileSystemEntries(downloader.CacheDir);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            (_, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport);

            Click(picker, canvas, at);
            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(await pending.WaitAsync(HangGuard));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(cachePath));
            Assert.Equal(entries, Directory.GetFileSystemEntries(downloader.CacheDir));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALoaderThatThrows_EndsWithTheCouldNotLoadStatus()
    {
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (_, _) => Task.FromException<AirportGroundLayout?>(new IOException("disk gone"))
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("Could not load the vNAS map for OAK: disk gone", picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False(picker.FindControl<Button>("OkButton")!.IsEnabled);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheLayoutLoader_RunsOffTheUiThread()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        bool? onUiThread = null;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (id, ct) =>
                {
                    onUiThread = Dispatcher.UIThread.CheckAccess();
                    return downloader.GetLayoutAsync(id, ct);
                }
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.False(onUiThread);
            Assert.NotEmpty(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Scene.Stands);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALoaderThatCancelsOnItsOwn_WhileTheWindowIsOpen_EndsWithTheCouldNotLoadStatus()
    {
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (_, _) => Task.FromException<AirportGroundLayout?>(new OperationCanceledException("gave up"))
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("Could not load the vNAS map for OAK: gave up", picker.FindControl<TextBlock>("StatusText")!.Text);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(OakParkingLayout.GeoJson) };

    private static Window ShowOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    private static async Task LoadAsync(ParkingPickerWindow picker)
    {
        Dispatcher.UIThread.RunJobs();
        await picker.LoadTask.WaitAsync(HangGuard);
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            picker.UpdateLayout();
        }
    }

    private static void Click(Window window, ParkingPickerCanvas canvas, Point at)
    {
        Point inWindow = canvas.TranslatePoint(at, window)!.Value;
        window.MouseMove(inWindow);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // A downloader over this test's own cache folder whose every request respond answers; vNAS is never reached.
    private AirportLayoutDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond));
        var downloader = new AirportLayoutDownloader(http, Path.Combine(_root, "cache", "airports"));
        _owned.Add(downloader);
        _owned.Add(http);
        return downloader;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
