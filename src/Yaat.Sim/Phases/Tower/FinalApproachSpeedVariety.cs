namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Assigns each arriving aircraft its own distance-from-threshold (NM) at which it settles at
/// final approach speed (Vref), so arrivals slow down at varied distances instead of every
/// aircraft flying the same tight competent profile. This reproduces the live-network spread
/// where virtual pilots reduce to FAS anywhere from the tight competent floor out to a draggy
/// early slow-down that compresses the arrival stream and makes sequencing harder.
///
/// The value is a pure function of the aircraft's callsign via an FNV-1a hash, so it consumes no
/// shared-RNG state (no perturbation of <c>SimulationWorld.Rng</c>, so existing scenarios still
/// spawn identically) and reproduces the same distance on every run — including rewind
/// reconstruction, which rebuilds aircraft from the scenario JSON and re-derives the identical
/// value. The two shared aircraft builders (<c>ScenarioLoader</c>, <c>AircraftGenerator</c>) assign
/// it at construction and store it on the aircraft
/// (<see cref="AircraftApproachState.FinalApproachFasReachGateNm"/>), so the decision is durable
/// across snapshot, rewind, and replay. Aircraft reconstructed from a recorded snapshot keep their
/// recorded value (or null → the tight floor for pre-feature recordings).
///
/// Distribution (aviation-reviewed against FAA 7110.65 §5-7 and the AIM): right-skewed and clipped
/// to <c>[FloorNm, CapNm]</c>, clustered near the competent floor with a thinning tail toward the
/// cap — median ~3.0 NM (≈1000 ft AGL, the realistic fleet norm), ~65-70% within 2.0-3.5 NM.
/// </summary>
public static class FinalApproachSpeedVariety
{
    /// <summary>
    /// Competent floor (NM) — the tightest any aircraft slows to Vref (≈640 ft AGL on a 3° path).
    /// Equals <c>FinalApproachPhase.FasReachGateNm</c>, the fallback used when no per-aircraft value
    /// is assigned, so an aircraft drawing the floor behaves exactly as before this feature.
    /// </summary>
    public const double FloorNm = 2.0;

    /// <summary>
    /// Outer cap (NM). At Vref this far out the aircraft is a long, slow drag-in that compresses the
    /// arrival stream in the zone ATC is still speed-managing — the gratuitous "slowed far too early"
    /// behavior we deliberately exclude (no 8-10 NM Vref drag-ins). ~1590 ft AGL on a 3° path, and
    /// already below the 7110.65 §5-7-2 ATC-assignable floor (170 kt jet / 150 kt turboprop within
    /// 20 mi), though inside the final the pilot owns approach speed regardless.
    /// </summary>
    public const double CapNm = 5.0;

    /// <summary>
    /// Right-skew exponent applied to the uniform hash value. &gt;1 clusters aircraft near
    /// <see cref="FloorNm"/> with a thinning tail toward <see cref="CapNm"/>; 1.5 places the median
    /// at ~3.0 NM and keeps ~65-70% within 2.0-3.5 NM.
    /// </summary>
    private const double SkewExponent = 1.5;

    /// <summary>
    /// Deterministic per-aircraft FAS settle distance (NM) in <c>[FloorNm, CapNm]</c>, right-skewed
    /// toward the floor. Pure function of <paramref name="callsign"/> — no RNG state consumed.
    /// </summary>
    public static double ComputeReachGateNm(string callsign)
    {
        double u = UnitInterval(callsign);
        double skewed = System.Math.Pow(u, SkewExponent);
        return FloorNm + ((CapNm - FloorNm) * skewed);
    }

    /// <summary>FNV-1a hash of the callsign mapped to <c>[0, 1)</c>.</summary>
    private static double UnitInterval(string callsign)
    {
        uint h = 2166136261u;
        foreach (var c in callsign)
        {
            h = (h ^ c) * 16777619u;
        }

        return h / 4294967296.0; // h / 2^32 -> [0, 1)
    }
}
