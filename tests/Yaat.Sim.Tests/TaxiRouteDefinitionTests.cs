using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="TaxiRouteDefinition"/> — the per-route value type carried in the unified
/// airport sidecar's <c>taxiRoutes</c> section. Covers canonical-command synthesis, path tokenization,
/// and JSON round-tripping independent of the loader.
/// </summary>
public class TaxiRouteDefinitionTests
{
    [Theory]
    [InlineData("W", "30", null, null, "TAXI W RWY 30")]
    [InlineData("K W", "28L", null, null, "TAXI K W RWY 28L")]
    [InlineData("A B", null, "G7", null, "TAXI A B @G7")]
    [InlineData("T T3 B", null, null, "S2", "TAXI T T3 B $S2")]
    [InlineData("K W", null, null, null, "TAXI K W")]
    [InlineData("  K   W  ", "28L", null, null, "TAXI K W RWY 28L")]
    public void ToCanonicalCommand_BuildsExpectedString(string path, string? destRunway, string? destParking, string? destSpot, string expected)
    {
        var def = new TaxiRouteDefinition
        {
            Name = "test",
            Path = path,
            DestinationRunway = destRunway,
            DestinationParking = destParking,
            DestinationSpot = destSpot,
        };

        Assert.Equal(expected, def.ToCanonicalCommand());
    }

    [Theory]
    [InlineData("T T3 B", new[] { "T", "T3", "B" })]
    [InlineData("W", new[] { "W" })]
    [InlineData("  K   W  ", new[] { "K", "W" })]
    [InlineData("\tA\tB\t", new[] { "A", "B" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void GetPathTokens_SplitsOnWhitespace(string path, string[] expected)
    {
        var def = new TaxiRouteDefinition { Name = "test", Path = path };
        Assert.Equal(expected, def.GetPathTokens());
    }

    [Fact]
    public void ToCanonicalCommand_RoundTripsThroughGroundCommandParser()
    {
        // The canonical command emitted by a preset must re-parse via the standard GroundCommandParser
        // into a TaxiCommand with the same path/destination, so the server's command pipeline accepts it.
        var def = new TaxiRouteDefinition
        {
            Name = "DEP 10R via T-T3-B",
            Path = "T T3 B",
            DestinationRunway = "10R",
        };

        string canonical = def.ToCanonicalCommand();
        Assert.Equal("TAXI T T3 B RWY 10R", canonical);

        var result = GroundCommandParser.ParseTaxi(canonical["TAXI ".Length..]);

        Assert.True(result.IsSuccess, $"Parse failed: {result.Reason}");
        var taxi = (TaxiCommand)result.Value!;
        Assert.Equal(["T", "T3", "B"], taxi.Path);
        Assert.Equal("10R", taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiRouteDefinition_RoundTripsThroughJson()
    {
        var def = new TaxiRouteDefinition
        {
            Name = "DEP 28R via B-K",
            Path = "B K",
            DestinationRunway = "28R",
            Tags = ["dep", "28R"],
        };

        string json = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = false });
        var parsed = JsonSerializer.Deserialize<TaxiRouteDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(parsed);
        Assert.Equal(def.Name, parsed!.Name);
        Assert.Equal(def.Path, parsed.Path);
        Assert.Equal(def.DestinationRunway, parsed.DestinationRunway);
        Assert.Equal(def.Tags, parsed.Tags);
    }
}
