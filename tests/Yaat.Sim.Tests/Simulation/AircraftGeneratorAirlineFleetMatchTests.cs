using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Realism guardrail: when the generator picks an airline + aircraft type for an IFR
/// arrival, the chosen type must be one that airline actually operates (per
/// <see cref="AirlineFleets"/>). Without this, callsigns like "Southwest 1234" with an
/// A320 — which Southwest doesn't fly — would slip through.
///
/// We sample many spawns per (weight, engine) bucket and assert every generated
/// (airline, type) pair is operationally realistic. We pin <see cref="Random"/>
/// seeds so failures reproduce.
/// </summary>
public class AircraftGeneratorAirlineFleetMatchTests(ITestOutputHelper output)
{
    private const int SamplesPerSeed = 200;

    [Theory]
    [InlineData(WeightClass.Large, EngineKind.Jet)]
    [InlineData(WeightClass.Heavy, EngineKind.Jet)]
    [InlineData(WeightClass.Large, EngineKind.Turboprop)]
    public void GeneratedTypeIsOperatedByGeneratedAirline(WeightClass weight, EngineKind engine)
    {
        TestVnasData.EnsureInitialized();
        if (!AircraftProfileDatabase.IsInitialized)
        {
            output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }
        if (AirlineFleets.AirlineCount == 0)
        {
            output.WriteLine("AirlineFleets data not loaded; skipping");
            return;
        }

        var rng = new Random(0xA1BCAA);
        int mismatches = 0;
        int fleetUnknown = 0;
        var sampleMismatches = new List<string>();

        for (int i = 0; i < SamplesPerSeed; i++)
        {
            var request = new SpawnRequest
            {
                Rules = FlightRulesKind.Ifr,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.Bearing,
                Bearing = 270,
                DistanceNm = 30,
                Altitude = 8000,
            };

            var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, rng);
            Assert.Null(error);
            Assert.NotNull(state);

            // Recover the airline ICAO from the callsign prefix (3 letters before the digits).
            var airline = ExtractAirlineIcao(state.Callsign);
            Assert.NotNull(airline);

            if (!AirlineFleets.TryGetTypes(airline, out var fleet))
            {
                fleetUnknown++;
                continue;
            }

            if (!fleet.ContainsKey(state.AircraftType))
            {
                mismatches++;
                if (sampleMismatches.Count < 5)
                {
                    sampleMismatches.Add($"{airline} -> {state.AircraftType} (fleet has: {string.Join(",", fleet.Keys)})");
                }
            }
        }

        output.WriteLine($"{weight}+{engine}: samples={SamplesPerSeed}, mismatches={mismatches}, fleetUnknown={fleetUnknown}");
        foreach (var mismatch in sampleMismatches)
        {
            output.WriteLine($"  MISMATCH: {mismatch}");
        }

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void ExplicitAirlineConstrainsType()
    {
        TestVnasData.EnsureInitialized();
        if (!AircraftProfileDatabase.IsInitialized)
        {
            output.WriteLine("AircraftProfileDatabase not initialized; skipping");
            return;
        }
        if (AirlineFleets.AirlineCount == 0)
        {
            output.WriteLine("AirlineFleets data not loaded; skipping");
            return;
        }

        // Southwest only flies 737-family. Sample heavily and assert every type is in its fleet.
        var rng = new Random(0xBEEF);
        if (!AirlineFleets.TryGetTypes("SWA", out var swaFleet))
        {
            output.WriteLine("SWA missing from AirlineFleets; skipping");
            return;
        }

        for (int i = 0; i < 50; i++)
        {
            var request = new SpawnRequest
            {
                Rules = FlightRulesKind.Ifr,
                Weight = WeightClass.Large,
                Engine = EngineKind.Jet,
                PositionType = SpawnPositionType.Bearing,
                Bearing = 270,
                DistanceNm = 30,
                Altitude = 8000,
                ExplicitAirline = "SWA",
            };

            var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, rng);
            Assert.Null(error);
            Assert.NotNull(state);
            Assert.StartsWith("SWA", state.Callsign);
            Assert.True(
                swaFleet.ContainsKey(state.AircraftType),
                $"SWA paired with {state.AircraftType}, which is not in its fleet ({string.Join(",", swaFleet.Keys)})"
            );
        }
    }

    private static string? ExtractAirlineIcao(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || callsign.Length < 4)
        {
            return null;
        }
        // Airline ICAO callsigns are 3 letters then digits (e.g. "SWA1234", "NKS5678").
        // VFR N-numbers are 'N' then mostly digits (e.g. "N123AB"). Disambiguate by
        // checking whether the first three chars are all letters — works for "NKS"
        // (N-K-S all letters → airline) and excludes "N123" (digits at index 1).
        var prefix = callsign[..3];
        return prefix.All(char.IsLetter) ? prefix.ToUpperInvariant() : null;
    }
}
