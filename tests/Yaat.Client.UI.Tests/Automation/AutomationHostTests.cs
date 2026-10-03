using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// The automation pipe host end to end over a real named pipe: <c>ping</c>, <c>list_windows</c>, coded errors, the
/// discovery file and the automation-mode guard. Each test has its own pipe name and discovery directory.
/// </summary>
public sealed class AutomationHostTests : AutomationHostFixture
{
    private Window ShowWindow(string title, Window? owner) =>
        Show(
            new Window
            {
                Title = title,
                Width = 300,
                Height = 200,
            },
            owner
        );

    private static JsonElement EntryTitled(JsonElement windows, string title) =>
        Assert.Single(windows.EnumerateArray(), entry => entry.TryGetProperty("title", out JsonElement t) && (t.GetString() == title));

    [AvaloniaFact]
    public async Task Ping_ReturnsPidAndProtocolVersion()
    {
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement result = Result(await client.SendAsync(ProtocolMethods.Ping));

        Assert.Equal(Environment.ProcessId, result.GetProperty("pid").GetInt32());
        Assert.Equal(ProtocolVersion.Current, result.GetProperty("protocolVersion").GetString());
    }

    [AvaloniaFact]
    public async Task ListWindows_ReturnsShownWindowAndOwnedWindow_WithStableIds()
    {
        using AutomationHost host = StartHost(() => Windows.Take(1));
        Window owner = ShowWindow("Owner window", null);
        ShowWindow("Owned window", owner);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement first = Result(await client.SendAsync(ProtocolMethods.ListWindows));
        JsonElement second = Result(await client.SendAsync(ProtocolMethods.ListWindows));

        JsonElement ownerEntry = EntryTitled(first, "Owner window");
        JsonElement ownedEntry = EntryTitled(first, "Owned window");
        int ownerId = ownerEntry.GetProperty("nodeId").GetInt32();
        int ownedId = ownedEntry.GetProperty("nodeId").GetInt32();
        Assert.NotEqual(ownerId, ownedId);
        Assert.False(ownerEntry.GetProperty("isPopup").GetBoolean());
        Assert.False(ownerEntry.TryGetProperty("ownerId", out _));
        Assert.Equal(ownerId, ownedEntry.GetProperty("ownerId").GetInt32());
        Assert.Equal(ownerId, EntryTitled(second, "Owner window").GetProperty("nodeId").GetInt32());
        Assert.Equal(ownedId, EntryTitled(second, "Owned window").GetProperty("nodeId").GetInt32());
    }

    [AvaloniaFact]
    public async Task ListWindows_IncludesAnOpenPopup()
    {
        using AutomationHost host = StartHost(() => Windows);
        Window window = ShowWindow("Popup owner", null);
        var panel = new StackPanel();
        window.Content = panel;
        // The headless platform has no popup windows: every popup opens in the window's overlay layer, as it does in
        // automation mode on the desktop (OverlayPopups).
        var popup = new Popup
        {
            Child = new Border { Width = 40, Height = 30 },
        };
        panel.Children.Add(popup);
        popup.IsOpen = true;
        await using AutomationPipeTestClient client = await Connect();

        JsonElement first = Result(await client.SendAsync(ProtocolMethods.ListWindows));
        JsonElement second = Result(await client.SendAsync(ProtocolMethods.ListWindows));

        int ownerId = EntryTitled(first, "Popup owner").GetProperty("nodeId").GetInt32();
        JsonElement popupEntry = Assert.Single(first.EnumerateArray(), entry => entry.GetProperty("isPopup").GetBoolean());
        Assert.Equal(nameof(OverlayPopupHost), popupEntry.GetProperty("typeName").GetString());
        Assert.Equal(ownerId, popupEntry.GetProperty("ownerId").GetInt32());
        Assert.NotEqual(ownerId, popupEntry.GetProperty("nodeId").GetInt32());
        JsonElement popupAgain = Assert.Single(second.EnumerateArray(), entry => entry.GetProperty("isPopup").GetBoolean());
        Assert.Equal(popupEntry.GetProperty("nodeId").GetInt32(), popupAgain.GetProperty("nodeId").GetInt32());
    }

    [AvaloniaFact]
    public async Task UnknownMethod_ReturnsCodedErrorWithHint()
    {
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement response = await client.SendRawAsync("""{"id":"7","method":"no_such_method"}""");

        Assert.Equal("7", response.GetProperty("id").GetString());
        Assert.False(response.TryGetProperty("result", out _));
        JsonElement error = response.GetProperty("errorInfo");
        Assert.Equal(AutomationErrorCodes.InvalidParam, error.GetProperty("code").GetString());
        Assert.Contains("no_such_method", error.GetProperty("message").GetString());
        string? hint = error.GetProperty("suggested").GetString();
        Assert.Contains(ProtocolMethods.Ping, hint);
        Assert.Contains(ProtocolMethods.ListWindows, hint);
    }

    [AvaloniaFact]
    public async Task MalformedJson_ReturnsCodedError_AndConnectionStaysUsable()
    {
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement response = await client.SendRawAsync("{not json");

        JsonElement error = response.GetProperty("errorInfo");
        Assert.Equal(AutomationErrorCodes.InvalidParam, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("suggested").GetString()));
        JsonElement ping = Result(await client.SendAsync(ProtocolMethods.Ping));
        Assert.Equal(Environment.ProcessId, ping.GetProperty("pid").GetInt32());
    }

    [AvaloniaFact]
    public void DiscoveryFile_WrittenOnStart_RemovedOnDispose()
    {
        string expectedPath = Path.Combine(DiscoveryDirectory, $"{Environment.ProcessId}.json");
        AutomationHost host = StartHost(() => []);
        try
        {
            Assert.Equal(expectedPath, host.DiscoveryFilePath);
            DiscoveryInfo? info = ProtocolSerializer.Deserialize<DiscoveryInfo>(File.ReadAllText(expectedPath));
            Assert.NotNull(info);
            Assert.Equal(Environment.ProcessId, info.Pid);
            Assert.Equal(PipeName, info.PipeName);
            Assert.Equal(ProtocolVersion.Current, info.ProtocolVersion);
            Assert.Empty(Directory.GetFiles(DiscoveryDirectory, "*.tmp"));
        }
        finally
        {
            host.Dispose();
        }

        Assert.False(File.Exists(expectedPath));
    }

    // No process can have pid int.MaxValue (Windows pids are multiples of 4; Linux caps them far lower), while
    // pid 4 (Windows' System process) or 1 (init) is always running.
    private const int NeverRunningPid = int.MaxValue;
    private static readonly int AlwaysRunningPid = OperatingSystem.IsWindows() ? 4 : 1;

    private static string RunningProcessName(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.ProcessName;
    }

    private string WriteDiscoveryFile(int pid, string processName)
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        string path = Path.Combine(DiscoveryDirectory, $"{pid}.json");
        var info = new DiscoveryInfo
        {
            Pid = pid,
            PipeName = $"yaat-automation-{pid}",
            ProcessName = processName,
            StartTime = DateTimeOffset.UtcNow,
            ProtocolVersion = ProtocolVersion.Current,
        };
        File.WriteAllText(path, ProtocolSerializer.Serialize(info));
        return path;
    }

    [AvaloniaFact]
    public void DiscoveryFile_SweepsStalePidFiles()
    {
        string stale = WriteDiscoveryFile(NeverRunningPid, "Yaat.Client");
        string staleTemp = Path.Combine(DiscoveryDirectory, $"{NeverRunningPid}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(staleTemp, "{");
        string live = WriteDiscoveryFile(AlwaysRunningPid, RunningProcessName(AlwaysRunningPid));
        string unrelated = Path.Combine(DiscoveryDirectory, "notes.json");
        File.WriteAllText(unrelated, "{}");

        using AutomationHost host = StartHost(() => []);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(host.DiscoveryFilePath));
    }

    [AvaloniaFact]
    public void DiscoveryFile_SweepsFileWhosePidBelongsToAnotherProcess()
    {
        // The pid is running, but as another program than the file names: the pid was reused after that client exited.
        // This test process's own pid cannot carry the case: the host replaces its own pid's file whatever the sweep does.
        string reused = WriteDiscoveryFile(AlwaysRunningPid, $"{RunningProcessName(AlwaysRunningPid)}-not-this-process");

        using AutomationHost host = StartHost(() => []);

        Assert.False(File.Exists(reused));
        Assert.True(File.Exists(host.DiscoveryFilePath));
    }

    [AvaloniaFact]
    public async Task MissingIdOrMethod_ReturnsCodedError()
    {
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        JsonElement noId = await client.SendRawAsync("""{"method":"ping"}""");
        JsonElement noMethod = await client.SendRawAsync("""{"id":"3"}""");

        Assert.Equal("unknown", noId.GetProperty("id").GetString());
        Assert.Equal(AutomationErrorCodes.InvalidParam, noId.GetProperty("errorInfo").GetProperty("code").GetString());
        Assert.Equal("Request is missing 'id'", noId.GetProperty("errorInfo").GetProperty("message").GetString());
        Assert.Equal("3", noMethod.GetProperty("id").GetString());
        Assert.Equal(AutomationErrorCodes.InvalidParam, noMethod.GetProperty("errorInfo").GetProperty("code").GetString());
        Assert.Equal("Request is missing 'method'", noMethod.GetProperty("errorInfo").GetProperty("message").GetString());
        Result(await client.SendAsync(ProtocolMethods.Ping));
    }

    [AvaloniaFact]
    public async Task Dispose_ClosesOpenConnectionAndStopsAccepting()
    {
        AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();
        Result(await client.SendAsync(ProtocolMethods.Ping));

        host.Dispose();

        string? line;
        try
        {
            line = await client.ReadLineAsync();
        }
        catch (IOException)
        {
            line = null;
        }

        Assert.Null(line);
        await Assert.ThrowsAsync<TimeoutException>(() => AutomationPipeTestClient.ConnectAsync(PipeName, TimeSpan.FromMilliseconds(300)));
    }

    [AvaloniaFact]
    public async Task Pipe_IsRestrictedToCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Pipe ACLs are a Windows concept; elsewhere CurrentUserOnly checks the peer's uid.");
            return;
        }

        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        AssertOnlyCurrentUserAllowed(client.GetAccessControl());
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOnlyCurrentUserAllowed(PipeSecurity security)
    {
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);
        List<PipeAccessRule> allowRules =
        [
            .. security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow),
        ];
        Assert.NotEmpty(allowRules);
        Assert.All(allowRules, rule => Assert.Equal(currentUser, rule.IdentityReference));
    }

    [AvaloniaFact]
    public async Task TwoClients_CanConnectInTurn()
    {
        using AutomationHost host = StartHost(() => []);

        await using (AutomationPipeTestClient first = await Connect())
        {
            Result(await first.SendAsync(ProtocolMethods.Ping));

            await using AutomationPipeTestClient concurrent = await Connect();
            Result(await concurrent.SendAsync(ProtocolMethods.Ping));
            Result(await first.SendAsync(ProtocolMethods.Ping));
        }

        await using AutomationPipeTestClient second = await Connect();
        JsonElement result = Result(await second.SendAsync(ProtocolMethods.Ping));
        Assert.Equal(Environment.ProcessId, result.GetProperty("pid").GetInt32());
    }

    [AvaloniaFact]
    public async Task Host_IsNotStarted_WhenAutomationModeOff()
    {
        AutomationHost? host = AutomationHostFactory.StartIfEnabled(false, PipeName, DiscoveryDirectory, () => Windows, () => null, () => null);

        Assert.Null(host);
        Assert.False(Directory.Exists(DiscoveryDirectory));
        await Assert.ThrowsAsync<TimeoutException>(() => AutomationPipeTestClient.ConnectAsync(PipeName, TimeSpan.FromMilliseconds(300)));
    }
}
