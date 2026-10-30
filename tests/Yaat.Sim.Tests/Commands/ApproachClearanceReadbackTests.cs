using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// An approach clearance reads back the approach and runway it clears (AIM 4-4-7.b.2/.b.4) in 7110.65 4-8-1's words: the
/// type, its variant letter before the runway ("cleared RNAV Zulu runway two eight right approach"), "straight in" for a
/// straight-in clearance, "join the" for a join, and a PTAC's heading and altitude first (7110.65 5-9-4.b.2). The expected
/// approach reads back its own type ("expect localizer runway two eight right approach"). Spoken text spells digits and
/// phonetic letters; the terminal uses digits and the raw letter.
/// </summary>
public class ApproachClearanceReadbackTests
{
    public ApproachClearanceReadbackTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Five miles east of KOAK 28R, heading for it.
    private static AircraftState MakeArrival(double altitude) =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.735, -122.12),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    // Over the bay west of KOAK, heading for 10R.
    private static AircraftState MakeWestArrival() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(115),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.70, -122.33),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    // North of KCCR, heading for 19R.
    private static AircraftState MakeConcordArrival() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(190),
            Altitude = 3000,
            IndicatedAirspeed = 160,
            Position = new LatLon(38.07, -122.08),
            FlightPlan = new AircraftFlightPlan { Destination = "CCR" },
        };

    private static ParsedCommand Parse(string text) => CommandParser.Parse(text).Value!;

    private static CommandResult Dispatch(string text, AircraftState aircraft) =>
        CommandDispatcher.Dispatch(Parse(text), aircraft, TestDispatch.Context(new Random(1)));

    private static void AssertReadback(CommandResult result, CanonicalCommandType type, string terminal, string tts)
    {
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.PilotReadback);
        Assert.Equal(type, result.PilotReadback.Command);
        Assert.Equal(terminal, result.PilotReadback.Speech.Terminal);
        Assert.Equal(tts, result.PilotReadback.Speech.Tts);
    }

    // --- CAPP, by approach type ---

    [Fact]
    public void Capp_Ils_ReadsBackTheIlsAndRunway() =>
        AssertReadback(
            Dispatch("CAPP ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproach,
            "cleared ILS runway 28R approach",
            "cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Capp_RnavWithNoVariant_ReadsBackRnavAndRunway() =>
        AssertReadback(
            Dispatch("CAPP R10R", MakeWestArrival()),
            CanonicalCommandType.ClearedApproach,
            "cleared RNAV runway 10R approach",
            "cleared RNAV runway one zero right approach"
        );

    [Fact]
    public void Capp_RnavZ_SaysTheVariantBeforeTheRunway() =>
        AssertReadback(
            Dispatch("CAPP H28RZ", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproach,
            "cleared RNAV Z runway 28R approach",
            "cleared RNAV Zulu runway two eight right approach"
        );

    [Fact]
    public void Capp_Localizer_ReadsBackTheLocalizer() =>
        AssertReadback(
            Dispatch("CAPP LOC28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproach,
            "cleared LOC runway 28R approach",
            "cleared localizer runway two eight right approach"
        );

    [Fact]
    public void Capp_Vor_ReadsBackTheVor() =>
        AssertReadback(
            Dispatch("CAPP S10R", MakeWestArrival()),
            CanonicalCommandType.ClearedApproach,
            "cleared VOR runway 10R approach",
            "cleared VOR runway one zero right approach"
        );

    [Fact]
    public void Capp_Lda_ReadsBackTheLda() =>
        AssertReadback(
            Dispatch("CAPP LDA19R", MakeConcordArrival()),
            CanonicalCommandType.ClearedApproach,
            "cleared LDA runway 19R approach",
            "cleared LDA runway one nine right approach"
        );

    /// <summary>A bare CAPP clears the expected approach, so it reads that approach back in full, never "cleared approach".</summary>
    [Fact]
    public void BareCapp_ResolvedToTheExpectedApproach_ReadsItBackInFull()
    {
        AircraftState aircraft = MakeArrival(altitude: 3000);
        aircraft.Approach.Expected = "I28R";

        AssertReadback(
            Dispatch("CAPP", aircraft),
            CanonicalCommandType.ClearedApproach,
            "cleared ILS runway 28R approach",
            "cleared ILS runway two eight right approach"
        );
    }

    [Fact]
    public void Capp_DirectToAFixWithACrossingAltitude_ReadsBackTheRouteAndRestrictionFirst() =>
        AssertReadback(
            Dispatch("CAPP DCT SUNOL CFIX A034 ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproach,
            "cleared direct SUNOL, cross SUNOL at or above 3400, cleared ILS runway 28R approach",
            "cleared direct sunol, cross sunol at or above three thousand four hundred, cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Capp_AtAFix_ReadsBackTheFixFirst() =>
        AssertReadback(
            Dispatch("CAPP AT SUNOL ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproach,
            "at SUNOL, cleared ILS runway 28R approach",
            "at sunol, cleared ILS runway two eight right approach"
        );

    /// <summary>
    /// A crossing restriction at the fix the clearance starts at names that fix itself, so the readback drops the "at" lead
    /// (7110.65 4-8-1.a.4: "cross SUNOL at or above three thousand four hundred, cleared ILS ...").
    /// </summary>
    [Fact]
    public void Capp_AtAFixWithACrossingAltitudeThere_ReadsBackTheCrossingWithoutTheAtLead()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("SUNOL") ?? throw new InvalidOperationException("SUNOL missing");
        var capp = new ClearedApproachCommand("ILS28R", null, false, "SUNOL", lat, lon, null, null, null, 3400, CrossFixAltitudeType.AtOrAbove);

        AssertReadback(
            CommandDispatcher.Dispatch(capp, MakeArrival(altitude: 3000), TestDispatch.Context(new Random(1))),
            CanonicalCommandType.ClearedApproach,
            "cross SUNOL at or above 3400, cleared ILS runway 28R approach",
            "cross sunol at or above three thousand four hundred, cleared ILS runway two eight right approach"
        );
    }

    /// <summary>
    /// The handler clears no circling approach (it has no runway to fly to), so a dispatched CAPP to one is refused and has no
    /// readback; the naming helper still names it ("VOR Alpha", see <see cref="ForProcedure_NamesTheResolvedApproach"/>).
    /// </summary>
    [Fact]
    public void Capp_Circling_IsRefusedWithNoReadback()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "C172",
            TrueHeading = new TrueHeading(120),
            Altitude = 4000,
            IndicatedAirspeed = 100,
            Position = new LatLon(33.98, -117.70),
            FlightPlan = new AircraftFlightPlan { Destination = "AJO" },
        };

        CommandResult result = Dispatch("CAPP VOR-A", aircraft);

        Assert.False(result.Success);
        Assert.Equal("No runway for approach VOR-A", result.Message);
        Assert.Null(result.PilotReadback);
    }

    [Fact]
    public void Capp_BackCourse_ReadsBackLocalizerBackCourse()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(120),
            Altitude = 3000,
            IndicatedAirspeed = 160,
            Position = new LatLon(37.36, -120.62),
            FlightPlan = new AircraftFlightPlan { Destination = "MCE" },
        };

        AssertReadback(
            Dispatch("CAPP B12", aircraft),
            CanonicalCommandType.ClearedApproach,
            "cleared LOC BC runway 12 approach",
            "cleared localizer back course runway one two approach"
        );
    }

    // --- Straight in, join, forced twins ---

    [Fact]
    public void Cappsi_ReadsBackStraightIn() =>
        AssertReadback(
            Dispatch("CAPPSI ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproachStraightIn,
            "cleared straight-in ILS runway 28R approach",
            "cleared straight in ILS runway two eight right approach"
        );

    [Fact]
    public void Japp_ReadsBackJoinThe() =>
        AssertReadback(
            Dispatch("JAPP ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.JoinApproach,
            "join the ILS runway 28R approach",
            "join the ILS runway two eight right approach"
        );

    [Fact]
    public void Jappsi_ReadsBackJoinTheStraightIn() =>
        AssertReadback(
            Dispatch("JAPPSI ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.JoinApproachStraightIn,
            "join the straight-in ILS runway 28R approach",
            "join the straight in ILS runway two eight right approach"
        );

    [Fact]
    public void Cappf_ReadsBackTheSameWordsAsCapp() =>
        AssertReadback(
            Dispatch("CAPPF ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.ClearedApproachForce,
            "cleared ILS runway 28R approach",
            "cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Jappf_ReadsBackTheSameWordsAsJapp() =>
        AssertReadback(
            Dispatch("JAPPF ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.JoinApproachForce,
            "join the ILS runway 28R approach",
            "join the ILS runway two eight right approach"
        );

    [Fact]
    public void Ptacf_ReadsBackTheSameWordsAsPtac() =>
        AssertReadback(
            Dispatch("PTACF 240 030 ILS28R", MakeArrival(altitude: 5000)),
            CanonicalCommandType.PositionTurnAltitudeClearanceForce,
            "fly heading 240, descend and maintain 3000, cleared ILS runway 28R approach",
            "fly heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"
        );

    // --- PTAC ---

    [Fact]
    public void Ptac_AboveTheAssignedAltitude_ReadsBackDescend() =>
        AssertReadback(
            Dispatch("PTAC 240 030 ILS28R", MakeArrival(altitude: 5000)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "fly heading 240, descend and maintain 3000, cleared ILS runway 28R approach",
            "fly heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Ptac_BelowTheAssignedAltitude_ReadsBackClimb() =>
        AssertReadback(
            Dispatch("PTAC 240 030 ILS28R", MakeArrival(altitude: 2000)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "fly heading 240, climb and maintain 3000, cleared ILS runway 28R approach",
            "fly heading two four zero, climb and maintain three thousand, cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Ptac_AtTheAssignedAltitude_ReadsBackMaintain() =>
        AssertReadback(
            Dispatch("PTAC 240 030 ILS28R", MakeArrival(altitude: 3000)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "fly heading 240, maintain 3000, cleared ILS runway 28R approach",
            "fly heading two four zero, maintain three thousand, cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Ptac_PresentHeading_ReadsBackFlyPresentHeading() =>
        AssertReadback(
            Dispatch("PTAC PH 030 ILS28R", MakeArrival(altitude: 5000)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "fly present heading, descend and maintain 3000, cleared ILS runway 28R approach",
            "fly present heading, descend and maintain three thousand, cleared ILS runway two eight right approach"
        );

    /// <summary>
    /// <c>PA</c> keeps the altitude the aircraft is at, rounded to 100 ft, so the readback says "maintain" even when the
    /// rounding lands above or below the present altitude: an aircraft at 2,960 ft does not "climb and maintain three thousand".
    /// </summary>
    [Fact]
    public void Ptac_PresentAltitude_ReadsBackMaintainWhateverTheRounding() =>
        AssertReadback(
            Dispatch("PTAC 240 PA ILS28R", MakeArrival(altitude: 2960)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "fly heading 240, maintain 3000, cleared ILS runway 28R approach",
            "fly heading two four zero, maintain three thousand, cleared ILS runway two eight right approach"
        );

    /// <summary>A PTAC given with a turn direction reads it back as given (7110.65 5-6-2.a.1, AIM 4-4-7.b.2).</summary>
    [Fact]
    public void Ptac_WithLeftTurn_ReadsBackTurnLeftHeading() =>
        AssertReadback(
            Dispatch("PTAC L240 030 ILS28R", MakeArrival(altitude: 5000)),
            CanonicalCommandType.PositionTurnAltitudeClearance,
            "turn left heading 240, descend and maintain 3000, cleared ILS runway 28R approach",
            "turn left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"
        );

    [Fact]
    public void Ptacf_WithRightTurn_ReadsBackTurnRightHeading() =>
        AssertReadback(
            Dispatch("PTACF R240 030 ILS28R", MakeArrival(altitude: 5000)),
            CanonicalCommandType.PositionTurnAltitudeClearanceForce,
            "turn right heading 240, descend and maintain 3000, cleared ILS runway 28R approach",
            "turn right heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach"
        );

    // --- Later blocks: no handler readback, the parsed command speaks ---

    private static PilotSpeechText LaterBlockReadback(string compound)
    {
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound(compound).Value!,
            new CommandResult(true, "queued"),
            MakeArrival(altitude: 5000),
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        Assert.NotNull(readback);
        return readback;
    }

    [Fact]
    public void Ptac_InALaterBlock_ReadsBackHeadingAltitudeAndApproach()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; PTAC 240 030 ILS28R");

        Assert.Contains("then fly heading 240, descend and maintain 3000, cleared ILS runway 28R approach", readback.Terminal);
        Assert.Contains(
            "then fly heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach",
            readback.Tts
        );
    }

    [Fact]
    public void Ptac_WithTurnInALaterBlock_ReadsBackTheTurn()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; PTAC R240 030 ILS28R");

        Assert.Contains("then turn right heading 240, descend and maintain 3000, cleared ILS runway 28R approach", readback.Terminal);
        Assert.Contains(
            "then turn right heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach",
            readback.Tts
        );
    }

    /// <summary>A later-block PTAC PH PA names no heading or altitude of its own: it reads back the present heading and the approach.</summary>
    [Fact]
    public void PtacPresentHeadingAndAltitude_InALaterBlock_ReadsBackPresentHeadingAndApproach()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; PTAC PH PA ILS28R");

        Assert.EndsWith("then fly present heading, cleared ILS runway 28R approach", readback.Terminal);
        Assert.Contains("then fly present heading, cleared ILS runway two eight right approach,", readback.Tts);
    }

    [Fact]
    public void Japp_InALaterBlock_ReadsBackJoinThe()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; JAPP ILS28R");

        Assert.EndsWith("then join the ILS runway 28R approach", readback.Terminal);
        Assert.Contains("then join the ILS runway two eight right approach,", readback.Tts);
    }

    [Fact]
    public void Cappsi_InALaterBlock_ReadsBackStraightIn()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; CAPPSI ILS28R");

        Assert.EndsWith("then cleared straight-in ILS runway 28R approach", readback.Terminal);
        Assert.Contains("then cleared straight in ILS runway two eight right approach,", readback.Tts);
    }

    [Fact]
    public void Capp_InALaterBlock_WithAnIdNamingNoType_ReadsBackClearedApproach()
    {
        PilotSpeechText readback = LaterBlockReadback("DM 30; CAPP Z28R");

        Assert.EndsWith("then cleared approach", readback.Terminal);
        Assert.Contains("then cleared approach,", readback.Tts);
    }

    [Fact]
    public void Capp_InALaterBlock_ReadsBackTheApproachFromItsId()
    {
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound("DM 30; CAPP ILS28R").Value!,
            new CommandResult(true, "queued"),
            MakeArrival(altitude: 5000),
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.Contains("then cleared ILS runway 28R approach", readback.Terminal);
        Assert.Contains("then cleared ILS runway two eight right approach", readback.Tts);
    }

    [Fact]
    public void BareCapp_InALaterBlock_ReadsBackClearedApproach()
    {
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound("DM 30; CAPP").Value!,
            new CommandResult(true, "queued"),
            MakeArrival(altitude: 5000),
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.EndsWith("then cleared approach", readback.Terminal);
        Assert.Contains("then cleared approach,", readback.Tts);
    }

    // --- EAPP ---

    [Theory]
    [InlineData("ILS28R", "expect ILS runway two eight right approach", "expect ILS runway 28R approach")]
    [InlineData("RNAV28RZ", "expect RNAV Zulu runway two eight right approach", "expect RNAV Z runway 28R approach")]
    [InlineData("LOC28R", "expect localizer runway two eight right approach", "expect LOC runway 28R approach")]
    [InlineData("VOR10R", "expect VOR runway one zero right approach", "expect VOR runway 10R approach")]
    [InlineData("LDA19R", "expect LDA runway one nine right approach", "expect LDA runway 19R approach")]
    public void Eapp_ReadsBackItsOwnApproachType(string approachId, string tts, string terminal)
    {
        var cmd = new ExpectApproachCommand(approachId, null);

        Assert.Equal(tts, PhraseologyVerbalizer.Verbalize(cmd));
        Assert.Equal(terminal, PhraseologyVerbalizer.VerbalizeTerminal(cmd));
    }

    // --- The naming helper ---

    [Theory]
    [InlineData("I28R", "ILS runway two eight right", "ILS runway 28R")]
    [InlineData("R28R", "RNAV runway two eight right", "RNAV runway 28R")]
    [InlineData("H28R", "RNAV runway two eight right", "RNAV runway 28R")]
    [InlineData("L28R", "localizer runway two eight right", "LOC runway 28R")]
    [InlineData("B12", "localizer back course runway one two", "LOC BC runway 12")]
    [InlineData("V28R", "VOR runway two eight right", "VOR runway 28R")]
    [InlineData("S28R", "VOR runway two eight right", "VOR runway 28R")]
    [InlineData("D28R", "VOR DME runway two eight right", "VOR DME runway 28R")]
    [InlineData("N28R", "NDB runway two eight right", "NDB runway 28R")]
    [InlineData("Q28R", "NDB DME runway two eight right", "NDB DME runway 28R")]
    [InlineData("P28R", "GPS runway two eight right", "GPS runway 28R")]
    [InlineData("X28R", "LDA runway two eight right", "LDA runway 28R")]
    [InlineData("T28R", "TACAN runway two eight right", "TACAN runway 28R")]
    [InlineData("U28R", "SDF runway two eight right", "SDF runway 28R")]
    [InlineData("R28RZ", "RNAV Zulu runway two eight right", "RNAV Z runway 28R")]
    [InlineData("H12-Z", "RNAV Zulu runway one two", "RNAV Z runway 12")]
    [InlineData("ILS28R", "ILS runway two eight right", "ILS runway 28R")]
    [InlineData("RNAV28RY", "RNAV Yankee runway two eight right", "RNAV Y runway 28R")]
    public void ForShorthand_NamesTypeVariantAndRunway(string approachId, string tts, string terminal)
    {
        PilotSpeechText? name = ApproachReadbackName.ForShorthand(approachId);

        Assert.NotNull(name);
        Assert.Equal(tts, name.Tts);
        Assert.Equal(terminal, name.Terminal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("28R")]
    [InlineData("Z28R")]
    [InlineData("VIS28R")]
    public void ForShorthand_NoTypeItCanName_IsNull(string? approachId) => Assert.Null(ApproachReadbackName.ForShorthand(approachId));

    [Theory]
    [InlineData("KOAK", "I28R", "ILS runway two eight right", "ILS runway 28R")]
    [InlineData("KOAK", "H12-Z", "RNAV Zulu runway one two", "RNAV Z runway 12")]
    [InlineData("KOAK", "R28RY", "RNAV Yankee runway two eight right", "RNAV Y runway 28R")]
    [InlineData("KMCE", "B12", "localizer back course runway one two", "LOC BC runway 12")]
    [InlineData("KBLH", "D26", "VOR DME runway two six", "VOR DME runway 26")]
    [InlineData("KFOT", "P11", "GPS runway one one", "GPS runway 11")]
    [InlineData("KCCR", "X19R", "LDA runway one nine right", "LDA runway 19R")]
    [InlineData("KAJO", "VOR-A", "VOR Alpha", "VOR A")]
    public void ForProcedure_NamesTheResolvedApproach(string airport, string approachId, string tts, string terminal)
    {
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach(airport, approachId) ?? throw new InvalidOperationException($"{airport} {approachId} missing");

        PilotSpeechText? name = ApproachReadbackName.ForProcedure(procedure);

        Assert.NotNull(name);
        Assert.Equal(tts, name.Tts);
        Assert.Equal(terminal, name.Terminal);
    }
}
