using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CVA issuance weather gates (7110.65 §7-4-3.b, AIM §5-5-11.b.1): a visual approach may
/// not be cleared unless the reported weather is at/above 1000 ft ceiling and 3 SM
/// visibility (basic-VFR minimums); when no weather is available the clearance is allowed
/// (the §7-4-3.c weather-not-available path). The wide-angle pattern-entry geometry builds
/// a 2000 ft AGL IFR downwind, so it additionally needs ceiling ≥ 2500 ft — otherwise the
/// downwind would be built inside the deck and immediately trip the lost-reference
/// consequence. CVAF (instructor force) bypasses both gates.
/// </summary>
public class CvaWeatherGateTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-cva-weather-gate",
                ScenarioName = "CVA Weather Gate",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
    }

    private static WeatherProfile OakMetar(string tail) => new() { Metars = [$"KOAK 121853Z 27012KT {tail} 20/12 A2992"] };

    /// <summary>
    /// Spawns a B738 with a forced field report so only the weather gate is under test.
    /// Heading either straight down the final (angleOff ≈ 0 → straight-in geometry) or
    /// reciprocal (angleOff ≈ 180 → pattern-entry geometry).
    /// </summary>
    private static AircraftState Spawn(SimulationEngine engine, double finalDistanceNm, double altitude, bool towardRunway)
    {
        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        var (lat, lon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, finalDistanceNm);
        double heading = towardRunway ? rwy.TrueHeading.Degrees : reciprocal;
        var ac = new AircraftState
        {
            Callsign = "GATE1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr((int)altitude),
            },
        };
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand("GATE1", "RFISF").Success);
        return ac;
    }

    [Fact]
    public void Cva_RejectedBelowMinimumCeiling()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return; // navdata absent → skip, per no-synthetic-data convention
        }

        engine.World.Weather = OakMetar("5SM OVC009");
        Spawn(engine, finalDistanceNm: 6.0, altitude: 2000, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.False(result.Success, $"CVA must be rejected under a 900 ft ceiling, got: {result.Message}");
    }

    [Fact]
    public void Cva_RejectedBelowMinimumVisibility()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        engine.World.Weather = OakMetar("2SM CLR");
        Spawn(engine, finalDistanceNm: 6.0, altitude: 2000, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.False(result.Success, $"CVA must be rejected under 2 SM visibility, got: {result.Message}");
    }

    [Fact]
    public void Cva_AtExactBasicVfrMinimums_Allowed()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        // 1000/3 is legal ("ceiling AT OR ABOVE 1,000 feet and visibility 3 miles OR GREATER").
        engine.World.Weather = OakMetar("3SM OVC010");
        Spawn(engine, finalDistanceNm: 3.0, altitude: 900, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.True(result.Success, $"a 1000/3 visual is legally clearable, got: {result.Message}");
    }

    [Fact]
    public void Cva_WeatherNotAvailable_Allowed()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        engine.World.Weather = null;
        Spawn(engine, finalDistanceNm: 6.0, altitude: 2000, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.True(result.Success, $"no reported weather → §7-4-3.c weather-not-available path, got: {result.Message}");
    }

    [Fact]
    public void Cvaf_ForcesThroughWeatherGate()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        engine.World.Weather = OakMetar("2SM OVC009");
        Spawn(engine, finalDistanceNm: 6.0, altitude: 2000, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVAF 30");

        Assert.True(result.Success, $"CVAF is the instructor override and bypasses the weather gate, got: {result.Message}");
    }

    [Fact]
    public void Cva_PatternEntry_RejectedUnderLowCeiling()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        // 1500/3 passes basic minimums, but the wide-angle geometry builds a 2000 ft AGL
        // downwind — inside the OVC015 deck. Reject rather than build an in-cloud pattern.
        engine.World.Weather = OakMetar("3SM OVC015");
        Spawn(engine, finalDistanceNm: 6.0, altitude: 2500, towardRunway: false);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.False(result.Success, $"a pattern-entry CVA must be rejected when the IFR downwind would sit in the deck, got: {result.Message}");
    }

    [Fact]
    public void Cva_StraightIn_AllowedUnderSameCeiling()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        // Same 1500/3 weather, straight-in geometry: no downwind is built, so only the
        // basic minimums apply.
        engine.World.Weather = OakMetar("3SM OVC015");
        Spawn(engine, finalDistanceNm: 4.0, altitude: 1200, towardRunway: true);

        var result = engine.SendCommand("GATE1", "CVA 30");

        Assert.True(result.Success, $"a straight-in visual under 1500/3 is legal, got: {result.Message}");
    }
}
