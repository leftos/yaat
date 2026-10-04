extern alias mcp;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using mcp::Yaat.ClientDriver.Mcp;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>screenshot</c> on an id from a YAAT client's automation pipe: the client renders the element itself, the PNG it sends is
/// saved to the shots folder (downscaled when wider than <c>maxWidth</c>) and returned as an image block. A real host in this
/// process answers for the sizes and refusals; its headless PNG is never decoded, so a scripted pipe server sends a real PNG
/// where the bytes matter.
/// </summary>
public sealed class ScreenshotPipeTests : AutomationHostFixture
{
    private const string Captured = " — captured ";

    private static int Pid => Environment.ProcessId;

    private static string ShotFixture => Path.Combine(AppContext.BaseDirectory, "TestData", "shot-200x100.png");

    [Fact]
    public async Task Screenshot_PipeId_WritesTheHostsPngAndReturnsImageBlock()
    {
        byte[] png = await File.ReadAllBytesAsync(ShotFixture, TestContext.Current.CancellationToken);
        List<JsonElement?> requests = [];
        var answer = new ScreenshotResult(200, 100, 1, Convert.ToBase64String(png));
        (PipeDirectory directory, Task server) = await StartStubAsync(answer, requests);
        try
        {
            var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
            string id = registry.Register(Pid, 7);

            List<ContentBlock> blocks = [.. await NewTools(registry, directory).ScreenshotAsync(id, CancellationToken.None, 1280)];

            string text = Assert.IsType<TextContentBlock>(blocks[0]).Text;
            string path = text[..text.IndexOf(Captured, StringComparison.Ordinal)];
            Assert.Equal($"{path}{Captured}200x100 from the client's render at scale 1 (pipe), returned 200x100 ({png.Length} bytes)", text);
            Assert.Equal(png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("image/png", Assert.IsType<ImageContentBlock>(blocks[1]).MimeType);
            JsonElement parameters = Assert.Single(requests)!.Value;
            Assert.Equal(7, parameters.GetProperty("nodeId").GetInt32());
            Assert.False(parameters.TryGetProperty("windowNodeId", out _), "screenshot always names the element as nodeId");
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }

        await server;
    }

    [AvaloniaFact]
    public async Task Screenshot_PipeWindow_IsItsClientSize()
    {
        using AutomationHost host = StartHost(() => Windows);
        Window window = ShowWindow("PipeWindow", new StackPanel { Children = { Pad() } }, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
            InspectTools tools = NewTools(registry, directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));

            List<ContentBlock> blocks = [.. await tools.ScreenshotAsync(windowId, CancellationToken.None, 1280)];

            int width = (int)Math.Ceiling(window.ClientSize.Width);
            int height = (int)Math.Ceiling(window.ClientSize.Height);
            string text = Assert.IsType<TextContentBlock>(blocks[0]).Text;
            Assert.Contains(
                $"{Captured}{width}x{height} from the client's render at scale 1 (pipe), returned {width}x{height} (",
                text,
                StringComparison.Ordinal
            );
            Assert.True(File.Exists(text[..text.IndexOf(Captured, StringComparison.Ordinal)]), "the shot is written to the shots folder");
            Assert.IsType<ImageContentBlock>(blocks[1]);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    [Fact]
    public async Task Screenshot_PipeWiderThanMaxWidth_Downscales()
    {
        byte[] png = await File.ReadAllBytesAsync(ShotFixture, TestContext.Current.CancellationToken);
        var answer = new ScreenshotResult(200, 100, 1.5, Convert.ToBase64String(png));
        (PipeDirectory directory, Task server) = await StartStubAsync(answer, []);
        try
        {
            var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
            string id = registry.Register(Pid, 7);

            List<ContentBlock> blocks = [.. await NewTools(registry, directory).ScreenshotAsync(id, CancellationToken.None, 100)];

            string text = Assert.IsType<TextContentBlock>(blocks[0]).Text;
            Assert.Contains($"{Captured}200x100 from the client's render at scale 1.5 (pipe), returned 100x50 (", text, StringComparison.Ordinal);
            byte[] written = await File.ReadAllBytesAsync(
                text[..text.IndexOf(Captured, StringComparison.Ordinal)],
                TestContext.Current.CancellationToken
            );
            Assert.Equal(100, PngWidth(written));
            Assert.Equal(50, PngHeight(written));
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }

        await server;
    }

    [AvaloniaFact]
    public async Task Screenshot_PipeHiddenElement_ReportsDisabled()
    {
        using AutomationHost host = StartHost(() => Windows);
        var hidden = new Border
        {
            Name = "Hidden",
            Width = 40,
            Height = 30,
            IsVisible = false,
        };
        ShowWindow("PipeWindow", new StackPanel { Children = { Pad(), hidden } }, null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            var registry = new ElementRegistry(NullLogger<ElementRegistry>.Instance);
            InspectTools tools = NewTools(registry, directory);
            string windowId = IdOf(await tools.ListWindowsAsync(Pid, CancellationToken.None));
            string hiddenId = IdOf(await tools.FindElementsAsync(windowId, CancellationToken.None, "", "Hidden", ""));

            McpException failure = await Assert.ThrowsAsync<McpException>(() => tools.ScreenshotAsync(hiddenId, CancellationToken.None, 1280));

            Assert.StartsWith("ELEMENT_DISABLED: ", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
        }
    }

    // A PNG's IHDR chunk follows the 8-byte signature and the chunk's length and type: width at byte 16, height at byte 20.
    private static int PngWidth(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));

    private static int PngHeight(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

    private static string IdOf(string row) => row.TrimStart().Split(" | ")[0];

    private static InspectTools NewTools(ElementRegistry registry, PipeDirectory directory) =>
        new(registry, directory, NullLogger<InspectTools>.Instance);

    /// <summary>
    /// Advertises and serves a scripted host answering every screenshot with <paramref name="answer"/>, recording each
    /// request's params.
    /// </summary>
    private async Task<(PipeDirectory Directory, Task Server)> StartStubAsync(ScreenshotResult answer, List<JsonElement?> requests)
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, TestContext.Current.CancellationToken);
        return (NewPipeDirectory(), ServeAsync(pipeName, answer, requests));
    }

    private static async Task ServeAsync(string pipeName, ScreenshotResult answer, List<JsonElement?> requests)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await StubPipeHost.ServeAsync(
            pipeName,
            (_, parameters) =>
            {
                requests.Add(parameters);
                return answer;
            },
            TimeSpan.Zero,
            timeout.Token
        );
    }

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
