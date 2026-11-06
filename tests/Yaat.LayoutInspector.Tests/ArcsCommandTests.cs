using System.Text.Json;
using Xunit;
using Xunit.Sdk;
using Yaat.LayoutInspector.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.LayoutInspector.Tests;

/// <summary>
/// <c>--arcs</c> lists every fillet arc of the loaded layout, tightest effective radius first; <c>--arcs-max-radius &lt;ft&gt;</c>
/// keeps only the arcs at or below that radius.
/// </summary>
public class ArcsCommandTests
{
    [Fact]
    public void ArcsFlag_SetsArcsWithNoRadiusFilter()
    {
        CliOptions options = Parse("--arcs");

        Assert.True(options.Arcs);
        Assert.Null(options.ArcsMaxRadiusFt);
    }

    [Fact]
    public void ArcsMaxRadiusFlag_SetsTheFilterAndTheArcsMode()
    {
        CliOptions options = Parse("--arcs-max-radius", "150");

        Assert.True(options.Arcs);
        Assert.Equal(150.0, options.ArcsMaxRadiusFt);
    }

    [Fact]
    public void ArcsMaxRadiusFlag_RejectsANonNumber()
    {
        Assert.False(CliOptions.TryParse(["oak.geojson", "--arcs-max-radius", "tight"], out _, out string? error));
        Assert.Contains("--arcs-max-radius", error);
    }

    /// <summary>Every OAK arc is listed once, tightest effective radius first, each radius the planner's own.</summary>
    [Fact]
    public void BuildRows_OnOak_ListsEveryArcTightestEffectiveRadiusFirst()
    {
        AirportGroundLayout layout = LoadOak();

        List<ArcReportRow> rows = ArcsCommand.BuildRows(layout, null);

        Assert.Equal(layout.Arcs.Count, rows.Count);
        List<double> radii = [.. rows.Select(r => r.EffectiveRadiusFt ?? double.PositiveInfinity)];
        List<double> expected = [.. layout.Arcs.Select(FollowRoutePlanner.EffectiveArcRadiusFt).Order()];
        Assert.Equal(expected, radii);
    }

    /// <summary>The radius filter keeps the arcs at or below it and drops the rest.</summary>
    [Fact]
    public void BuildRows_OnOakWithAMaxRadius_KeepsOnlyArcsAtOrBelowIt()
    {
        AirportGroundLayout layout = LoadOak();
        List<ArcReportRow> all = ArcsCommand.BuildRows(layout, null);
        double median = all[all.Count / 2].EffectiveRadiusFt ?? throw new XunitException("median arc does not turn");

        List<ArcReportRow> tight = ArcsCommand.BuildRows(layout, median);

        Assert.NotEmpty(tight);
        Assert.True(tight.Count < all.Count, $"{tight.Count} of {all.Count} arcs kept at {median:F0} ft");
        Assert.All(tight, r => Assert.True(r.EffectiveRadiusFt <= median, $"{r.FromNodeId}-{r.ToNodeId}: {r.EffectiveRadiusFt} ft"));
        Assert.Equal(all.Count(r => r.EffectiveRadiusFt <= median), tight.Count);
    }

    /// <summary>OAK's tightest corner, the arc from node 440 to node 483, joins taxiway K at its first end to L at its second.</summary>
    [Fact]
    public void BuildRows_OnOak_NamesTheTaxiwayAtEachEndOfTheKToLCorner()
    {
        ArcReportRow row = Assert.Single(ArcsCommand.BuildRows(LoadOak(), null), r => (r.FromNodeId == 440) && (r.ToNodeId == 483));

        Assert.Equal("K", row.FromTaxiway);
        Assert.Equal("L", row.ToTaxiway);
    }

    /// <summary>The <c>--json</c> output names each row's fields in camelCase.</summary>
    [Fact]
    public void ToJson_NamesEveryFieldOfARow()
    {
        List<ArcReportRow> rows = [.. ArcsCommand.BuildRows(LoadOak(), null).Take(1)];

        using var doc = JsonDocument.Parse(ArcsCommand.ToJson(rows));

        JsonElement row = Assert.Single(doc.RootElement.EnumerateArray());
        string[] expected =
        [
            "effectiveRadiusFt",
            "minBezierRadiusFt",
            "headingChangeDeg",
            "lengthFt",
            "fromNodeId",
            "toNodeId",
            "fromTaxiway",
            "toTaxiway",
        ];
        Assert.Equal(expected, row.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>A radius column prints <c>inf</c> for no turn and for a straight fillet's <see cref="double.MaxValue"/> radius.</summary>
    [Theory]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.MaxValue, "inf")]
    [InlineData(14.4, "14")]
    public void FormatRadiusFt_PrintsInfForNoTurn(double radiusFt, string expected) => Assert.Equal(expected, ArcsCommand.FormatRadiusFt(radiusFt));

    private static AirportGroundLayout LoadOak()
    {
        string testData = Bootstrap.FindDefaultNavDataDir() ?? throw new XunitException($"no yaat.slnx at or above {AppContext.BaseDirectory}");
        TestVnasData.SetTestDataDir(testData);
        TestVnasData.EnsureInitialized();
        return LayoutAnalyzer.Load(Path.Combine(testData, "oak.geojson"), "OAK", FilletMode.Standard, null).Layout;
    }

    private static CliOptions Parse(params string[] args)
    {
        string[] all = ["oak.geojson", .. args];
        Assert.True(CliOptions.TryParse(all, out CliOptions options, out string? error), error);
        return options;
    }
}
