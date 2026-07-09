using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A Small-class arrival is general aviation — bizjets (Citations), light pistons (C172/SR22), and light
/// turboprops (Caravan/PC-12) that no scheduled airline operates. The generator must give these N-number
/// (general-aviation) callsigns rather than forcing them onto an airline. This is the callsign side of the
/// banded randomize-weight change: a Small/SmallPlus generator now actually reaches the Small bucket, so the
/// stream mixes in GA traffic instead of being all airline callsigns.
/// </summary>
public class AircraftGeneratorGaCallsignTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(EngineKind.Jet)]
    [InlineData(EngineKind.Piston)]
    [InlineData(EngineKind.Turboprop)]
    public void SmallArrivals_GetGeneralAviationNNumbers(EngineKind engine)
    {
        TestVnasData.EnsureInitialized();
        if (!AircraftProfileDatabase.IsInitialized)
        {
            output.WriteLine("AircraftProfileDatabase not initialized; skipping (test data missing)");
            return;
        }

        var rng = new Random(0x6A1C0DE);
        for (int i = 0; i < 100; i++)
        {
            var request = new SpawnRequest
            {
                Rules = FlightRulesKind.Ifr,
                Weight = WeightClass.Small,
                Engine = engine,
                PositionType = SpawnPositionType.Bearing,
                Bearing = 270,
                DistanceNm = 30,
                Altitude = 8000,
                PreferredAirlineAirportId = "OAK",
            };

            var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, rng, new BeaconCodePool());
            Assert.Null(error);
            Assert.NotNull(state);

            // GA callsigns are N-numbers: 'N' then a leading digit. An airline callsign is three letters
            // then digits (e.g. "SWA1234"), so the second character being a digit excludes airline codes.
            Assert.StartsWith("N", state.Callsign);
            Assert.True(char.IsDigit(state.Callsign[1]), $"Small {engine} produced a non-GA callsign: {state.Callsign}");
        }
    }
}
