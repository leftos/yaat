using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>The arrival's own parking choice: operator-appropriate, deterministic per callsign, skipping spots in use.</summary>
public class ArrivalParkingPickerTests
{
    private static readonly IReadOnlySet<string> NoneTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] OakLikeNames =
    [
        "1",
        "10",
        "29",
        "8B",
        "A",
        "B",
        "CARGO1",
        "DHL1",
        "DHL2",
        "FDX1",
        "FDX2",
        "GA1",
        "GA13",
        "JSX1",
        "NEW5",
        "SIG1",
    ];

    public ArrivalParkingPickerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Airline_TaxiesToANumberedGate()
    {
        var pick = ArrivalParkingPicker.Pick("SWA1234", OakLikeNames, NoneTaken, 0);

        Assert.NotNull(pick);
        Assert.True(ArrivalParkingPicker.IsGateNumber(pick), pick);
        Assert.Equal(["1", "10", "29", "8B"], ArrivalParkingPicker.Candidates("SWA1234", OakLikeNames));
    }

    [Fact]
    public void OperatorWithItsOwnRamp_TaxiesThere()
    {
        Assert.Equal(["FDX1", "FDX2"], ArrivalParkingPicker.Candidates("FDX440", OakLikeNames));
        Assert.Equal(["JSX1"], ArrivalParkingPicker.Candidates("JSX176", OakLikeNames));
        // The DHL family flies under other ICAO codes but parks on the DHL ramp.
        Assert.Equal(["DHL1", "DHL2"], ArrivalParkingPicker.Candidates("DHK123", OakLikeNames));
    }

    [Fact]
    public void CargoCarrierWithoutItsOwnRamp_TaxiesToTheCargoApron_ElseAGate()
    {
        Assert.Equal(["CARGO1"], ArrivalParkingPicker.Candidates("GTI8123", OakLikeNames));
        string[] noApron = ["1", "29", "FDX1", "GA1"];
        Assert.Equal(["1", "29"], ArrivalParkingPicker.Candidates("GTI8123", noApron));
    }

    [Fact]
    public void GeneralAviation_TaxiesToANonGateSpot()
    {
        var candidates = ArrivalParkingPicker.Candidates("N152SP", OakLikeNames);

        Assert.Equal(["A", "B", "GA1", "GA13", "NEW5", "SIG1"], candidates);
        var pick = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        Assert.Contains(pick, candidates);
    }

    [Fact]
    public void Pick_IsDeterministicPerCallsign_AndASaltRepicksWithinThePool()
    {
        var first = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        var again = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        var resalted = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 1);

        Assert.Equal(first, again);
        Assert.Contains(resalted, ArrivalParkingPicker.Candidates("N152SP", OakLikeNames));
    }

    [Fact]
    public void TakenSpots_AreSkipped_AndAFullRampFallsBackToEverything()
    {
        var first = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0)!;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };

        var second = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, taken, 0);
        Assert.NotEqual(first, second);

        var everything = new HashSet<string>(OakLikeNames, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(ArrivalParkingPicker.Pick("N152SP", OakLikeNames, everything, 0));
        Assert.Null(ArrivalParkingPicker.Pick("N152SP", [], NoneTaken, 0));
    }

    [Fact]
    public void TakenSpots_ReadParkedAircraft_TaxiDestinations_AndOpenTaxiInRequests()
    {
        var parked = new AircraftState
        {
            Callsign = "N1",
            AircraftType = "C172",
            Ground = new AircraftGroundOps { ParkingSpot = "GA1" },
            Phases = new PhaseList(),
        };
        parked.Phases.Add(new AtParkingPhase());
        var asking = new AircraftState
        {
            Callsign = "N2",
            AircraftType = "C172",
            PendingPilotRequest = new PilotPendingRequest
            {
                Kind = PilotPendingRequestKind.Taxi,
                FirstRequestedAtSeconds = 0,
                LastPilotLine = "",
                LastPilotLineTts = "",
                ParkingName = "SIG1",
            },
        };
        var self = new AircraftState
        {
            Callsign = "N3",
            AircraftType = "C172",
            Ground = new AircraftGroundOps { ParkingSpot = "NEW5" },
            Phases = new PhaseList(),
        };
        self.Phases.Add(new AtParkingPhase());

        var taken = ArrivalParkingPicker.TakenSpots([parked, asking, self], "N3");

        Assert.Equal(["GA1", "SIG1"], taken.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void OakLayout_GivesARegistrationANonGateSpot()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "N152SP", AircraftType = "C172" };

        var pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.False(ArrivalParkingPicker.IsGateNumber(pick), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Theory]
    [InlineData("29", true)]
    [InlineData("8B", true)]
    [InlineData("1", true)]
    [InlineData("A", false)]
    [InlineData("GA13", false)]
    [InlineData("FDX1", false)]
    [InlineData("41-10", false)]
    public void IsGateNumber_DigitsWithAtMostOneTrailingLetter(string name, bool expected)
    {
        Assert.Equal(expected, ArrivalParkingPicker.IsGateNumber(name));
    }
}
