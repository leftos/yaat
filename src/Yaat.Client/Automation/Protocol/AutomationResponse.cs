// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// One line of the automation pipe protocol sent back by the host: the request's <c>id</c> with either a
/// <c>result</c> or an <c>errorInfo</c>.
/// </summary>
public sealed class AutomationResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    /// <summary>Structured error payload with a stable machine code and an optional recovery hint.</summary>
    [JsonPropertyName("errorInfo")]
    public AutomationError? ErrorInfo { get; init; }

    public static AutomationResponse Success(string id, JsonElement result) => new() { Id = id, Result = result };

    public static AutomationResponse Failure(string id, AutomationError error) => new() { Id = id, ErrorInfo = error };
}
