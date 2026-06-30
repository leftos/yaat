using System.Text.RegularExpressions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
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
public sealed partial class AtpaProcessor
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

        // vNAS disables a volume by repointing its airportId at an unrelated airport (e.g. the SFO side-by
        // volumes set to OVE) while leaving the threshold at the real runway. Such a volume resolves no
        // runway; drop it so it neither captures traffic nor competes in best-fit association.
        var activeVolumes = new List<AtpaVolumeConfig>(volumes.Count);
        foreach (var volume in volumes)
        {
            if (AtpaVolumeGeometry.IsActiveVolume(volume))
            {
                activeVolumes.Add(volume);
            }
        }

        if (activeVolumes.Count == 0)
        {
            return [];
        }

        // Phase 1 — exclusive membership. ATPA in-trail monitoring is strictly per-final approach course
        // (7110.65 §5-9-5/§5-9-6): an aircraft belongs to exactly one volume. Where adapted volumes overlap
        // (convergent or closely-spaced finals), associate each established aircraft to the single best-fit
        // volume so a track on one final is never sequenced in-trail against a track on another.
        var members = new List<(AircraftState Ac, double DistFromThreshold, bool SubjectEligible)>[activeVolumes.Count];
        for (int v = 0; v < activeVolumes.Count; v++)
        {
            members[v] = [];
        }

        foreach (var ac in snapshot)
        {
            var best = -1;
            var bestScore = double.MaxValue;
            var bestScratchpadMatch = false;
            var bestSubjectEligible = true;
            for (int v = 0; v < activeVolumes.Count; v++)
            {
                var volume = activeVolumes[v];
                if (!AtpaVolumeGeometry.IsInside(volume, ac))
                {
                    continue;
                }

                if (IsExcludedByTcp(volume, ac, tcpCodeByUlid))
                {
                    continue;
                }

                var disposition = ClassifyScratchpad(volume, ac);
                if (disposition == ScratchpadDisposition.Excluded)
                {
                    continue;
                }

                if (!AtpaVolumeGeometry.IsEstablishedOnApproach(volume, ac))
                {
                    continue;
                }

                var score = FitScore(volume, ac);
                var scratchpadMatch = ScratchpadMatchesVolumeRunway(ac, volume);
                if (IsBetterFit(best, score, scratchpadMatch, bestScore, bestScratchpadMatch))
                {
                    best = v;
                    bestScore = score;
                    bestScratchpadMatch = scratchpadMatch;
                    bestSubjectEligible = disposition != ScratchpadDisposition.Ineligible;
                }
            }

            if (best >= 0)
            {
                members[best].Add((ac, AtpaVolumeGeometry.DistanceFromThreshold(activeVolumes[best], ac), bestSubjectEligible));
            }
        }

        // Phase 2 — pair in-trail within each volume.
        var results = new Dictionary<string, AtpaResult>(StringComparer.OrdinalIgnoreCase);
        for (int v = 0; v < activeVolumes.Count; v++)
        {
            PairVolume(activeVolumes[v], members[v], tcpCodeByUlid, results);
        }

        return results;
    }

    /// <summary>
    /// Sequences the aircraft already associated to one volume: sort by along-track distance (lead nearest
    /// the threshold) and pair each follower with the aircraft immediately ahead.
    /// </summary>
    private static void PairVolume(
        AtpaVolumeConfig volume,
        List<(AircraftState Ac, double DistFromThreshold, bool SubjectEligible)> inVolume,
        Dictionary<string, string> tcpCodeByUlid,
        Dictionary<string, AtpaResult> results
    )
    {
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

        // Each aircraft (except the lead) gets a result referencing the aircraft ahead. An "Ineligible"
        // scratchpad track stays in the chain — it can be the lead/reference for the track behind it — but
        // gets no cone of its own, so skip emitting a result for it.
        for (int i = 1; i < inVolume.Count; i++)
        {
            if (!inVolume[i].SubjectEligible)
            {
                continue;
            }

            var follower = inVolume[i].Ac;
            var lead = inVolume[i - 1].Ac;

            var actual = GeoMath.DistanceNm(follower.Position, lead.Position);
            var required = ComputeRequiredSeparation(lead, follower, volume, inVolume[i].DistFromThreshold);
            var closureKt = follower.GroundSpeed - lead.GroundSpeed;
            var coneState = DetermineConeState(actual, required, closureKt);

            // The monitor/alert TCP code lists are static volume adaptation (who is allowed to see the
            // cone); the live cone state above is what drives Monitor vs Warning vs Alert rendering.
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

    /// <summary>Heading-deviation normalizer for <see cref="FitScore"/> — the approach-established tolerance.</summary>
    private const double HeadingFitReferenceDeg = AtpaVolumeGeometry.ApproachHeadingTolerance;

    /// <summary>
    /// Cross-track normalizer for <see cref="FitScore"/> (nm). A FIXED reference, deliberately not the
    /// volume's own half-width: normalizing by per-volume width would discount a geometrically wide volume
    /// and could pull a track nearest its OWN centerline into a wider neighbor on closely-spaced parallels.
    /// A fixed scale makes absolute nearest-centerline the discriminator (1 nm of offset ≈ the cost of the
    /// 30° heading tolerance).
    /// </summary>
    private const double CrossTrackFitReferenceNm = 1.0;

    /// <summary>Score difference within which two volumes count as a geometric tie, deferring to the scratchpad.</summary>
    private const double FitTieEpsilon = 0.05;

    /// <summary>
    /// Best-fit score for associating an aircraft with a volume — lower is better. Combines heading
    /// deviation (normalized to the established tolerance) and absolute cross-track distance to centerline
    /// (normalized to a fixed reference). Both terms matter: heading deviation alone degenerates for true
    /// parallels (28L/28R share a course), cross-track alone can misjudge convergent finals — so a track on
    /// its own final scores ~0 while a neighbor's traffic scores high.
    /// </summary>
    private static double FitScore(AtpaVolumeConfig volume, AircraftState ac)
    {
        var headingDev = Math.Abs(AtpaVolumeGeometry.HeadingDelta(ac.TrueTrack.Degrees, AtpaVolumeGeometry.VolumeTrueHeadingDeg(volume)));
        var cross = Math.Abs(AtpaVolumeGeometry.CrossTrackNm(volume, ac));
        return (headingDev / HeadingFitReferenceDeg) + (cross / CrossTrackFitReferenceNm);
    }

    /// <summary>
    /// Whether <paramref name="score"/> is a better volume fit than the current best. A clearly lower score
    /// (beyond <see cref="FitTieEpsilon"/>) wins outright; within the tie band, a scratchpad-runway match
    /// breaks the tie; otherwise the earlier (input-order) volume is kept for determinism.
    /// </summary>
    private static bool IsBetterFit(int currentBest, double score, bool scratchpadMatch, double bestScore, bool bestScratchpadMatch)
    {
        if (currentBest < 0)
        {
            return true;
        }

        var diff = score - bestScore;
        if (diff < -FitTieEpsilon)
        {
            return true;
        }

        if (diff > FitTieEpsilon)
        {
            return false;
        }

        // Geometric tie: prefer the volume whose runway matches the track's scratchpad. Never demote a
        // current scratchpad match to a non-match.
        if (scratchpadMatch != bestScratchpadMatch)
        {
            return scratchpadMatch;
        }

        return false;
    }

    /// <summary>
    /// True when the runway parsed from the track's primary scratchpad (e.g. "I30", "I8R") names the volume's
    /// runway. Tolerant suffix match after de-padding, because scenario scratchpads are lossy — "I8R" stands
    /// for runway 28R, so "8R" matches the canonical "28R" (and "7L" matches "17L", disambiguating parallels).
    /// Empty or unparseable scratchpads simply don't match, leaving association to geometry alone.
    /// </summary>
    internal static bool ScratchpadMatchesVolumeRunway(AircraftState ac, AtpaVolumeConfig volume)
    {
        var scratchpadRunway = ParseRunwayToken(ac.Stars.Scratchpad1);
        if (scratchpadRunway is null)
        {
            return false;
        }

        var volumeRunway = AtpaVolumeGeometry.VolumeRunwayDesignator(volume);
        if (string.IsNullOrEmpty(volumeRunway))
        {
            return false;
        }

        var sp = RunwayIdentifier.ToDisplayDesignator(scratchpadRunway);
        var vol = RunwayIdentifier.ToDisplayDesignator(volumeRunway);
        return vol.Equals(sp, StringComparison.OrdinalIgnoreCase) || vol.EndsWith(sp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts a runway token ("28R", "8R", "30") from a scratchpad: an optional leading approach-type letter then the runway.</summary>
    private static string? ParseRunwayToken(string? scratchpad)
    {
        if (string.IsNullOrWhiteSpace(scratchpad))
        {
            return null;
        }

        var match = ScratchpadRunwayRegex().Match(scratchpad.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^[A-Za-z]?(\d{1,2}[LRC]?)", RegexOptions.IgnoreCase)]
    private static partial Regex ScratchpadRunwayRegex();

    private static double ComputeRequiredSeparation(
        AircraftState lead,
        AircraftState follower,
        AtpaVolumeConfig volume,
        double followerDistanceFromThresholdNm
    )
    {
        // FAA CWT mile-based wake separation (7110.65 TBL 5-5-2), floored at the applicable terminal radar
        // minimum. Reduced 2.5 NM final separation (7110.65 5-5-4) applies only when the volume enables it AND
        // the trailing aircraft is within the configured distance of the threshold; outside that the floor is
        // 3.0 NM. Wake still binds under reduced separation (5-5-4 para 10).
        var reducedApplies = volume.TwoPointFiveApproachEnabled && (followerDistanceFromThresholdNm <= volume.TwoPointFiveApproachDistance);
        var radarFloor = reducedApplies ? 2.5 : 3.0;
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

    /// <summary>How a volume's scratchpad rules dispose of a track.</summary>
    private enum ScratchpadDisposition
    {
        /// <summary>No matching rule — the track is a normal monitored member.</summary>
        Eligible,

        /// <summary>Matched an "Ineligible" rule — no cone is generated for it, but it remains a valid lead/reference for the track behind it.</summary>
        Ineligible,

        /// <summary>Matched an "Exclude" rule — removed from the volume entirely (neither subject nor reference).</summary>
        Excluded,
    }

    private const string ScratchpadTypeIneligible = "Ineligible";

    /// <summary>
    /// Classifies a track against the volume's scratchpad rules. "Exclude" drops it from the sequence
    /// entirely; "Ineligible" keeps it as a spacing reference (lead) for the aircraft behind it but
    /// suppresses its own cone. Any non-"Ineligible" type is treated as the stricter Exclude.
    /// </summary>
    private static ScratchpadDisposition ClassifyScratchpad(AtpaVolumeConfig volume, AircraftState ac)
    {
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
                return sp.Type.Equals(ScratchpadTypeIneligible, StringComparison.OrdinalIgnoreCase)
                    ? ScratchpadDisposition.Ineligible
                    : ScratchpadDisposition.Excluded;
            }
        }

        return ScratchpadDisposition.Eligible;
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
