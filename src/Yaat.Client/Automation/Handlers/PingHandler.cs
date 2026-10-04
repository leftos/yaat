// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Handlers;

/// <summary><c>ping</c>: answers the host's process id and protocol version without touching the UI thread.</summary>
public sealed class PingHandler : IRequestHandler
{
    public string Method => ProtocolMethods.Ping;

    public Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new PingResult(Environment.ProcessId, ProtocolVersion.Current));
}
