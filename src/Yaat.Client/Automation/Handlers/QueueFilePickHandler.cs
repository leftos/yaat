using System.Text.Json;
using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>queue_file_pick</c>: adds one answer to <see cref="FilePickQueue"/> for the next file dialog the client opens in
/// automation mode — a <c>path</c>, or a <c>cancel</c>. The result is the queue's length after the enqueue, so the caller
/// can tell how many picks are still waiting. Nothing is validated beyond the params: a path that does not exist is the
/// call site's problem, not the queue's.
/// </summary>
public sealed class QueueFilePickHandler : IRequestHandler
{
    public string Method => ProtocolMethods.QueueFilePick;

    public Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(request.Params);
        if (objectError is not null)
        {
            return Task.FromResult<object>(objectError);
        }

        (string? path, HandlerErrorResult? pathError) = InputParams.ReadOptionalString(element, "path");
        if (pathError is not null)
        {
            return Task.FromResult<object>(pathError);
        }

        (bool? cancel, HandlerErrorResult? cancelError) = ReadCancel(element);
        if (cancelError is not null)
        {
            return Task.FromResult<object>(cancelError);
        }

        HandlerErrorResult? error = Validate(path, cancel);
        if (error is not null)
        {
            return Task.FromResult<object>(error);
        }

        FilePickAnswer answer = cancel == true ? FilePickAnswer.Cancelled : FilePickAnswer.ForPath(path!);
        return Task.FromResult<object>(new QueueFilePickResult(FilePickQueue.Enqueue(answer)));
    }

    /// <summary><c>cancel</c> given as a boolean, anything else an error; absent or JSON null is not given.</summary>
    private static (bool? Value, HandlerErrorResult? Error) ReadCancel(JsonElement element)
    {
        if (!element.TryGetProperty("cancel", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        (bool? Value, HandlerErrorResult? Error) parsed = value.ValueKind switch
        {
            JsonValueKind.True => (true, null),
            JsonValueKind.False => (false, null),
            _ => (null, HandlerResult.InvalidParam("cancel", "'cancel' must be true.")),
        };

        return parsed;
    }

    private static HandlerErrorResult? Validate(string? path, bool? cancel)
    {
        if ((path is not null) && (cancel is not null))
        {
            return HandlerResult.InvalidParam("cancel", "Give either 'path' or 'cancel', not both.");
        }

        if ((path is null) && (cancel is null))
        {
            return HandlerResult.InvalidParam("path", "Give 'path' or 'cancel'.");
        }

        if ((path is not null) && string.IsNullOrWhiteSpace(path))
        {
            return HandlerResult.InvalidParam("path", "'path' must not be blank.");
        }

        if (cancel is false)
        {
            return HandlerResult.InvalidParam("cancel", "'cancel' must be true, or give 'path' instead.");
        }

        return null;
    }
}
