using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>
/// The <c>--slots</c> part of a <c>--corpus-wer</c> run: the corpus rooms and gold, the hotwords each clip's room gives its
/// stream, and the context decoder run over each clip's output under every room. Segments are decoded one at a time, since the
/// room builder is not thread-safe.
/// </summary>
public sealed class CorpusSlotRun
{
    private readonly RoomContextBuilder _builder;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>> _gold;
    private readonly IReadOnlyList<(string Feed, IReadOnlyList<string> Callsigns)> _unbuilt;

    private CorpusSlotRun(
        RoomContextBuilder builder,
        IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>> gold,
        CorpusSlotOptions options,
        IReadOnlyList<(string Feed, IReadOnlyList<string> Callsigns)> unbuilt
    )
    {
        _builder = builder;
        _gold = gold;
        _unbuilt = unbuilt;
        Options = options;
        UnbuiltCallsigns = [.. unbuilt.SelectMany(feed => feed.Callsigns.Select(callsign => $"{feed.Feed}: {callsign}"))];
    }

    /// <summary>The run's slot options.</summary>
    public CorpusSlotOptions Options { get; }

    /// <summary>The settings every decode and room runs under.</summary>
    public static DecoderSettings Settings => DecoderSettings.Default;

    /// <summary>Each run feed's gold callsigns that got no entity, as <c>&lt;feed&gt;: &lt;callsign&gt;</c>, feeds and callsigns ordinal.</summary>
    public IReadOnlyList<string> UnbuiltCallsigns { get; }

    /// <summary>
    /// Sets up the run's slot work when <paramref name="options"/> asks for it, printing the feeds with unbuilt callsigns. False
    /// (after printing the FATAL line) when it cannot be set up; true with a null run when the run scores no slots.
    /// </summary>
    public static bool TrySetUp(string? corpusDir, IReadOnlyList<CorpusSegment> segments, CorpusSlotOptions? options, out CorpusSlotRun? run)
    {
        run = null;
        if (options is null)
        {
            return true;
        }
        string? error = "--slots needs a corpus folder";
        run = (corpusDir is null) ? null : TryCreate(corpusDir, segments, options, out error);
        if (run is null)
        {
            Console.Error.WriteLine($"FATAL: {error}");
            return false;
        }
        run.PrintUnbuilt();
        return true;
    }

    /// <summary>The run's unbuilt callsigns for <c>results.json</c>; null for a run without slots.</summary>
    public static IReadOnlyList<string>? UnbuiltOf(CorpusSlotRun? run) => run?.UnbuiltCallsigns;

    /// <summary>
    /// Reads the corpus folder's rooms and gold and checks every segment has both; null with the first error otherwise. Loads
    /// the navdata first.
    /// </summary>
    public static CorpusSlotRun? TryCreate(string corpusDir, IReadOnlyList<CorpusSegment> segments, CorpusSlotOptions options, out string? error)
    {
        string? repo = OuroborosRunner.TryFindRepoRoot();
        if (repo is null)
        {
            error = "--slots must run from a yaat checkout";
            return null;
        }
        if (!SandboxNavData.TryInitialize(out error))
        {
            return null;
        }
        try
        {
            return Create(repo, corpusDir, segments, options, out error);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The clip's stream bias: <paramref name="armBias"/> unchanged without <c>--room-hotwords</c>, else with the hotwords of the
    /// clip's room in place of its own.
    /// </summary>
    /// <exception cref="InvalidOperationException">A room form carries a character the recognizer reads as hotword syntax.</exception>
    public SttBias BiasFor(CorpusSegment segment, SttBias armBias)
    {
        if (Options.HotwordRoom is not SlotRoom room)
        {
            return armBias;
        }
        IReadOnlyList<string> phrases = RoomHotwords.Select(Context(segment.Id, room), Options.HotwordCap);
        if (phrases.Count == 0)
        {
            return armBias with { Hotwords = [] };
        }
        IReadOnlyList<string> parsed = SherpaHotwords.ParseLines(phrases, Options.HotwordCasing, out string? error);
        return (error is null)
            ? armBias with
            {
                Hotwords = parsed,
            }
            : throw new InvalidOperationException($"{segment.Id}: room hotwords refused: {error}");
    }

    /// <summary>
    /// Decodes a clip's transcript against one room: every hypothesis with fuzzy matching when <paramref name="resolve"/>, else
    /// the best one matched exactly. A clip with no transcript is <see cref="DecodeState.NoCallsign"/> without a decode.
    /// </summary>
    public static DecodeResult Decode(SttHypotheses transcript, TransmissionContext context, bool resolve, DecoderSettings settings)
    {
        if ((transcript.Entries.Count == 0) || (transcript.Text is null))
        {
            return new DecodeResult(DecodeState.NoCallsign, null, null, [], string.Empty, 0, 0, false);
        }
        DecoderInput input = resolve
            ? new DecoderInput([.. transcript.Entries.Select(ToDecoderHypothesis)], InputSource.Spoken, MatchMode.Fuzzy)
            : new DecoderInput([ToDecoderHypothesis(transcript.Entries[0])], InputSource.Spoken, MatchMode.ExactOnly);
        return ContextDecoder.Decode(input, context, settings);
    }

    /// <summary>
    /// A recognizer hypothesis as the decoder takes it: a sherpa piece's leading space becomes the <c>▁</c> word mark, and an
    /// entry without tokens has none.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry's tokens and log-probabilities differ in count.</exception>
    public static DecoderHypothesis ToDecoderHypothesis(SttHypothesis entry)
    {
        if (entry.Tokens.Count != entry.TokenLogProbs.Count)
        {
            throw new InvalidDataException(
                $"A hypothesis has {entry.Tokens.Count} tokens but {entry.TokenLogProbs.Count} token log-probabilities: '{entry.Text}'."
            );
        }
        if (entry.Tokens.Count == 0)
        {
            return new DecoderHypothesis(entry.Text, null, entry.Score);
        }
        TokenLogProb[] tokens = [.. entry.Tokens.Select((piece, i) => new TokenLogProb(WordMarked(piece), entry.TokenLogProbs[i]))];
        return new DecoderHypothesis(entry.Text, tokens, entry.Score);
    }

    /// <summary>A fresh accumulator for one prompt arm's clips.</summary>
    public CorpusSlotArm StartArm() => new(this);

    /// <summary>The clip's gold slots.</summary>
    public IReadOnlyList<CorpusSlot> Gold(string clipId) => _gold[clipId];

    /// <summary>The clip's context under <paramref name="room"/>.</summary>
    public TransmissionContext Context(string clipId, SlotRoom room)
    {
        RoomContexts contexts = _builder.For(clipId);
        return room switch
        {
            SlotRoom.Unpadded => contexts.Unpadded,
            SlotRoom.Padded => contexts.Padded,
            SlotRoom.Oracle => contexts.Oracle,
            _ => throw new ArgumentOutOfRangeException(nameof(room), room, "no such slot room"),
        };
    }

    private static string WordMarked(string piece) => piece.StartsWith(' ') ? "▁" + piece[1..] : piece;

    private static CorpusSlotRun? Create(
        string repo,
        string corpusDir,
        IReadOnlyList<CorpusSegment> segments,
        CorpusSlotOptions options,
        out string? error
    )
    {
        var rooms = CorpusRooms.Read(corpusDir);
        if (rooms is null)
        {
            error = $"--slots needs {CorpusRooms.FileName} in {corpusDir}";
            return null;
        }
        IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>>? gold = CorpusSlots.Read(corpusDir);
        if (gold is null)
        {
            error = $"--slots needs {CorpusSlots.FileName} in {corpusDir}";
            return null;
        }
        error = SegmentError(segments, rooms, gold);
        if (error is not null)
        {
            return null;
        }
        var builder = new RoomContextBuilder(Sources(repo, rooms, gold), Settings);
        return new CorpusSlotRun(builder, gold, options, Unbuilt(builder, segments));
    }

    /// <summary>The first segment without gold, else the first whose feed has no room; null when every segment has both.</summary>
    private static string? SegmentError(
        IReadOnlyList<CorpusSegment> segments,
        CorpusRooms rooms,
        IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>> gold
    )
    {
        CorpusSegment? ungolded = segments.FirstOrDefault(segment => !gold.ContainsKey(segment.Id));
        if (ungolded is not null)
        {
            return $"{ungolded.Id} has no entry in {CorpusSlots.FileName}";
        }
        CorpusSegment? roomless = segments.FirstOrDefault(segment => !HasRoom(segment, rooms));
        return (roomless is null) ? null : $"{roomless.Id}: feed '{CorpusRoles.FeedFromId(roomless.Id)}' has no room in {CorpusRooms.FileName}";
    }

    private static bool HasRoom(CorpusSegment segment, CorpusRooms rooms) =>
        CorpusRoles.FeedFromId(segment.Id) is { } feed && rooms.Feeds.ContainsKey(feed);

    /// <summary>The unbuilt callsigns of each feed the run's segments come from, feeds in ordinal order, only feeds with some.</summary>
    private static List<(string Feed, IReadOnlyList<string> Callsigns)> Unbuilt(RoomContextBuilder builder, IReadOnlyList<CorpusSegment> segments)
    {
        IEnumerable<string> feeds = segments
            .Select(segment => CorpusRoles.FeedFromId(segment.Id))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [.. feeds.Select(feed => (feed, builder.UnbuiltCallsigns(feed))).Where(entry => entry.Item2.Count > 0)];
    }

    private void PrintUnbuilt()
    {
        foreach ((string feed, IReadOnlyList<string> callsigns) in _unbuilt)
        {
            Console.WriteLine($"slots: {feed} has no entity for {string.Join(", ", callsigns)}");
        }
    }

    /// <summary>The room builder's sources: the checkout's test layouts and ZOA landmark fixes, each loaded once per airport.</summary>
    private static RoomSources Sources(string repo, CorpusRooms rooms, IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>> gold)
    {
        var layouts = new Dictionary<string, AirportGroundLayout?>(StringComparer.OrdinalIgnoreCase);
        var landmarks = new Dictionary<string, IReadOnlyList<ContextEntity>>(StringComparer.OrdinalIgnoreCase);
        return new RoomSources(
            rooms,
            gold,
            airport => Cached(layouts, airport, key => LoadLayout(repo, key)),
            airport => Cached(landmarks, airport, key => LoadLandmarks(repo, key)),
            NavigationDatabase.Instance
        );
    }

    private static T Cached<T>(Dictionary<string, T> cache, string airport, Func<string, T> load)
    {
        if (!cache.TryGetValue(airport, out T? value))
        {
            value = load(airport);
            cache[airport] = value;
        }
        return value;
    }

    private static AirportGroundLayout? LoadLayout(string repo, string airport)
    {
        string faa = AirportLayoutDownloader.ToFaaCode(airport).ToLowerInvariant();
        string path = Path.Combine(repo, "tests", "Yaat.Sim.Tests", "TestData", $"{faa}.geojson");
        return File.Exists(path) ? AirportLayoutFile.Load(path, airport) : null;
    }

    private static IReadOnlyList<ContextEntity> LoadLandmarks(string repo, string airport)
    {
        string faa = AirportLayoutDownloader.ToFaaCode(airport).ToLowerInvariant();
        string path = Path.Combine(repo, "src", "Yaat.Sim", "Data", "ARTCCs", "ZOA", "CustomFixes", $"{faa}-landmarks.json");
        if (!File.Exists(path))
        {
            return [];
        }
        List<CustomFixDefinition> definitions =
            JsonSerializer.Deserialize<List<CustomFixDefinition>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{path} is empty or not a JSON array of custom fixes.");
        return
        [
            .. definitions
                .Where(definition => (definition.Aliases.Count > 0) && (definition.SpokenPatterns is { Count: > 0 }))
                .Select(definition => ContextEntityFactory.Landmark(airport, definition.Aliases[0], definition.SpokenPatterns!)),
        ];
    }
}

/// <summary>
/// One prompt arm's slot work: every clip decoded under the unpadded, padded and oracle rooms, with each room's clips,
/// form coverage, gated count and, under <c>--resolve</c>, the word error counts of its rewritten transcripts.
/// </summary>
public sealed class CorpusSlotArm
{
    private readonly RoomTally[] _rooms = [.. SlotRooms.All.Select(room => new RoomTally(room))];

    internal CorpusSlotArm(CorpusSlotRun run) => Run = run;

    /// <summary>The run the arm belongs to.</summary>
    public CorpusSlotRun Run { get; }

    /// <summary>Decodes one clip under every room into the arm's tallies and returns what its <c>results.jsonl</c> line records.</summary>
    /// <param name="segment">The clip.</param>
    /// <param name="transcript">The engine's alternatives for it.</param>
    /// <param name="bias">The bias its stream ran under, whose hotwords are the clip's room hotwords under <c>--room-hotwords</c>.</param>
    public CorpusSlotClipLines Add(CorpusSegment segment, SttHypotheses transcript, SttBias bias)
    {
        IReadOnlyList<CorpusSlot> gold = Run.Gold(segment.Id);
        bool resolve = Run.Options.Resolve;
        var lines = new List<CorpusSlotDecodeLine>(_rooms.Length);
        foreach (RoomTally room in _rooms)
        {
            TransmissionContext context = Run.Context(segment.Id, room.Room);
            DecodeResult result = CorpusSlotRun.Decode(transcript, context, resolve, CorpusSlotRun.Settings);
            room.Add(new CorpusSlotClip(segment, gold, result), RoomCoverage.Count(gold, context), resolve);
            lines.Add(DecodeLine(room.Label, result));
        }
        int? phrases = (Run.Options.HotwordRoom is null) ? null : bias.Hotwords.Count;
        return new CorpusSlotClipLines(phrases, lines);
    }

    /// <summary>The arm's slot results, one per room in room order.</summary>
    public IReadOnlyList<CorpusSlotRoomResult> Results() => [.. _rooms.Select(room => room.Result(Run.Options.Resolve))];

    private static CorpusSlotDecodeLine DecodeLine(string room, DecodeResult result) =>
        new(
            room,
            StateLabel(result.State),
            result.Callsign?.Entity.Value,
            result.BestGuess?.Entity.Value,
            [.. result.Entities.Select(match => $"{CorpusSlots.KindLabel(match.Entity.Kind)}:{match.Entity.Value}")],
            result.RewrittenTranscript,
            result.HypothesisIndex,
            result.JointScore,
            result.ConfidenceGated
        );

    private static string StateLabel(DecodeState state) =>
        state switch
        {
            DecodeState.Resolved => "resolved",
            DecodeState.SayAgain => "sayagain",
            DecodeState.NoCallsign => "nocallsign",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no such decode state"),
        };

    /// <summary>One room's running counts over the arm's clips.</summary>
    private sealed class RoomTally(SlotRoom room)
    {
        private readonly List<CorpusSlotClip> _clips = [];
        private readonly CorpusSlotCoverage[] _coverage = [.. Enum.GetValues<EntityKind>().Select(kind => new CorpusSlotCoverage(kind, 0, 0, 0))];
        private readonly WerTally _overall = new();
        private readonly List<(CorpusSegment Segment, WerCounts Counts)> _scored = [];
        private int _gated;

        public SlotRoom Room { get; } = room;

        public string Label { get; } = SlotRooms.Label(room);

        public void Add(CorpusSlotClip clip, IReadOnlyList<CorpusSlotCoverage> coverage, bool resolve)
        {
            _clips.Add(clip);
            foreach (CorpusSlotCoverage row in coverage)
            {
                CorpusSlotCoverage sum = _coverage[(int)row.Kind];
                _coverage[(int)row.Kind] = sum with
                {
                    Slots = sum.Slots + row.Slots,
                    Reachable = sum.Reachable + row.Reachable,
                    FormMatched = sum.FormMatched + row.FormMatched,
                };
            }
            if (clip.Result.ConfidenceGated)
            {
                _gated++;
            }
            if (resolve)
            {
                AddResolved(clip);
            }
        }

        public CorpusSlotRoomResult Result(bool resolve) =>
            new(
                Label,
                CorpusSlotScorer.Score(_clips),
                [.. _coverage.Select(row => new CorpusSlotCoverageRow(CorpusSlots.KindLabel(row.Kind), row.Slots, row.Reachable, row.FormMatched))],
                resolve ? ResolvedRows() : null,
                _gated
            );

        /// <summary>Scores the clip's rewritten transcript against its reference.</summary>
        private void AddResolved(CorpusSlotClip clip)
        {
            string rewritten = clip.Result.RewrittenTranscript;
            string reference = WerTextNormalizer.Normalize(clip.Segment.Reference);
            WerCounts counts = WerScorer.Score(reference, (rewritten.Length == 0) ? null : WerTextNormalizer.Normalize(rewritten));
            _overall.Add(counts);
            _scored.Add((clip.Segment, counts));
        }

        private List<CorpusWerRoleTotals> ResolvedRows() =>
            [CorpusWerRoleTotals.From("all", _overall), .. CorpusWerGroups.ProvenanceRows(_scored) ?? [], .. CorpusWerGroups.FeedRows(_scored) ?? []];
    }
}
