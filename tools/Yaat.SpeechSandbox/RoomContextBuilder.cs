using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>Everything a <see cref="RoomContextBuilder"/> reads.</summary>
/// <param name="Rooms">The corpus rooms: each feed's airport, facility and seed, and the telephony and type maps.</param>
/// <param name="Slots">The gold slots of every clip, by clip id.</param>
/// <param name="Layouts">The ground layout of an airport, by ICAO id; null when none is loaded.</param>
/// <param name="Landmarks">The landmark entities of an airport, by ICAO id.</param>
/// <param name="Nav">The navigation database.</param>
public sealed record RoomSources(
    CorpusRooms Rooms,
    IReadOnlyDictionary<string, IReadOnlyList<CorpusSlot>> Slots,
    Func<string, AirportGroundLayout?> Layouts,
    Func<string, IReadOnlyList<ContextEntity>> Landmarks,
    NavigationDatabase Nav
);

/// <summary>The three contexts one clip is decoded against.</summary>
/// <param name="Unpadded">The feed's real aircraft, each with the entities the feed's other clips gave it.</param>
/// <param name="Padded">The real aircraft and the seeded distractors, <see cref="RoomContextBuilder.PaddedRoomSize"/> in all.</param>
/// <param name="Oracle">Only the clip's own gold: its callsigns and the entities it names.</param>
public sealed record RoomContexts(TransmissionContext Unpadded, TransmissionContext Padded, TransmissionContext Oracle);

/// <summary>
/// Builds the rooms a corpus clip is decoded against from the feed's room in <c>rooms.json</c> and the gold in <c>slots.json</c>.
/// A real aircraft's own entities come from the feed's other clips only (leave one out), so a clip never sees its own gold
/// outside the oracle. Feed work and clip contexts are computed once and cached; an instance is not thread-safe.
/// </summary>
public sealed class RoomContextBuilder(RoomSources sources, DecoderSettings settings)
{
    /// <summary>How many aircraft the padded room holds, real ones and distractors together.</summary>
    public const int PaddedRoomSize = 35;

    private readonly Dictionary<string, FeedRoom> _feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomContexts> _clips = new(StringComparer.Ordinal);

    /// <summary>The clip's unpadded, padded and oracle contexts.</summary>
    /// <exception cref="InvalidOperationException">The clip's feed has no room, or the clip has no gold.</exception>
    public RoomContexts For(string clipId)
    {
        if (_clips.TryGetValue(clipId, out RoomContexts? cached))
        {
            return cached;
        }
        string? feed = CorpusRoles.FeedFromId(clipId);
        if ((feed is null) || !sources.Rooms.Feeds.ContainsKey(feed))
        {
            throw new InvalidOperationException($"Clip {clipId}: its feed '{feed}' has no room in {CorpusRooms.FileName}.");
        }
        if (!sources.Slots.TryGetValue(clipId, out IReadOnlyList<CorpusSlot>? gold))
        {
            throw new InvalidOperationException($"Clip {clipId} has no gold slots in {CorpusSlots.FileName}.");
        }
        RoomContexts contexts = Room(feed).ContextsFor(clipId, gold);
        _clips[clipId] = contexts;
        return contexts;
    }

    /// <summary>The entities of the feed's airport (and, for an approach feed, of the nearby airports with an instrument approach).</summary>
    /// <exception cref="InvalidOperationException">The feed has no room.</exception>
    public IReadOnlyList<ContextEntity> AirportEntities(string feed) => Room(feed).AirportEntities;

    /// <summary>The feed's gold callsigns that got no callsign entity (an airline whose telephony is unknown), in ordinal order.</summary>
    /// <exception cref="InvalidOperationException">The feed has no room.</exception>
    public IReadOnlyList<string> UnbuiltCallsigns(string feed) => Room(feed).Unbuilt;

    private FeedRoom Room(string feed)
    {
        if (_feeds.TryGetValue(feed, out FeedRoom? room))
        {
            return room;
        }
        if (!sources.Rooms.Feeds.TryGetValue(feed, out CorpusRoomFeed? spec))
        {
            throw new InvalidOperationException($"Feed '{feed}' has no room in {CorpusRooms.FileName}.");
        }
        room = new FeedRoom(feed, spec, sources, settings);
        _feeds[feed] = room;
        return room;
    }
}

/// <summary>One feed's rooms: its airport entities, its real aircraft, its padded room and its gold lookup.</summary>
internal sealed class FeedRoom
{
    private const double LocalRadiusNm = 10;
    private const double ApproachRadiusNm = 40;

    private readonly RoomSources _sources;
    private readonly DecoderSettings _settings;
    private readonly string _airport;
    private readonly IReadOnlyList<string> _clipIds;
    private readonly Dictionary<(EntityKind Kind, string Value), ContextEntity> _byValue = [];
    private readonly Dictionary<string, ContextEntity> _unpaddedCallsigns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextEntity> _paddedCallsigns = new(StringComparer.Ordinal);
    private readonly PaddedRoom _padded;

    public FeedRoom(string feed, CorpusRoomFeed spec, RoomSources sources, DecoderSettings settings)
    {
        _sources = sources;
        _settings = settings;
        _airport = spec.Airport;
        AirportEntities = BuildAirportEntities(spec);
        foreach (ContextEntity entity in AirportEntities)
        {
            _byValue.TryAdd((entity.Kind, entity.Value.ToUpperInvariant()), entity);
        }
        _clipIds = [.. sources.Slots.Keys.Where(id => CorpusRoles.FeedFromId(id) == feed).Order(StringComparer.Ordinal)];
        string[] real = [.. _clipIds.SelectMany(id => Callsigns(sources.Slots[id])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        BuildUnpadded(real);
        Unbuilt = [.. real.Where(callsign => !_unpaddedCallsigns.ContainsKey(callsign))];
        string[] built = [.. real.Where(_unpaddedCallsigns.ContainsKey)];
        _padded = RoomDistractors.Draw(new DistractorRequest(feed, spec, built, AirportEntities, sources.Rooms.Telephony, settings));
        BuildPadded();
    }

    public IReadOnlyList<ContextEntity> AirportEntities { get; }

    public IReadOnlyList<string> Unbuilt { get; }

    public RoomContexts ContextsFor(string clipId, IReadOnlyList<CorpusSlot> gold)
    {
        var own = _unpaddedCallsigns.Keys.ToDictionary<string, string, IReadOnlyList<ContextEntity>>(
            callsign => callsign,
            callsign => OwnEntities(callsign, clipId),
            StringComparer.Ordinal
        );
        RoomAircraft[] unpadded =
        [
            .. _padded.Order.Where(own.ContainsKey).Select(callsign => new RoomAircraft(_unpaddedCallsigns[callsign], own[callsign])),
        ];
        RoomAircraft[] padded =
        [
            .. _padded.Order.Select(callsign => new RoomAircraft(
                _paddedCallsigns[callsign],
                own.TryGetValue(callsign, out IReadOnlyList<ContextEntity>? entities) ? entities : _padded.DistractorOwn[callsign]
            )),
        ];
        return new RoomContexts(
            new TransmissionContext(_airport, unpadded, AirportEntities),
            new TransmissionContext(_airport, padded, AirportEntities),
            Oracle(gold)
        );
    }

    private static IEnumerable<string> Callsigns(IReadOnlyList<CorpusSlot> gold) =>
        gold.Where(slot => slot.Kind == EntityKind.Callsign).Select(slot => slot.Value.ToUpperInvariant());

    private List<ContextEntity> BuildAirportEntities(CorpusRoomFeed spec)
    {
        NavigationDatabase nav = _sources.Nav;
        bool approach = spec.Facility == "app";
        double radius = approach ? ApproachRadiusNm : LocalRadiusNm;
        var entities = new List<ContextEntity>(
            AirportContextBuilder.Build(spec.Airport, _sources.Layouts(spec.Airport), radius, _sources.Landmarks(spec.Airport), nav, _settings)
        );
        if (approach)
        {
            entities.AddRange(NearbyInstrumentAirports(spec.Airport));
        }
        var seen = new HashSet<(EntityKind Kind, string Value)>();
        return [.. entities.Where(entity => seen.Add((entity.Kind, entity.Value)))];
    }

    /// <summary>The runways and approaches of every other airport within the approach radius that has an instrument approach.</summary>
    private IEnumerable<ContextEntity> NearbyInstrumentAirports(string airport)
    {
        NavigationDatabase nav = _sources.Nav;
        (double Lat, double Lon) point =
            nav.GetAirportPosition(airport)
            ?? throw new InvalidOperationException($"Airport '{airport}' has no reference point in the navigation database.");
        string home = AirportLayoutDownloader.ToFaaCode(airport);
        foreach ((string id, LatLon _) in nav.FindAirportsWithin(new LatLon(point.Lat, point.Lon), ApproachRadiusNm))
        {
            string icao = ToIcao(id);
            if (AirportLayoutDownloader.ToFaaCode(icao) == home)
            {
                continue;
            }
            IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches(icao, nav, _settings);
            if (approaches.Count == 0)
            {
                continue;
            }
            foreach (ContextEntity entity in ContextEntityFactory.Runways(icao, nav, _settings).Concat(approaches))
            {
                yield return entity;
            }
        }
    }

    /// <summary>A navdata airport id as an ICAO id: a three-letter FAA id gains its "K".</summary>
    private static string ToIcao(string id) => ((id.Length == 3) && id.All(char.IsLetter)) ? "K" + id : id;

    /// <summary>Builds each real callsign's entity against the real ones, leaving out the ones whose telephony is unknown.</summary>
    private void BuildUnpadded(IReadOnlyList<string> real)
    {
        CorpusRooms rooms = _sources.Rooms;
        foreach (string callsign in real)
        {
            if (ContextEntityFactory.Callsign(callsign, rooms.Types.GetValueOrDefault(callsign), rooms.Telephony, real, _settings) is { } entity)
            {
                _unpaddedCallsigns[callsign] = entity;
            }
        }
    }

    /// <summary>Builds every padded-room callsign's entity against the padded room: real ones with their type, distractors without.</summary>
    private void BuildPadded()
    {
        CorpusRooms rooms = _sources.Rooms;
        foreach (string callsign in _padded.Order)
        {
            string? type = _padded.DistractorOwn.ContainsKey(callsign) ? null : rooms.Types.GetValueOrDefault(callsign);
            _paddedCallsigns[callsign] =
                ContextEntityFactory.Callsign(callsign, type, rooms.Telephony, _padded.Order, _settings)
                ?? throw new InvalidOperationException($"Callsign {callsign} has no callsign entity in the padded room of {_airport}.");
        }
    }

    /// <summary>
    /// The entities the feed's other clips give <paramref name="callsign"/>: every non-callsign gold slot of each clip other than
    /// <paramref name="clipId"/> whose first callsign is it, resolved and deduplicated in clip-id order.
    /// </summary>
    private IReadOnlyList<ContextEntity> OwnEntities(string callsign, string clipId)
    {
        IEnumerable<CorpusSlot> slots = _clipIds
            .Where(id => (id != clipId) && (Callsigns(_sources.Slots[id]).FirstOrDefault() == callsign))
            .SelectMany(id => _sources.Slots[id]);
        return ResolveAll(slots);
    }

    private TransmissionContext Oracle(IReadOnlyList<CorpusSlot> gold)
    {
        CorpusRooms rooms = _sources.Rooms;
        string[] callsigns = [.. Callsigns(gold).Distinct(StringComparer.Ordinal)];
        RoomAircraft[] aircraft =
        [
            .. callsigns
                .Select(callsign =>
                    ContextEntityFactory.Callsign(callsign, rooms.Types.GetValueOrDefault(callsign), rooms.Telephony, callsigns, _settings)
                )
                .OfType<ContextEntity>()
                .Select(entity => new RoomAircraft(entity, [])),
        ];
        return new TransmissionContext(_airport, aircraft, ResolveAll(gold));
    }

    /// <summary>The non-callsign slots resolved to entities, deduplicated by kind and value with the first kept.</summary>
    private List<ContextEntity> ResolveAll(IEnumerable<CorpusSlot> slots)
    {
        var seen = new HashSet<(EntityKind Kind, string Value)>();
        return
        [
            .. slots
                .Where(slot => slot.Kind != EntityKind.Callsign)
                .Select(Resolve)
                .OfType<ContextEntity>()
                .Where(entity => seen.Add((entity.Kind, entity.Value))),
        ];
    }

    /// <summary>The airport entity a gold slot names; a fix outside the radius is built from the navdata; anything else is null.</summary>
    private ContextEntity? Resolve(CorpusSlot slot)
    {
        if (_byValue.TryGetValue((slot.Kind, slot.Value.ToUpperInvariant()), out ContextEntity? entity))
        {
            return entity;
        }
        return slot.Kind == EntityKind.Fix ? ContextEntityFactory.Fix(slot.Value, _sources.Nav) : null;
    }
}

/// <summary>What the padded room's draws need.</summary>
/// <param name="Feed">The feed's name, for the error messages.</param>
/// <param name="Spec">The feed's room: its airport and seed.</param>
/// <param name="Real">The feed's buildable real callsigns, in ordinal order.</param>
/// <param name="AirportEntities">The airport entities a distractor's own entities are picked from.</param>
/// <param name="Telephony">The corpus's telephony overrides.</param>
/// <param name="Settings">The decoder settings.</param>
internal sealed record DistractorRequest(
    string Feed,
    CorpusRoomFeed Spec,
    IReadOnlyList<string> Real,
    IReadOnlyList<ContextEntity> AirportEntities,
    IReadOnlyDictionary<string, string> Telephony,
    DecoderSettings Settings
);

/// <summary>The padded room's aircraft order and each distractor's own entities.</summary>
/// <param name="Order">Real callsigns and distractors, shuffled.</param>
/// <param name="DistractorOwn">Each distractor's own entities, by callsign.</param>
internal sealed record PaddedRoom(IReadOnlyList<string> Order, IReadOnlyDictionary<string, IReadOnlyList<ContextEntity>> DistractorOwn);

/// <summary>
/// Draws the distractors that pad a feed's room to <see cref="RoomContextBuilder.PaddedRoomSize"/>: N-numbers at the feed's
/// GA share, airlines weighted by their arrivals at the airport, never a real callsign or a real callsign's digits. One seeded
/// random source drives the draws, the own-entity picks and the shuffle, so a feed's padded room is the same on every run.
/// </summary>
internal static class RoomDistractors
{
    private const int MaxDraws = 1000;
    private const int MaxOwnEntities = 3;

    public static PaddedRoom Draw(DistractorRequest request)
    {
        var rng = new Random(request.Spec.Seed);
        List<string> distractors = DrawCallsigns(request, rng);
        var own = new Dictionary<string, IReadOnlyList<ContextEntity>>(StringComparer.Ordinal);
        foreach (string distractor in distractors)
        {
            own[distractor] = PickOwn(request.AirportEntities, rng);
        }
        List<string> order = [.. request.Real, .. distractors];
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return new PaddedRoom(order, own);
    }

    private static List<string> DrawCallsigns(DistractorRequest request, Random rng)
    {
        IReadOnlyList<string> real = request.Real;
        var existing = new HashSet<string>(real, StringComparer.OrdinalIgnoreCase);
        HashSet<string> realDigits = [.. real.Select(Digits)];
        double gaShare = real.Count == 0 ? 0 : real.Count(IsGa) / (double)real.Count;
        var distractors = new List<string>();
        for (int draws = 0; real.Count + distractors.Count < RoomContextBuilder.PaddedRoomSize; draws++)
        {
            if (draws == MaxDraws)
            {
                throw new InvalidOperationException(
                    $"Feed '{request.Feed}': {MaxDraws} draws did not find {RoomContextBuilder.PaddedRoomSize - real.Count} distractors."
                );
            }
            string candidate =
                rng.NextDouble() < gaShare
                    ? AircraftGenerator.GenerateNNumber(existing, rng)
                    : AircraftGenerator.GenerateAirlineCallsign(PickAirline(request.Spec.Airport, rng), existing, rng);
            if (Acceptable(candidate, existing, realDigits, request))
            {
                existing.Add(candidate);
                distractors.Add(candidate);
            }
        }
        return distractors;
    }

    private static bool Acceptable(string candidate, HashSet<string> existing, HashSet<string> realDigits, DistractorRequest request) =>
        !existing.Contains(candidate)
        && !realDigits.Contains(Digits(candidate))
        && (ContextEntityFactory.Callsign(candidate, null, request.Telephony, [candidate], request.Settings) is not null);

    /// <summary>An airline serving the airport, weighted by its arrivals as the scenario generator weights them.</summary>
    private static string PickAirline(string airport, Random rng)
    {
        if (!AirportAirlines.TryGetAirlinesForAirport(airport, out IReadOnlyList<AirportAirlineEntry>? airlines) || (airlines.Count == 0))
        {
            throw new InvalidOperationException($"Airport '{airport}' has no airlines in the airport-airlines table to draw distractors from.");
        }
        double totalWeight = airlines.Sum(a => (double)Math.Max(1, a.Arrivals));
        double pick = rng.NextDouble() * totalWeight;
        foreach (AirportAirlineEntry entry in airlines)
        {
            pick -= Math.Max(1, entry.Arrivals);
            if (pick <= 0)
            {
                return entry.Icao;
            }
        }
        return airlines[^1].Icao;
    }

    /// <summary>Up to three distinct airport entities, their count and each pick drawn from <paramref name="rng"/>.</summary>
    private static List<ContextEntity> PickOwn(IReadOnlyList<ContextEntity> airport, Random rng)
    {
        int count = Math.Min(rng.Next(0, MaxOwnEntities + 1), airport.Count);
        var picked = new List<int>();
        while (picked.Count < count)
        {
            int index = rng.Next(airport.Count);
            if (!picked.Contains(index))
            {
                picked.Add(index);
            }
        }
        return [.. picked.Select(index => airport[index])];
    }

    private static bool IsGa(string callsign) => (callsign.Length >= 2) && (callsign[0] == 'N') && char.IsAsciiDigit(callsign[1]);

    private static string Digits(string callsign) => new([.. callsign.Where(char.IsAsciiDigit)]);
}
