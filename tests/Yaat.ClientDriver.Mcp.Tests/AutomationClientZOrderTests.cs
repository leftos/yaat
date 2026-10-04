using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// A client started in automation mode, as <c>launch_yaat</c> starts it, must open every window behind the windows already
/// on screen, with an owned window above its owner. Windows creates a new top-level window at the top of the Z-order
/// whenever the creating process may set the foreground window, which a client started from the foreground app's process
/// tree (an agent's shell) may; a never-activated show leaves it there, painted over whatever the user is working in. The
/// reference window these tests create stands in for the user's window. A client without that right has its windows inserted
/// directly below the foreground window, which is still above the reference window, so the assertions hold the same whichever
/// right the test process has.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class AutomationClientZOrderTests
{
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShowNoActivate = 4;
    private const uint GwHwndNext = 2;
    private const uint WmQuit = 0x0012;
    private const int ReferenceX = -32000;
    private const int ReferenceY = -32000;
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReferenceWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ThreadStopWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AutomationClient_OpensItsWindowsBehindAWindowAlreadyOnScreen()
    {
        string exePath = ClientExePath();
        using var reference = new ReferenceWindow();
        string appDataDir = NewAppDataDirectory();
        using Process client = StartClient(exePath, appDataDir);
        try
        {
            List<ClientWindow> windows = await WaitForWindowsAsync(client, appDataDir, "MainWindow");

            foreach (ClientWindow window in windows)
            {
                Assert.True(
                    IsBelow(window.Hwnd, reference.Handle),
                    $"{window.TypeName} (hwnd 0x{window.Hwnd:X}) opened above the window already on screen"
                );
            }
        }
        finally
        {
            await StopClientAsync(client);
            DeleteAppData(appDataDir);
        }
    }

    /// <summary>
    /// The dialog is Help → About: <c>OnAboutClick</c> opens <c>AboutWindow</c> through <c>DialogPresenter.ShowModalAsync</c>,
    /// which shows it over the main window, needs no server, and passes through the gate.
    /// </summary>
    [Fact]
    public async Task AutomationClient_OpensAnOwnedDialogAboveItsOwnerAndBelowTheWindowAlreadyOnScreen()
    {
        string exePath = ClientExePath();
        using var reference = new ReferenceWindow();
        string appDataDir = NewAppDataDirectory();
        using Process client = StartClient(exePath, appDataDir);
        try
        {
            await WaitForWindowsAsync(client, appDataDir, "MainWindow");
            await using AutomationPipeTestClient pipe = await AutomationPipeTestClient.ConnectAsync(PipeName(client.Id), TimeSpan.FromSeconds(5));
            await RequestAsync(pipe, "1", "click", new { selector = "MenuItem[Header=\"_Help\"]" });
            await RequestAsync(
                pipe,
                "2",
                "wait_for",
                new
                {
                    selector = "#AboutMenuItem",
                    condition = "exists",
                    timeoutMs = 5000,
                }
            );
            await RequestAsync(pipe, "3", "click", new { selector = "#AboutMenuItem" });

            List<ClientWindow> windows = await WaitForWindowsAsync(client, appDataDir, "AboutWindow");
            ClientWindow main = windows.Single(window => window.TypeName == "MainWindow");
            ClientWindow dialog = windows.Single(window => window.TypeName == "AboutWindow");

            Assert.True(
                IsAbove(dialog.Hwnd, main.Hwnd),
                $"AboutWindow (hwnd 0x{dialog.Hwnd:X}) opened below MainWindow (hwnd 0x{main.Hwnd:X}), its owner"
            );
            Assert.True(IsBelow(dialog.Hwnd, reference.Handle), $"AboutWindow (hwnd 0x{dialog.Hwnd:X}) opened above the window already on screen");
        }
        finally
        {
            await StopClientAsync(client);
            DeleteAppData(appDataDir);
        }
    }

    /// <summary>The client this test project builds, in the test's own configuration (Debug or Release).</summary>
    private static string ClientExePath()
    {
        var testDir = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testDir.Parent!.Name;
        DirectoryInfo repoRoot = testDir;
        while (!File.Exists(Path.Combine(repoRoot.FullName, "yaat.slnx")))
        {
            repoRoot = repoRoot.Parent ?? throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}");
        }

        string exe = Path.Combine(repoRoot.FullName, "src", "Yaat.Client", "bin", configuration, "net10.0", "Yaat.Client.exe");
        Assert.True(File.Exists(exe), $"No client at {exe}; build src/Yaat.Client -c {configuration} first");
        return exe;
    }

    private static string NewAppDataDirectory() => Directory.CreateTempSubdirectory("yaat-zorder-").FullName;

    private static string PipeName(int pid) => $"yaat-automation-{pid}";

    private static string ClientLogPath(string appDataDir) => Path.Combine(appDataDir, "yaat-client.log");

    private static Process StartClient(string exePath, string appDataDir)
    {
        var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        startInfo.Environment["YAAT_AUTOMATION"] = "1";
        startInfo.Environment["YAAT_APPDATA_DIR"] = appDataDir;
        return Process.Start(startInfo)!;
    }

    private static async Task StopClientAsync(Process client)
    {
        if (!client.HasExited)
        {
            client.Kill(entireProcessTree: true);
        }

        await client.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes the run's app-data directory; a file the client's own shutdown still holds is reported, not thrown.</summary>
    private static void DeleteAppData(string appDataDir)
    {
        try
        {
            Directory.Delete(appDataDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not delete {appDataDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Polls the client's automation pipe until a window of <paramref name="requiredTypeName"/> is visible; returns every
    /// visible window with its HWND then. The client exiting ends the wait at once with its exit code and log path, and a
    /// host that keeps answering with an error has that answer quoted when the deadline passes.
    /// </summary>
    private static async Task<List<ClientWindow>> WaitForWindowsAsync(Process client, string appDataDir, string requiredTypeName)
    {
        string logPath = ClientLogPath(appDataDir);
        var deadline = Stopwatch.StartNew();
        string lastError = "(no answer)";
        while (deadline.Elapsed < WindowWait)
        {
            if (client.HasExited)
            {
                throw new InvalidOperationException(
                    $"The client (pid {client.Id}) exited with code {client.ExitCode} before {requiredTypeName} was visible; its log is " + logPath
                );
            }

            try
            {
                await using AutomationPipeTestClient pipe = await AutomationPipeTestClient.ConnectAsync(PipeName(client.Id), TimeSpan.FromSeconds(2));
                JsonElement response = await pipe.SendAsync("list_windows");
                if (response.TryGetProperty("error", out JsonElement error))
                {
                    lastError = error.GetRawText();
                }
                else if (response.TryGetProperty("result", out JsonElement rows))
                {
                    List<ClientWindow> visible = ReadVisibleWindows(rows);
                    if (visible.Any(window => window.TypeName == requiredTypeName))
                    {
                        return visible;
                    }

                    lastError = $"{visible.Count} visible windows, none of them {requiredTypeName}";
                }
            }
            catch (TimeoutException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"Client {client.Id} showed no {requiredTypeName} within {WindowWait.TotalSeconds} s; last list_windows answer: {lastError}; "
                + $"its log is {logPath}"
        );
    }

    private static List<ClientWindow> ReadVisibleWindows(JsonElement rows)
    {
        List<ClientWindow> visible = [];
        foreach (JsonElement row in rows.EnumerateArray())
        {
            long hwnd = row.GetProperty("hwnd").GetInt64();
            if (row.GetProperty("isVisible").GetBoolean() && (hwnd != 0))
            {
                visible.Add(new ClientWindow(row.GetProperty("typeName").GetString()!, (nint)hwnd));
            }
        }

        return visible;
    }

    /// <summary>Sends one pipe request and returns its result, failing the test with the host's own error text when it has one.</summary>
    private static async Task<JsonElement> RequestAsync(AutomationPipeTestClient pipe, string id, string method, object? parameters)
    {
        string line =
            (parameters is null)
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(
                    new
                    {
                        id,
                        method,
                        @params = parameters,
                    }
                );
        JsonElement response = await pipe.SendRawAsync(line);
        Assert.True(response.TryGetProperty("result", out JsonElement result), $"{method} answered without a result: {response.GetRawText()}");
        return result;
    }

    /// <summary>True when <paramref name="window"/> comes after <paramref name="other"/> in the desktop's Z-order (top first).</summary>
    private static bool IsBelow(nint window, nint other)
    {
        uint target = (uint)window;
        uint belowThis = (uint)other;
        bool passedOther = false;
        for (nint hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            uint handle = (uint)hwnd;
            if (handle == belowThis)
            {
                passedOther = true;
                continue;
            }

            if (handle == target)
            {
                return passedOther;
            }
        }

        throw new InvalidOperationException($"0x{target:X} is not a top-level window");
    }

    /// <summary>True when <paramref name="window"/> comes before <paramref name="other"/> in the desktop's Z-order (top first).</summary>
    private static bool IsAbove(nint window, nint other)
    {
        uint target = (uint)window;
        uint aboveThis = (uint)other;
        bool passedTarget = false;
        for (nint hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            uint handle = (uint)hwnd;
            if (handle == target)
            {
                passedTarget = true;
                continue;
            }

            if (handle == aboveThis)
            {
                return passedTarget;
            }
        }

        throw new InvalidOperationException($"Neither 0x{target:X} nor 0x{aboveThis:X} is a top-level window");
    }

    private static void Report(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    /// <summary>One visible top-level window of the client, as the pipe reported it.</summary>
    private readonly record struct ClientWindow(string TypeName, nint Hwnd);

    /// <summary>
    /// The window that stands in for the user's: a real top-level window, shown off-screen so the tests never cover the
    /// user's app. Win32 pumps a window's messages on the thread that created it and destroys it there too, so the window
    /// lives on a thread of its own that runs a message loop until <see cref="Dispose"/> asks it to quit.
    /// </summary>
    private sealed class ReferenceWindow : IDisposable
    {
        private readonly ManualResetEventSlim _created = new();
        private readonly Thread _thread;
        private nint _handle;
        private uint _threadId;
        private int _createError;
        private bool _destroyed;
        private int _destroyError;

        public ReferenceWindow()
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "z-order reference window" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_created.Wait(ReferenceWait))
            {
                throw new TimeoutException($"The reference window thread did not start within {ReferenceWait.TotalSeconds} s");
            }

            if (_handle == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed for the reference window: Win32 error {_createError}");
            }
        }

        public nint Handle => _handle;

        public void Dispose()
        {
            if (_handle == 0)
            {
                _created.Dispose();
                return;
            }

            if (!PostThreadMessage(_threadId, WmQuit, 0, 0))
            {
                Report($"PostThreadMessage(WM_QUIT) failed for the reference window thread: Win32 error {Marshal.GetLastWin32Error()}");
            }

            if (!_thread.Join(ThreadStopWait))
            {
                Report(
                    $"The reference window thread did not stop within {ThreadStopWait.TotalSeconds} s; window 0x{_handle:X} is left on the desktop"
                );
            }
            else if (!_destroyed)
            {
                Report($"DestroyWindow failed for the reference window 0x{_handle:X}: Win32 error {_destroyError}");
            }

            _created.Dispose();
        }

        private void Pump()
        {
            _threadId = GetCurrentThreadId();
            _handle = CreateWindowEx(
                WsExNoActivate,
                "STATIC",
                "YAAT z-order reference",
                WsOverlappedWindow,
                ReferenceX,
                ReferenceY,
                240,
                120,
                0,
                0,
                0,
                0
            );
            if (_handle == 0)
            {
                _createError = Marshal.GetLastWin32Error();
            }
            else
            {
                ShowWindow(_handle, SwShowNoActivate);
            }

            _created.Set();
            if (_handle == 0)
            {
                return;
            }

            while (GetMessage(out NativeMessage message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            _destroyed = DestroyWindow(_handle);
            if (!_destroyed)
            {
                _destroyError = Marshal.GetLastWin32Error();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
