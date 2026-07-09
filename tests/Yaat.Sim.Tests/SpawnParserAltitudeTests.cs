using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// Altitude arguments on the ADD command's airborne variants — bearing (<c>-{brg} {dist} {alt}</c>)
/// and at-fix (<c>@{fix} {alt}</c>). Both resolve through AltitudeResolver, so a bare number under
/// 1000 is hundreds of feet ("035" = 3500, "005" = 500), a number at or above 1000 is literal feet,
/// and the "{airport}+{hundreds}" form is AGL above field elevation. Extra position tokens are an
/// error rather than being silently dropped.
/// </summary>
public class SpawnParserAltitudeTests
{
    private static readonly NavigationDatabase Elevations = TestNavDbFactory.WithElevations(("KOAK", 9.0), ("OAK", 9.0));

    public SpawnParserAltitudeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Bearing variant ---

    [Theory]
    [InlineData("035", 3500)]
    [InlineData("005", 500)]
    [InlineData("15", 1500)]
    [InlineData("3500", 3500)]
    [InlineData("10000", 10000)]
    public void Parse_BearingVariant_ResolvesAltitudeShorthand(string altToken, double expected)
    {
        var (request, error) = SpawnParser.Parse($"V S P -360 15 {altToken}");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Bearing, request.PositionType);
        Assert.Equal(360, request.Bearing);
        Assert.Equal(15, request.DistanceNm);
        Assert.Equal(expected, request.Altitude);
    }

    [Fact]
    public void Parse_BearingVariant_AglAltitude_AddsFieldElevation()
    {
        using var _ = NavigationDatabase.ScopedOverride(Elevations);

        var (request, error) = SpawnParser.Parse("V S P -270 8 KOAK+010");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(1009, request.Altitude);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-100")]
    [InlineData("abcde")]
    public void Parse_BearingVariant_InvalidAltitude_IsRejected(string altToken)
    {
        var (request, error) = SpawnParser.Parse($"V S P -360 15 {altToken}");

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("altitude", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BearingVariant_ExtraPositionToken_IsRejected()
    {
        var (request, error) = SpawnParser.Parse("V S P -360 15 035 42");

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("Too many position arguments", error);
    }

    // --- At-fix variant ---

    [Theory]
    [InlineData("035", 3500)]
    [InlineData("005", 500)]
    [InlineData("8000", 8000)]
    public void Parse_FixVariant_ResolvesAltitudeShorthand(string altToken, double expected)
    {
        var (request, error) = SpawnParser.Parse($"V S P @BERKS {altToken}");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.AtFix, request.PositionType);
        Assert.Equal("BERKS", request.FixId);
        Assert.Equal(expected, request.Altitude);
    }

    [Fact]
    public void Parse_FixVariant_AglAltitude_ResolvesAsFixNotParking()
    {
        using var _ = NavigationDatabase.ScopedOverride(Elevations);

        var (request, error) = SpawnParser.Parse("V S P @BERKS KOAK+010");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.AtFix, request.PositionType);
        Assert.Equal("BERKS", request.FixId);
        Assert.Equal(1009, request.Altitude);
    }

    [Fact]
    public void Parse_FixVariant_ZeroAltitude_IsRejected()
    {
        var (request, error) = SpawnParser.Parse("V S P @BERKS 0");

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("altitude", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reported case: "@BERKS 0 035" silently dropped the 035 and spawned at 0 ft.
    /// </summary>
    [Fact]
    public void Parse_FixVariant_ExtraPositionToken_IsRejected()
    {
        var (request, error) = SpawnParser.Parse("V S P @BERKS 0 035");

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("Too many position arguments", error);
    }

    // --- Parking variant stays reachable (no altitude token) ---

    [Fact]
    public void Parse_ParkingVariant_BareSpot_IsParking()
    {
        var (request, error) = SpawnParser.Parse("V S H @H1");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Parking, request.PositionType);
        Assert.Equal("H1", request.ParkingName);
    }
}
