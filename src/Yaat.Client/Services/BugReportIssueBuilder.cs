using System.Text;

namespace Yaat.Client.Services;

/// <summary>What the user typed into the File Bug Report dialog.</summary>
public sealed record BugReportForm(string Title, string WhatHappened, string Expected, string Callsigns);

/// <summary>Build facts the issue body reports, gathered by the caller when the report is filed.</summary>
public sealed record BugReportEnvironment(string YaatVersion, string BuildKind, string OperatingSystem, string? ScenarioName, bool InRoom);

/// <summary>
/// Builds the prefilled GitHub "new issue" URL for <b>Scenario → File Bug Report...</b>. GitHub's
/// new-issue URL cannot carry attachments, so the bundle the caller wrote is only named in the body —
/// the user drags it into the issue from the folder the file manager was pointed at. The body mirrors
/// <c>.github/ISSUE_TEMPLATE/bug_report.md</c>.
/// </summary>
public static class BugReportIssueBuilder
{
    /// <summary>Length ceiling the composed URL is held under; longer text is truncated with a marker.</summary>
    public const int MaxUrlLength = 8000;

    /// <summary>Ceiling on the title alone; a pasted wall of text is cut here before the body is measured.</summary>
    private const int MaxTitleLength = 200;

    private const string Marker = "\n\n…(truncated — please paste the rest)";
    private const string NotProvided = "_Not provided_";
    private const string NoBundle = "YAAT could not create the bundle — see the client log.";

    public static string BuildUrl(BugReportForm form, BugReportEnvironment env, string? attachmentFileName)
    {
        string title = TruncateTitle(form.Title.Trim());
        string happened = form.WhatHappened.Trim();
        string expected = form.Expected.Trim();
        string callsigns = form.Callsigns.Trim();
        string originalHappened = happened;
        string originalExpected = expected;

        string Compose(string whatHappened, string wanted, string involved) =>
            ComposeUrl(title, BuildBody(whatHappened, wanted, involved, env, attachmentFileName));

        string url = Compose(happened, expected, callsigns);
        if (url.Length <= MaxUrlLength)
        {
            return url;
        }

        // Shorten the longer free-text field first — that is the one that overran the ceiling — then
        // the other if the URL is still too long. Every cut is made on the unescaped text and the
        // shortened result is re-escaped, so a cut never lands inside an escape sequence.
        if (happened.Length >= expected.Length)
        {
            happened = WithMarker(LargestPrefixThatFits(originalHappened, prefix => Compose(WithMarker(prefix), originalExpected, callsigns)));
            if (Compose(happened, expected, callsigns).Length > MaxUrlLength)
            {
                expected = WithMarker(LargestPrefixThatFits(originalExpected, prefix => Compose(happened, WithMarker(prefix), callsigns)));
            }
        }
        else
        {
            expected = WithMarker(LargestPrefixThatFits(originalExpected, prefix => Compose(originalHappened, WithMarker(prefix), callsigns)));
            if (Compose(happened, expected, callsigns).Length > MaxUrlLength)
            {
                happened = WithMarker(LargestPrefixThatFits(originalHappened, prefix => Compose(WithMarker(prefix), expected, callsigns)));
            }
        }

        url = Compose(happened, expected, callsigns);
        if (url.Length <= MaxUrlLength)
        {
            return url;
        }

        // Last thing dropped is the optional callsign list, so the URL the browser gets is still one
        // GitHub accepts.
        return Compose(happened, expected, "");
    }

    private static string ComposeUrl(string title, string body) =>
        $"{DocLinks.Repo}/issues/new?labels=bug&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    private static string BuildBody(string whatHappened, string expected, string callsigns, BugReportEnvironment env, string? attachmentFileName)
    {
        string scenario = string.IsNullOrWhiteSpace(env.ScenarioName) ? "none" : env.ScenarioName;

        var body = new StringBuilder();
        body.Append("**Describe the bug**\n").Append(whatHappened).Append("\n\n");
        body.Append("**Expected behavior**\n").Append(expected.Length == 0 ? NotProvided : expected).Append("\n\n");
        if (callsigns.Length > 0)
        {
            body.Append("**Callsigns involved**\n").Append(callsigns).Append("\n\n");
        }

        string bundle = attachmentFileName is { Length: > 0 } name ? $"Drag `{name}` from the folder YAAT just opened into this issue." : NoBundle;
        body.Append("**Bug report bundle**\n").Append(bundle).Append("\n\n");

        body.Append("**Environment**\n");
        body.Append("- YAAT: ").Append(env.YaatVersion).Append(" (").Append(env.BuildKind).Append(")\n");
        body.Append("- OS: ").Append(env.OperatingSystem).Append('\n');
        body.Append("- Scenario: ").Append(scenario).Append('\n');
        body.Append("- In a room: ").Append(env.InRoom ? "yes" : "no");
        return body.ToString();
    }

    /// <summary>
    /// Caps the title at <see cref="MaxTitleLength"/> characters, cutting before a surrogate pair rather
    /// than through it.
    /// </summary>
    private static string TruncateTitle(string title)
    {
        if (title.Length <= MaxTitleLength)
        {
            return title;
        }

        int cut = char.IsHighSurrogate(title[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return title[..cut];
    }

    /// <summary>
    /// Longest prefix of <paramref name="text"/> whose composed URL fits, found by binary search.
    /// Returns the empty string when not even the empty prefix fits.
    /// </summary>
    private static string LargestPrefixThatFits(string text, Func<string, string> composeWithPrefix)
    {
        int low = 0;
        int high = text.Length;
        int best = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;

            // A prefix that ends between the halves of a surrogate pair escapes to a longer string than
            // the pair itself, so that case probes the shorter prefix — but the search must still advance
            // from the midpoint it picked: advancing from the probe would leave low where it already was
            // and the loop would never end.
            int probe = mid > 0 && mid < text.Length && char.IsHighSurrogate(text[mid - 1]) ? mid - 1 : mid;
            if (composeWithPrefix(text[..probe]).Length <= MaxUrlLength)
            {
                best = probe;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best <= 0 ? "" : text[..best];
    }

    private static string WithMarker(string text) => text.Length == 0 ? Marker.Trim() : text + Marker;
}
