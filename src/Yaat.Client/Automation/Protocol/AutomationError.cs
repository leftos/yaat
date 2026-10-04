// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>Structured error payload returned in <see cref="AutomationResponse.ErrorInfo"/>.</summary>
/// <param name="Message">Human-readable error text.</param>
/// <param name="Code">Stable machine-readable code from <see cref="AutomationErrorCodes"/>.</param>
/// <param name="Suggested">Recovery hint such as "call list_windows to refresh node IDs", or null when there is none.</param>
/// <param name="Details">Structured context (e.g. <c>{ "param": "selector" }</c>), or null when there is none.</param>
public sealed record AutomationError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("suggested")] string? Suggested,
    [property: JsonPropertyName("details")] object? Details
);
