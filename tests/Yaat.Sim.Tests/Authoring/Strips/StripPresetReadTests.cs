using System.Text.Json;
using Xunit;
using Yaat.Sim.Authoring.Strips;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Tests.Authoring.Strips;

/// <summary>
/// The Read classifier over the matcher examples: owned steps are one strip command in the editor's spelling, anything
/// else holding a strip command is external, and the rest is no step. Expected values come from the parser's record,
/// and the aircraft decides whether the full-strip forms carry the arrival id.
/// </summary>
public class StripPresetReadTests
{
    private const string ArrivalId = "ARRIVAL_CS";
    private const string AirborneArrivalId = "ARRIVAL_ASA451";

    private static readonly StartingConditions Airborne = new()
    {
        Type = "Coordinates",
        Coordinates = new ScenarioCoordinates { Lat = 37.85, Lon = -122.3 },
        Altitude = 6000,
        Speed = 250,
    };

    private static readonly StartingConditions Parked = new() { Type = "Parking", Parking = "A1" };

    private static readonly StartingConditions OnRunway = new() { Type = "OnRunway", Runway = "30" };

    private static readonly StartingConditions Ground = new()
    {
        Type = "Coordinates",
        Coordinates = new ScenarioCoordinates { Lat = 37.7213, Lon = -122.2208 },
        Altitude = 9,
        Speed = 0,
    };

    public StripPresetReadTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>An aircraft airborne and filed out of the primary airport: never an arrival.</summary>
    private static ScenarioAircraft Departure() => Aircraft("KOAK", "KSEA", Airborne, "CS");

    /// <summary>An aircraft airborne and filed into the primary airport: an arrival.</summary>
    private static ScenarioAircraft Arrival() => Aircraft("KSEA", "KOAK", Airborne, "CS");

    private static ScenarioAircraft Aircraft(string departure, string destination, StartingConditions conditions, string callsign)
    {
        return new ScenarioAircraft
        {
            Id = "a1",
            AircraftId = callsign,
            AircraftType = "B738",
            StartingConditions = conditions,
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
        return Assert.Single(Assert.Single(parsed.Value!.Blocks).Commands);
    }

    private static (IReadOnlyList<StripStep> Owned, IReadOnlyList<StripStep> External) Read(
        string command,
        ScenarioAircraft? aircraft = null,
        string? primaryAirportId = "OAK"
    )
    {
        ScenarioAircraft target = aircraft ?? Departure();
        target.PresetCommands =
        [
            new PresetCommand
            {
                Id = "P1",
                Command = command,
                TimeOffset = 42,
            },
        ];
        return StripPresets.Read(target, primaryAirportId);
    }

    private static StripStep Owned(string command, ScenarioAircraft? aircraft = null)
    {
        (IReadOnlyList<StripStep> owned, IReadOnlyList<StripStep> external) = Read(command, aircraft);
        Assert.Empty(external);
        StripStep step = Assert.Single(owned);
        Assert.Equal("P1", step.PresetId);
        Assert.Equal(42, step.TimeOffsetSeconds);
        Assert.Null(step.SourceText);
        return step;
    }

    private static StripStep External(string command, ScenarioAircraft? aircraft = null)
    {
        (IReadOnlyList<StripStep> owned, IReadOnlyList<StripStep> external) = Read(command, aircraft);
        Assert.Empty(owned);
        StripStep step = Assert.Single(external);
        Assert.Equal("P1", step.PresetId);
        Assert.Equal(command, step.SourceText);
        Assert.Null(step.DerivedTimeSeconds);
        return step;
    }

    private static void AssertNoStep(string command)
    {
        (IReadOnlyList<StripStep> owned, IReadOnlyList<StripStep> external) = Read(command);
        Assert.Empty(owned);
        Assert.Empty(external);
    }

    private static void AssertAnnotate(string command, StripStep step)
    {
        var record = (StripAnnotateCommand)ParseOne(command);
        Assert.Equal(StripStepKind.Annotate, step.Kind);
        Assert.Equal(record.Box, step.Box!.Canonical);
        Assert.Equal(record.Text, step.Text);
    }

    [Fact]
    public void An10_IsOwned() => AssertAnnotate("AN 10 X", Owned("AN 10 X"));

    [Fact]
    public void An8a_IsOwned() => AssertAnnotate("AN 8a X", Owned("AN 8a X"));

    [Fact]
    public void An8BUpper_IsOwned() => AssertAnnotate("AN 8B x", Owned("AN 8B x"));

    [Fact]
    public void AnArrivalId_IsOwnedForThatArrival() => AssertAnnotate("AN ARRIVAL_CS 10 X", Owned("AN ARRIVAL_CS 10 X", Arrival()));

    [Fact]
    public void An1_ReadsAsBox1AndIsWrittenBackAsAn10()
    {
        StripStep step = Owned("AN 1 x");
        AssertAnnotate("AN 1 x", step);

        PresetCommand written = Assert.Single(StripPresets.Write(Departure(), "OAK", [step], () => "P2"));

        Assert.Equal("AN 10 x", written.Command);
    }

    [Fact]
    public void An19_DoesNotParse_NoStep() => AssertNoStep("AN 19 X");

    [Fact]
    public void LowerCase_ParsesAsTheParserDoes()
    {
        AssertAnnotate("an 10 x", Owned("an 10 x"));
        StripStep move = Owned("strip oak/ground 1/2");
        Assert.Equal(("OAK", "GROUND 1", 2, (int?)null), (move.FacilityId, move.BayName, move.Rack, move.Slot));
    }

    [Fact]
    public void StripWithSlot_IsOwned()
    {
        var record = (StripMoveCommand)ParseOne("STRIP OAK/Ground 1/2/1");
        Assert.Equal(["OAK/Ground", "1/2/1"], record.Tokens);

        StripStep step = Owned("STRIP OAK/Ground 1/2/1");

        Assert.Equal(StripStepKind.Move, step.Kind);
        Assert.Equal(("OAK", "GROUND 1", 2, (int?)1), (step.FacilityId, step.BayName, step.Rack, step.Slot));
    }

    [Fact]
    public void StripRackOnly_IsOwned()
    {
        StripStep step = Owned("STRIP OAK/Ground 1/2");

        Assert.Equal(StripStepKind.Move, step.Kind);
        Assert.Equal(("OAK", "GROUND 1", 2, (int?)null), (step.FacilityId, step.BayName, step.Rack, step.Slot));
    }

    [Fact]
    public void Stripo_IsOwned()
    {
        Assert.Null(((StripOffsetCommand)ParseOne("STRIPO")).StripId);
        Assert.Equal(StripStepKind.Offset, Owned("STRIPO").Kind);
    }

    [Fact]
    public void Stripd_IsOwned()
    {
        Assert.Null(((StripDeleteCommand)ParseOne("STRIPD")).StripId);
        Assert.Equal(StripStepKind.Delete, Owned("STRIPD").Kind);
    }

    [Fact]
    public void Hsc_IsOwned()
    {
        var record = (HalfStripCreateCommand)ParseOne("HSC OAK/Local 1/2 a\\b");

        StripStep step = Owned("HSC OAK/Local 1/2 a\\b");

        Assert.Equal(StripStepKind.HalfCreate, step.Kind);
        Assert.Equal((record.FacilityId, record.BayName, record.Rack + 1), (step.FacilityId, step.BayName, step.Rack));
        Assert.Equal(record.Lines, step.Lines);
    }

    [Fact]
    public void Hsa_IsOwned()
    {
        var record = (HalfStripAmendCommand)ParseOne("HSA a\\b");

        StripStep step = Owned("HSA a\\b");

        Assert.Equal(StripStepKind.HalfAmend, step.Kind);
        Assert.Equal(record.Tokens, step.Lines);
    }

    [Fact]
    public void Hsm_IsOwned()
    {
        StripStep step = Owned("HSM OAK/Local 1/1");

        Assert.Equal(StripStepKind.HalfMove, step.Kind);
        Assert.Equal(("OAK", "LOCAL 1", 1), (step.FacilityId, step.BayName, step.Rack));
    }

    [Fact]
    public void Hso_IsOwned() => Assert.Equal(StripStepKind.HalfOffset, Owned("HSO").Kind);

    [Fact]
    public void Hss_IsOwned() => Assert.Equal(StripStepKind.HalfSlide, Owned("HSS").Kind);

    [Fact]
    public void Hsd_IsOwned() => Assert.Equal(StripStepKind.HalfDelete, Owned("HSD").Kind);

    [Fact]
    public void AnKeyed_IsExternal()
    {
        Assert.Equal("STRIP_CS_ab12", ((StripAnnotateCommand)ParseOne("AN STRIP_CS_ab12 3 y")).StripId);
        Assert.Equal(StripStepKind.Annotate, External("AN STRIP_CS_ab12 3 y").Kind);
    }

    [Fact]
    public void AnArrivalId_IsExternalForADeparture() => External("AN ARRIVAL_CS 10 X");

    [Fact]
    public void AnBare_OnAnArrival_IsExternal() => Assert.Equal(StripStepKind.Annotate, External("AN 10 X", Arrival()).Kind);

    [Fact]
    public void HsaBayScoped_IsExternal()
    {
        Assert.Equal("OAK", ((HalfStripAmendCommand)ParseOne("HSA OAK/Local 1/1 KEY\\a")).FacilityId);
        Assert.Equal(StripStepKind.HalfAmend, External("HSA OAK/Local 1/1 KEY\\a").Kind);
    }

    [Fact]
    public void HsaHalfStripId_IsExternal() => Assert.Equal(StripStepKind.HalfAmend, External("HSA HSTRIP_x a\\b").Kind);

    [Fact]
    public void HsdHalfStripId_IsExternal() => Assert.Equal(StripStepKind.HalfDelete, External("HSD HSTRIP_x").Kind);

    [Fact]
    public void HsoKey_IsExternal()
    {
        Assert.Equal("KEY", ((HalfStripOffsetCommand)ParseOne("HSO KEY")).LookupKey);
        Assert.Equal(StripStepKind.HalfOffset, External("HSO KEY").Kind);
    }

    [Fact]
    public void WaitChain_IsExternal()
    {
        StripStep step = External("WAIT 5 AN 10 X");

        Assert.Equal(StripStepKind.Annotate, step.Kind);
        Assert.Equal(42, step.TimeOffsetSeconds);
    }

    [Fact]
    public void AtFixChain_IsExternalWithItsCondition()
    {
        StripStep step = External("AT SUNOL AN 10 X");

        Assert.Equal(StripStepKind.Annotate, step.Kind);
        Assert.Equal("at SUNOL", step.ConditionText);
    }

    [Fact]
    public void LevelChain_IsExternalWithItsCondition()
    {
        StripStep step = External("LV 100 STRIP OAK/Ground 1/2");

        Assert.Equal(StripStepKind.Move, step.Kind);
        Assert.StartsWith("at ", step.ConditionText);
    }

    [Fact]
    public void SemicolonChain_IsExternal() => Assert.Equal(StripStepKind.Annotate, External("AN 10 X; AN 11 Y").Kind);

    [Fact]
    public void CommaChain_IsExternal() => Assert.Equal(StripStepKind.Annotate, External("AN 10 X, STRIPO").Kind);

    [Fact]
    public void FlightPlanThenStrip_IsExternal() => Assert.Equal(StripStepKind.Move, External("DA C172 055; STRIP OAK/Ground 1/2").Kind);

    [Fact]
    public void Scan_IsOther() => Assert.Equal(StripStepKind.Other, External("SCAN NCT/NCT/1").Kind);

    [Fact]
    public void Sep_IsOther() => Assert.Equal(StripStepKind.Other, External("SEP H OAK/Ground 1/1 HOLD").Kind);

    [Fact]
    public void RackSort_IsOther() => Assert.Equal(StripStepKind.Other, External("RACKSORT OAK/Ground/1 ON").Kind);

    [Fact]
    public void Blank_IsOther() => Assert.Equal(StripStepKind.Other, External("BLANK OAK/Ground 1/1").Kind);

    [Fact]
    public void Empty_IsNoStep() => AssertNoStep("");

    [Fact]
    public void Whitespace_IsNoStep() => AssertNoStep("   ");

    [Fact]
    public void NonStripCommand_IsNoStep() => AssertNoStep("FH 270");

    [Fact]
    public void IsArrival_ParkedAircraftFiledIntoTheAirport_ReadsBareDepartureForms()
    {
        ScenarioAircraft parked = Aircraft("KSEA", "KOAK", Parked, "ASA451");

        Assert.Null(StripCommandText.ArrivalStripId(parked, "OAK"));
        Assert.Equal(StripStepKind.Annotate, Owned("AN 10 X", parked).Kind);
        Assert.Equal(StripStepKind.Annotate, External("AN ARRIVAL_CS 10 X", parked).Kind);
    }

    [Fact]
    public void IsArrival_OnRunwayStart_ReadsBareDepartureForms()
    {
        ScenarioAircraft linedUp = Aircraft("KSEA", "KOAK", OnRunway, "ASA451");

        Assert.Null(StripCommandText.ArrivalStripId(linedUp, "OAK"));
        Assert.Equal(StripStepKind.Offset, Owned("STRIPO", linedUp).Kind);
    }

    [Fact]
    public void IsArrival_GroundCoordinateSpawn_ReadsBareDepartureForms()
    {
        ScenarioAircraft taxiing = Aircraft("KSEA", "KOAK", Ground, "ASA451");

        Assert.Null(StripCommandText.ArrivalStripId(taxiing, "OAK"));
        Assert.Equal(StripStepKind.Delete, Owned("STRIPD", taxiing).Kind);
    }

    [Fact]
    public void IsArrival_AirborneFiledIntoTheAirport_ReadsArrivalForms()
    {
        ScenarioAircraft arrival = Aircraft("KSEA", "KOAK", Airborne, "ASA451");

        Assert.Equal(AirborneArrivalId, StripCommandText.ArrivalStripId(arrival, "OAK"));
        Assert.Equal(StripStepKind.Annotate, Owned("AN ARRIVAL_ASA451 10 X", arrival).Kind);
        Assert.Equal(StripStepKind.Move, Owned("STRIP ARRIVAL_ASA451 OAK/Ground 1/2", arrival).Kind);
    }

    [Fact]
    public void IsArrival_AirborneFiledOutAndIntoTheAirport_ReadsArrivalForms()
    {
        ScenarioAircraft patternWork = Aircraft("KOAK", "KOAK", Airborne, "ASA451");

        Assert.Equal(AirborneArrivalId, StripCommandText.ArrivalStripId(patternWork, "OAK"));
        Assert.Equal(StripStepKind.Offset, Owned("STRIPO ARRIVAL_ASA451", patternWork).Kind);
    }

    [Fact]
    public void IsArrival_AirborneFiledOutOfTheAirport_ReadsBareDepartureForms()
    {
        ScenarioAircraft departure = Aircraft("KOAK", "KSEA", Airborne, "ASA451");

        Assert.Null(StripCommandText.ArrivalStripId(departure, "OAK"));
        Assert.Equal(StripStepKind.Offset, Owned("STRIPO", departure).Kind);
    }

    [Fact]
    public void IsArrival_NoFlightPlan_ReadsBareDepartureForms()
    {
        ScenarioAircraft coldCall = Aircraft("", "", Airborne, "ASA451");
        coldCall.FlightPlan = null;

        Assert.Null(StripCommandText.ArrivalStripId(coldCall, "OAK"));
        Assert.Equal(StripStepKind.Offset, Owned("STRIPO", coldCall).Kind);
    }

    [Fact]
    public void Read_WithNoPrimaryAirport_NoAircraftIsAnArrival()
    {
        ScenarioAircraft arrival = Arrival();

        Assert.Null(StripCommandText.ArrivalStripId(arrival, null));
        (IReadOnlyList<StripStep> owned, _) = Read("AN 10 X", arrival, primaryAirportId: null);
        Assert.Equal(StripStepKind.Annotate, Assert.Single(owned).Kind);

        List<PresetCommand> written = StripPresets.Write(Arrival(), null, [new StripStep(StripStepKind.Offset, 0)], () => "N1");
        Assert.Equal("STRIPO", Assert.Single(written).Command);
    }

    [Fact]
    public void IsArrivalCandidate_Shadow_IsNotAnArrival()
    {
        var sample = new LiveTrafficSample(0, 37.9, -122.4, 8000, 250, 340, null, LiveTrafficSource.Stars, null);
        AircraftState shadow = LiveTrafficKinematics.CreateShadow("ASA451", "B738", sample, new AircraftFlightPlan { Destination = "KOAK" });

        Assert.True(shadow.IsShadow);
        Assert.False(StripRequests.IsArrivalCandidate(shadow, "OAK"));
    }

    [Fact]
    public void Write_KeepsEveryNonOwnedPresetUntouchedInPlace()
    {
        using var extra = JsonDocument.Parse("{\"note\":1}");
        var extension = new Dictionary<string, JsonElement> { ["x"] = extra.RootElement.Clone() };
        var first = new PresetCommand
        {
            Id = "E1",
            Command = "FH 270",
            TimeOffset = 3,
            ExtensionData = extension,
        };
        var owned = new PresetCommand
        {
            Id = "O1",
            Command = "AN 10 X",
            TimeOffset = 20,
        };
        var keyed = new PresetCommand
        {
            Id = "E2",
            Command = "HSO KEY",
            TimeOffset = 9,
        };
        var chain = new PresetCommand
        {
            Id = "E3",
            Command = "WAIT 5 AN 10 X",
            TimeOffset = 1,
        };
        ScenarioAircraft aircraft = Departure();
        aircraft.PresetCommands = [first, owned, keyed, chain];
        (IReadOnlyList<StripStep> read, _) = StripPresets.Read(aircraft, "OAK");

        List<PresetCommand> result = StripPresets.Write(aircraft, "OAK", [read[0] with { Text = "Y" }], () => "NEW");

        Assert.Equal(4, result.Count);
        Assert.Same(first, result[0]);
        Assert.Same(keyed, result[2]);
        Assert.Same(chain, result[3]);
        Assert.Equal(("E1", "FH 270", 3), (first.Id, first.Command, first.TimeOffset));
        Assert.Same(extension, first.ExtensionData);
        Assert.Equal(("O1", "AN 10 Y", 20), (result[1].Id, result[1].Command, result[1].TimeOffset));

        Assert.Equal(4, aircraft.PresetCommands.Count);
        Assert.Same(first, aircraft.PresetCommands[0]);
        Assert.Same(owned, aircraft.PresetCommands[1]);
        Assert.Same(keyed, aircraft.PresetCommands[2]);
        Assert.Same(chain, aircraft.PresetCommands[3]);
    }
}
