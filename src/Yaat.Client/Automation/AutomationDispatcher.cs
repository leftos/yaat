// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tools;
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

    /// <param name="registry">The node ids of the windows the host reports.</param>
    /// <param name="stateProvider">The simulation state, or null while the main window is not up; read on the UI thread.</param>
    /// <param name="toolsProvider">The app tools bound to the main window's view model, or null while it is not up; read on the UI thread.</param>
    public AutomationDispatcher(NodeRegistry registry, Func<IAutomationState?> stateProvider, Func<AutomationTools?> toolsProvider)
    {
        Register(new PingHandler());
        Register(new QueueFilePickHandler());
        Register(new ListWindowsHandler(registry));
        var engine = new SelectorEngine(registry);
        var selectors = new SelectorRequestHelper(engine, registry);
        var targets = new TargetResolver(registry, selectors);
        Register(new TreeHandler(registry, targets, new NodeInfoBuilder(registry)));
        Register(new ClickHandler(registry, targets));
        Register(new ClickPointHandler(registry, targets));
        Register(new SendKeysHandler(registry, targets));
        Register(new SetTextHandler(registry, targets));
        Register(new FocusHandler(registry, targets));
        Register(new WaitForHandler(engine));
        var screenshots = new ScreenshotHandler(registry, targets);
        Register(screenshots);
        Register(new WaitUntilHandler(stateProvider, screenshots));
        Register(new GetSimTimeHandler(stateProvider));
        Register(new ListAppToolsHandler(toolsProvider));
        Register(new CallAppToolHandler(toolsProvider));
    }

    private void Register(IRequestHandler handler) => _handlers[handler.Method] = handler;

    /// <summary>
    /// Answers <paramref name="json"/>. A cancellation of <paramref name="cancellationToken"/> (the client disconnected or the
    /// host stopped) is the one failure not answered: the <see cref="OperationCanceledException"/> reaches the caller, since
    /// there is no one to answer.
    /// </summary>
    public async Task<string> Dispatch(string json, CancellationToken cancellationToken)
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

        return await Invoke(handler, request, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="handler"/> and answers with its result or error, carrying in <c>clientErrors</c> the errors the client
    /// logged from the call's start until its answer was ready (at most <see cref="AutomationResponse.MaxClientErrors"/>), including the handler
    /// failure's own. The capture is by time window, not by caller: an error another thread logs while the call runs comes back
    /// with it too, and every error past the cap or dropped by the log's ring is counted in <c>clientErrorsOmitted</c>. A
    /// cancelled call is not answered, so it carries nothing.
    /// </summary>
    private static async Task<string> Invoke(IRequestHandler handler, AutomationRequest request, string id, CancellationToken cancellationToken)
    {
        long start = AppLog.RecentErrors.LastSequence;
        AutomationResponse response = await Answer(handler, request, id, cancellationToken).ConfigureAwait(false);
        long end = AppLog.RecentErrors.LastSequence;
        List<ClientLogEntry>? errors = ClientErrorsSince(start);
        int omitted = (int)((end - start) - (errors?.Count ?? 0));
        return ProtocolSerializer.Serialize(response with { ClientErrors = errors, ClientErrorsOmitted = omitted });
    }

    private static async Task<AutomationResponse> Answer(
        IRequestHandler handler,
        AutomationRequest request,
        string id,
        CancellationToken cancellationToken
    )
    {
        try
        {
            object result = await handler.Handle(request, cancellationToken).ConfigureAwait(false);
            if (result is HandlerErrorResult error)
            {
                return AutomationResponse.Failure(id, error.Error);
            }

            return AutomationResponse.Success(id, ProtocolSerializer.ToElement(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.LogDebug("Automation method {Method} (request {Id}) cancelled", handler.Method, id);
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Automation method {Method} (request {Id}) failed", handler.Method, id);
            return AutomationResponse.Failure(
                id,
                new AutomationError(ex.Message, AutomationErrorCodes.Internal, "See the client log for the stack trace.", null)
            );
        }
    }

    private static List<ClientLogEntry>? ClientErrorsSince(long sequence)
    {
        IReadOnlyList<RecentErrorEntry> entries = AppLog.RecentErrors.Since(sequence, AutomationResponse.MaxClientErrors);
        if (entries.Count == 0)
        {
            return null;
        }

        return [.. entries.Select(entry => new ClientLogEntry(entry.Level.ToString(), entry.Category, entry.Message, entry.Exception))];
    }

    private static string Fail(string id, string code, string message, string? hint) =>
        ProtocolSerializer.Serialize(AutomationResponse.Failure(id, new AutomationError(message, code, hint, null)));
}
