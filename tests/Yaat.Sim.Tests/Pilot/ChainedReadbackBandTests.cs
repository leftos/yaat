using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A readback chaining things the pilot does together (two or more commands, in one block or several) is never chatty: on a
/// quiet frequency every clause speaks its normal-band words and the readback gets no "alright" or ", thanks"; on a busy one
/// every clause keeps its terse rows. A single command keeps its band.
/// </summary>
public class ChainedReadbackBandTests
{
    private const string Cs = "november one two three alpha bravo";

    public ChainedReadbackBandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(270),
            Altitude = 15000,
            IndicatedAirspeed = 280,
            Position = new LatLon(37.6, -121.8),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    private static PilotSpeechContext Varied(FrequencyLoadBand band, int seed, int second) =>
        new(PilotPersonality.Varied, band, seed, second)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };

    private static CompoundCommand ParseCompound(string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' was refused: {parsed.Reason}");
        return parsed.Value!;
    }

    /// <summary>The spoken readbacks of <paramref name="compound"/> in <paramref name="band"/>, seeds 0-4 by seconds 0-199, in draw order.</summary>
    private static List<string> Spoken(CompoundCommand compound, FrequencyLoadBand band)
    {
        AircraftState ac = MakeAircraft();
        return
        [
            .. Enumerable
                .Range(0, 5)
                .SelectMany(seed =>
                    Enumerable
                        .Range(0, 200)
                        .Select(second => PilotResponder.BuildReadback(compound, ac, Varied(band, seed, second), ActiveRunways.Empty)!.Tts)
                ),
        ];
    }

    [Theory]
    [InlineData("DM 120, SPD 250")]
    [InlineData("DM 120; SPD 250")]
    [InlineData("CM A040, CFIX SUNOL A049")]
    [InlineData("CFIX SUNOL A049; EXP")]
    [InlineData("DM 20; CAPP ILS28R")]
    [InlineData("DM 20, EAPP RNAV28RZ")]
    public void Quiet_ChainedReadback_SpeaksExactlyWhatNormalSpeaks(string text)
    {
        CompoundCommand compound = ParseCompound(text);

        List<string> quiet = Spoken(compound, FrequencyLoadBand.Quiet);
        List<string> normal = Spoken(compound, FrequencyLoadBand.Normal);

        Assert.Equal(normal, quiet);
        Assert.DoesNotContain(quiet, tts => tts.StartsWith("alright", StringComparison.Ordinal));
        Assert.DoesNotContain(quiet, tts => tts.Contains("thanks", StringComparison.Ordinal));
        Assert.DoesNotContain(quiet, tts => tts.Contains("we'll", StringComparison.Ordinal));
    }

    [Fact]
    public void Busy_ChainedReadback_SpeaksTheBusyRowsOfEveryClause()
    {
        AircraftState ac = MakeAircraft();
        var restriction = new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrAbove);
        ParsedCommand crossing = ParseCompound("CFIX SUNOL A049").Blocks[0].Commands[0];
        CompoundCommand compound = ParseCompound("CM A040, CFIX SUNOL A049");

        IReadOnlyList<string> first = PhraseologyVerbalizer.SpokenCandidates(restriction, ac, FrequencyLoadBand.Busy, null);
        IReadOnlyList<string> second = PhraseologyVerbalizer.SpokenCandidates(crossing, ac, FrequencyLoadBand.Busy, null);
        HashSet<string> allowed = [.. first.SelectMany(a => second.Select(b => $"{a}, {b}, {Cs}."))];

        HashSet<string> busy = [.. Spoken(compound, FrequencyLoadBand.Busy)];

        Assert.Subset(allowed, busy);
        Assert.Contains("at or above four thousand, cross sunol at or above four thousand nine hundred, " + Cs + ".", busy);
    }

    [Fact]
    public void Quiet_SingleCommand_StillPicksItsQuietRows() =>
        Assert.Contains($"maintaining at or above four thousand, {Cs}, thanks.", Spoken(ParseCompound("CM A040"), FrequencyLoadBand.Quiet));

    /// <summary>A condition does not chain: a conditioned climb is one clause and is offered its quiet "climbing" row.</summary>
    [Fact]
    public void Quiet_SingleConditionedCommand_KeepsItsQuietRows()
    {
        var compound = new CompoundCommand([new ParsedBlock(AtFixCondition.FromName("SUNOL"), [new ClimbMaintainCommand(16000)])]);

        Assert.Contains(Spoken(compound, FrequencyLoadBand.Quiet), tts => tts.Contains("climbing", StringComparison.Ordinal));
        Assert.DoesNotContain(Spoken(compound, FrequencyLoadBand.Normal), tts => tts.Contains("climbing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A command with no readback clause (acknowledging a contact, a say-exit-fix estimate) chains nothing: the climb beside
    /// it is the only clause and keeps its quiet rows.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Quiet_OneSpokenCommandBesideOneWithNoClause_KeepsItsQuietRows(bool acknowledge)
    {
        ParsedCommand silent = acknowledge ? new AcknowledgePilotContactCommand() : new SayExitFixEstimateCommand();
        var compound = new CompoundCommand([new ParsedBlock(null, [new ClimbMaintainCommand(16000), silent])]);

        Assert.Contains(Spoken(compound, FrequencyLoadBand.Quiet), tts => tts.Contains("climbing", StringComparison.Ordinal));
    }
}
