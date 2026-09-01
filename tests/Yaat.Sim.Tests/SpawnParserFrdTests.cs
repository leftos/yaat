using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #413: pasting a radar-scope "Copy FRD" string (e.g. "AAAME093002") into the ADD
/// command produced an unhelpful error. The at-fix variant (<c>@{fix_or_FRD} {alt}</c>) already
/// resolves FRDs; these tests pin that behavior and require every other variant that receives a
/// full FRD-shaped token (bearing, runway, parking) to redirect the user to the <c>@</c> form
/// instead of failing with a generic or misleading error.
/// </summary>
public class SpawnParserFrdTests
{
    public SpawnParserFrdTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- The @ form accepts an FRD (existing behavior, previously untested on the ADD path) ---

    [Fact]
    public void Parse_FixVariant_FrdToken_ParsesAsAtFix()
    {
        var (request, error) = SpawnParser.Parse("V S P @AAAME093002 035");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.AtFix, request.PositionType);
        Assert.Equal("AAAME093002", request.FixId);
        Assert.Equal(3500, request.Altitude);
    }

    // --- Bearing variant: FRD-shaped token redirects to the @ form ---

    [Theory]
    [InlineData("V S P - AAAME093002")]
    [InlineData("V S P -AAAME093002")]
    [InlineData("V S P - AAAME093002 035")]
    public void Parse_BearingVariant_FrdToken_RedirectsToFixVariant(string args)
    {
        var (request, error) = SpawnParser.Parse(args);

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("@AAAME093002", error);
    }

    // --- Runway variant (bare paste, no prefix): FRD-shaped token redirects to the @ form ---

    [Theory]
    [InlineData("V S P AAAME093002")]
    [InlineData("V S P AAAME093002 035")]
    public void Parse_RunwayVariant_FrdToken_RedirectsToFixVariant(string args)
    {
        var (request, error) = SpawnParser.Parse(args);

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("@AAAME093002", error);
    }

    // --- Parking variant: @FRD with no altitude asks for the altitude instead of a spot lookup ---

    [Fact]
    public void Parse_ParkingVariant_FrdToken_RequiresAltitude()
    {
        var (request, error) = SpawnParser.Parse("V S P @AAAME093002");

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains("altitude", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@AAAME093002", error);
    }

    // --- Non-FRD controls: existing variants stay reachable ---

    [Fact]
    public void Parse_FixVariant_BareFixName_StillAtFix()
    {
        var (request, error) = SpawnParser.Parse("V S P @BERKS 035");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.AtFix, request.PositionType);
        Assert.Equal("BERKS", request.FixId);
    }

    [Fact]
    public void Parse_BearingVariant_NumericTokens_StillBearing()
    {
        var (request, error) = SpawnParser.Parse("V S P -090 10 035");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Bearing, request.PositionType);
        Assert.Equal(90, request.Bearing);
        Assert.Equal(10, request.DistanceNm);
    }

    [Fact]
    public void Parse_RunwayVariant_RunwayAndDistance_StillOnFinal()
    {
        var (request, error) = SpawnParser.Parse("V S P 28R 5");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnFinal, request.PositionType);
        Assert.Equal("28R", request.RunwayId);
        Assert.Equal(5, request.FinalDistanceNm);
    }

    [Fact]
    public void Parse_ParkingVariant_BareSpot_StillParking()
    {
        var (request, error) = SpawnParser.Parse("V S H @H1");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Parking, request.PositionType);
        Assert.Equal("H1", request.ParkingName);
    }

    // --- Generator wiring: an FRD position spawns at the FRD-resolved point ---

    [Fact]
    public void Generate_AtFixFrd_SpawnsAtResolvedFrdPosition()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var expected = FrdResolver.Resolve("BERKS090010", navDb);
        if (expected is null)
        {
            return;
        }

        var (request, parseError) = SpawnParser.Parse("V S P @BERKS090010 035");
        Assert.Null(parseError);
        Assert.NotNull(request);

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, new Random(42), new BeaconCodePool());

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(expected.Value.Lat, state.Position.Lat, 6);
        Assert.Equal(expected.Value.Lon, state.Position.Lon, 6);
        Assert.Equal(3500, state.Altitude);
    }
}
