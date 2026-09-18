using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yaat.ClientDriver.Mcp;

// Per-monitor-v2 awareness must be set before any UI Automation or screen-capture call, so it comes first:
// without it Windows virtualises coordinates on scaled monitors and UIA rectangles stop matching screen pixels.
if (!NativeInput.EnablePerMonitorDpiAwareness())
{
    Console.Error.WriteLine("yaat-client-driver: per-monitor-v2 DPI awareness was refused; element rectangles may not match screen pixels.");
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<ElementRegistry>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
