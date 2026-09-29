using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class BugReportIssueBuilderTests
{
    private const string UrlPrefix = "https://github.com/leftos/yaat/issues/new?labels=bug&title=";

    private static readonly BugReportEnvironment Env = new(
        YaatVersion: "0.1.1-alpha",
        BuildKind: "dev build",
        OperatingSystem: "Microsoft Windows 11 Pro 10.0.26200",
        ScenarioName: "OAK Tower",
        InRoom: true
    );

    private static BugReportForm Form(
        string whatHappened = "N12345 vanished from the scope.",
        string expected = "It should stay visible.",
        string callsigns = ""
    ) => new("Aircraft disappears", whatHappened, expected, callsigns);

    private static string QueryValue(string url, string key)
    {
        string marker = $"{key}=";
        int start = url.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"query value '{key}' missing from {url}");
        string rest = url[(start + marker.Length)..];
        int amp = rest.IndexOf('&', StringComparison.Ordinal);
        return Uri.UnescapeDataString(amp < 0 ? rest : rest[..amp]);
    }

    /// <summary>A cut inside an astral character would leave a high surrogate with no low one after it.</summary>
    private static void AssertNoLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), $"lone high surrogate at index {i}");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), $"lone low surrogate at index {i}");
            }
        }
    }

    [Fact]
    public void BuildUrl_StartsWithGitHubNewIssueQuery()
    {
        string url = BugReportIssueBuilder.BuildUrl(Form(), Env, "20260101-120000-OAK Tower.yaat-bug-report-bundle.zip");

        Assert.StartsWith(UrlPrefix, url);
        Assert.Equal("Aircraft disappears", QueryValue(url, "title"));
    }

    [Fact]
    public void BuildUrl_TitleAndBodyRoundTripSpecialCharacters()
    {
        const string whatHappened = "Ü & # ? in a title\nsecond line\twith a tab";
        const string expected = "100% of the time — never <crash>";

        string url = BugReportIssueBuilder.BuildUrl(
            new BugReportForm(whatHappened, whatHappened, expected, ""),
            Env,
            "20260101-120000-session.yaat-bug-report-bundle.zip"
        );

        Assert.Equal(whatHappened, QueryValue(url, "title"));
        string body = QueryValue(url, "body");
        Assert.Contains(whatHappened, body);
        Assert.Contains(expected, body);
    }

    [Fact]
    public void BuildUrl_BodyCarriesTheFormTheBundleNameAndTheEnvironment()
    {
        string url = BugReportIssueBuilder.BuildUrl(Form(callsigns: "UAL123 SWA456"), Env, "20260101-120000-OAK Tower.yaat-bug-report-bundle.zip");
        string body = QueryValue(url, "body");

        Assert.Contains("N12345 vanished from the scope.", body);
        Assert.Contains("It should stay visible.", body);
        Assert.Contains("UAL123 SWA456", body);
        Assert.Contains("20260101-120000-OAK Tower.yaat-bug-report-bundle.zip", body);
        Assert.Contains("0.1.1-alpha", body);
        Assert.Contains("dev build", body);
        Assert.Contains("Microsoft Windows 11 Pro 10.0.26200", body);
        Assert.Contains("OAK Tower", body);
        Assert.Contains("In a room: yes", body);
    }

    [Fact]
    public void BuildUrl_OutsideARoom_SaysTheEnvironmentIsNotInARoom()
    {
        BugReportEnvironment env = Env with { InRoom = false, ScenarioName = null };

        string body = QueryValue(BugReportIssueBuilder.BuildUrl(Form(), env, null), "body");

        Assert.Contains("In a room: no", body);
        Assert.Contains("Scenario: none", body);
    }

    [Fact]
    public void BuildUrl_BlankCallsigns_OmitsTheSection()
    {
        string body = QueryValue(BugReportIssueBuilder.BuildUrl(Form(callsigns: "   "), Env, "bundle.zip"), "body");

        Assert.DoesNotContain("Callsigns involved", body);
    }

    [Fact]
    public void BuildUrl_CallsignsPresent_IncludeTheSection()
    {
        string body = QueryValue(BugReportIssueBuilder.BuildUrl(Form(callsigns: "UAL123"), Env, "bundle.zip"), "body");

        Assert.Contains("Callsigns involved", body);
    }

    [Fact]
    public void BuildUrl_BlankExpected_ReportsNotProvided()
    {
        string body = QueryValue(BugReportIssueBuilder.BuildUrl(Form(expected: "  "), Env, "bundle.zip"), "body");

        Assert.Contains("_Not provided_", body);
    }

    [Fact]
    public void BuildUrl_NoAttachment_SaysTheBundleCouldNotBeCreated()
    {
        string body = QueryValue(BugReportIssueBuilder.BuildUrl(Form(), Env, null), "body");

        Assert.Contains("YAAT could not create the bundle", body);
        Assert.DoesNotContain(".zip", body);
    }

    [Fact]
    public void BuildUrl_HugeDescription_TruncatesToTheUrlCeilingAndMarksIt()
    {
        string whatHappened = new('x', 20_000);

        string url = BugReportIssueBuilder.BuildUrl(Form(whatHappened: whatHappened), Env, "bundle.zip");

        Assert.True(url.Length <= BugReportIssueBuilder.MaxUrlLength, $"URL was {url.Length} characters");
        string body = QueryValue(url, "body");
        Assert.StartsWith("**Describe the bug**", body);
        Assert.Contains("(truncated", body);
        Assert.Contains("It should stay visible.", body);
    }

    [Fact]
    public void BuildUrl_HugeDescriptionOfMultiByteCharacters_NeverSplitsAnEscapeSequence()
    {
        string whatHappened = string.Concat(Enumerable.Repeat("ü", 20_000));

        string url = BugReportIssueBuilder.BuildUrl(Form(whatHappened: whatHappened, expected: string.Empty), Env, "bundle.zip");
        string body = QueryValue(url, "body");

        Assert.True(url.Length <= BugReportIssueBuilder.MaxUrlLength, $"URL was {url.Length} characters");
        // A cut inside an escape sequence would survive UnescapeDataString as a literal "%xx",
        // so a decoded body with no "%" proves the cut landed between characters.
        Assert.DoesNotContain("%", body);
        Assert.Contains("(truncated", body);
    }

    [Fact]
    public async Task BuildUrl_HugeDescriptionOfAstralCharacters_FinishesAndLeavesNoHalfACodePoint()
    {
        string whatHappened = string.Concat(Enumerable.Repeat("😀", 20_000));
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task<string> build = Task.Run(
            () => BugReportIssueBuilder.BuildUrl(Form(whatHappened: whatHappened, expected: string.Empty), Env, "bundle.zip"),
            ct
        );
        string url = await build.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.True(url.Length <= BugReportIssueBuilder.MaxUrlLength, $"URL was {url.Length} characters");
        AssertNoLoneSurrogate(QueryValue(url, "body"));
    }

    [Fact]
    public void BuildUrl_HugeTitle_CapsTheTitleAtTwoHundredCharacters()
    {
        string title = new('t', 5_000);

        string url = BugReportIssueBuilder.BuildUrl(new BugReportForm(title, "N12345 vanished from the scope.", "", ""), Env, "bundle.zip");

        Assert.True(url.Length <= BugReportIssueBuilder.MaxUrlLength, $"URL was {url.Length} characters");
        Assert.Equal(200, QueryValue(url, "title").Length);
    }

    [Fact]
    public void BuildUrl_HugeExpected_TruncatesTheExpectationToo()
    {
        string expected = new('y', 20_000);

        string url = BugReportIssueBuilder.BuildUrl(Form(expected: expected), Env, "bundle.zip");

        Assert.True(url.Length <= BugReportIssueBuilder.MaxUrlLength, $"URL was {url.Length} characters");
        string body = QueryValue(url, "body");
        Assert.StartsWith("**Describe the bug**", body);
        Assert.Contains("N12345 vanished from the scope.", body);
        Assert.Contains("In a room: yes", body);
    }
}
