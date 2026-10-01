// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Transport;
using Yaat.Client.Automation.Tree;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation;

/// <summary>
/// The in-process automation endpoint: a named pipe speaking line-delimited JSON requests, found through a discovery
/// file. <see cref="Start"/> opens the pipe and writes the file; <see cref="Dispose"/> closes the pipe and deletes it.
/// </summary>
/// <param name="pipeName">The pipe's name, unique per process.</param>
/// <param name="discoveryDirectory">The directory the discovery file <c>&lt;pid&gt;.json</c> is written to.</param>
/// <param name="rootsProvider">The top-level windows to report; read on the UI thread.</param>
public sealed class AutomationHost(string pipeName, string discoveryDirectory, Func<IEnumerable<TopLevel>> rootsProvider) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger("AutomationHost");
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AutomationDispatcher _dispatcher = new(new NodeRegistry(rootsProvider));
    private readonly NamedPipeTransport _transport = new(pipeName);
    private readonly DiscoveryFile _discoveryFile = new(discoveryDirectory, pipeName);
    private int _disposed;

    /// <summary>The path of this process's discovery file.</summary>
    public string DiscoveryFilePath => _discoveryFile.FilePath;

    public void Start()
    {
        _transport.Start(HandleConnection);
        _discoveryFile.Write();
        Log.LogInformation("Automation host listening on pipe {PipeName}; discovery file {DiscoveryFile}", pipeName, _discoveryFile.FilePath);
    }

    private async Task HandleConnection(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Utf8NoBom);
        var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
        await using ConfiguredAsyncDisposable writerScope = writer.ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            string response = await _dispatcher.Dispatch(line).ConfigureAwait(false);
            await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _transport.Dispose();
        _discoveryFile.Dispose();
        Log.LogInformation("Automation host on pipe {PipeName} stopped", pipeName);
    }
}
