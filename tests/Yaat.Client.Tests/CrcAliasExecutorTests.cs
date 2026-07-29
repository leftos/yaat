using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CrcAliasExecutorTests
{
    private static readonly CrcAliasContext Oakland = new("KOAK", "KJFK", "SUNOL Q126 ALTAM");

    private static CrcAliasExecution Plan(string expanded) => CrcAliasExecutor.Plan(expanded, Oakland);

    [Fact]
    public void Echo_PrintsItsBody()
    {
        var execution = Plan(".echo DESIGNATOR: C172 | RECAT: I");

        Assert.Equal(CrcAliasAction.Echo, execution.Action);
        Assert.Equal(["DESIGNATOR: C172 | RECAT: I"], execution.EchoLines);
    }

    /// <summary>
    /// Alias files are single-line, so <c>\n</c> is the only way to author the multi-line reference cards
    /// controllers build, and <c>\s</c>/<c>\t</c> restore whitespace that tokenizing would collapse.
    /// </summary>
    [Fact]
    public void Echo_ExpandsEscapesIntoSeparateLines()
    {
        var execution = Plan(@".echo TITLE\n\s\sindented\n\ttabbed");

        Assert.Equal(["TITLE", "  indented", "    tabbed"], execution.EchoLines);
    }

    [Fact]
    public void Echo_WithNoBodyFails()
    {
        Assert.Equal(CrcAliasAction.Failed, Plan(".echo").Action);
    }

    [Theory]
    [InlineData(".ff")]
    [InlineData(".FF")]
    [InlineData(".marker")]
    [InlineData(".markers")]
    [InlineData(".nomarkers")]
    public void MarkerVerbs_RouteToTheScopeMarkerHandler(string verb)
    {
        var execution = Plan($"{verb} SUNOL ALTAM");

        Assert.Equal(CrcAliasAction.ScopeMarkers, execution.Action);
        Assert.Equal($"{verb} SUNOL ALTAM", execution.CommandText);
    }

    [Fact]
    public void OpenUrl_ReturnsTheUrl()
    {
        var execution = Plan(".openurl https://reference.oakartcc.org");

        Assert.Equal(CrcAliasAction.OpenUrl, execution.Action);
        Assert.Equal("https://reference.oakartcc.org/", execution.Url);
    }

    [Fact]
    public void OpenUrl_SubstitutesFlightPlanVariables()
    {
        var execution = Plan(".openurl https://reference.oakartcc.org/routes?dep=$dep&dest=$arr");

        Assert.Equal(CrcAliasAction.OpenUrl, execution.Action);
        Assert.Contains("dep=KOAK&dest=KJFK", execution.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of an alias wrapping a spaced value in <c>$urlescape(...)</c> is that the browser
    /// receives it encoded. <see cref="Uri.ToString" /> un-escapes, so the percent-encoding must survive.
    /// </summary>
    [Fact]
    public void OpenUrl_KeepsPercentEncodingFromUrlEscape()
    {
        var execution = Plan(".openurl https://skyvector.com/?fpl=$urlescape($fullroute)");

        Assert.Equal(CrcAliasAction.OpenUrl, execution.Action);
        Assert.Equal("https://skyvector.com/?fpl=KOAK%20SUNOL%20Q126%20ALTAM%20KJFK", execution.Url);
    }

    [Fact]
    public void OpenUrl_PrependsHttpWhenSchemeless()
    {
        Assert.Equal("http://example.test/", Plan(".openurl example.test").Url);
    }

    [Fact]
    public void OpenUrl_RefusesNonWebSchemes()
    {
        Assert.Equal(CrcAliasAction.Failed, Plan(".openurl file:///C:/Windows/System32").Action);
    }

    [Fact]
    public void OpenUrl_WithNoArgumentFails()
    {
        Assert.Equal(CrcAliasAction.Failed, Plan(".openurl").Action);
    }

    [Theory]
    [InlineData(".am rte +..OAL.J92.BTY..+")]
    [InlineData(".msg UAL123 hello")]
    [InlineData(".autotrack KOAK")]
    [InlineData(".wallop pilot is unresponsive")]
    public void VerbsWithoutAYaatEquivalent_AreReportedAsUnsupported(string expanded)
    {
        var execution = Plan(expanded);

        Assert.Equal(CrcAliasAction.Unsupported, execution.Action);
        Assert.Contains("not supported", execution.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CRC transmits a verb-less body to a text pilot; YAAT has none.</summary>
    [Fact]
    public void ProseBody_IsReportedAsUnsupported()
    {
        var execution = Plan("Hold for release, remain this frequency");

        Assert.Equal(CrcAliasAction.Unsupported, execution.Action);
        Assert.Contains("radio transmission", execution.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyExpansion_Fails()
    {
        Assert.Equal(CrcAliasAction.Failed, Plan("   ").Action);
    }
}
