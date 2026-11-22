using Xunit;
using Yaat.Sim.Authoring.Routes;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="PreferredRouteParser"/> reads the FAA NASR <c>PFR_BASE.csv</c>. These run it over the committed real extract
/// (TestData/PreferredRoutes) and over real rows edited to be malformed or of an unknown type.
/// </summary>
public class PreferredRouteParserTests
{
    private const string Header =
        "\"EFF_DATE\",\"ORIGIN_ID\",\"ORIGIN_CITY\",\"ORIGIN_STATE_CODE\",\"ORIGIN_COUNTRY_CODE\",\"DSTN_ID\",\"DSTN_CITY\","
        + "\"DSTN_STATE_CODE\",\"DSTN_COUNTRY_CODE\",\"PFR_TYPE_CODE\",\"ROUTE_NO\",\"SPECIAL_AREA_DESCRIP\",\"ALT_DESCRIP\",\"AIRCRAFT\","
        + "\"HOURS\",\"ROUTE_DIR_DESCRIP\",\"DESIGNATOR\",\"NAR_TYPE\",\"INLAND_FAC_FIX\",\"COASTAL_FIX\",\"DESTINATION\",\"ROUTE_STRING\"";

    private const string SfoDenH1 =
        "\"2026/10/29\",\"SFO\",\"SAN FRANCISCO\",\"CA\",\"US\",\"DEN\",\"DENVER\",\"CO\",\"US\",\"H\",1,\"\",\"\",\"\",\"1400-0000\",\"\","
        + "\"\",\"\",\"\",\"\",\"\",\"TIPRE Q126 INSLO LBERT LONGZ4\"";

    private const string SfoSmfTec1 =
        "\"2026/10/29\",\"SFO\",\"SAN FRANCISCO\",\"CA\",\"US\",\"SMF\",\"SACRAMENTO\",\"CA\",\"US\",\"TEC\",1,\"SFO HAF TO SMF\",\"J30\","
        + "\"\",\"\",\"SFOW\",\"SFO01\",\"\",\"\",\"\",\"\",\"FEVTA FEVTA1\"";

    internal static string ExtractPath() => Path.Combine(AppContext.BaseDirectory, "TestData", "PreferredRoutes", "PFR_BASE-extract.csv");

    private static PreferredRouteTable ParseText(string text) => PreferredRouteParser.Parse(new StringReader(text));

    [Fact]
    public void Extract_ParsesKnownRows_WithTheirFaaFieldsUnchanged()
    {
        string[] lines = File.ReadAllLines(ExtractPath());
        Assert.Contains(SfoDenH1, lines);
        Assert.Contains(SfoSmfTec1, lines);

        using StreamReader reader = File.OpenText(ExtractPath());
        PreferredRouteTable table = PreferredRouteParser.Parse(reader);

        Assert.Equal(14, table.DataRows);
        Assert.Equal(0, table.MalformedRows);
        Assert.False(table.IsUnreadable);
        Assert.Empty(table.MissingColumns);
        PreferredRouteRow den = Assert.Single(table.Rows, r => r.RouteText == "TIPRE Q126 INSLO LBERT LONGZ4");
        Assert.Equal(("SFO", "DEN"), (den.Origin, den.Destination));
        Assert.Equal(
            new PreferredRouteInfo("H", Area: null, Altitude: null, Aircraft: null, Hours: "1400-0000", Direction: null, Designator: null),
            den.Info
        );
        PreferredRouteRow smf = Assert.Single(table.Rows, r => r.RouteText == "FEVTA FEVTA1");
        Assert.Equal(("SFO", "SMF"), (smf.Origin, smf.Destination));
        Assert.Equal(
            new PreferredRouteInfo("TEC", "SFO HAF TO SMF", "J30", Aircraft: null, Hours: null, Direction: "SFOW", Designator: "SFO01"),
            smf.Info
        );
    }

    [Fact]
    public void Extract_DropsNarRows_AndKeepsEveryOfferedType()
    {
        using StreamReader reader = File.OpenText(ExtractPath());
        PreferredRouteTable table = PreferredRouteParser.Parse(reader);

        Assert.DoesNotContain(table.Rows, r => r.Origin == "ALLEX");
        Assert.Equal(["H", "H", "HPD", .. Enumerable.Repeat("TEC", 10)], table.Rows.Select(r => r.Info.Type).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void HeaderMissingARequiredColumn_NamesIt_AndEveryRowIsMalformed()
    {
        string header = Header.Replace("\"ROUTE_STRING\"", "\"ROUTE_TEXT\"", StringComparison.Ordinal);

        PreferredRouteTable table = ParseText(header + "\n" + SfoDenH1 + "\n");

        Assert.Equal(["ROUTE_STRING"], table.MissingColumns);
        Assert.Empty(table.Rows);
        Assert.True(table.IsUnreadable);
    }

    [Fact]
    public void UnknownTypeCode_IsDropped_AndNotCountedMalformed()
    {
        string row = SfoDenH1.Replace("\"US\",\"H\",1", "\"US\",\"XYZ\",1", StringComparison.Ordinal);

        PreferredRouteTable table = ParseText(Header + "\n" + row + "\n");

        Assert.Empty(table.Rows);
        Assert.Equal(0, table.MalformedRows);
        Assert.False(table.IsUnreadable);
    }

    [Fact]
    public void MalformedRow_IsSkippedAndCounted_TheOthersParse()
    {
        string truncated = SfoSmfTec1[..SfoSmfTec1.IndexOf(",\"SFO HAF", StringComparison.Ordinal)];

        PreferredRouteTable table = ParseText(Header + "\n" + truncated + "\n" + SfoDenH1 + "\n");

        Assert.Equal(2, table.DataRows);
        Assert.Equal(1, table.MalformedRows);
        Assert.False(table.IsUnreadable);
        Assert.Equal("TIPRE Q126 INSLO LBERT LONGZ4", Assert.Single(table.Rows).RouteText);
    }

    [Fact]
    public void EveryRowMalformed_IsUnreadable()
    {
        PreferredRouteTable table = ParseText("<html><body>\nService unavailable\n</body></html>\n");

        Assert.Empty(table.Rows);
        Assert.True(table.IsUnreadable);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Header + "\n")]
    public void EmptyFile_IsEmpty_NotUnreadable(string text)
    {
        PreferredRouteTable table = ParseText(text);

        Assert.Empty(table.Rows);
        Assert.Equal(0, table.DataRows);
        Assert.False(table.IsUnreadable);
    }
}
