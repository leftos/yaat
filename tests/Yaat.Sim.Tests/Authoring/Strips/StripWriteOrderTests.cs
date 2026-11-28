using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Strips;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Strips;

/// <summary>The owned block Write produces: time, then move, annotate, offset, delete; stable ties; id reuse; minting.</summary>
public class StripWriteOrderTests
{
    private static readonly StripBox Box10 = StripBox.FromCanonical("1")!;

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

    private static Func<string> Counter(string prefix)
    {
        int next = 0;
        return () => $"{prefix}{++next}";
    }

    [Fact]
    public void Write_SortsByTimeThenMoveAnnotateOffsetDelete()
    {
        List<StripStep> steps =
        [
            new(StripStepKind.Delete, 10),
            new(StripStepKind.Offset, 10),
            new(StripStepKind.Annotate, 10, Box: Box10, Text: "X"),
            new(StripStepKind.Move, 10, FacilityId: "OAK", BayName: "GROUND 1", Rack: 1),
            new(StripStepKind.Annotate, 0, Box: Box10, Text: "EARLY"),
        ];

        List<PresetCommand> result = StripPresets.Write(Departure(), "OAK", steps, Counter("N"));

        Assert.Equal(["AN 10 EARLY", "STRIP OAK/GROUND 1/1", "AN 10 X", "STRIPO", "STRIPD"], result.Select(p => p.Command).ToList());
        Assert.Equal([0, 10, 10, 10, 10], result.Select(p => p.TimeOffset).ToList());
    }

    [Fact]
    public void Write_HalfStripKindsRankWithTheirFullStripCounterparts()
    {
        List<StripStep> steps =
        [
            new(StripStepKind.HalfDelete, 0),
            new(StripStepKind.HalfOffset, 0),
            new(StripStepKind.HalfAmend, 0, Lines: ["A"]),
            new(StripStepKind.HalfCreate, 0, FacilityId: "OAK", BayName: "LOCAL 1", Rack: 1, Lines: ["A"]),
        ];

        List<PresetCommand> result = StripPresets.Write(Departure(), "OAK", steps, Counter("N"));

        Assert.Equal(["HSC OAK/LOCAL 1/1 A", "HSA A", "HSO", "HSD"], result.Select(p => p.Command).ToList());
    }

    [Fact]
    public void Write_EqualTimeAndKind_KeepsTheGivenOrder()
    {
        // Boxes and text both run against the given order, so a sort by either one shows up as a different list.
        List<StripStep> steps =
        [
            new(StripStepKind.Annotate, 5, Box: StripBox.FromCanonical("3"), Text: "ZULU"),
            new(StripStepKind.Annotate, 5, Box: StripBox.FromCanonical("1"), Text: "ALPHA"),
            new(StripStepKind.Annotate, 5, Box: StripBox.FromCanonical("2"), Text: "MIKE"),
        ];

        List<PresetCommand> result = StripPresets.Write(Departure(), "OAK", steps, Counter("N"));

        Assert.Equal(["AN 12 ZULU", "AN 10 ALPHA", "AN 11 MIKE"], result.Select(p => p.Command).ToList());
    }

    [Fact]
    public void Write_ReplacesAnExistingArrivalAnnotation()
    {
        ScenarioAircraft arrival = Arrival();
        arrival.PresetCommands =
        [
            new PresetCommand
            {
                Id = "A1",
                Command = "AN ARRIVAL_ASA451 10 X",
                TimeOffset = 10,
            },
        ];
        (IReadOnlyList<StripStep> read, IReadOnlyList<StripStep> external) = StripPresets.Read(arrival, "OAK");
        Assert.Empty(external);
        StripStep survivor = Assert.Single(read);

        List<PresetCommand> result = StripPresets.Write(arrival, "OAK", [survivor with { Text = "Y" }], Counter("N"));

        Assert.Equal([("A1", "AN ARRIVAL_ASA451 10 Y")], result.Select(p => (p.Id, p.Command)).ToList());
    }

    [Fact]
    public void Write_ReusesASurvivingStepsPresetIdAndMintsTheRest()
    {
        ScenarioAircraft aircraft = Departure();
        aircraft.PresetCommands =
        [
            new PresetCommand
            {
                Id = "KEEP",
                Command = "AN 10 X",
                TimeOffset = 0,
            },
            new PresetCommand
            {
                Id = "DROP",
                Command = "STRIPO",
                TimeOffset = 0,
            },
        ];
        (IReadOnlyList<StripStep> read, _) = StripPresets.Read(aircraft, "OAK");
        StripStep survivor = read.Single(s => s.PresetId == "KEEP");

        List<PresetCommand> result = StripPresets.Write(aircraft, "OAK", [survivor with { Text = "Y" }, new(StripStepKind.Delete, 30)], Counter("N"));

        Assert.Equal([("KEEP", "AN 10 Y"), ("N1", "STRIPD")], result.Select(p => (p.Id, p.Command)).ToList());
    }

    [Fact]
    public void Write_AnIdNamingANonOwnedPreset_IsMintedAfresh()
    {
        ScenarioAircraft aircraft = Departure();
        aircraft.PresetCommands =
        [
            new PresetCommand
            {
                Id = "EXT",
                Command = "WAIT 5 AN 10 X",
                TimeOffset = 0,
            },
        ];

        List<PresetCommand> result = StripPresets.Write(aircraft, "OAK", [new(StripStepKind.Offset, 0, PresetId: "EXT")], Counter("N"));

        Assert.Equal([("EXT", "WAIT 5 AN 10 X"), ("N1", "STRIPO")], result.Select(p => (p.Id, p.Command)).ToList());
    }

    [Fact]
    public void Write_MintsDeterministicallyFromTheInjectedFactory()
    {
        List<StripStep> steps = [new(StripStepKind.Offset, 0), new(StripStepKind.Delete, 0)];
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var randomA = new Random(11);
        var randomB = new Random(11);
        List<PresetCommand> first = StripPresets.Write(Departure(), "OAK", steps, () => ScenarioIdGenerator.Create(clock, randomA));
        List<PresetCommand> second = StripPresets.Write(Departure(), "OAK", steps, () => ScenarioIdGenerator.Create(clock, randomB));

        List<string> firstIds = [.. first.Select(p => p.Id)];
        List<string> secondIds = [.. second.Select(p => p.Id)];
        Assert.Equal(firstIds, secondIds);
        Assert.Equal(2, first.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Write_OwnedBlockTakesThePlaceOfTheFirstOwnedPreset()
    {
        ScenarioAircraft aircraft = Departure();
        aircraft.PresetCommands =
        [
            new PresetCommand { Id = "A", Command = "FH 270" },
            new PresetCommand { Id = "O", Command = "STRIPO" },
            new PresetCommand { Id = "B", Command = "CM 50" },
        ];

        List<PresetCommand> result = StripPresets.Write(aircraft, "OAK", [new(StripStepKind.Delete, 0), new(StripStepKind.Offset, 0)], Counter("N"));

        Assert.Equal(["A", "N1", "N2", "B"], result.Select(p => p.Id).ToList());
        Assert.Equal(["FH 270", "STRIPO", "STRIPD", "CM 50"], result.Select(p => p.Command).ToList());
    }

    [Fact]
    public void Write_WithNoOwnedPreset_AppendsTheBlock()
    {
        ScenarioAircraft aircraft = Departure();
        aircraft.PresetCommands = [new PresetCommand { Id = "A", Command = "FH 270" }];

        List<PresetCommand> result = StripPresets.Write(aircraft, "OAK", [new(StripStepKind.Offset, 0)], Counter("N"));

        Assert.Equal(["A", "N1"], result.Select(p => p.Id).ToList());
    }
}
