namespace Yaat.Client.Services;

/// <summary>What executing a CRC alias resolved to.</summary>
public enum CrcAliasAction
{
    /// <summary>Print <see cref="CrcAliasExecution.EchoLines" /> to the terminal.</summary>
    Echo,

    /// <summary>Run <see cref="CrcAliasExecution.CommandText" /> through YAAT's scope-marker handler.</summary>
    ScopeMarkers,

    /// <summary>Open <see cref="CrcAliasExecution.Url" /> in the default browser.</summary>
    OpenUrl,

    /// <summary>The alias resolved to something YAAT has no equivalent for; show <see cref="CrcAliasExecution.Message" />.</summary>
    Unsupported,

    /// <summary>The alias was malformed; show <see cref="CrcAliasExecution.Message" />.</summary>
    Failed,
}

/// <summary>Outcome of planning a CRC alias — the caller performs the effect.</summary>
public sealed record CrcAliasExecution
{
    public required CrcAliasAction Action { get; init; }

    public IReadOnlyList<string> EchoLines { get; init; } = [];

    public string CommandText { get; init; } = "";

    public string Url { get; init; } = "";

    public string Message { get; init; } = "";

    public static CrcAliasExecution Echo(IReadOnlyList<string> lines) => new() { Action = CrcAliasAction.Echo, EchoLines = lines };

    public static CrcAliasExecution ScopeMarkers(string commandText) => new() { Action = CrcAliasAction.ScopeMarkers, CommandText = commandText };

    public static CrcAliasExecution OpenUrl(string url) => new() { Action = CrcAliasAction.OpenUrl, Url = url };

    public static CrcAliasExecution Unsupported(string message) => new() { Action = CrcAliasAction.Unsupported, Message = message };

    public static CrcAliasExecution Failed(string message) => new() { Action = CrcAliasAction.Failed, Message = message };
}

/// <summary>
/// Decides what an expanded CRC alias should do in YAAT.
/// </summary>
/// <remarks>
/// YAAT executes the three CRC verbs that have a real equivalent here — <c>.echo</c>, the
/// <c>.ff</c>/<c>.marker</c>/<c>.markers</c>/<c>.nomarkers</c> family, and <c>.openurl</c>. Everything else
/// (<c>.am</c>, <c>.msg</c>, <c>.autotrack</c>, <c>.wallop</c>) depends on a live VATSIM network or a
/// flight-plan route amendment YAAT doesn't have, and is reported rather than silently ignored.
///
/// A body with no leading dot is a radio transmission to a text pilot in CRC. YAAT has no text pilots, so
/// those are reported as unsupported too.
/// </remarks>
public static class CrcAliasExecutor
{
    /// <summary>
    /// Substitutes variables into the already alias-expanded text, then resolves the leading verb.
    /// Variables are substituted before the verb is read, matching CRC's ordering.
    /// </summary>
    public static CrcAliasExecution Plan(string expandedText, CrcAliasContext context)
    {
        var text = CrcAliasVariables.Substitute(expandedText, context).Trim();
        if (text.Length == 0)
        {
            return CrcAliasExecution.Failed("Alias expanded to nothing");
        }

        var separator = text.IndexOf(' ', StringComparison.Ordinal);
        var verb = separator < 0 ? text : text[..separator];
        var body = separator < 0 ? "" : text[(separator + 1)..].Trim();

        if (!verb.StartsWith('.'))
        {
            return CrcAliasExecution.Unsupported("Alias expands to a radio transmission, which YAAT does not support");
        }

        switch (verb.ToLowerInvariant())
        {
            case ".echo":
                return body.Length == 0 ? CrcAliasExecution.Failed("\".echo\" needs text to print") : CrcAliasExecution.Echo(SplitEchoLines(body));
            case ".ff":
            case ".marker":
            case ".markers":
            case ".nomarkers":
                return CrcAliasExecution.ScopeMarkers(text);
            case ".openurl":
                return PlanOpenUrl(body);
            default:
                return CrcAliasExecution.Unsupported($"\"{verb}\" is not supported in YAAT");
        }
    }

    /// <summary>
    /// Applies CRC's <c>.echo</c> escapes and splits the result into terminal lines. Alias files are
    /// single-line, so <c>\n</c> is the only way to write the multi-line reference cards controllers
    /// build (position-relief checklists, phraseology cards); <c>\s</c> and <c>\t</c> restore the leading
    /// whitespace that tokenizing the alias body would otherwise collapse.
    /// </summary>
    public static List<string> SplitEchoLines(string body)
    {
        var text = body.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "    ", StringComparison.Ordinal)
            .Replace("\\s", " ", StringComparison.Ordinal);

        return [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
    }

    private static CrcAliasExecution PlanOpenUrl(string body)
    {
        // CRC reads only the first token, which is why aliases wrap a spaced value in $urlescape(...).
        var url = CrcAliasFileParser.Tokenize(body).FirstOrDefault();
        if (string.IsNullOrEmpty(url))
        {
            return CrcAliasExecution.Failed("\".openurl\" needs a URL");
        }

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = $"http://{url}";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return CrcAliasExecution.Failed($"\".openurl\" refused a non-web URL: {url}");
        }

        // AbsoluteUri, not ToString() — ToString() un-escapes, which would undo the $urlescape(...) that
        // aliases wrap spaced values in and hand the browser a URL with raw spaces in it.
        return CrcAliasExecution.OpenUrl(parsed.AbsoluteUri);
    }
}
