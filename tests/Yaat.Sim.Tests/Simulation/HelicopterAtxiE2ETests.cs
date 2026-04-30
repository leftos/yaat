using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Spawns an EC35 helicopter at KOAK parking spot HELI and exercises the ATXI
/// (air-taxi) command. Asserts the desired controller-facing behavior:
///   1. ATXI accepts all three destination types — parking spot, taxiway spot,
///      and runway — with or without the @ prefix where it would be natural.
///   2. The helicopter takes off in place, climbs to 100 ft AGL, air-taxies
///      direct to the destination, and lands safely on the spot.
///
/// Several sub-cases are expected to fail today and document concrete sim gaps:
///   * `ATXI @FDX1` (and any `@`-prefixed destination): the ATXI parser at
///     CommandParser.cs:645 just trims/uppercases the arg without stripping the
///     '@', so FindSpotByName misses. TAXI/LAND handle this via StartsWith('@').
///   * `ATXI 28R` (and any runway designator): TryAirTaxi resolves only via
///     AirportGroundLayout.FindSpotByName, which covers helipad/parking/spot
///     but not runways. AirportGroundLayout.FindRunway is not called.
///   * Climb cap of 50 ft AGL during cruise: CategoryPerformance.AirTaxiAltitudeAgl
///     returns 50 ft, even though FAA 7110.65 §3-11-1.c puts the ceiling at 100 ft.
///   * Never lands at the destination: TryAirTaxi queues only AirTaxiPhase, so
///     after the heli arrives and stops to a hover it sits there indefinitely.
///     TryLand chains AirTaxiPhase → HelicopterLandingPhase → AtParkingPhase —
///     ATXI should do the same.
///
/// The per-tick xUnit log on the full-flight test shows exactly what the
/// helicopter actually does so the reader can answer "what happens" without
/// rerunning the test.
/// </summary>
public class HelicopterAtxiE2ETests(ITestOutputHelper output)
{
    private const double KoakFieldElevFt = 6.0;
    private const int MaxTicks = 600;

    /// <summary>
    /// Asserts that ATXI accepts every controller-natural destination form for
    /// the three supported categories (parking, taxiway spot, runway). Each
    /// case is independent — failures are reported per row by xUnit so you can
    /// see at a glance which destination types still need work.
    /// </summary>
    [Theory]
    [InlineData("ATXI FDX1", "parking spot, no prefix")]
    [InlineData("ATXI @FDX1", "parking spot, @ prefix (controller-natural)")]
    [InlineData("ATXI 7A", "taxiway spot, no prefix")]
    [InlineData("ATXI 28R", "runway designator")]
    public void Atxi_AcceptsAllSupportedDestinationTypes(string command, string description)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var (engine, _, heli, _) = SetupHeliAtHeli();

        var result = engine.SendCommand("TEST1", command);
        output.WriteLine($"{command} ({description}): success={result.Success} message=\"{result.Message}\"");

        Assert.True(result.Success, $"ATXI should accept {description}: {command} → {result.Message}");
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
    }

    /// <summary>
    /// Full-flight E2E: takeoff in place → climb to 100 ft AGL → air-taxi to
    /// FDX1 → land safely. Issues `ATXI FDX1` (the form known to parse today)
    /// so this test focuses on the climb/cruise/land behavior rather than the
    /// destination-syntax gaps covered by the [Theory] above. Two assertions
    /// are expected to fail today (peak AGL and never-lands).
    /// </summary>
    [Fact]
    public void Atxi_FromHeli_ToFdx1_LiftsClimbsAirTaxisAndLands()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var (engine, _, heli, fdx1Spot) = SetupHeliAtHeli();

        var atxi = engine.SendCommand("TEST1", "ATXI FDX1");
        output.WriteLine($"ATXI FDX1: success={atxi.Success} message=\"{atxi.Message}\"");
        Assert.True(atxi.Success, $"ATXI rejected: {atxi.Message}");
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);

        bool liftedOff = false;
        double peakAgl = 0;
        double minDistNm = double.MaxValue;
        int landedAtTick = -1;

        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();

            double agl = heli.Altitude - KoakFieldElevFt;
            double distNm = GeoMath.DistanceNm(heli.Position, fdx1Spot.Position);
            peakAgl = Math.Max(peakAgl, agl);
            minDistNm = Math.Min(minDistNm, distNm);
            if (!heli.IsOnGround)
            {
                liftedOff = true;
            }

            output.WriteLine(
                $"t={t, 3}s phase={heli.Phases?.CurrentPhase?.Name ?? "<none>"} "
                    + $"ground={heli.IsOnGround} alt={heli.Altitude:F0}ft agl={agl:F0}ft "
                    + $"gs={heli.GroundSpeed:F0}kt dist={distNm:F3}nm hdg={heli.TrueHeading.Degrees:F0}°"
            );

            if (liftedOff && heli.IsOnGround && landedAtTick < 0)
            {
                landedAtTick = t;
                break;
            }
        }

        output.WriteLine($"Summary: liftedOff={liftedOff} peakAgl={peakAgl:F0}ft " + $"minDist={minDistNm:F3}nm landedAtTick={landedAtTick}");

        Assert.True(liftedOff, "Helicopter never left the ground.");

        Assert.True(peakAgl >= 100, $"Expected peak ≥100 ft AGL during air taxi; got {peakAgl:F0} ft.");

        Assert.True(minDistNm <= 0.05, $"Helicopter never reached FDX1; closest approach {minDistNm:F3} nm.");

        Assert.True(landedAtTick > 0, $"Helicopter never landed within {MaxTicks}s — ATXI hovers indefinitely after arrival.");
        double landingDistFt = GeoMath.DistanceNm(heli.Position, fdx1Spot.Position) * 6076.12;
        Assert.True(landingDistFt <= 50, $"Helicopter landed {landingDistFt:F0} ft from FDX1 (expected ≤50 ft).");
        Assert.Equal(0, heli.GroundSpeed, 1.0);
    }

    private (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Heli, GroundNode Fdx1) SetupHeliAtHeli()
    {
        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);

        var heliSpot = layout.FindSpotByName("HELI");
        var fdx1Spot = layout.FindSpotByName("FDX1");
        Assert.NotNull(heliSpot);
        Assert.NotNull(fdx1Spot);

        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-helicopter-atxi-e2e",
                ScenarioName = "Helicopter ATXI E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        var heli = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "EC35",
            Position = heliSpot.Position,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = KoakFieldElevFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        heli.Phases = new PhaseList();
        heli.Phases.Add(new AtParkingPhase());
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, layout));
        engine.World.AddAircraft(heli);

        double directNm = GeoMath.DistanceNm(heliSpot.Position, fdx1Spot.Position);
        output.WriteLine($"HELI at ({heliSpot.Position.Lat:F6},{heliSpot.Position.Lon:F6})");
        output.WriteLine($"FDX1 at ({fdx1Spot.Position.Lat:F6},{fdx1Spot.Position.Lon:F6})");
        output.WriteLine($"Direct distance HELI→FDX1: {directNm:F3} nm");

        return (engine, layout, heli, fdx1Spot);
    }
}
