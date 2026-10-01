// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tree;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation;

/// <summary>
/// Turns one request line into one response line. Every failure — malformed JSON, a missing <c>id</c> or
/// <c>method</c>, an unknown method, a handler error or exception — is answered as a coded error;
/// <see cref="Dispatch"/> never throws.
/// </summary>
public sealed class AutomationDispatcher
{
    private const string UnknownId = "unknown";
    private const string RequestShapeHint = """Send one JSON object per line, e.g. {"id":"1","method":"ping"}.""";

    private static readonly ILogger Log = AppLog.CreateLogger("AutomationDispatcher");

    private readonly Dictionary<string, IRequestHandler> _handlers = new(StringComparer.Ordinal);

    public AutomationDispatcher(NodeRegistry registry)
    {
        Register(new PingHandler());
        Register(new ListWindowsHandler(registry));
        Register(new TreeHandler(registry, new SelectorRequestHelper(new SelectorEngine(registry), registry), new NodeInfoBuilder(registry)));
    }

    private void Register(IRequestHandler handler) => _handlers[handler.Method] = handler;

    public async Task<string> Dispatch(string json)
    {
        AutomationRequest? request;
        try
        {
            request = ProtocolSerializer.Deserialize<AutomationRequest>(json);
        }
        catch (JsonException ex)
        {
            Log.LogDebug(ex, "Automation request is not valid JSON");
            return Fail(UnknownId, AutomationErrorCodes.InvalidParam, $"Invalid JSON: {ex.Message}", RequestShapeHint);
        }

        if (request is null)
        {
            return Fail(UnknownId, AutomationErrorCodes.InvalidParam, "Request is empty", RequestShapeHint);
        }

        if (request.Id is not { Length: > 0 } id)
        {
            return Fail(UnknownId, AutomationErrorCodes.InvalidParam, "Request is missing 'id'", RequestShapeHint);
        }

        if (request.Method is not { Length: > 0 } method)
        {
            return Fail(id, AutomationErrorCodes.InvalidParam, "Request is missing 'method'", RequestShapeHint);
        }

        if (!_handlers.TryGetValue(method, out IRequestHandler? handler))
        {
            return Fail(
                id,
                AutomationErrorCodes.InvalidParam,
                $"Unknown method: {method}",
                $"Use one of the methods this host registers: {string.Join(", ", _handlers.Keys.Order(StringComparer.Ordinal))}."
            );
        }

        return await Invoke(handler, request, id).ConfigureAwait(false);
    }

    private static async Task<string> Invoke(IRequestHandler handler, AutomationRequest request, string id)
    {
        try
        {
            object result = await handler.Handle(request).ConfigureAwait(false);
            if (result is HandlerErrorResult error)
            {
                return ProtocolSerializer.Serialize(AutomationResponse.Failure(id, error.Error));
            }

            return ProtocolSerializer.Serialize(AutomationResponse.Success(id, ProtocolSerializer.ToElement(result)));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Automation method {Method} (request {Id}) failed", handler.Method, id);
            return Fail(id, AutomationErrorCodes.Internal, ex.Message, "See the client log for the stack trace.");
        }
    }

    private static string Fail(string id, string code, string message, string? hint) =>
        ProtocolSerializer.Serialize(AutomationResponse.Failure(id, new AutomationError(message, code, hint, null)));
}
