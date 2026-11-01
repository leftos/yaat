using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Data;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Tests;

/// <summary>
/// The accurate-mode mapping entry: an n-best decoded against the room resolves the callsign and the command text from the
/// hypothesis that names it, and every other decode falls back to the callsign parser on the top hypothesis.
/// </summary>
public sealed class SttAccurateModeTests
{
    public SttAccurateModeTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    [Fact]
    public async Task Resolves_The_Only_Hypothesis_That_Names_A_Room_Callsign()
    {
        TransmissionContext room = Context(new("SWA2495", "B737", [], null), new("UAL1", "B772", [], null));
        var hypotheses = new SttHypotheses(
            [
                new SttHypothesis("runway two eight right cleared to land", [], [], null),
                new SttHypothesis("runway two eight right cleared to land", [], [], null),
                new SttHypothesis("southwest twenty four ninety five runway two eight right cleared to land", [], [], null),
                new SttHypothesis("runway two eight right cleared to land", [], [], null),
            ],
            null,
            false
        );

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "UAL1"], AccurateCommandText.RawHypothesis);

        Assert.Equal(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal(2, mapped.Decode.HypothesisIndex);
        Assert.Equal("SWA2495", mapped.Result.Callsign);
        Assert.Equal(AtcNumberParser.NormalizeDigits("runway two eight right cleared to land"), mapped.Result.CommandText);
        Assert.NotNull(mapped.Result.Canonical);
    }

    [Fact]
    public async Task Strips_A_Trailing_Callsign_Span()
    {
        TransmissionContext room = Context(new("SWA2495", "B737", [], null), new("UAL1", "B772", [], null));
        var hypotheses = new SttHypotheses(
            [new SttHypothesis("turn left heading three one zero southwest twenty four ninety five", [], [], null)],
            null,
            false
        );

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "UAL1"], AccurateCommandText.RawHypothesis);

        Assert.Equal(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("SWA2495", mapped.Result.Callsign);
        Assert.Equal(AtcNumberParser.NormalizeDigits("turn left heading three one zero"), mapped.Result.CommandText);
        Assert.Equal("TL 310", mapped.Result.Canonical);
    }

    [Fact]
    public async Task Falls_Back_To_The_Callsign_Parser_When_The_Room_Lacks_The_Callsign()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));
        var hypotheses = new SttHypotheses(
            [
                new SttHypothesis("november three four six golf turn left heading three one zero", [], [], null),
                new SttHypothesis("november three four six golf turn left heading three one two", [], [], null),
            ],
            null,
            false
        );

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "N346G"], AccurateCommandText.RawHypothesis);

        Assert.NotEqual(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("N346G", mapped.Result.Callsign);
        Assert.Equal("TL 310", mapped.Result.Canonical);
    }

    [Fact]
    public async Task An_Empty_N_Best_Maps_To_Nothing_Without_Throwing()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));

        HypothesesMapResult mapped = await MapAsync(new SttHypotheses([], null, false), room, ["SWA2495"], AccurateCommandText.RawHypothesis);

        Assert.Equal(DecodeState.NoCallsign, mapped.Decode.State);
        Assert.Null(mapped.Result.Callsign);
        Assert.Null(mapped.Result.Canonical);
    }

    [Fact]
    public async Task Raw_Text_Hands_The_Mapper_The_Heard_Kizer()
    {
        HypothesesMapResult mapped = await MapAsync(OneHypothesis(KizerHypothesis), KaiserRoom(), ["SWA2495"], AccurateCommandText.RawHypothesis);

        Assert.Equal(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("SWA2495", mapped.Result.Callsign);
        Assert.Equal(AtcNumberParser.NormalizeDigits("abeam kizer"), mapped.Result.CommandText);
    }

    [Fact]
    public async Task Rewritten_Text_Hands_The_Mapper_The_Spoken_Kaiser()
    {
        HypothesesMapResult mapped = await MapAsync(OneHypothesis(KizerHypothesis), KaiserRoom(), ["SWA2495"], AccurateCommandText.Rewritten);

        Assert.Equal(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("SWA2495", mapped.Result.Callsign);
        Assert.Equal(AtcNumberParser.NormalizeDigits("abeam kaiser"), mapped.Result.CommandText);
    }

    /// <summary>The transcript whose "kizer" the KAISER landmark's spoken form corrects to "kaiser".</summary>
    private const string KizerHypothesis = "southwest twenty four ninety five abeam kizer";

    private static SttHypotheses OneHypothesis(string text) => new([new SttHypothesis(text, [], [], null)], null, false);

    /// <summary>The room's aircraft beside the KAISER landmark, which gives the heard "kizer" a spoken form to be rewritten to.</summary>
    private static TransmissionContext KaiserRoom()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));
        return room with
        {
            AirportEntities = [.. room.AirportEntities, ContextEntityFactory.Landmark("KOAK", "kaiser", ["kaiser", "kaiser center"])],
        };
    }

    private static TransmissionContext Context(params RoomAircraftInput[] aircraft) =>
        RoomContextFactory.Build(
            new RoomContextCache(),
            new RoomContextRequest("KOAK", false, null, aircraft, []),
            NavigationDatabase.Instance,
            DecoderSettings.Default
        );

    private static Task<HypothesesMapResult> MapAsync(
        SttHypotheses hypotheses,
        TransmissionContext room,
        IReadOnlyList<string> activeCallsigns,
        AccurateCommandText commandText
    ) =>
        SpeechRecognitionService.MapHypothesesWithTraceAsync(
            hypotheses,
            new AccurateDecodeInput(room, DecoderSettings.Default, commandText),
            new SpeechContext(activeCallsigns, [], string.Empty),
            new SpeechMappers(new PhraseologyCommandMapper(), null, null),
            CancellationToken.None
        );
}
