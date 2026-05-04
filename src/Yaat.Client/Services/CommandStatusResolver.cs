using System.Diagnostics;

namespace Yaat.Client.Services;

/// <summary>
/// Resolves the status-bar text after a command roundtrip. Success clears any prior
/// error; failure surfaces the server's message (or a diagnostic placeholder when
/// the server returned a failure with no message — that path is a server-side bug,
/// caught in DEBUG via <see cref="Debug.Fail(string)"/>).
/// </summary>
internal static class CommandStatusResolver
{
    public static string Resolve(CommandResultDto result, string contextLabel)
    {
        if (result.Success)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return result.Message;
        }

        Debug.Fail($"Command rejected with empty Message — server failure path missing reason text (context={contextLabel})");
        return $"Command rejected with no reason supplied (context: {contextLabel}) — please file an issue";
    }
}
