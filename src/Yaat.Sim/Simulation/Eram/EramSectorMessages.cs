using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Yaat.Sim.Simulation.Eram;

/// <summary>
/// The room's ERAM sector messages: the text an <c>SM</c> entry (FAA ERAM EDSM SRS §C.2, R-position Message Text) left
/// for a sector, one per (facility, sector), until that sector deletes it with <c>SM DE</c>. A sector that holds none
/// has no entry.
///
/// <para>
/// Written only by <see cref="TryApply"/>, from a <see cref="RecordedEramRoomEntry"/>, in two absolute shapes:
/// <c>SM {sector} {text}</c> stores (or overwrites) the sector's message and <c>SMDE {sector}</c> deletes it. The
/// recorder has already expanded <c>ALL</c> into one entry per adapted sector, so applying an entry never consults
/// adaptation.
/// </para>
///
/// <para>
/// Copy-on-write, like <see cref="EramRoomSettings"/>: every write builds a new immutable dictionary and publishes it
/// with one volatile reference swap, so a reader outside the room gate sees one consistent state.
/// </para>
/// </summary>
public sealed class EramSectorMessages
{
    private const string CreatePrefix = "SM ";

    private const string DeletePrefix = "SMDE ";

    /// <summary>The ERAM field-14 token for every adapted sector, which the recorder expands and a recorded entry never carries.</summary>
    private const string AllSectorsToken = "ALL";

    private FrozenDictionary<(string FacilityId, string SectorId), EramSectorMessage> _messages = FrozenDictionary<
        (string FacilityId, string SectorId),
        EramSectorMessage
    >.Empty;

    /// <summary>Every stored message, in no particular order.</summary>
    public IReadOnlyList<EramSectorMessage> Messages => Volatile.Read(ref _messages).Values;

    /// <summary>The message <paramref name="sectorId"/> of <paramref name="facilityId"/> holds, if any.</summary>
    public bool TryGet(string facilityId, string sectorId, [NotNullWhen(true)] out string? text)
    {
        if (Volatile.Read(ref _messages).TryGetValue((facilityId, sectorId), out EramSectorMessage? message))
        {
            text = message.Text;
            return true;
        }

        text = null;
        return false;
    }

    /// <summary>
    /// Applies one recorded entry to <paramref name="facilityId"/>'s messages. Returns false, changing nothing, when the
    /// entry is not one of the two shapes: a missing or <c>ALL</c> sector, an empty text, or text with leading or trailing
    /// blanks (the recorder writes it trimmed). Deleting a sector that holds no message applies and changes nothing.
    /// </summary>
    public bool TryApply(string facilityId, string entry)
    {
        if (entry.StartsWith(DeletePrefix, StringComparison.Ordinal))
        {
            string sector = entry[DeletePrefix.Length..];
            if (!IsSectorId(sector))
            {
                return false;
            }

            var remaining = new Dictionary<(string FacilityId, string SectorId), EramSectorMessage>(Volatile.Read(ref _messages));
            remaining.Remove((facilityId, sector));
            Publish(remaining);
            return true;
        }

        if (!entry.StartsWith(CreatePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string body = entry[CreatePrefix.Length..];
        int space = body.IndexOf(' ');
        if (space < 0)
        {
            return false;
        }

        string sectorId = body[..space];
        string text = body[(space + 1)..];
        if (!IsSectorId(sectorId) || (text.Length == 0) || (text.Trim().Length != text.Length))
        {
            return false;
        }

        var next = new Dictionary<(string FacilityId, string SectorId), EramSectorMessage>(Volatile.Read(ref _messages))
        {
            [(facilityId, sectorId)] = new EramSectorMessage(facilityId, sectorId, text),
        };
        Publish(next);
        return true;
    }

    /// <summary>Replaces every message with <paramref name="messages"/>; an empty sequence leaves none.</summary>
    public void Replace(IEnumerable<EramSectorMessage> messages)
    {
        var next = new Dictionary<(string FacilityId, string SectorId), EramSectorMessage>();
        foreach (EramSectorMessage message in messages)
        {
            next[(message.FacilityId, message.SectorId)] = message;
        }

        Publish(next);
    }

    /// <summary>Deletes every message.</summary>
    public void Clear() => Publish([]);

    /// <summary>A recorded sector id: non-empty, one token, and never the literal <c>ALL</c>.</summary>
    private static bool IsSectorId(string sector) => (sector.Length > 0) && !sector.Contains(' ') && (sector != AllSectorsToken);

    /// <summary>Publishes <paramref name="next"/> as the messages readers see.</summary>
    private void Publish(Dictionary<(string FacilityId, string SectorId), EramSectorMessage> next) =>
        Volatile.Write(ref _messages, next.ToFrozenDictionary());
}

/// <summary>One sector's stored message: the facility and sector it was left for, and its text without the clear-weather symbol.</summary>
public sealed record EramSectorMessage(string FacilityId, string SectorId, string Text);
