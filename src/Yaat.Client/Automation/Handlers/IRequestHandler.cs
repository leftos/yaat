// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Handlers;

/// <summary>Serves one protocol method. The result is a DTO record, or a <see cref="HandlerErrorResult"/>.</summary>
public interface IRequestHandler
{
    string Method { get; }

    /// <summary>
    /// Serves <paramref name="request"/>. <paramref name="cancellationToken"/> is cancelled when the client disconnects or
    /// the host stops; a handler that waits honours it, one that answers at once may ignore it.
    /// </summary>
    Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken);
}
