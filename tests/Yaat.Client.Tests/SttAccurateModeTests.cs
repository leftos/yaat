using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Data;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Tests;

/// <summary>
/// The accurate-mode mapping entry: an n-best decoded against the room resolves the callsign and the command text from the
/// decoder's rewritten transcript of the hypothesis that names it, and every other decode falls back to the callsign parser
/// on that transcript.
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

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "UAL1"]);

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

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "UAL1"]);

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

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "N346G"]);

        Assert.NotEqual(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("N346G", mapped.Result.Callsign);
        Assert.Equal("TL 310", mapped.Result.Canonical);
    }

    [Fact]
    public async Task Recovers_An_Active_Ga_Tail_Outside_The_Room_From_Its_Unique_Letter_Extension()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));
        var hypotheses = new SttHypotheses(
            [new SttHypothesis("november nine two two five lien turn left heading three one zero", [], [], null)],
            null,
            false
        );

        HypothesesMapResult mapped = await MapAsync(hypotheses, room, ["SWA2495", "N9225L"]);

        Assert.Equal(DecodeState.NoCallsign, mapped.Decode.State);
        Assert.Equal("N9225L", mapped.Result.Callsign);
    }

    [Fact]
    public async Task An_Empty_N_Best_Maps_To_Nothing_Without_Throwing()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));

        HypothesesMapResult mapped = await MapAsync(new SttHypotheses([], null, false), room, ["SWA2495"]);

        Assert.Equal(DecodeState.NoCallsign, mapped.Decode.State);
        Assert.Null(mapped.Result.Callsign);
        Assert.Null(mapped.Result.Canonical);
    }

    [Fact]
    public async Task Keeps_An_Unaddressed_Say_Again_That_Named_No_Callsign()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));

        HypothesesMapResult mapped = await MapAsync(OneHypothesis("aircraft calling say again"), room, ["SWA2495"]);

        Assert.NotEqual(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("AGAINU", mapped.Result.Canonical);
        Assert.Null(mapped.Result.Callsign);
        Assert.Null(StatusOf(mapped));
    }

    [Fact]
    public async Task Keeps_A_Callsign_Only_Unaddressed_Say_Again_That_Named_No_Callsign()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));

        HypothesesMapResult mapped = await MapAsync(OneHypothesis("station calling say again your callsign"), room, ["SWA2495"]);

        Assert.NotEqual(DecodeState.Resolved, mapped.Decode.State);
        Assert.Equal("AGAINU CS", mapped.Result.Canonical);
        Assert.Null(mapped.Result.Callsign);
        Assert.Null(StatusOf(mapped));
    }

    [Fact]
    public async Task Withholds_An_Ordinary_Command_That_Named_No_Callsign()
    {
        TransmissionContext room = Context(new RoomAircraftInput("SWA2495", "B737", [], null));

        HypothesesMapResult mapped = await MapAsync(OneHypothesis("climb and maintain five thousand"), room, ["SWA2495"]);

        Assert.NotEqual(DecodeState.Resolved, mapped.Decode.State);
        Assert.Null(mapped.Result.Canonical);
        Assert.NotNull(StatusOf(mapped));
    }

    /// <summary>
    /// The status the live pipeline would show for <paramref name="mapped"/>: the decode trace <c>MapAccurateAsync</c> builds
    /// around it, the callsign it accepted and the canonical it kept.
    /// </summary>
    private static string? StatusOf(HypothesesMapResult mapped) =>
        SpeechRecognitionService.CallsignUnclearStatus(
            new PressDecodeTrace(TranscriptionMode.Accurate, null, mapped.Decode.State, mapped.Decode.BestGuess?.Entity.Value, null, null),
            mapped.Result.Callsign,
            mapped.Result.Canonical
        );

    [Fact]
    public async Task Hands_The_Mapper_The_Spoken_Form_The_Decoder_Rewrote()
    {
        HypothesesMapResult mapped = await MapAsync(OneHypothesis(KizerHypothesis), KaiserRoom(), ["SWA2495"]);

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

    private static Task<HypothesesMapResult> MapAsync(SttHypotheses hypotheses, TransmissionContext room, IReadOnlyList<string> activeCallsigns) =>
        SpeechRecognitionService.MapHypothesesWithTraceAsync(
            hypotheses,
            new AccurateDecodeInput(room, DecoderSettings.Default),
            new SpeechContext(activeCallsigns, [], string.Empty),
            new SpeechMappers(new PhraseologyCommandMapper(), null, null),
            CancellationToken.None
        );
}
