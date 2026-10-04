using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>One error the client logged while a request ran, as an answer's <c>clientErrors</c> carries it.</summary>
/// <param name="Level">The log level's name: <c>Error</c> or <c>Critical</c>.</param>
/// <param name="Category">The logger category, e.g. the class that logged it.</param>
/// <param name="Message">The formatted log message.</param>
/// <param name="Exception">The exception's type and message on one line, or null when none was logged.</param>
public sealed record ClientLogEntry(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("exception")] string? Exception
);
