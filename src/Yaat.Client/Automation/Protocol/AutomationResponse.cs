// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// One line of the automation pipe protocol sent back by the host: the request's <c>id</c> with either a
/// <c>result</c> or an <c>errorInfo</c>.
/// </summary>
public sealed record AutomationResponse
{
    /// <summary>
    /// The most client errors one answer lists in <see cref="ClientErrors"/>. Every error the request logged beyond that — and
    /// any the error log's ring had already dropped — is counted in <see cref="ClientErrorsOmitted"/> instead of listed.
    /// </summary>
    public const int MaxClientErrors = 20;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    /// <summary>Structured error payload with a stable machine code and an optional recovery hint.</summary>
    [JsonPropertyName("errorInfo")]
    public AutomationError? ErrorInfo { get; init; }

    /// <summary>
    /// The errors the client logged while the request ran, oldest first and at most <see cref="MaxClientErrors"/>; null, and
    /// left out of the line, when there were none. Carried on a result and an error alike.
    /// </summary>
    [JsonPropertyName("clientErrors")]
    public IReadOnlyList<ClientLogEntry>? ClientErrors { get; init; }

    /// <summary>
    /// How many of the errors logged while the request ran the answer counts but does not list in <see cref="ClientErrors"/>:
    /// the ones past <see cref="MaxClientErrors"/>, and any the error log's ring dropped before the answer was built. Zero when
    /// every error the request logged is listed, and left out of the line then.
    /// </summary>
    [JsonPropertyName("clientErrorsOmitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ClientErrorsOmitted { get; init; }

    public static AutomationResponse Success(string id, JsonElement result) => new() { Id = id, Result = result };

    public static AutomationResponse Failure(string id, AutomationError error) => new() { Id = id, ErrorInfo = error };
}
