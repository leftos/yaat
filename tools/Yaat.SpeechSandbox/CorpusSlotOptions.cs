using System.Globalization;

namespace Yaat.SpeechSandbox;

/// <summary>The rooms a corpus clip is decoded against: the feed's real aircraft, those padded with distractors, or the clip's own gold.</summary>
public enum SlotRoom
{
    Unpadded,
    Padded,
    Oracle,
}

/// <summary>
/// Which forms a two-pass run's second decode is biased toward, once the first decode named the addressed aircraft.
/// </summary>
public enum TwoPassList
{
    /// <summary>The aircraft's callsign forms and its own entities' forms.</summary>
    Aircraft,

    /// <summary>Those, plus the room's airport entities' forms.</summary>
    AircraftAndAirport,
}

/// <summary>
/// The slot-scoring part of a run: whether the decoder resolves over the n-best, which room's hotwords bias each clip (null for
/// none), how many phrases at most, their casing, and which list a second, per-aircraft decode takes (null for one pass).
/// </summary>
public sealed record CorpusSlotOptions(bool Resolve, SlotRoom? HotwordRoom, int HotwordCap, HotwordCase HotwordCasing, TwoPassList? TwoPass);

/// <summary>The labels the <c>--two-pass</c> flag, the results and the report give a <see cref="TwoPassList"/>.</summary>
public static class TwoPassLists
{
    /// <summary><c>aircraft</c> or <c>aircraft-airport</c>.</summary>
    public static string Label(TwoPassList list) =>
        list switch
        {
            TwoPassList.Aircraft => "aircraft",
            TwoPassList.AircraftAndAirport => "aircraft-airport",
            _ => throw new ArgumentOutOfRangeException(nameof(list), list, "no such two-pass list"),
        };
}

/// <summary>The slot rooms in report order and the labels the results, the console and the flags give them.</summary>
public static class SlotRooms
{
    /// <summary>Every room, in the order unpadded, padded, oracle.</summary>
    public static readonly IReadOnlyList<SlotRoom> All = [SlotRoom.Unpadded, SlotRoom.Padded, SlotRoom.Oracle];

    /// <summary>The room's lowercase label: <c>unpadded</c>, <c>padded</c> or <c>oracle</c>.</summary>
    public static string Label(SlotRoom room) => room.ToString().ToLowerInvariant();
}

/// <summary>
/// The <c>--slots</c>, <c>--resolve</c>, <c>--room-hotwords</c>, <c>--hotword-cap</c> and <c>--two-pass</c> flags as given, their checks against the
/// rest of the command line, and the <see cref="CorpusSlotOptions"/> they resolve to.
/// </summary>
internal sealed class CorpusSlotArgs
{
    /// <summary>How many room-hotword phrases a clip's stream takes when <c>--hotword-cap</c> is not given.</summary>
    public const int DefaultHotwordCap = 300;

    public bool Slots { get; private set; }

    public bool Resolve { get; private set; }

    public SlotRoom? RoomHotwords { get; private set; }

    public int? HotwordCap { get; private set; }

    public TwoPassList? TwoPass { get; private set; }

    /// <summary>
    /// Applies the slot flag at <paramref name="i"/> (advancing past its value); an error, or null. Any other flag is
    /// unrecognised.
    /// </summary>
    public string? Apply(string[] args, ref int i)
    {
        string flag = args[i];
        switch (flag)
        {
            case "--slots":
                Slots = true;
                return null;
            case "--resolve":
                Resolve = true;
                return null;
            case "--room-hotwords" or "--hotword-cap" or "--two-pass":
                return (i + 1 < args.Length) ? ApplyValue(flag, args[++i]) : $"{flag} needs a value";
            default:
                return $"unrecognised argument '{flag}'";
        }
    }

    /// <summary>The first slot flag that does not fit the rest of the command line, or null.</summary>
    /// <param name="uwb">The corpus is the UWB-ATCC download.</param>
    /// <param name="beam">The run decodes by beam search.</param>
    /// <param name="hotwordsFile">The run reads a <c>--hotwords-file</c>.</param>
    public string? Error(bool uwb, bool beam, bool hotwordsFile)
    {
        if (Slots && uwb)
        {
            return "--slots needs a corpus folder, not uwb-atcc";
        }
        if (Resolve && !Slots)
        {
            return "--resolve needs --slots";
        }
        string? error = (RoomHotwords is null) ? CapError() : RoomHotwordsError(beam, hotwordsFile);
        return error ?? TwoPassError();
    }

    /// <summary>The run's hotwords note under <c>--room-hotwords</c>, naming the <c>--two-pass</c> list when set; null without it.</summary>
    public string? Note(float score, HotwordCase casing)
    {
        if (RoomHotwords is not SlotRoom room)
        {
            return null;
        }
        string scoreText = score.ToString(CultureInfo.InvariantCulture);
        string caseText = casing.ToString().ToLowerInvariant();
        string twoPass = (TwoPass is TwoPassList list) ? $", two-pass {TwoPassLists.Label(list)}" : string.Empty;
        return $"room {SlotRooms.Label(room)}, cap {HotwordCap ?? DefaultHotwordCap}, score {scoreText}, case {caseText}{twoPass}";
    }

    /// <summary>The run's slot options; null without <c>--slots</c>.</summary>
    public CorpusSlotOptions? ToOptions(HotwordCase casing) =>
        Slots ? new(Resolve, RoomHotwords, HotwordCap ?? DefaultHotwordCap, casing, TwoPass) : null;

    private string? ApplyValue(string flag, string value) =>
        flag switch
        {
            "--room-hotwords" => ApplyRoom(value),
            "--hotword-cap" => ApplyCap(value),
            "--two-pass" => ApplyTwoPass(value),
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "not a slot flag that takes a value"),
        };

    private string? ApplyTwoPass(string value)
    {
        TwoPassList? list = value switch
        {
            "aircraft" => TwoPassList.Aircraft,
            "aircraft-airport" => TwoPassList.AircraftAndAirport,
            _ => null,
        };
        if (list is null)
        {
            return $"--two-pass must be aircraft or aircraft-airport, got '{value}'";
        }
        TwoPass = list;
        return null;
    }

    private string? TwoPassError()
    {
        if (TwoPass is null)
        {
            return null;
        }
        if (RoomHotwords is null)
        {
            return "--two-pass needs --room-hotwords";
        }
        return Resolve ? null : "--two-pass needs --resolve";
    }

    private string? ApplyRoom(string value)
    {
        SlotRoom? room = value switch
        {
            "unpadded" => SlotRoom.Unpadded,
            "padded" => SlotRoom.Padded,
            "oracle" => SlotRoom.Oracle,
            _ => null,
        };
        if (room is null)
        {
            return $"--room-hotwords must be unpadded, padded or oracle, got '{value}'";
        }
        RoomHotwords = room;
        return null;
    }

    private string? ApplyCap(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int cap) || (cap <= 0))
        {
            return $"--hotword-cap needs a positive integer, got '{value}'";
        }
        HotwordCap = cap;
        return null;
    }

    private string? CapError() => (HotwordCap is null) ? null : "--hotword-cap needs --room-hotwords";

    private string? RoomHotwordsError(bool beam, bool hotwordsFile)
    {
        if (!Slots)
        {
            return "--room-hotwords needs --slots";
        }
        if (!beam)
        {
            return "--room-hotwords needs --decoding beam";
        }
        return hotwordsFile ? "--room-hotwords and --hotwords-file exclude each other" : null;
    }
}
