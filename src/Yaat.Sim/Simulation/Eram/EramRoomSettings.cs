using System.Collections.Frozen;

namespace Yaat.Sim.Simulation.Eram;

/// <summary>
/// The room's ERAM conflict-alert settings, per facility: the <c>CA</c> entry's function and per-sector display
/// switches for conventional Conflict Alert and for Mode C Intruder alerts (FAA ERAM EDSM SRS §C.2, <c>CA</c>). Detection
/// never reads them — the alert set stays whole — and a host consults <see cref="ShowsConflict"/> to decide what one
/// sector's display is shown. A facility with no entry has every function and display on.
///
/// <para>
/// Written only by <see cref="TryApply"/>, from a <see cref="RecordedEramRoomEntry"/>, in the four absolute shapes
/// <c>CA {CA|MCI} FUNCTION {ON|OFF}</c> and <c>CA {CA|MCI} DISPLAY {sector}[ {sector}…] {ON|OFF}</c>: the recorder has
/// already expanded <c>ALL</c> into adapted sector ids, so applying an entry never consults adaptation.
/// </para>
///
/// <para>
/// Copy-on-write: writes happen under the room gate but the CRC broadcast reads outside it, so every write builds a
/// new immutable dictionary of immutable <see cref="EramFacilityConflictSettings"/> and publishes it with one volatile
/// reference swap, then bumps <see cref="Version"/>. A reader takes the reference once and sees one consistent state.
/// </para>
/// </summary>
public sealed class EramRoomSettings
{
    /// <summary>The ERAM field-14 token for every adapted sector, which the recorder expands and a recorded entry never carries.</summary>
    private const string AllSectorsToken = "ALL";

    /// <summary>
    /// The last version any instance published. Shared across instances so an engine swap (a rewind, a scenario load)
    /// never reads as unchanged to a reader comparing versions.
    /// </summary>
    private static long s_lastVersion;

    private FrozenDictionary<string, EramFacilityConflictSettings> _facilities = FrozenDictionary<string, EramFacilityConflictSettings>.Empty;

    private long _version;

    /// <summary>The facilities holding a non-default setting, keyed by facility id. Every other facility is all on.</summary>
    public IReadOnlyDictionary<string, EramFacilityConflictSettings> Facilities => Volatile.Read(ref _facilities);

    /// <summary>
    /// Changes on every publish (<see cref="TryApply"/>, <see cref="Replace"/>, <see cref="Clear"/>) and is unique across
    /// instances; a host folds it into a change-detection key instead of enumerating the settings. Zero until the first publish.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Whether an alert should be shown to <paramref name="sectorId"/>'s display in <paramref name="facilityId"/>.
    /// False when the facility's CA function is off; false for an MCI pair (one with an intruder) when its MCI function is
    /// off; false when the sector's CA display is off, or — for an MCI pair — its MCI display is off; true otherwise,
    /// including for a facility that holds no settings. A null <paramref name="sectorId"/> answers for the facility's
    /// functions only.
    /// </summary>
    public bool ShowsConflict(string facilityId, string? sectorId, bool isMciPair)
    {
        if (!Volatile.Read(ref _facilities).TryGetValue(facilityId, out EramFacilityConflictSettings? settings))
        {
            return true;
        }

        if (!settings.CaFunctionOn || (isMciPair && !settings.MciFunctionOn))
        {
            return false;
        }

        if (sectorId is null)
        {
            return true;
        }

        return !settings.CaDisplayOffSectors.Contains(sectorId) && !(isMciPair && settings.MciDisplayOffSectors.Contains(sectorId));
    }

    /// <summary>
    /// Applies one recorded entry to <paramref name="facilityId"/>'s settings. Returns false, changing nothing, when the
    /// entry is not one of the four shapes, including a display entry naming the literal <c>ALL</c>; a facility whose
    /// settings end up all default is dropped, so a room back at the defaults holds nothing.
    /// </summary>
    public bool TryApply(string facilityId, string entry)
    {
        if (ParseEntry(entry) is not { } parsed)
        {
            return false;
        }

        FrozenDictionary<string, EramFacilityConflictSettings> current = Volatile.Read(ref _facilities);
        EramFacilityConflictSettings settings = current.GetValueOrDefault(facilityId) ?? EramFacilityConflictSettings.Default(facilityId);
        EramFacilityConflictSettings updated = parsed.Sectors is null
            ? ApplyFunction(settings, parsed)
            : ApplyDisplay(settings, parsed, parsed.Sectors);

        var next = new Dictionary<string, EramFacilityConflictSettings>(current, StringComparer.Ordinal);
        if (updated.IsDefault)
        {
            next.Remove(facilityId);
        }
        else
        {
            next[facilityId] = updated;
        }

        Publish(next);
        return true;
    }

    /// <summary>Replaces every facility's settings with <paramref name="facilities"/>; an empty sequence restores the defaults.</summary>
    public void Replace(IEnumerable<EramFacilityConflictSettings> facilities)
    {
        var next = new Dictionary<string, EramFacilityConflictSettings>(StringComparer.Ordinal);
        foreach (EramFacilityConflictSettings settings in facilities)
        {
            if (!settings.IsDefault)
            {
                next[settings.FacilityId] = settings;
            }
        }

        Publish(next);
    }

    /// <summary>Returns every facility to the defaults: all functions and displays on.</summary>
    public void Clear() => Publish([]);

    /// <summary>
    /// One recorded entry's parts: MCI or conventional CA, on or off, and the sectors of a display entry (null for a
    /// function entry). Null when the entry is not one of the four shapes.
    /// </summary>
    private static ParsedEntry? ParseEntry(string entry)
    {
        string[] tokens = entry.Split(' ');
        if ((tokens.Length < 4) || (tokens[0] != "CA") || tokens.Any(t => t.Length == 0))
        {
            return null;
        }

        bool? isMci = tokens[1] switch
        {
            "CA" => false,
            "MCI" => true,
            _ => null,
        };
        bool? on = tokens[^1] switch
        {
            "ON" => true,
            "OFF" => false,
            _ => null,
        };
        if ((isMci is not { } mci) || (on is not { } turnOn))
        {
            return null;
        }

        if ((tokens[2] == "FUNCTION") && (tokens.Length == 4))
        {
            return new ParsedEntry(mci, turnOn, null);
        }

        string[] sectors = tokens[3..^1];
        bool isDisplay = (tokens[2] == "DISPLAY") && (sectors.Length > 0) && !sectors.Contains(AllSectorsToken);
        return isDisplay ? new ParsedEntry(mci, turnOn, sectors) : null;
    }

    private static EramFacilityConflictSettings ApplyFunction(EramFacilityConflictSettings settings, ParsedEntry parsed) =>
        parsed.IsMci ? settings with { MciFunctionOn = parsed.TurnOn } : settings with { CaFunctionOn = parsed.TurnOn };

    private static EramFacilityConflictSettings ApplyDisplay(EramFacilityConflictSettings settings, ParsedEntry parsed, string[] sectors)
    {
        FrozenSet<string> current = parsed.IsMci ? settings.MciDisplayOffSectors : settings.CaDisplayOffSectors;
        IEnumerable<string> off = parsed.TurnOn ? current.Except(sectors, StringComparer.Ordinal) : current.Union(sectors, StringComparer.Ordinal);
        var updated = off.ToFrozenSet(StringComparer.Ordinal);
        return parsed.IsMci ? settings with { MciDisplayOffSectors = updated } : settings with { CaDisplayOffSectors = updated };
    }

    /// <summary>Publishes <paramref name="next"/> as the settings readers see, then bumps <see cref="Version"/>.</summary>
    private void Publish(Dictionary<string, EramFacilityConflictSettings> next)
    {
        Volatile.Write(ref _facilities, next.ToFrozenDictionary(StringComparer.Ordinal));
        Volatile.Write(ref _version, Interlocked.Increment(ref s_lastVersion));
    }

    private sealed record ParsedEntry(bool IsMci, bool TurnOn, string[]? Sectors);
}

/// <summary>
/// One facility's conflict-alert settings, immutable: the CA and MCI functions (default on) and the adapted sector ids
/// whose CA or MCI display is off, stored as the entry carried them (e.g. <c>"44"</c>) and compared ordinally.
/// </summary>
public sealed record EramFacilityConflictSettings(
    string FacilityId,
    bool CaFunctionOn,
    bool MciFunctionOn,
    FrozenSet<string> CaDisplayOffSectors,
    FrozenSet<string> MciDisplayOffSectors
)
{
    /// <summary>A facility's settings with every function and display on — the state of a facility that holds none.</summary>
    public static EramFacilityConflictSettings Default(string facilityId) => new(facilityId, true, true, [], []);

    /// <summary>True when every function and display is on — the state of a facility that holds no settings.</summary>
    public bool IsDefault => CaFunctionOn && MciFunctionOn && (CaDisplayOffSectors.Count == 0) && (MciDisplayOffSectors.Count == 0);
}
