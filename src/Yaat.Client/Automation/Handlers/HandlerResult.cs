// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// Returned by a handler to signal a structured error: the dispatcher answers it as
/// <see cref="AutomationResponse.ErrorInfo"/> instead of serialising it as a result.
/// </summary>
public sealed class HandlerErrorResult(AutomationError error)
{
    public AutomationError Error { get; } = error;
}

/// <summary>Builds handler error results with the standard <see cref="AutomationErrorCodes"/>.</summary>
public static class HandlerResult
{
    private const string RefreshNodeIdsHint = "Call list_windows or get_tree to refresh node IDs.";

    public static HandlerErrorResult Error(string code, string message, string? suggested, object? details) =>
        new(new AutomationError(message, code, suggested, details));

    public static HandlerErrorResult StaleNode(int nodeId) =>
        Error(
            AutomationErrorCodes.StaleNode,
            $"Node {nodeId} not found (may have been garbage collected or detached from the visual tree).",
            RefreshNodeIdsHint,
            new { nodeId }
        );

    public static HandlerErrorResult StaleNode(int nodeId, string reason) =>
        Error(AutomationErrorCodes.StaleNode, $"Node {nodeId}: {reason}", RefreshNodeIdsHint, new { nodeId });

    public static HandlerErrorResult InvalidParam(string paramName, string message) =>
        Error(AutomationErrorCodes.InvalidParam, message, $"Provide a valid value for '{paramName}'.", new { param = paramName });

    public static HandlerErrorResult Unsupported(string operation, string elementType) =>
        Error(
            AutomationErrorCodes.UnsupportedOperation,
            $"Operation '{operation}' is not supported on element type '{elementType}'.",
            null,
            new { operation, elementType }
        );
}
