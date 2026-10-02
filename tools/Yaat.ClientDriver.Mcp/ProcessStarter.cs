using System.Diagnostics;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>Starts the client process for <c>launch_yaat</c>, injected so the launch's wait can be tested.</summary>
public interface IProcessStarter
{
    /// <summary>Starts <paramref name="startInfo"/> and returns the running process.</summary>
    /// <exception cref="McpException">Windows refused to start the executable.</exception>
    Process Start(ProcessStartInfo startInfo);
}

/// <summary>The real starter: <see cref="Process.Start(ProcessStartInfo)"/>, refusing an executable Windows would not run.</summary>
public sealed class ProcessStarter : IProcessStarter
{
    /// <inheritdoc />
    public Process Start(ProcessStartInfo startInfo) =>
        Process.Start(startInfo) ?? throw new McpException($"Windows did not start '{startInfo.FileName}'");
}
