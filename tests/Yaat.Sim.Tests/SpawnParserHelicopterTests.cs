using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// The ADD command's engine slot accepts <c>H</c> for helicopters (issue #265). A bare
/// <c>ADD V S H @spot</c> auto-selects a light civil helicopter (R22/R44/B06); an explicit
/// heli type (<c>H60</c>, <c>PUMA</c>, …) is honored verbatim and drives the Helicopter
/// category. The weight token is cosmetic for helicopters — any weight resolves to the same
/// light pool. Explicit-type detection for all-letter codes (PUMA) is data-driven so airport
/// ICAOs (KOAK) on a STAR arrival are never misread as aircraft types.
/// </summary>
public class SpawnParserHelicopterTests
{
    public SpawnParserHelicopterTests()
    {
        // IsLikelyAircraftType's all-letter branch and AircraftCategorization.Categorize both read
        // the specs lookup; pin it before any test body runs (static-singleton-race guidance).
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Parse_HEngine_WithExplicitHeliType_ParkingVariant()
    {
        var (request, error) = SpawnParser.Parse("V S H @H1 R22");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(EngineKind.Helicopter, request.Engine);
        Assert.Equal("R22", request.ExplicitType);
        Assert.Equal(SpawnPositionType.Parking, request.PositionType);
        Assert.Equal("H1", request.ParkingName);
    }

    [Fact]
    public void Parse_HEngine_NoExplicitType_LeavesTypeForAutoSelection()
    {
        var (request, error) = SpawnParser.Parse("V S H @H1");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(EngineKind.Helicopter, request.Engine);
        Assert.Null(request.ExplicitType);
        Assert.Equal(SpawnPositionType.Parking, request.PositionType);
    }

    [Theory]
    [InlineData("V S H @H1")]
    [InlineData("V S+ H @H1")]
    [InlineData("V L H @H1")]
    [InlineData("V H H @H1")]
    public void Parse_HEngine_AcceptedForAnyWeight(string args)
    {
        var (request, error) = SpawnParser.Parse(args);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(EngineKind.Helicopter, request.Engine);
    }

    [Fact]
    public void Parse_HEngine_AllLetterHeliType_RecognizedViaDataLookup()
    {
        // PUMA is a real all-letter helicopter ICAO designator. It must be captured as an explicit
        // type override, not misread as a position token.
        if (!AircraftCategorization.IsKnownType("B738"))
        {
            return; // specs data unavailable in this environment
        }

        var (request, error) = SpawnParser.Parse("V S H @H1 PUMA");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("PUMA", request.ExplicitType);
        Assert.Equal(SpawnPositionType.Parking, request.PositionType);
    }

    [Fact]
    public void Parse_StarArrival_AirportToken_NotMisreadAsType()
    {
        // Guards the data-driven all-letter detector: KOAK is a 4-letter all-letter airport ICAO,
        // not an aircraft type, so it must remain the destination airport.
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R LVL KOAK");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("KOAK", request.DestinationAirportId);
        Assert.Null(request.ExplicitType);
    }

    [Fact]
    public void Generate_HeliParkingSpawn_AutoType_IsHelicopterOnStandbyAtParking()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(groundLayout);

        var (request, parseError) = SpawnParser.Parse("V S H @NEW1");
        Assert.Null(parseError);
        Assert.NotNull(request);

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout, new Random(7), new BeaconCodePool());

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Contains(state.AircraftType, new[] { "R22", "R44", "B06" });
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(state.AircraftType));
        Assert.StartsWith("N", state.Callsign); // VFR helicopter → GA N-number
        Assert.Equal("Standby", state.Transponder.Mode);
        Assert.True(state.IsOnGround);
        Assert.NotNull(state.Phases);
        Assert.IsType<AtParkingPhase>(state.Phases.CurrentPhase);
    }

    [Fact]
    public void Generate_HeliExplicitH60_SpawnsBlackHawkInHelicopterCategory()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(groundLayout);

        var (request, parseError) = SpawnParser.Parse("V S H @NEW1 H60");
        Assert.Null(parseError);
        Assert.NotNull(request);

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout, new Random(7), new BeaconCodePool());

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("H60", state.AircraftType);
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(state.AircraftType));
    }

    [Fact]
    public void Generate_IfrHeli_GetsNNumberAndDiscreteCode_NoScheduledAirline()
    {
        // No curated airline operates R22/R44/B06, so the airline picker returns none and an IFR
        // helicopter falls through to a GA N-number (with a discrete beacon, unlike VFR's 1200).
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Small,
            Engine = EngineKind.Helicopter,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 90,
            DistanceNm = 12,
            Altitude = 3000,
        };

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, new Random(11), new BeaconCodePool());

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize(state.AircraftType));
        Assert.StartsWith("N", state.Callsign);
        Assert.True(char.IsDigit(state.Callsign[1]), $"IFR helicopter produced a non-GA callsign: {state.Callsign}");
        Assert.NotEqual((uint)0, state.Transponder.AssignedCode);
        Assert.NotEqual((uint)1200, state.Transponder.AssignedCode);
    }
}
