// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>One line of the automation pipe protocol sent by a client: <c>{"id": "...", "method": "...", "params": {...}}</c>.</summary>
public sealed class AutomationRequest
{
    /// <summary>The method to run; null when the line omits it (answered as a coded error).</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    /// <summary>The request id the response echoes; null when the line omits it (answered as a coded error).</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
