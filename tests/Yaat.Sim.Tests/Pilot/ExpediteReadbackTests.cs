using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pilot readbacks for EXP. The rule table is keyed on canonical type without seeing arguments or
/// aircraft state, so every EXP form used to fall onto the first-declared "expedite climb …"
/// template — a descending aircraft read back a climb, and a taxiing one read back a climb too.
///
/// The correct forms: FAA JO 7110.65 §2-1-5.b makes <c>EXP &lt;alt&gt;</c> the same clearance as
/// CM/DM with an expedite qualifier appended, so its readback mirrors the CM/DM wording and must
/// carry the altitude (AIM §4-4-7.b). §3-7-2.j gives the two ground forms as "TAXI WITHOUT DELAY"
/// and "EXIT … WITHOUT DELAY".
/// </summary>
public sealed class ExpediteReadbackTests
{
    public ExpediteReadbackTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Aircraft(double altitude)
    {
        return new AircraftState
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            Position = new LatLon(37.8, -122.2),
            TrueHeading = new TrueHeading(193),
            TrueTrack = new TrueHeading(193),
            Altitude = altitude,
            IndicatedAirspeed = 115,
        };
    }

    private static PilotSpeechText Readback(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess);
        PilotSpeechText? result = PilotResponder.BuildReadback(parsed.Value!, aircraft, ActiveRunways.Empty);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void ExpediteToLowerAltitude_ReadsBackADescent()
    {
        AircraftState ac = Aircraft(2658);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        PilotSpeechText result = Readback(ac, "EXP 014");

        Assert.Equal("descend and maintain 1400, expedite descent", result.Terminal);
        Assert.Contains("descend and maintain one thousand four hundred, expedite descent", result.Tts);
        Assert.DoesNotContain("climb", result.Tts);
    }

    [Fact]
    public void ExpediteToHigherAltitude_ReadsBackAClimb()
    {
        AircraftState ac = Aircraft(2000);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        PilotSpeechText result = Readback(ac, "EXP 110");

        Assert.Equal("climb and maintain 11000, expedite climb", result.Terminal);
        Assert.Contains("climb and maintain one one thousand, expedite climb", result.Tts);
        Assert.DoesNotContain("descen", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileDescending_ReadsBackADescent()
    {
        AircraftState ac = Aircraft(5000);
        ac.Targets.TargetAltitude = 2000;

        PilotSpeechText result = Readback(ac, "EXP");

        Assert.Equal("expedite descent", result.Terminal);
        Assert.DoesNotContain("climb", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileClimbing_ReadsBackAClimb()
    {
        AircraftState ac = Aircraft(2000);
        ac.Targets.TargetAltitude = 9000;

        PilotSpeechText result = Readback(ac, "EXP");

        Assert.Equal("expedite climb", result.Terminal);
        Assert.DoesNotContain("descen", result.Tts);
    }

    [Fact]
    public void BareExpedite_WhileTaxiing_ReadsBackTaxiWithoutDelay()
    {
        AircraftState ac = Aircraft(13);
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        PilotSpeechText result = Readback(ac, "EXP");

        Assert.Equal("taxi without delay", result.Terminal);
        Assert.DoesNotContain("climb", result.Tts);
    }

    private const string SpokenCallsign = "november two bravo papa";

    /// <summary>The readbacks of <paramref name="command"/> for a varied pilot in <paramref name="band"/>, seconds 0-199.</summary>
    private static List<PilotSpeechText> VariedReadbacks(AircraftState aircraft, ParsedCommand command, FrequencyLoadBand band, int seed) =>
        [
            .. Enumerable
                .Range(0, 200)
                .Select(second =>
                    PilotResponder.BuildReadback(
                        new CompoundCommand([new ParsedBlock(null, [command])]),
                        aircraft,
                        new PilotSpeechContext(PilotPersonality.Varied, band, seed, second)
                        {
                            ShortenCallsign = false,
                            GroupContactFrequency = false,
                            CurrentIndicatedAirspeed = null,
                        },
                        ActiveRunways.Empty
                    )!
                ),
        ];

    /// <summary>
    /// A busy expedite with an altitude keeps the verb and says "expediting", in the digit form or, from
    /// 10,000 to 17,999 ft, the group form; never a bare "wilco"; the Terminal text stays canonical.
    /// </summary>
    [Fact]
    public void ExpediteToAltitude_VariedBusy_SaysClimbingExpeditingInEitherNumberForm()
    {
        List<PilotSpeechText> readbacks = VariedReadbacks(Aircraft(5000), new ExpediteCommand(11000), FrequencyLoadBand.Busy, 1);
        List<string> spoken = [.. readbacks.Select(readback => readback.Tts)];

        // Seed 1: the first second in 0-199 that speaks the group form.
        Assert.Equal(1, spoken.IndexOf($"climbing eleven thousand, expediting, {SpokenCallsign}."));
        Assert.Contains($"climbing one one thousand, expediting, {SpokenCallsign}.", spoken);
        Assert.All(spoken, tts => Assert.StartsWith("climbing ", tts, StringComparison.Ordinal));
        Assert.DoesNotContain(spoken, tts => tts.Contains("wilco", StringComparison.Ordinal));
        Assert.All(readbacks, readback => Assert.Equal("climb and maintain 11000, expedite climb", readback.Terminal));
    }

    [Fact]
    public void ExpediteToFlightLevel_VariedBusy_NeverSpeaksTheGroupForm()
    {
        IEnumerable<string> spoken = Enumerable
            .Range(0, 5)
            .SelectMany(seed => VariedReadbacks(Aircraft(5000), new ExpediteCommand(24000), FrequencyLoadBand.Busy, seed))
            .Select(readback => readback.Tts);

        Assert.All(spoken, tts => Assert.Equal($"climbing flight level two four zero, expediting, {SpokenCallsign}.", tts));
    }

    [Fact]
    public void BareExpedite_WhileDescending_VariedQuiet_MaySayExpeditingDescent()
    {
        AircraftState ac = Aircraft(5000);
        ac.Targets.TargetAltitude = 2000;
        string[] allowed = [$"expedite descent, {SpokenCallsign}.", $"expediting descent, {SpokenCallsign}."];

        List<string> spoken = [.. VariedReadbacks(ac, new ExpediteCommand(), FrequencyLoadBand.Quiet, 1).Select(readback => readback.Tts)];

        // Seed 1: the first second in 0-199 whose pick is the quiet row.
        Assert.Equal(1, spoken.IndexOf(allowed[1]));
        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
    }

    [Fact]
    public void BareExpedite_WhileTaxiing_VariedQuiet_SaysTaxiingWithoutDelayOrTheCanonical()
    {
        AircraftState ac = Aircraft(13);
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        string[] allowed = [$"taxi without delay, {SpokenCallsign}.", $"taxiing without delay, {SpokenCallsign}."];

        List<string> spoken = [.. VariedReadbacks(ac, new ExpediteCommand(), FrequencyLoadBand.Quiet, 1).Select(readback => readback.Tts)];

        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
        Assert.Contains(allowed[1], spoken);
    }

    [Fact]
    public void BareExpedite_WhileLeavingTheRunway_VariedQuiet_SaysExitingWithoutDelayOrTheCanonical()
    {
        AircraftState ac = Aircraft(13);
        ac.IsOnGround = true;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new LandingPhase { Status = PhaseStatus.Active });
        string[] allowed = [$"exit without delay, {SpokenCallsign}.", $"exiting without delay, {SpokenCallsign}."];

        List<string> spoken = [.. VariedReadbacks(ac, new ExpediteCommand(), FrequencyLoadBand.Quiet, 1).Select(readback => readback.Tts)];

        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
        Assert.Contains(allowed[1], spoken);
    }

    [Fact]
    public void BareExpedite_WhileDescending_VariedNormal_MaySayExpediting()
    {
        AircraftState ac = Aircraft(5000);
        ac.Targets.TargetAltitude = 2000;
        string[] allowed = [$"expedite descent, {SpokenCallsign}.", $"expediting, {SpokenCallsign}."];

        List<string> spoken = [.. VariedReadbacks(ac, new ExpediteCommand(), FrequencyLoadBand.Normal, 1).Select(readback => readback.Tts)];

        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
        Assert.Contains(allowed[1], spoken);
    }
}
