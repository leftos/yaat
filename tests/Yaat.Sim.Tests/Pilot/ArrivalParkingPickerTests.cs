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
        string? pick = ArrivalParkingPicker.Pick("SWA1234", OakLikeNames, NoneTaken, 0);

        Assert.NotNull(pick);
        Assert.True(ArrivalParkingPicker.IsGateName(pick), pick);
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
        IReadOnlyList<string> candidates = ArrivalParkingPicker.Candidates("N152SP", OakLikeNames);

        Assert.Equal(["A", "B", "GA1", "GA13", "NEW5", "SIG1"], candidates);
        string? pick = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        Assert.Contains(pick, candidates);
    }

    [Fact]
    public void Pick_IsDeterministicPerCallsign_AndASaltRepicksWithinThePool()
    {
        string? first = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        string? again = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0);
        string? resalted = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 1);

        Assert.Equal(first, again);
        Assert.Contains(resalted, ArrivalParkingPicker.Candidates("N152SP", OakLikeNames));
    }

    [Fact]
    public void TakenSpots_AreSkipped_AndAFullRampFallsBackToEverything()
    {
        string first = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, NoneTaken, 0)!;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };

        string? second = ArrivalParkingPicker.Pick("N152SP", OakLikeNames, taken, 0);
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

        HashSet<string> taken = ArrivalParkingPicker.TakenSpots([parked, asking, self], "N3");

        Assert.Equal(["GA1", "SIG1"], taken.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void OakLayout_GivesARegistrationANonGateSpot()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "N152SP", AircraftType = "C172" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.False(ArrivalParkingPicker.IsGateName(pick), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Fact]
    public void Airline_WithDigitLedAndLetteredGates_PrefersTheDigitLedOnes()
    {
        string[] names = ["1", "29", "S5", "F8", "GA1"];

        Assert.Equal(["1", "29"], ArrivalParkingPicker.Candidates("SWA1234", names));
    }

    [Fact]
    public void Airline_WithOnlyLetteredGates_UsesThem()
    {
        string[] names = ["A1", "F8", "G101", "GA1", "CG1"];

        Assert.Equal(["A1", "F8", "G101"], ArrivalParkingPicker.Candidates("SWA1234", names));
    }

    [Fact]
    public void Airline_WithDigitLedGatesUnderHalfOfThePool_UsesEveryGate()
    {
        string[] names = ["1", "2", "A13R", "F8", "G101"];

        Assert.Equal(["1", "2", "A13R", "F8", "G101"], ArrivalParkingPicker.Candidates("UAL123", names));
    }

    [Fact]
    public void Airline_WithDigitLedGatesAtLeastHalfOfThePool_UsesTheDigitLedOnes()
    {
        string[] names = ["1", "29", "8B", "F8"];

        Assert.Equal(["1", "29", "8B"], ArrivalParkingPicker.Candidates("UAL123", names));
    }

    [Fact]
    public void GeneralAviation_LeavesEveryGateNameOutOfItsPool()
    {
        string[] names = ["GA1", "S5", "T1", "KILO RAMP", "29"];

        Assert.Equal(["GA1", "KILO RAMP"], ArrivalParkingPicker.Candidates("N123AB", names));
    }

    [Fact]
    public void SfoLayout_GivesAnAirlineAGateName()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "UAL123", AircraftType = "B738" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.True(char.IsLetter(pick[0]), pick);
        Assert.True(ArrivalParkingPicker.IsGateName(pick), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Fact]
    public void OakLayout_GivesAnAirlineADigitLedGate()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "SWA1234", AircraftType = "B738" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.True(char.IsDigit(pick[0]), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Fact]
    public void MiaLayout_GivesAnAirlineALetterNamedGate()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("MIA");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "UAL123", AircraftType = "B738" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.True(char.IsLetter(pick[0]), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Fact]
    public void FllLayout_GivesAnAirlineALetterNamedGate()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("FLL");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "UAL123", AircraftType = "B738" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.True(char.IsLetter(pick[0]), pick);
        Assert.NotNull(layout.FindParkingByName(pick));
    }

    [Fact]
    public void SmfLayout_GivesAnAirlineATerminalGate()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SMF");
        Assert.NotNull(layout);
        var aircraft = new AircraftState { Callsign = "AAL123", AircraftType = "B738" };

        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0);

        Assert.NotNull(pick);
        Assert.Matches("^[AB]\\d", pick);
    }

    [Theory]
    [InlineData("29", true)]
    [InlineData("8B", true)]
    [InlineData("1", true)]
    [InlineData("F8", true)]
    [InlineData("A13R", true)]
    [InlineData("G101", true)]
    [InlineData("B22", true)]
    [InlineData("S5A", true)]
    [InlineData("T1", true)]
    [InlineData("8081", false)]
    [InlineData("A", false)]
    [InlineData("GA13", false)]
    [InlineData("GA1", false)]
    [InlineData("CG1", false)]
    [InlineData("UB1", false)]
    [InlineData("FDX1", false)]
    [InlineData("CARGO1", false)]
    [InlineData("41-10", false)]
    [InlineData("2-1A", false)]
    [InlineData("7AB", false)]
    [InlineData("KILO RAMP", false)]
    [InlineData("GATE B22", false)]
    [InlineData("٢٩", false)]
    [InlineData("", false)]
    public void IsGateName_OptionalLetterThenUpToThreeDigitsThenOptionalLetter(string name, bool expected) =>
        Assert.Equal(expected, ArrivalParkingPicker.IsGateName(name));
}
