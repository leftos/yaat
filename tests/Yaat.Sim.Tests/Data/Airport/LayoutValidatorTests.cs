using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// <see cref="LayoutValidator"/> on real committed layouts, built as LayoutInspector's <c>--validate</c> builds them (standard
/// fillets, the airport's sidecar inputs, no tower-cab videomap, the airport code for runway widths), reports exactly the
/// warnings captured in <c>TestData/LayoutValidator/&lt;id&gt;.validate.json</c>: same count, codes, messages and origins, in order.
/// </summary>
public class LayoutValidatorTests
{
    private static readonly JsonSerializerOptions FixtureJson = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    public void Validate_RealLayout_MatchesGoldenWarnings(string airportId)
    {
        TestVnasData.EnsureInitialized();
        string geoJson = File.ReadAllText(Path.Combine("TestData", $"{airportId.ToLowerInvariant()}.geojson"));
        AirportGroundLayout layout = GeoJsonParser.Parse(airportId, geoJson, airportId, TestLayoutInputs.For(airportId, null), FilletMode.Standard);

        List<ValidationWarning> actual = new LayoutValidator(layout).Validate();

        string fixture = File.ReadAllText(Path.Combine("TestData", "LayoutValidator", $"{airportId.ToLowerInvariant()}.validate.json"));
        List<ValidationWarning> expected = JsonSerializer.Deserialize<List<ValidationWarning>>(fixture, FixtureJson)!;
        Assert.Equal(expected, actual);
    }
}
