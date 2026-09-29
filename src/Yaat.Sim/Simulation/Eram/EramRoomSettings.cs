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
/// </summary>
public sealed class EramRoomSettings
{
    private readonly Dictionary<string, EramFacilityConflictSettings> _facilities = new(StringComparer.Ordinal);

    /// <summary>The facilities holding a non-default setting, keyed by facility id. Every other facility is all on.</summary>
    public IReadOnlyDictionary<string, EramFacilityConflictSettings> Facilities => _facilities;

    /// <summary>
    /// Whether an alert should be shown to <paramref name="sectorId"/>'s display in <paramref name="facilityId"/>.
    /// False when the facility's CA function is off; false for an MCI pair (one with an intruder) when its MCI function is
    /// off; false when the sector's CA display is off, or — for an MCI pair — its MCI display is off; true otherwise,
    /// including for a facility that holds no settings. A null <paramref name="sectorId"/> answers for the facility's
    /// functions only.
    /// </summary>
    public bool ShowsConflict(string facilityId, string? sectorId, bool isMciPair)
    {
        if (!_facilities.TryGetValue(facilityId, out EramFacilityConflictSettings? settings))
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
    /// entry is not one of the four shapes; a facility whose settings end up all default is dropped, so a room back at
    /// the defaults holds nothing.
    /// </summary>
    public bool TryApply(string facilityId, string entry)
    {
        string[] tokens = entry.Split(' ');
        if ((tokens.Length < 4) || (tokens[0] != "CA") || tokens.Any(t => t.Length == 0))
        {
            return false;
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
            return false;
        }

        if ((tokens[2] == "FUNCTION") && (tokens.Length == 4))
        {
            EramFacilityConflictSettings settings = GetOrAdd(facilityId);
            if (mci)
            {
                settings.MciFunctionOn = turnOn;
            }
            else
            {
                settings.CaFunctionOn = turnOn;
            }

            DropIfDefault(facilityId, settings);
            return true;
        }

        if ((tokens[2] == "DISPLAY") && (tokens.Length >= 5))
        {
            EramFacilityConflictSettings settings = GetOrAdd(facilityId);
            HashSet<string> offSectors = mci ? settings.MciDisplayOffSectors : settings.CaDisplayOffSectors;
            foreach (string sector in tokens[3..^1])
            {
                if (turnOn)
                {
                    offSectors.Remove(sector);
                }
                else
                {
                    offSectors.Add(sector);
                }
            }

            DropIfDefault(facilityId, settings);
            return true;
        }

        return false;
    }

    /// <summary>Replaces every facility's settings with <paramref name="facilities"/>; an empty sequence restores the defaults.</summary>
    public void Replace(IEnumerable<EramFacilityConflictSettings> facilities)
    {
        _facilities.Clear();
        foreach (EramFacilityConflictSettings settings in facilities)
        {
            if (!settings.IsDefault)
            {
                _facilities[settings.FacilityId] = settings;
            }
        }
    }

    /// <summary>Returns every facility to the defaults: all functions and displays on.</summary>
    public void Clear() => _facilities.Clear();

    private EramFacilityConflictSettings GetOrAdd(string facilityId)
    {
        if (!_facilities.TryGetValue(facilityId, out EramFacilityConflictSettings? settings))
        {
            settings = new EramFacilityConflictSettings(facilityId);
            _facilities[facilityId] = settings;
        }

        return settings;
    }

    private void DropIfDefault(string facilityId, EramFacilityConflictSettings settings)
    {
        if (settings.IsDefault)
        {
            _facilities.Remove(facilityId);
        }
    }
}

/// <summary>
/// One facility's conflict-alert settings: the CA and MCI functions (default on) and the adapted sector ids whose CA or
/// MCI display is off, stored as the entry carried them (e.g. <c>"44"</c>) and compared ordinally.
/// </summary>
public sealed class EramFacilityConflictSettings(string facilityId)
{
    public string FacilityId { get; } = facilityId;

    public bool CaFunctionOn { get; set; } = true;

    public bool MciFunctionOn { get; set; } = true;

    public HashSet<string> CaDisplayOffSectors { get; } = new(StringComparer.Ordinal);

    public HashSet<string> MciDisplayOffSectors { get; } = new(StringComparer.Ordinal);

    /// <summary>True when every function and display is on — the state of a facility that holds no settings.</summary>
    public bool IsDefault => CaFunctionOn && MciFunctionOn && (CaDisplayOffSectors.Count == 0) && (MciDisplayOffSectors.Count == 0);
}
