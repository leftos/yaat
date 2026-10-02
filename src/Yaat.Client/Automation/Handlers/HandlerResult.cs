// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Globalization;
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
            new NodeErrorDetails(nodeId)
        );

    /// <summary>A stale-node error whose message is <paramref name="reason"/>, e.g. <see cref="Tree.NodeRegistry.ResolveChecked"/>'s.</summary>
    public static HandlerErrorResult StaleNode(int nodeId, string reason) =>
        Error(AutomationErrorCodes.StaleNode, reason, RefreshNodeIdsHint, new NodeErrorDetails(nodeId));

    public static HandlerErrorResult InvalidParam(string paramName, string message) =>
        Error(AutomationErrorCodes.InvalidParam, message, $"Provide a valid value for '{paramName}'.", new ParamErrorDetails(paramName));

    public static HandlerErrorResult Unsupported(string operation, string elementType) =>
        Error(
            AutomationErrorCodes.UnsupportedOperation,
            $"Operation '{operation}' is not supported on element type '{elementType}'.",
            null,
            new UnsupportedOperationDetails(operation, elementType)
        );

    /// <summary>An <c>ELEMENT_DISABLED</c> error for an element that is disabled itself or through an ancestor.</summary>
    public static HandlerErrorResult ElementDisabled(int nodeId, string elementType) => ElementDisabled(nodeId, elementType, "It is disabled.");

    /// <summary>An <c>ELEMENT_DISABLED</c> error; <paramref name="reason"/> ends the message, e.g. "Its command cannot execute.".</summary>
    public static HandlerErrorResult ElementDisabled(int nodeId, string elementType, string reason) =>
        Error(
            AutomationErrorCodes.ElementDisabled,
            $"Element '{elementType}' cannot take this input. {reason}",
            "Wait until the element, its command and its ancestors are enabled, visible and editable (check isEnabled with get_tree), "
                + "then try again.",
            new ElementErrorDetails(nodeId, elementType)
        );

    public static HandlerErrorResult NotFocusable(int nodeId, string elementType) =>
        Error(
            AutomationErrorCodes.NotFocusable,
            $"Element '{elementType}' cannot take the keyboard focus (not focusable, disabled, hidden, or it refused the focus).",
            "Target a focusable, enabled and visible element such as a TextBox, or click the element instead.",
            new ElementErrorDetails(nodeId, elementType)
        );

    public static HandlerErrorResult OutOfBounds(double x, double y, double width, double height) =>
        Error(
            AutomationErrorCodes.OutOfBounds,
            string.Create(CultureInfo.InvariantCulture, $"Point ({x}, {y}) is outside the window's {width} x {height} client area."),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Give window-relative DIPs with x from 0 and below {width}, and y from 0 and below {height}."
            ),
            new PointErrorDetails(x, y, width, height)
        );

    /// <summary>An <c>OUT_OF_BOUNDS</c> error for an element with no area to capture.</summary>
    public static HandlerErrorResult ZeroSize(int nodeId, string elementType, double width, double height) =>
        Error(
            AutomationErrorCodes.OutOfBounds,
            string.Create(CultureInfo.InvariantCulture, $"Element '{elementType}' is {width} x {height} DIPs and has no area to capture."),
            "Capture an element that is laid out with a non-zero size, or its window.",
            new SizeErrorDetails(nodeId, elementType, width, height)
        );

    public static HandlerErrorResult MissingSelector() =>
        Error(AutomationErrorCodes.MissingSelector, "Selector is required.", "Provide the selector parameter.", new ParamErrorDetails("selector"));

    public static HandlerErrorResult InvalidSelector(string selector, string message, int position) =>
        Error(
            AutomationErrorCodes.InvalidSelector,
            message,
            "Check the selector syntax (e.g. Type, #name, #42, [Property=Value], :nth(N), A > B).",
            new InvalidSelectorDetails(selector, position)
        );

    public static HandlerErrorResult NoMatch(string selector) =>
        Error(
            AutomationErrorCodes.NoMatch,
            $"No element matched selector '{selector}'.",
            "Check the selector, or call get_tree to inspect the available elements.",
            new SelectorErrorDetails(selector)
        );

    public static HandlerErrorResult AmbiguousSelector(string selector, int matchCount) =>
        Error(
            AutomationErrorCodes.AmbiguousSelector,
            $"Selector '{selector}' matched {matchCount} elements.",
            $"Use :nth(0) through :nth({matchCount - 1}), or narrow the selector with #name or [Property=Value].",
            new AmbiguousSelectorDetails(selector, matchCount)
        );
}
