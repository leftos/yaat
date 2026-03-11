using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Vnas;

/// <summary>Separation result for one aircraft relative to the aircraft immediately ahead in its ATPA volume.</summary>
public record AtpaResult(
    string TargetTrackId,
    double AllowedSeparation,
    double ActualSeparation,
    List<string> AtpaMonitorTcps,
    List<string> AtpaAlertTcps
);

/// <summary>
/// Computes ATPA in-trail sequencing for all volumes and produces per-aircraft results
/// suitable for inclusion in STARS track data.
/// </summary>
public sealed class AtpaProcessor
{
    /// <summary>
    /// Processes all ATPA volumes and returns results keyed by callsign.
    /// Only aircraft that are in-trail (not the lead aircraft in their volume) get a result.
    /// </summary>
    public Dictionary<string, AtpaResult> Process(List<AircraftState> snapshot, List<AtpaVolumeConfig> volumes, StarsConfig starsConfig)
    {
        if (volumes.Count == 0 || snapshot.Count == 0)
        {
            return [];
        }

        // Pre-build ULID → "SubsetSectorId" map once per call
        var tcpCodeByUlid = BuildTcpCodeMap(starsConfig.Tcps);
        var results = new Dictionary<string, AtpaResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            ProcessVolume(volume, snapshot, tcpCodeByUlid, results);
        }

        return results;
    }

    private static void ProcessVolume(
        AtpaVolumeConfig volume,
        List<AircraftState> snapshot,
        Dictionary<string, string> tcpCodeByUlid,
        Dictionary<string, AtpaResult> results
    )
    {
        // Collect aircraft inside this volume, filtered by scratchpad and excluded TCP
        var inVolume = new List<(AircraftState Ac, double DistFromThreshold)>();
        foreach (var ac in snapshot)
        {
            if (!AtpaVolumeGeometry.IsInside(volume, ac))
            {
                continue;
            }

            if (IsExcludedByTcp(volume, ac))
            {
                continue;
            }

            if (IsExcludedByScratchpad(volume, ac))
            {
                continue;
            }

            inVolume.Add((ac, AtpaVolumeGeometry.DistanceFromThreshold(volume, ac)));
        }

        if (inVolume.Count < 2)
        {
            // Need at least two aircraft to form a pair
            return;
        }

        // Sort ascending: closest to threshold first (lead aircraft at index 0)
        inVolume.Sort((x, y) => x.DistFromThreshold.CompareTo(y.DistFromThreshold));

        // Resolve TCP lists for this volume once
        var monitorTcpCodes = ResolveTcpCodes(volume.Tcps, "Monitor", tcpCodeByUlid);
        var alertTcpCodes = ResolveTcpCodes(volume.Tcps, "Alert", tcpCodeByUlid);

        // Each aircraft (except the lead) gets a result referencing the aircraft ahead
        for (int i = 1; i < inVolume.Count; i++)
        {
            var follower = inVolume[i].Ac;
            var lead = inVolume[i - 1].Ac;

            var actual = GeoMath.DistanceNm(follower.Latitude, follower.Longitude, lead.Latitude, lead.Longitude);
            var required = ComputeRequiredSeparation(lead, follower, volume.TwoPointFiveApproachEnabled);

            var alertTcps = alertTcpCodes.Count > 0 && actual < required ? alertTcpCodes : [];

            // Overwrite: if this callsign already has a result from a prior volume, the most recent wins.
            // Real STARS shows only one ATPA pairing per aircraft at a time.
            results[follower.Callsign] = new AtpaResult(
                TargetTrackId: $"CALLSIGN{lead.Callsign}",
                AllowedSeparation: required,
                ActualSeparation: actual,
                AtpaMonitorTcps: monitorTcpCodes,
                AtpaAlertTcps: alertTcps
            );
        }
    }

    private static double ComputeRequiredSeparation(AircraftState lead, AircraftState follower, bool twoPointFiveEnabled)
    {
        if (twoPointFiveEnabled)
        {
            return 2.5;
        }

        // FAA wake turbulence separation based on the LEAD aircraft weight class.
        // Uses CWT (A-I) when available, falls back to AircraftCategory.
        var leadCwt = WakeTurbulenceData.GetCwt(lead.AircraftType);
        var followerCwt = WakeTurbulenceData.GetCwt(follower.AircraftType);

        var leadClass = MapCwtToWeightClass(leadCwt, AircraftCategorization.Categorize(lead.AircraftType));
        var followerClass = MapCwtToWeightClass(followerCwt, AircraftCategorization.Categorize(follower.AircraftType));

        return leadClass switch
        {
            WeightClass.Super => followerClass switch
            {
                WeightClass.Super => 6.0,
                WeightClass.Heavy => 6.0,
                WeightClass.Large => 7.0,
                WeightClass.Small => 8.0,
                _ => 6.0,
            },
            WeightClass.Heavy => followerClass switch
            {
                WeightClass.Heavy => 4.0,
                WeightClass.Large => 5.0,
                WeightClass.Small => 6.0,
                _ => 4.0,
            },
            _ => 3.0,
        };
    }

    private static WeightClass MapCwtToWeightClass(string? cwt, AircraftCategory fallback)
    {
        if (cwt is not null)
        {
            return cwt switch
            {
                "A" => WeightClass.Super, // Super (A388)
                "B" or "C" => WeightClass.Heavy, // Heavy (B77W, B763)
                "D" or "E" or "F" or "G" => WeightClass.Large, // B757, Large, Upper/Lower Medium
                "H" or "I" => WeightClass.Small, // Upper Small, Small
                _ => WeightClass.Large,
            };
        }

        return fallback switch
        {
            AircraftCategory.Jet => WeightClass.Large,
            AircraftCategory.Turboprop => WeightClass.Large,
            AircraftCategory.Piston => WeightClass.Small,
            AircraftCategory.Helicopter => WeightClass.Small,
            _ => WeightClass.Large,
        };
    }

    private static bool IsExcludedByTcp(AtpaVolumeConfig volume, AircraftState ac)
    {
        if (volume.ExcludedTcpIds.Count == 0 || ac.Owner is null)
        {
            return false;
        }

        // Owner.SectorId matches the TCP sector ID, but excluded list uses ULID IDs.
        // We store the ULID in Tcp.Id on AircraftState.Owner. However, TrackOwner
        // does not carry the ULID — it carries Subset and SectorId.
        // The ExcludedTcpIds list contains ULIDs; we can't match without the ULID.
        // For now, return false (no exclusion by TCP) — the ULID is not propagated
        // to TrackOwner. This matches safe/conservative behaviour (show more alerts).
        return false;
    }

    private static bool IsExcludedByScratchpad(AtpaVolumeConfig volume, AircraftState ac)
    {
        if (volume.Scratchpads.Count == 0)
        {
            return false;
        }

        // Each entry is a scratchpad value that, when matched, excludes or marks the aircraft as ineligible.
        // Both "Exclude" and "Ineligible" types cause the aircraft to be removed from ATPA sequencing.
        foreach (var sp in volume.Scratchpads)
        {
            var entry = sp.Entry;
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var scratchpadToCheck = sp.ScratchPadNumber.Equals("Two", StringComparison.OrdinalIgnoreCase) ? ac.Scratchpad2 : ac.Scratchpad1;

            if (!string.IsNullOrEmpty(scratchpadToCheck) && scratchpadToCheck.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ResolveTcpCodes(List<AtpaVolumeTcpConfig> volumeTcps, string coneType, Dictionary<string, string> tcpCodeByUlid)
    {
        var codes = new List<string>();
        foreach (var vtcp in volumeTcps)
        {
            if (!vtcp.ConeType.Equals(coneType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tcpCodeByUlid.TryGetValue(vtcp.TcpId, out var code))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    private static Dictionary<string, string> BuildTcpCodeMap(List<TcpConfig> tcps)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tcp in tcps)
        {
            if (!string.IsNullOrEmpty(tcp.Id))
            {
                map[tcp.Id] = $"{tcp.Subset}{tcp.SectorId}";
            }
        }

        return map;
    }

    private enum WeightClass
    {
        Super,
        Heavy,
        Large,
        Small,
    }
}
