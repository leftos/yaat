using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yaat.ClientDriver.Mcp;
using Yaat.ClientDriver.Mcp.Pipe;

// Per-monitor-v2 awareness must be set before any UI Automation or screen-capture call, so it comes first:
// without it Windows virtualises coordinates on scaled monitors and UIA rectangles stop matching screen pixels.
if (!NativeInput.EnablePerMonitorDpiAwareness())
{
    Console.Error.WriteLine("yaat-client-driver: per-monitor-v2 DPI awareness was refused; element rectangles may not match screen pixels.");
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<ElementRegistry>();
builder.Services.AddSingleton<IProcessStarter, ProcessStarter>();

// The client advertises its pipe under %TEMP%/yaat-automation, whatever YAAT_APPDATA_DIR either process runs with
// (AutomationHostFactory.DiscoveryDirectory in the client; the MCP links the protocol types, not the client).
builder.Services.AddSingleton(provider => new PipeDirectory(
    Path.Combine(Path.GetTempPath(), "yaat-automation"),
    PipeDirectory.ClientProcessName,
    provider.GetRequiredService<ILogger<PipeDirectory>>(),
    provider.GetRequiredService<ILogger<PipeClient>>()
));
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
