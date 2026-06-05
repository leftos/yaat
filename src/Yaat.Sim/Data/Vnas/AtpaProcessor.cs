using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// In-trail ATPA cone state for a track, per the STARS rules in the CRC manual (docs/crc/stars.md):
/// Monitor while separation is healthy, Warning when a loss is predicted within 45 s, Alert when
/// separation is already lost or predicted within 24 s.
/// </summary>
public enum AtpaConeState
{
    Monitor,
    Warning,
    Alert,
}

/// <summary>Separation result for one aircraft relative to the aircraft immediately ahead in its ATPA volume.</summary>
public record AtpaResult(
    string TargetTrackId,
    double AllowedSeparation,
    double ActualSeparation,
    AtpaConeState ConeState,
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

            if (IsExcludedByTcp(volume, ac, tcpCodeByUlid))
            {
                continue;
            }

            if (IsExcludedByScratchpad(volume, ac))
            {
                continue;
            }

            if (!AtpaVolumeGeometry.IsEstablishedOnApproach(volume, ac))
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

        // Resolve TCP lists for this volume once. vNAS adapts each TCP with AtpaConeType
        // { Alert, AlertAndMonitor } (Data/Facilities/AtpaConeType.cs): AlertAndMonitor positions
        // display the monitor cone; both Alert and AlertAndMonitor positions display alert/warning cones.
        var monitorTcpCodes = ResolveTcpCodes(volume.Tcps, monitorList: true, tcpCodeByUlid);
        var alertTcpCodes = ResolveTcpCodes(volume.Tcps, monitorList: false, tcpCodeByUlid);

        // Each aircraft (except the lead) gets a result referencing the aircraft ahead
        for (int i = 1; i < inVolume.Count; i++)
        {
            var follower = inVolume[i].Ac;
            var lead = inVolume[i - 1].Ac;

            var actual = GeoMath.DistanceNm(follower.Position, lead.Position);
            var required = ComputeRequiredSeparation(lead, follower, volume.TwoPointFiveApproachEnabled);
            var closureKt = follower.GroundSpeed - lead.GroundSpeed;
            var coneState = DetermineConeState(actual, required, closureKt);

            // The monitor/alert TCP code lists are static volume adaptation (who is allowed to see the
            // cone); the live cone state above is what drives Monitor vs Warning vs Alert rendering.
            // Overwrite: if this callsign already has a result from a prior volume, the most recent wins.
            // Real STARS shows only one ATPA pairing per aircraft at a time.
            results[follower.Callsign] = new AtpaResult(
                TargetTrackId: $"CALLSIGN{lead.Callsign}",
                AllowedSeparation: required,
                ActualSeparation: actual,
                ConeState: coneState,
                AtpaMonitorTcps: monitorTcpCodes,
                AtpaAlertTcps: alertTcpCodes
            );
        }
    }

    private static double ComputeRequiredSeparation(AircraftState lead, AircraftState follower, bool twoPointFiveEnabled)
    {
        // FAA CWT mile-based wake separation (7110.65 TBL 5-5-2), floored at the applicable terminal radar
        // minimum. Wake still binds under reduced 2.5 NM final separation (7110.65 5-5-4 para 10).
        var radarFloor = twoPointFiveEnabled ? 2.5 : 3.0;
        var wake = WakeTurbulenceData.OnApproachWakeSeparationNm(
            lead.AircraftType,
            AircraftCategorization.Categorize(lead.AircraftType),
            follower.AircraftType,
            AircraftCategorization.Categorize(follower.AircraftType)
        );
        return Math.Max(radarFloor, wake);
    }

    /// <summary>Predicted-violation horizon (seconds) for the Alert cone — 7110.65 STARS ATPA / CRC manual.</summary>
    internal const double AlertHorizonSeconds = 24.0;

    /// <summary>Predicted-violation horizon (seconds) for the Warning cone — CRC manual (docs/crc/stars.md).</summary>
    internal const double WarningHorizonSeconds = 45.0;

    /// <summary>
    /// Selects the ATPA cone state for an in-trail pair from the current separation and closure rate.
    /// Alert when separation is already lost or predicted lost within <see cref="AlertHorizonSeconds"/>;
    /// Warning when predicted lost within <see cref="WarningHorizonSeconds"/>; Monitor otherwise.
    /// Closure is the trailing track's ground-speed overtake of the leader (knots); a non-positive
    /// closure means the gap is steady or opening, so no future violation is predicted.
    /// The time-to-violation is linear closure — a first-order approximation. Real STARS projects the
    /// trailing track forward along its modeled deceleration profile; both established on the same final
    /// at similar speeds, the linear estimate is conservative and close enough for the cone color.
    /// </summary>
    public static AtpaConeState DetermineConeState(double actualNm, double allowedNm, double closureKt)
    {
        if (actualNm <= allowedNm)
        {
            return AtpaConeState.Alert;
        }

        if (closureKt <= 0.0)
        {
            return AtpaConeState.Monitor;
        }

        var secondsToViolation = (actualNm - allowedNm) / (closureKt / 3600.0);
        if (secondsToViolation <= AlertHorizonSeconds)
        {
            return AtpaConeState.Alert;
        }

        if (secondsToViolation <= WarningHorizonSeconds)
        {
            return AtpaConeState.Warning;
        }

        return AtpaConeState.Monitor;
    }

    private static bool IsExcludedByTcp(AtpaVolumeConfig volume, AircraftState ac, Dictionary<string, string> tcpCodeByUlid)
    {
        var owner = ac.Track.Owner;
        if (volume.ExcludedTcpIds.Count == 0 || owner is null)
        {
            return false;
        }

        // ExcludedTcpIds carries TCP ULIDs, but the track owner identifies its position by
        // Subset + SectorId. Resolve each excluded ULID to its "{Subset}{SectorId}" code (the
        // same projection BuildTcpCodeMap produces for the monitor/alert cones) and match it
        // against the owner's TCP code. A null Subset (non-STARS owner) yields a code that no
        // real STARS TCP produces, so it simply never matches.
        var ownerCode = $"{owner.Subset}{owner.SectorId}";
        foreach (var ulid in volume.ExcludedTcpIds)
        {
            if (tcpCodeByUlid.TryGetValue(ulid, out var code) && code.Equals(ownerCode, StringComparison.Ordinal))
            {
                return true;
            }
        }

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

            var scratchpadToCheck = sp.ScratchPadNumber.Equals("Two", StringComparison.OrdinalIgnoreCase)
                ? ac.Stars.Scratchpad2
                : ac.Stars.Scratchpad1;

            if (!string.IsNullOrEmpty(scratchpadToCheck) && scratchpadToCheck.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private const string ConeTypeAlert = "Alert";
    private const string ConeTypeAlertAndMonitor = "AlertAndMonitor";

    /// <summary>
    /// Resolves the volume's TCP adaptation list to "{Subset}{SectorId}" codes. With
    /// <paramref name="monitorList"/> true, returns the AlertAndMonitor-adapted positions (monitor
    /// cone viewers); false returns the Alert and AlertAndMonitor positions (alert/warning viewers).
    /// </summary>
    private static List<string> ResolveTcpCodes(List<AtpaVolumeTcpConfig> volumeTcps, bool monitorList, Dictionary<string, string> tcpCodeByUlid)
    {
        var codes = new List<string>();
        foreach (var vtcp in volumeTcps)
        {
            var isMonitor = vtcp.ConeType.Equals(ConeTypeAlertAndMonitor, StringComparison.OrdinalIgnoreCase);
            var isAlert = isMonitor || vtcp.ConeType.Equals(ConeTypeAlert, StringComparison.OrdinalIgnoreCase);
            if (monitorList ? !isMonitor : !isAlert)
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
}
