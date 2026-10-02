namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// A coded error the automation host answered a request with (the client's <c>AutomationError</c>): the stable machine
/// code, the host's message and its recovery hint. <see cref="Exception.Message"/> combines the code and the hint;
/// <see cref="RemoteMessage"/> holds the host's own text.
/// </summary>
/// <param name="code">The stable machine-readable code, e.g. <c>INVALID_PARAM</c>.</param>
/// <param name="message">The host's human-readable error text.</param>
/// <param name="suggested">The host's recovery hint, or null when it gave none.</param>
public sealed class PipeRemoteException(string code, string message, string? suggested) : Exception(BuildMessage(code, message, suggested))
{
    /// <summary>The stable machine-readable code the host reported.</summary>
    public string Code { get; } = code;

    /// <summary>The host's own error text, without this exception's code and hint prefix.</summary>
    public string RemoteMessage { get; } = message;

    /// <summary>The host's recovery hint, or null when it gave none.</summary>
    public string? Suggested { get; } = suggested;

    private static string BuildMessage(string code, string message, string? suggested) =>
        string.IsNullOrEmpty(suggested) ? $"{code}: {message}" : $"{code}: {message} Hint: {suggested}";
}
