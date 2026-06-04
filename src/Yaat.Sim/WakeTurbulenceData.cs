using Yaat.Sim.Data.Faa;

namespace Yaat.Sim;

/// <summary>
/// Maps ICAO aircraft type designators to CWT (Cooperative Wake Turbulence) codes (A-I).
/// Must be initialized via Initialize() with data from AircraftCwt.json before use.
/// Replaces the previous RECAT WTG (A-G) system with the FAA's CWT classification.
/// </summary>
public static class WakeTurbulenceData
{
    private static Dictionary<string, string> _cwtLookup = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(Dictionary<string, string> lookup)
    {
        _cwtLookup = new Dictionary<string, string>(lookup, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Get CWT code (A-I) for an aircraft type designator. Returns null if unknown.</summary>
    public static string? GetCwt(string aircraftType)
    {
        var baseType = AircraftState.StripTypePrefix(aircraftType).Trim().ToUpperInvariant();
        return _cwtLookup.TryGetValue(baseType, out var cwt) && !string.IsNullOrEmpty(cwt) ? cwt : null;
    }

    /// <summary>Coarse wake-turbulence weight class for separation purposes.</summary>
    public enum WakeClass
    {
        Small,
        Large,
        Heavy,
        Super,
    }

    /// <summary>
    /// Wake-turbulence weight class for an aircraft type. Uses CWT (A-I) when available,
    /// otherwise categorizes by engine type.
    /// </summary>
    public static WakeClass WakeClassForType(string aircraftType, AircraftCategory fallbackCategory)
    {
        var cwt = GetCwt(aircraftType);
        if (cwt is not null)
        {
            return cwt switch
            {
                "A" => WakeClass.Super, // Super (A388)
                "B" or "C" or "D" => WakeClass.Heavy, // Heavy widebodies (B77W, B763, B744/A339/IL76)
                "E" or "F" or "G" => WakeClass.Large, // B757 (E), Upper/Lower Large, regional jets (G)
                "H" or "I" => WakeClass.Small, // Upper Small, Small
                _ => WakeClass.Large,
            };
        }

        return fallbackCategory switch
        {
            AircraftCategory.Piston => WakeClass.Small,
            AircraftCategory.Helicopter => WakeClass.Small,
            _ => WakeClass.Large,
        };
    }

    /// <summary>
    /// On-approach wake-turbulence separation (nm) a FOLLOWER must trail a LEADER to the same runway,
    /// using the FAA CWT (Consolidated Wake Turbulence) mile-based minima in 7110.65 TBL 5-5-2, keyed by
    /// each aircraft's CWT category (A-I) when both are known. The mile-based CWT minima are tighter and
    /// more efficient than the legacy weight-class minima and are authorized by the .65. Returns 0 when
    /// the pair carries no wake requirement (the caller applies its own radar minimum — 3 NM, or 2.5 NM on
    /// final under FUSION). Falls back to the coarse weight-class minima when a CWT category is unavailable.
    /// </summary>
    public static double OnApproachWakeSeparationNm(
        string leaderType,
        AircraftCategory leaderCategory,
        string followerType,
        AircraftCategory followerCategory
    )
    {
        var leadCwt = GetCwt(leaderType);
        var followCwt = GetCwt(followerType);
        if (leadCwt is not null && followCwt is not null)
        {
            // Both CWT categories known: use the precise TBL 5-5-2 cell (a pair absent from the table
            // carries no wake requirement).
            return CwtOnApproachMinimaNm.TryGetValue((leadCwt[0], followCwt[0]), out var nm) ? nm : 0.0;
        }

        // Fall back to the coarse weight-class minima when a CWT category is unavailable.
        return OnApproachWakeSeparationNm(WakeClassForType(leaderType, leaderCategory), WakeClassForType(followerType, followerCategory));
    }

    /// <summary>
    /// Coarse legacy weight-class on-approach wake separation (nm), 0 when none is required. Used as the
    /// fallback when a CWT category is unavailable, and directly when only a weight class is known (e.g.
    /// the arrival generator, whose follower type is not yet chosen).
    /// </summary>
    public static double OnApproachWakeSeparationNm(WakeClass lead, WakeClass follower) =>
        lead switch
        {
            WakeClass.Super => follower switch
            {
                WakeClass.Small => 8.0,
                WakeClass.Large => 7.0,
                WakeClass.Heavy => 6.0,
                _ => 0.0,
            },
            WakeClass.Heavy => follower switch
            {
                WakeClass.Small => 6.0,
                WakeClass.Large => 5.0,
                WakeClass.Heavy => 4.0,
                _ => 0.0,
            },
            _ => 0.0,
        };

    /// <summary>
    /// FAA CWT on-approach wake minima (7110.65 TBL 5-5-2): minimum miles a follower must trail a leader,
    /// keyed by their CWT category pair (leader, follower). Pairs absent from the table carry no wake
    /// requirement (the standard terminal radar minimum applies).
    /// </summary>
    private static readonly Dictionary<(char Lead, char Follow), double> CwtOnApproachMinimaNm = new()
    {
        [('A', 'B')] = 5.0,
        [('A', 'C')] = 6.0,
        [('A', 'D')] = 6.0,
        [('A', 'E')] = 7.0,
        [('A', 'F')] = 7.0,
        [('A', 'G')] = 7.0,
        [('A', 'H')] = 8.0,
        [('A', 'I')] = 8.0,
        [('B', 'B')] = 3.0,
        [('B', 'C')] = 4.0,
        [('B', 'D')] = 4.0,
        [('B', 'E')] = 5.0,
        [('B', 'F')] = 5.0,
        [('B', 'G')] = 5.0,
        [('B', 'H')] = 5.0,
        [('B', 'I')] = 6.0,
        [('C', 'E')] = 3.5,
        [('C', 'F')] = 3.5,
        [('C', 'G')] = 3.5,
        [('C', 'H')] = 5.0,
        [('C', 'I')] = 6.0,
        [('D', 'B')] = 3.0,
        [('D', 'C')] = 4.0,
        [('D', 'D')] = 4.0,
        [('D', 'E')] = 5.0,
        [('D', 'F')] = 5.0,
        [('D', 'G')] = 5.0,
        [('D', 'H')] = 6.0,
        [('D', 'I')] = 6.0,
        [('E', 'I')] = 4.0,
        [('F', 'I')] = 4.0,
    };

    /// <summary>
    /// Max visual detection range (nm) for a target aircraft — the distance at which
    /// a pilot with normal vision, actively scanning, can first visually acquire the
    /// target silhouette in clear-air, high-contrast conditions.
    ///
    /// <para>Uses the FAA Aircraft Characteristics Database (wingspan, length, tail
    /// height) to compute a principled small-angle physics estimate. Falls back to
    /// CWT-bucketed values (derived by running the same formula on a representative
    /// type per bucket) when the aircraft type is not in the FAA ACD.</para>
    /// </summary>
    public static double TrafficDetectionRangeNm(string aircraftType, AircraftCategory fallbackCategory)
    {
        // Physical-dimensions path: most airline and GA types have real data here.
        var acd = FaaAircraftDatabase.Get(aircraftType);
        if (acd is not null)
        {
            var rangeFromDims = ComputeRangeFromDimensions(acd);
            if (rangeFromDims > 0.0)
            {
                return rangeFromDims;
            }
        }

        // Fallback 1: CWT bucket. Values derived by evaluating ComputeRangeFromDimensions
        // on a representative type per bucket with the 12 arcmin threshold (see below).
        var cwt = GetCwt(aircraftType);
        if (cwt is not null)
        {
            return cwt switch
            {
                "A" => 10.0, // Super (A388): silhouette ~332 ft → ~16 nm, clamped
                "B" => 10.0, // Upper Heavy (B744): silhouette ~288 ft → ~14 nm, clamped
                "C" => 10.0, // Lower Heavy (B763): silhouette ~219 ft → ~10 nm, clamped
                "D" => 8.5, // B757: silhouette ~181 ft
                "E" => 9.9, // Large Low (IL76): silhouette ~210 ft
                "F" => 7.6, // Upper Medium (B738): silhouette ~161 ft
                "G" => 4.8, // Lower Medium (CRJ7): silhouette ~102 ft
                "H" => 2.9, // Upper Small (C208): silhouette ~62 ft
                "I" => 2.0, // Small (C172/PA28): silhouette ~43 ft
                _ => 4.8,
            };
        }

        // Fallback 2: broad aircraft category. Values mirror a representative CWT bucket.
        return fallbackCategory switch
        {
            AircraftCategory.Jet => 7.6,
            AircraftCategory.Turboprop => 4.8,
            AircraftCategory.Piston => 2.0,
            AircraftCategory.Helicopter => 2.0,
            _ => 4.8,
        };
    }

    /// <summary>
    /// Computes clear-air visual detection range from physical dimensions using a
    /// small-angle geometry model: distance = effective_size / minimum_visual_angle.
    ///
    /// <para>The threshold angle (≈12 arcminutes) is an empirical value for first
    /// detection of a contrasting silhouette by a pilot scanning in good but not
    /// ideal conditions. Below the ~1 arcmin 20/20 Snellen resolution but coarser
    /// than theoretical CAVOK-best-case (~8 arcmin) to reflect typical training
    /// scenario conditions rather than empty-sky ideal. Consistent with the spirit
    /// of FAA AC 90-48 (Pilots' Role in Collision Avoidance) empirical studies and
    /// AIM §8-1-6 scanning discussion.</para>
    ///
    /// <para>Effective silhouette size blends wingspan (dominant from head-on/abeam),
    /// length (oblique views), and tail height (small vertical contribution). The
    /// weights give wingspan full weight, length 70%, and tail 30% — a rough average
    /// over pilot viewing geometries during approach and en-route scanning.</para>
    /// </summary>
    private static double ComputeRangeFromDimensions(FaaAircraftRecord rec)
    {
        // 12 arcmin ≈ 0.003491 rad. See AC 90-48 / AIM §8-1-6 discussion.
        const double DetectionThresholdRad = 0.003491;
        const double FtPerNm = 6076.12;
        const double MinRangeNm = 1.5;
        const double MaxRangeNm = 10.0;

        double wingspan = rec.WingspanFtWithWinglets ?? rec.WingspanFtWithoutWinglets ?? 0.0;
        double length = rec.LengthFt ?? 0.0;
        double tailHeight = rec.TailHeightAtOewFt ?? 0.0;

        if (wingspan <= 0.0 && length <= 0.0)
        {
            return 0.0;
        }

        double silhouetteFt = Math.Sqrt((wingspan * wingspan) + (0.7 * length * length) + (0.3 * tailHeight * tailHeight));

        double rangeFt = silhouetteFt / DetectionThresholdRad;
        double rangeNm = rangeFt / FtPerNm;

        return Math.Clamp(rangeNm, MinRangeNm, MaxRangeNm);
    }
}
