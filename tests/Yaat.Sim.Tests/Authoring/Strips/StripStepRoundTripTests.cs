using Xunit;
using Yaat.Sim.Authoring.Strips;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Strips;

/// <summary>
/// Every form the strip editor writes survives the trip through the real parser: a step is written to a preset, the
/// preset parses to the record the hand-typed canonical command parses to, and reading the preset back gives the
/// same step.
/// </summary>
public class StripStepRoundTripTests
{
    private const string PresetId = "01JTESTPRESET0000000000001";
    private const string ArrivalId = "ARRIVAL_ASA451";

    /// <summary>Airborne and filed out of the primary airport: the bare departure forms.</summary>
    private static ScenarioAircraft Departure() => Aircraft("KOAK", "KSEA");

    /// <summary>Airborne and filed into the primary airport: the arrival id forms.</summary>
    private static ScenarioAircraft Arrival() => Aircraft("KSEA", "KOAK");

    private static ScenarioAircraft Aircraft(string departure, string destination)
    {
        return new ScenarioAircraft
        {
            Id = "a1",
            AircraftId = "ASA451",
            AircraftType = "B738",
            StartingConditions = new StartingConditions
            {
                Type = "Coordinates",
                Coordinates = new ScenarioCoordinates { Lat = 37.85, Lon = -122.3 },
                Altitude = 6000,
                Speed = 250,
            },
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = departure,
                Destination = destination,
                CruiseAltitude = 35000,
            },
        };
    }

    private static ParsedCommand ParseOne(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        ParsedBlock block = Assert.Single(parsed.Value!.Blocks);
        Assert.Null(block.Condition);
        return Assert.Single(block.Commands);
    }

    /// <summary>Writes the step alone, checks the written preset against the canonical text's record, and reads it back.</summary>
    private static StripStep RoundTrip(StripStep step, string canonical, ScenarioAircraft? aircraft = null)
    {
        ScenarioAircraft target = aircraft ?? Departure();
        List<PresetCommand> written = StripPresets.Write(target, "OAK", [step], () => PresetId);
        PresetCommand preset = Assert.Single(written);
        Assert.Equal(step.TimeOffsetSeconds, preset.TimeOffset);
        Assert.Equivalent(ParseOne(canonical), ParseOne(preset.Command), strict: true);

        target.PresetCommands = written;
        (IReadOnlyList<StripStep> owned, IReadOnlyList<StripStep> external) = StripPresets.Read(target, "OAK");
        Assert.Empty(external);
        return Assert.Single(owned);
    }

    private static void AssertRoundTrips(StripStep step, string canonical, ScenarioAircraft? aircraft = null)
    {
        StripStep read = RoundTrip(step, canonical, aircraft);
        Assert.Equivalent(step with { PresetId = PresetId }, read, strict: true);
    }

    [Fact]
    public void Strip_WithRackAndSlot_RoundTrips() =>
        AssertRoundTrips(new StripStep(StripStepKind.Move, 30, FacilityId: "OAK", BayName: "GROUND 1", Rack: 2, Slot: 1), "STRIP OAK/GROUND 1/2/1");

    [Fact]
    public void Strip_WithRackOnly_RoundTrips() =>
        AssertRoundTrips(new StripStep(StripStepKind.Move, 0, FacilityId: "OAK", BayName: "GROUND 1", Rack: 2), "STRIP OAK/GROUND 1/2");

    [Fact]
    public void Strip_OtherFacilityQualifier_RoundTrips() =>
        AssertRoundTrips(new StripStep(StripStepKind.Move, 0, FacilityId: "NCT", BayName: "NCT", Rack: 1), "STRIP NCT/NCT/1");

    [Fact]
    public void Strip_ForAnArrival_UsesTheArrivalId() =>
        AssertRoundTrips(
            new StripStep(StripStepKind.Move, 0, FacilityId: "OAK", BayName: "GROUND 1", Rack: 2),
            $"STRIP {ArrivalId} OAK/GROUND 1/2",
            Arrival()
        );

    [Theory]
    [InlineData("8a")]
    [InlineData("8b")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    [InlineData("8")]
    [InlineData("9")]
    public void Annotate_EveryBox_RoundTripsInItsWrittenSpelling(string canonical)
    {
        StripBox box = StripBox.FromCanonical(canonical)!;
        AssertRoundTrips(new StripStep(StripStepKind.Annotate, 10, Box: box, Text: "RV"), $"AN {box.Written} RV");
    }

    [Fact]
    public void StripBox_WritesTenToEighteenAnd8a8b() =>
        Assert.Equal(["8a", "8b", "10", "11", "12", "13", "14", "15", "16", "17", "18"], StripBox.All.Select(b => b.Written).ToList());

    [Fact]
    public void Annotate_ForAnArrival_UsesTheArrivalId() =>
        AssertRoundTrips(new StripStep(StripStepKind.Annotate, 0, Box: StripBox.FromCanonical("1"), Text: "X"), $"AN {ArrivalId} 10 X", Arrival());

    [Fact]
    public void Annotate_QuestionMark_IsKept() =>
        AssertRoundTrips(new StripStep(StripStepKind.Annotate, 0, Box: StripBox.FromCanonical("1"), Text: "?"), "AN 10 ?");

    [Fact]
    public void Annotate_NoText_ClearsTheBox() =>
        AssertRoundTrips(new StripStep(StripStepKind.Annotate, 0, Box: StripBox.FromCanonical("1")), "AN 10");

    [Fact]
    public void Annotate_Separators_AreStripped()
    {
        var step = new StripStep(StripStepKind.Annotate, 0, Box: StripBox.FromCanonical("1"), Text: "A,B; C AND D then E");

        StripStep read = RoundTrip(step, "AN 10 AB C D E");

        Assert.Equal("AB C D E", read.Text);
    }

    [Fact]
    public void Offset_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.Offset, 5), "STRIPO");

    [Fact]
    public void Offset_ForAnArrival_UsesTheArrivalId() => AssertRoundTrips(new StripStep(StripStepKind.Offset, 5), $"STRIPO {ArrivalId}", Arrival());

    [Fact]
    public void Delete_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.Delete, 5), "STRIPD");

    [Fact]
    public void Delete_ForAnArrival_UsesTheArrivalId() => AssertRoundTrips(new StripStep(StripStepKind.Delete, 5), $"STRIPD {ArrivalId}", Arrival());

    [Fact]
    public void HalfCreate_MultiWordBay_RoundTrips() =>
        AssertRoundTrips(
            new StripStep(StripStepKind.HalfCreate, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 2, Lines: ["a", "b"]),
            "HSC OAK/Local 1/2 a\\b"
        );

    [Fact]
    public void HalfCreate_FiveLines_AreAccepted() =>
        AssertRoundTrips(
            new StripStep(StripStepKind.HalfCreate, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 1, Lines: ["1", "2", "3", "4", "5"]),
            "HSC OAK/Local 1/1 1\\2\\3\\4\\5"
        );

    [Fact]
    public void HalfCreate_SixthLine_IsRefused()
    {
        var step = new StripStep(StripStepKind.HalfCreate, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 1, Lines: ["1", "2", "3", "4", "5", "6"]);

        Assert.False(StripCommandText.TryFormat(step, null, out string? command, out string? error));
        Assert.Null(command);
        Assert.NotNull(error);
        Assert.Throws<ArgumentException>(() => StripPresets.Write(Departure(), "OAK", [step], () => PresetId));
    }

    [Fact]
    public void HalfCreate_LineSeparators_AreStripped()
    {
        var step = new StripStep(StripStepKind.HalfCreate, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 1, Lines: ["a\\b", "c,d;e"]);

        StripStep read = RoundTrip(step, "HSC OAK/Local 1/1 ab\\cde");

        Assert.Equal(["ab", "cde"], read.Lines);
    }

    [Fact]
    public void HalfAmend_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.HalfAmend, 0, Lines: ["a", "b"]), "HSA a\\b");

    [Fact]
    public void HalfMove_RoundTrips() =>
        AssertRoundTrips(new StripStep(StripStepKind.HalfMove, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 1), "HSM OAK/LOCAL 1/1");

    [Fact]
    public void HalfOffset_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.HalfOffset, 0), "HSO");

    [Fact]
    public void HalfSlide_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.HalfSlide, 0), "HSS");

    [Fact]
    public void HalfDelete_RoundTrips() => AssertRoundTrips(new StripStep(StripStepKind.HalfDelete, 0), "HSD");

    [Fact]
    public void Strip_SlotZero_IsRefused()
    {
        var step = new StripStep(StripStepKind.Move, 0, FacilityId: "OAK", BayName: "GROUND 1", Rack: 1, Slot: 0);

        Assert.False(StripCommandText.TryFormat(step, null, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Strip_RackZero_IsRefused()
    {
        var step = new StripStep(StripStepKind.Move, 0, FacilityId: "OAK", BayName: "GROUND 1", Rack: 0);

        Assert.False(StripCommandText.TryFormat(step, null, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Other_IsNotWritable()
    {
        var step = new StripStep(StripStepKind.Other, 0);

        Assert.False(StripCommandText.TryFormat(step, null, out _, out _));
    }
}
