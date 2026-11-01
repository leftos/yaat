using Xunit;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Testing;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--slots</c> run's per-clip work: how a sherpa hypothesis becomes a decoder hypothesis, how the context decoder runs
/// over a clip's n-best with and without resolution, and the set-up errors a corpus folder without its gold raises.
/// </summary>
public sealed class CorpusSlotRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-slot-run-").FullName;

    public CorpusSlotRunTests() => TestVnasData.EnsureInitialized();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ToDecoderHypothesis_LeadingSpaceBecomesWordMark()
    {
        var hypothesis = CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25, -0.5], -1.5));

        Assert.Equal("number", hypothesis.Text);
        Assert.Equal([new TokenLogProb("▁num", -0.25), new TokenLogProb("ber", -0.5)], hypothesis.Tokens);
        Assert.Equal(-1.5, hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_NoTokens_NullTokens()
    {
        var hypothesis = CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [], [], null));

        Assert.Null(hypothesis.Tokens);
        Assert.Null(hypothesis.AcousticScore);
    }

    [Fact]
    public void ToDecoderHypothesis_CountMismatch_Throws()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CorpusSlotRun.ToDecoderHypothesis(new SttHypothesis("number", [" num", "ber"], [-0.25], null))
        );

        Assert.Equal("A hypothesis has 2 tokens but 1 token log-probabilities: 'number'.", error.Message);
    }

    [Fact]
    public void Decode_NoTranscript_NoCallsign()
    {
        DecodeResult result = CorpusSlotRun.Decode(new SttHypotheses([], null, false), Context(), true, DecoderSettings.Default);

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Null(result.Callsign);
        Assert.Empty(result.Entities);
        Assert.Equal(string.Empty, result.RewrittenTranscript);
    }

    [Fact]
    public void Decode_ResolveOff_FirstHypothesisExactOnly()
    {
        var nearMiss = new SttHypotheses([new SttHypothesis("southwast two four nine five", [], [], null)], null, false);

        DecodeResult fuzzy = CorpusSlotRun.Decode(nearMiss, Context(), true, DecoderSettings.Default);
        DecodeResult result = CorpusSlotRun.Decode(nearMiss, Context(), false, DecoderSettings.Default);

        Assert.Equal(DecodeState.Resolved, fuzzy.State);
        Assert.Equal("SWA2495", fuzzy.Callsign?.Entity.Value);

        Assert.NotEqual(DecodeState.Resolved, result.State);
    }

    [Fact]
    public void Decode_ResolveOn_PicksResolvedHypothesis()
    {
        DecodeResult result = CorpusSlotRun.Decode(Transcript(), Context(), true, DecoderSettings.Default);

        Assert.Equal(DecodeState.Resolved, result.State);
        Assert.Equal("SWA2495", result.Callsign?.Entity.Value);
        Assert.Equal(1, result.HypothesisIndex);
    }

    [Fact]
    public void TryCreate_NoRoomsFile_Error()
    {
        File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), """{ "koak-gnd_seg0001": [] }""");

        var run = CorpusSlotRun.TryCreate(_dir, [Segment("koak-gnd_seg0001")], Options(), out string? error);

        Assert.Null(run);
        Assert.Equal($"--slots needs rooms.json in {_dir}", error);
    }

    [Fact]
    public void TryCreate_ClipMissingFromSlots_Error()
    {
        File.WriteAllText(
            Path.Combine(_dir, CorpusRooms.FileName),
            """{ "feeds": { "koak-gnd": { "airport": "KOAK", "facility": "gnd", "seed": 1 } } }"""
        );
        File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), """{ "koak-gnd_seg0001": [] }""");

        var run = CorpusSlotRun.TryCreate(_dir, [Segment("koak-gnd_seg0001"), Segment("koak-gnd_seg0002")], Options(), out string? error);

        Assert.Null(run);
        Assert.Equal("koak-gnd_seg0002 has no entry in slots.json", error);
    }

    [Fact]
    public void TryCreate_UnknownAirport_Error()
    {
        File.WriteAllText(
            Path.Combine(_dir, CorpusRooms.FileName),
            """{ "feeds": { "koak-gnd": { "airport": "ZZZZ", "facility": "gnd", "seed": 1 } } }"""
        );
        File.WriteAllText(Path.Combine(_dir, CorpusSlots.FileName), """{ "koak-gnd_seg0001": [] }""");

        var run = CorpusSlotRun.TryCreate(_dir, [Segment("koak-gnd_seg0001")], Options(), out string? error);

        Assert.Null(run);
        Assert.NotNull(error);
        Assert.Contains("ZZZZ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BiasFor_NoRoom_ArmBiasUnchanged()
    {
        CorpusSlotRun run = CreateRun(Options(), "{}");
        var bias = new SttBias(string.Empty, ["Southwest"]);

        Assert.Same(bias, run.BiasFor(Segment(GoldClip), bias));
    }

    [Fact]
    public void BiasFor_RoomWithoutForms_NoHotwords()
    {
        CorpusSlotRun run = CreateRun(RoomOptions(SlotRoom.Oracle), "{}");

        SttBias bias = run.BiasFor(Segment(EmptyClip), new SttBias(string.Empty, ["Southwest"]));

        Assert.Empty(bias.Hotwords);
    }

    [Fact]
    public void BiasFor_AppliesCasing()
    {
        CorpusSlotRun run = CreateRun(RoomOptions(SlotRoom.Oracle), "{}");

        SttBias bias = run.BiasFor(Segment(GoldClip), new SttBias(string.Empty, []));

        Assert.Contains("southwest twenty four ninety five", RoomHotwords.Select(run.Context(GoldClip, SlotRoom.Oracle), 300));
        Assert.Contains("Southwest Twenty Four Ninety Five", bias.Hotwords);
    }

    [Fact]
    public void BiasFor_FormWithSlash_ThrowsNamingSegment()
    {
        CorpusSlotRun run = CreateRun(RoomOptions(SlotRoom.Oracle), """{ "QXE": "hori/zon" }""");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            run.BiasFor(Segment(SlashClip), new SttBias(string.Empty, []))
        );

        Assert.Contains(SlashClip, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arms_NeverShareClips()
    {
        CorpusSlotRun run = CreateRun(Options(), "{}");
        CorpusSlotArm first = run.StartArm();
        CorpusSlotArm second = run.StartArm();

        first.Add(Segment(GoldClip), Spoken(), NoBias);
        second.Add(Segment(OtherGoldClip), Spoken(), NoBias);
        second.Add(Segment(EmptyClip), Spoken(), NoBias);

        Assert.All(first.Results(), room => Assert.Equal(1, room.Groups[0].Clips));
        Assert.All(second.Results(), room => Assert.Equal(2, room.Groups[0].Clips));
    }

    [Fact]
    public void Arm_SumsCoveragePerKind()
    {
        CorpusSlotArm arm = CreateRun(Options(), "{}").StartArm();

        arm.Add(Segment(GoldClip), Spoken(), NoBias);
        arm.Add(Segment(OtherGoldClip), Spoken(), NoBias);

        CorpusSlotRoomResult oracle = arm.Results()[2];
        Assert.Equal(2, oracle.Coverage.Single(row => row.Kind == "callsign").Slots);
        Assert.Equal(2, oracle.Coverage.Single(row => row.Kind == "taxiway").Slots);
        Assert.Equal(0, oracle.Coverage.Single(row => row.Kind == "fix").Slots);
    }

    [Fact]
    public void Arm_CountsConfidenceGatedClips()
    {
        CorpusSlotArm arm = CreateRun(Options(), "{}").StartArm();

        CorpusSlotClipLines withTokens = arm.Add(Segment(GoldClip), SpokenWithTokens(), NoBias);
        CorpusSlotClipLines withoutTokens = arm.Add(Segment(OtherGoldClip), Spoken(), NoBias);

        IReadOnlyList<CorpusSlotRoomResult> results = arm.Results();
        for (int i = 0; i < results.Count; i++)
        {
            int gated = (withTokens.Lines[i].ConfidenceGated ? 1 : 0) + (withoutTokens.Lines[i].ConfidenceGated ? 1 : 0);
            Assert.Equal(gated, results[i].ConfidenceGated);
            Assert.Equal(1, results[i].ConfidenceGated);
        }
    }

    [Fact]
    public void Arm_ResolvedWerRowsAreAllThenProvenanceThenFeeds()
    {
        CorpusSlotArm arm = CreateRun(Options() with { Resolve = true }, "{}").StartArm();

        arm.Add(Segment(GoldClip), Spoken(), NoBias);
        arm.Add(Segment(OtherGoldClip), Spoken(), NoBias);

        Assert.All(arm.Results(), room => Assert.Equal(["all", "owner", "owner+agreed", "koak-gnd"], room.ResolvedWer!.Select(row => row.Role)));
    }

    [Fact]
    public void Arm_ResolveOff_NoResolvedWer() =>
        Assert.All(AddOne(CreateRun(Options(), "{}").StartArm()).Results(), room => Assert.Null(room.ResolvedWer));

    [Fact]
    public void Arm_HotwordPhrases_NullWithoutRoomHotwords()
    {
        CorpusSlotArm arm = CreateRun(Options(), "{}").StartArm();

        CorpusSlotClipLines lines = arm.Add(Segment(GoldClip), Spoken(), new SttBias(string.Empty, ["Southwest"]));

        Assert.Null(lines.HotwordPhrases);
    }

    [Fact]
    public void Arm_HotwordPhrases_CountWithRoomHotwords()
    {
        CorpusSlotArm arm = CreateRun(RoomOptions(SlotRoom.Oracle), "{}").StartArm();
        SttBias bias = arm.BiasFor(Segment(GoldClip), NoBias);

        CorpusSlotClipLines lines = arm.Add(Segment(GoldClip), Spoken(), bias);

        Assert.NotEmpty(bias.Hotwords);
        Assert.Equal(bias.Hotwords.Count, lines.HotwordPhrases);
    }

    [Fact]
    public void Arm_DecodeLinesInRoomOrder()
    {
        CorpusSlotArm arm = CreateRun(Options(), "{}").StartArm();

        CorpusSlotClipLines lines = arm.Add(Segment(GoldClip), Spoken(), NoBias);

        Assert.Equal(["unpadded", "padded", "oracle"], lines.Lines.Select(line => line.Room));
    }

    [Fact]
    public void TwoPassTarget_Resolved_NarrowsToThatAircraft()
    {
        TransmissionContext context = TwoPassContext();
        RoomAircraft southwest = context.Aircraft[0];

        CorpusTwoPassTarget target = CorpusSlotRun.TwoPassTarget(
            GoldClip,
            Decoded(DecodeState.Resolved, Matched(southwest), null),
            context,
            SlotRoom.Padded,
            TwoPassList.Aircraft
        );

        Assert.Equal("resolved", target.Outcome);
        Assert.Equal("SWA2495", target.Aircraft);
        Assert.NotNull(target.Narrow);
        Assert.Equal("KOAK", target.Narrow.AirportId);
        Assert.Same(southwest, Assert.Single(target.Narrow.Aircraft));
        Assert.Equal([OwnRunway], target.Narrow.Aircraft[0].OwnEntities);
        Assert.Empty(target.Narrow.AirportEntities);
    }

    [Fact]
    public void TwoPassTarget_AircraftAndAirport_KeepsAirportEntities()
    {
        TransmissionContext context = TwoPassContext();

        CorpusTwoPassTarget target = CorpusSlotRun.TwoPassTarget(
            GoldClip,
            Decoded(DecodeState.Resolved, Matched(context.Aircraft[0]), null),
            context,
            SlotRoom.Padded,
            TwoPassList.AircraftAndAirport
        );

        Assert.NotNull(target.Narrow);
        Assert.Same(context.Aircraft[0], Assert.Single(target.Narrow.Aircraft));
        Assert.Equal([AirportTaxiway], target.Narrow.AirportEntities);
    }

    [Fact]
    public void TwoPassTarget_SayAgain_UsesBestGuess()
    {
        TransmissionContext context = TwoPassContext();
        RoomAircraft united = context.Aircraft[1];

        CorpusTwoPassTarget target = CorpusSlotRun.TwoPassTarget(
            GoldClip,
            Decoded(DecodeState.SayAgain, null, Matched(united)),
            context,
            SlotRoom.Padded,
            TwoPassList.Aircraft
        );

        Assert.Equal("sayagain", target.Outcome);
        Assert.Equal("UAL123", target.Aircraft);
        Assert.NotNull(target.Narrow);
        Assert.Same(united, Assert.Single(target.Narrow.Aircraft));
    }

    [Fact]
    public void TwoPassTarget_NoCallsign_NoPassTwo()
    {
        CorpusTwoPassTarget target = CorpusSlotRun.TwoPassTarget(
            GoldClip,
            Decoded(DecodeState.NoCallsign, null, null),
            TwoPassContext(),
            SlotRoom.Padded,
            TwoPassList.Aircraft
        );

        Assert.Equal(new CorpusTwoPassTarget("nocallsign", null, null), target);
    }

    [Fact]
    public void TwoPassBias_NNumberOnly_EmptyListNoPassTwo()
    {
        CorpusSlotRun run = CreateRun(RoomOptions(SlotRoom.Oracle), "{}");
        var november = new RoomAircraft(CallsignEntity("N123AB", ["N123AB"]), []);
        var context = new TransmissionContext("KOAK", [november], [], []);
        CorpusTwoPassTarget target = CorpusSlotRun.TwoPassTarget(
            GoldClip,
            Decoded(DecodeState.Resolved, Matched(november), null),
            context,
            SlotRoom.Oracle,
            TwoPassList.Aircraft
        );
        Assert.NotNull(target.Narrow);

        SttBias? bias = run.TwoPassBias(GoldClip, target.Narrow, NoBias);
        CorpusTwoPassTarget settled = target.Settled(bias);

        Assert.Null(bias);
        Assert.Equal(new CorpusTwoPassTarget("empty", "N123AB", null), settled);
    }

    [Fact]
    public void TwoPassBias_AppliesCasingAndCap()
    {
        CorpusSlotRun run = CreateRun(
            new CorpusSlotOptions(true, SlotRoom.Oracle, 1, HotwordCase.Title, TwoPassList.Aircraft, DecoderSettings.Default),
            "{}"
        );
        TransmissionContext context = TwoPassContext();
        var ownFix = new ContextEntity(EntityKind.Fix, "MITOE", [new SpokenForm("mitoe", 1.0)]);
        TransmissionContext narrow = context with { Aircraft = [new RoomAircraft(context.Aircraft[0].Callsign, [ownFix])], AirportEntities = [] };
        Assert.True(RoomHotwords.Select(narrow, 300).Count > 1);
        string first = RoomHotwords.Select(narrow, 1)[0];

        SttBias? bias = run.TwoPassBias(GoldClip, narrow, new SttBias("prompt", ["Other"]));

        Assert.NotNull(bias);
        Assert.Equal("prompt", bias.Prompt);
        string phrase = Assert.Single(bias.Hotwords);
        Assert.Equal(first, phrase, ignoreCase: true);
        Assert.NotEqual(first, phrase);
        Assert.True(char.IsUpper(phrase[0]), phrase);
    }

    [Fact]
    public void TwoPassTarget_AircraftNotInRoom_Throws()
    {
        var stranger = new RoomAircraft(CallsignEntity("DAL55", ["DAL55"]), []);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            CorpusSlotRun.TwoPassTarget(
                GoldClip,
                Decoded(DecodeState.Resolved, Matched(stranger), null),
                TwoPassContext(),
                SlotRoom.Padded,
                TwoPassList.Aircraft
            )
        );

        Assert.Equal($"{GoldClip}: the decoder named DAL55, which is not in the padded room", error.Message);
    }

    [Fact]
    public void TwoPassTarget_ResolvedWithoutCallsign_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            CorpusSlotRun.TwoPassTarget(GoldClip, Decoded(DecodeState.Resolved, null, null), TwoPassContext(), SlotRoom.Padded, TwoPassList.Aircraft)
        );

        Assert.Equal($"{GoldClip}: resolved with no callsign", error.Message);
    }

    [Fact]
    public void PlanTwoPass_ResolvedClip_GivesNarrowBias()
    {
        CorpusSlotRun run = CreateRun(
            new CorpusSlotOptions(true, SlotRoom.Oracle, 300, HotwordCase.Title, TwoPassList.Aircraft, DecoderSettings.Default),
            "{}"
        );

        (CorpusTwoPassTarget target, SttBias? bias) = run.PlanTwoPass(GoldClip, Spoken(), NoBias);

        Assert.Equal("resolved", target.Outcome);
        Assert.Equal("SWA2495", target.Aircraft);
        Assert.NotNull(bias);
        Assert.NotEmpty(bias.Hotwords);
    }

    [Fact]
    public void TwoPassResult_LineText()
    {
        CorpusSlotArm arm = CreateRun(
                new CorpusSlotOptions(true, SlotRoom.Oracle, 300, HotwordCase.Title, TwoPassList.Aircraft, DecoderSettings.Default),
                "{}"
            )
            .StartArm();
        string[] outcomes = ["resolved", "resolved", "resolved", "sayagain", "nocallsign", "nocallsign", "empty"];
        foreach (string outcome in outcomes)
        {
            arm.AddTwoPass(outcome);
        }

        CorpusTwoPassResult? result = arm.TwoPassResult();

        Assert.NotNull(result);
        Assert.Equal(
            "Two-pass (aircraft): re-decoded 4 of 7 clips (3 resolved, 1 from a say-again guess); kept pass 1 for 3 (2 no callsign, 1 empty list)",
            result.Line()
        );
    }

    private const string GoldClip = "koak-gnd_seg0001";
    private const string OtherGoldClip = "koak-gnd_seg0002";
    private const string EmptyClip = "koak-gnd_seg0003";
    private const string SlashClip = "koak-gnd_seg0004";

    private static readonly SttBias NoBias = new(string.Empty, []);

    private static CorpusSlotArm AddOne(CorpusSlotArm arm)
    {
        arm.Add(Segment(GoldClip), Spoken(), NoBias);
        return arm;
    }

    private CorpusSlotRun CreateRun(CorpusSlotOptions options, string telephony)
    {
        File.WriteAllText(
            Path.Combine(_dir, CorpusRooms.FileName),
            $$"""{ "telephony": {{telephony}}, "feeds": { "koak-gnd": { "airport": "KOAK", "facility": "gnd", "seed": 1 } } }"""
        );
        File.WriteAllText(
            Path.Combine(_dir, CorpusSlots.FileName),
            """
            {
              "koak-gnd_seg0001": [
                { "kind": "callsign", "value": "SWA2495", "spoken": "southwest two four nine five" },
                { "kind": "taxiway", "value": "KOAK/W4", "spoken": "whiskey four" }
              ],
              "koak-gnd_seg0002": [
                { "kind": "callsign", "value": "SWA2495", "spoken": "southwest two four nine five" },
                { "kind": "taxiway", "value": "KOAK/W4", "spoken": "whiskey four" }
              ],
              "koak-gnd_seg0003": [],
              "koak-gnd_seg0004": [ { "kind": "callsign", "value": "QXE2101", "spoken": "horizon two one zero one" } ]
            }
            """
        );
        CorpusSegment[] segments = [Segment(GoldClip), Segment(OtherGoldClip), Segment(EmptyClip), Segment(SlashClip)];

        var run = CorpusSlotRun.TryCreate(_dir, segments, options, out string? error);

        Assert.True(run is not null, error);
        return run;
    }

    private static SttHypotheses Spoken() =>
        new([new SttHypothesis("southwest two four nine five taxi via whiskey four", [], [], null)], null, false);

    private static SttHypotheses SpokenWithTokens() =>
        new(
            [
                new SttHypothesis(
                    "southwest two four nine five taxi via whiskey four",
                    [" southwest", " two", " four", " nine", " five", " taxi", " via", " whiskey", " four"],
                    [-0.1, -0.1, -0.1, -0.1, -0.1, -0.1, -0.1, -0.1, -0.1],
                    -1
                ),
            ],
            null,
            false
        );

    private static CorpusSlotOptions RoomOptions(SlotRoom room) => new(false, room, 300, HotwordCase.Title, null, DecoderSettings.Default);

    private static TransmissionContext Context() =>
        new(
            "KOAK",
            [
                new RoomAircraft(
                    ContextEntityFactory.Callsign("SWA2495", null, new Dictionary<string, string>(), ["SWA2495"], DecoderSettings.Default)!,
                    []
                ),
            ],
            [],
            []
        );

    private static readonly ContextEntity OwnRunway = new(EntityKind.Runway, "KOAK/30", [new SpokenForm("runway three zero", 1.0)]);

    private static readonly ContextEntity AirportTaxiway = new(EntityKind.Taxiway, "KOAK/W4", [new SpokenForm("whiskey four", 1.0)]);

    private static ContextEntity CallsignEntity(string callsign, IReadOnlyCollection<string> room) =>
        ContextEntityFactory.Callsign(callsign, null, new Dictionary<string, string>(), room, DecoderSettings.Default)!;

    /// <summary>A room of SWA2495 (with its own runway) and UAL123, and one airport taxiway.</summary>
    private static TransmissionContext TwoPassContext()
    {
        string[] room = ["SWA2495", "UAL123"];
        return new(
            "KOAK",
            [new RoomAircraft(CallsignEntity("SWA2495", room), [OwnRunway]), new RoomAircraft(CallsignEntity("UAL123", room), [])],
            [AirportTaxiway],
            []
        );
    }

    private static EntityMatch Matched(RoomAircraft aircraft) => new(aircraft.Callsign, new WordSpan(0, 1), 1.0, false, "heard");

    private static DecodeResult Decoded(DecodeState state, EntityMatch? callsign, EntityMatch? bestGuess) =>
        new(state, callsign, bestGuess, [], string.Empty, 0, 0, false);

    private static SttHypotheses Transcript() =>
        new(
            [new SttHypothesis("southwest two four nine six", [], [], null), new SttHypothesis("southwest two four nine five", [], [], null)],
            null,
            false
        );

    private static CorpusSegment Segment(string id) =>
        new(id, CorpusRole.Unknown, [], "southwest two four nine five taxi via whiskey four", "koak-gnd", "owner");

    private static CorpusSlotOptions Options() => new(false, null, 300, HotwordCase.Title, null, DecoderSettings.Default);
}
