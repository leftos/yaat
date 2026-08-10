namespace Yaat.Sim;

/// <summary>
/// Per-layer wind variability inputs after nullable-field resolution: the mean wind plus
/// the peak amplitudes the instantaneous wind may excurse by. <paramref name="GustExcessKts"/>
/// is the peak speed excursion above the mean (reported gust − mean; 0 = steady speed).
/// <paramref name="HalfSpreadDeg"/> is the peak direction excursion either side of the mean
/// (a METAR 180V240 on a 210 mean authors 30; 0 = steady direction). <paramref name="Variable"/>
/// selects the VRB model (no prevailing direction). <paramref name="HeightAglFt"/> is height
/// above the profile's surface layer, used for the altitude taper.
/// </summary>
public readonly record struct WindPerturbationInputs(
    double MeanDirectionDeg,
    double MeanSpeedKts,
    double GustExcessKts,
    double HalfSpreadDeg,
    bool Variable,
    double HeightAglFt
);

/// <summary>
/// Deterministic time-varying wind perturbation. Computes the instantaneous wind an
/// aircraft experiences from the authored mean wind plus per-layer variability
/// (direction spread, VRB, gusts) as a pure function of sim time — no RNG, no
/// accumulated state — so live runs and replays evaluate identically.
///
/// Structure: band-limited value noise (three octaves at Kolmogorov inertial-subrange
/// amplitude scaling, plus a slow lateral-only meander octave) drives bounded speed and
/// direction excursions around the mean. Because the noise is bounded, two invariants
/// hold exactly: instantaneous speed never exceeds mean + gust excess (the reported gust
/// is a hard ceiling) and direction never leaves the authored arc. VRB winds instead
/// wander the full circle on a slow meander timescale while their speed moves around the
/// reported 2-minute average. A layer that authors no variability passes through
/// bit-identical.
///
/// Reporting surfaces (METAR issuance, windsock) sample at phase 0; each aircraft samples
/// at a stable callsign-derived phase offset so wobble is decorrelated across the scope
/// (surface-turbulence lateral correlation length is ~100 m — aircraft even 1 nm apart
/// genuinely see decorrelated perturbations) while the mean wind stays common.
/// </summary>
public static class WindVariation
{
    // --- Temporal structure -------------------------------------------------------------

    /// <summary>
    /// Base eddy period numerator: Kaimal neutral surface-layer spectra peak at reduced
    /// frequency n = fz/U ≈ 0.045 at the 10 m anemometer height, giving P ≈ 432/U_kt.
    /// Strong winds feel busy (~12-25 s), light winds languid (~90-120 s).
    /// </summary>
    private const double BaseEddyPeriodNumerator = 432.0;

    private const double MinBaseEddyPeriodSeconds = 12.0;
    private const double MaxBaseEddyPeriodSeconds = 120.0;

    /// <summary>WMO/ASOS gust definitions are 3-5 s averages; the finest octave never goes below this.</summary>
    private const double MinOctavePeriodSeconds = 4.0;

    /// <summary>Octave amplitude ratio 3^(-1/3): Kolmogorov inertial-subrange σ(τ) ∝ τ^(1/3) scaling.</summary>
    private const double OctaveAmplitudeRatio = 0.693;

    /// <summary>Slow lateral-only meander: mesoscale low-frequency direction variance that speed variance lacks.</summary>
    private const double MeanderPeriodSeconds = 300.0;

    /// <summary>Meander amplitude relative to the first lateral octave.</summary>
    private const double MeanderAmplitudeWeight = 0.35;

    /// <summary>
    /// Light-wind VRB meandering is decoupled/stable-boundary-layer behavior with periods of
    /// minutes (Anfossi et al. 2005), not the 432/U eddy formula — which would spin the wind
    /// implausibly fast at 4 kt.
    /// </summary>
    private const double VrbBaseEddyPeriodSeconds = 240.0;

    // --- Amplitude model ----------------------------------------------------------------

    /// <summary>
    /// Peak factor relating a 3-5 s gust peak in a 10-min window to the longitudinal σ
    /// (Durst/Wieringa gust-factor theory, midpoint of 2.5-3.0).
    /// </summary>
    private const double GustPeakFactor = 2.75;

    /// <summary>σ_v/σ_u: 0.79 in neutral conditions (Kaimal/Panofsky), ~1.0 convective; variability skews convective.</summary>
    private const double LateralToLongitudinalSigmaRatio = 0.90;

    /// <summary>Observed 2-min direction range ≈ this many σ_θ (weakest-evidence constant; calibrated by round-trip tests).</summary>
    private const double SpreadToSigmaThetaFactor = 4.0;

    /// <summary>Lulls are shallower than gusts are tall (gust factor ~1.5 vs lull factor ~0.65): negative speed excursions scale by this.</summary>
    private const double LullAsymmetryFactor = 0.65;

    /// <summary>
    /// VRB speed excursion as a fraction of the reported speed. The reported VRB speed is a
    /// 2-minute average; instantaneous speed moves within ±this fraction of it, so the
    /// observed mean equals the report by construction and light wind stays light.
    /// </summary>
    private const double VrbSpeedVariationFraction = 0.5;

    /// <summary>
    /// VRB direction wander amplitude either side of the (weak) preferred direction. Direction
    /// wraps modulo 360, so an over-amplitude beyond ±180° is what lets typical noise
    /// excursions genuinely cover the full circle while still centering on the mean.
    /// </summary>
    private const double VrbWanderAmplitudeDeg = 540.0;

    /// <summary>Below this mean speed a directional layer does not perturb — calm air doesn't gust.</summary>
    private const double MinPerturbableMeanSpeedKt = 0.5;

    /// <summary>Engine safety clamp: Gust/Mean above 2.5 is gust-front territory, not stationary turbulence.</summary>
    private const double MaxGustFactor = 2.5;

    /// <summary>Engine safety clamp on authored half-spread; ≥90° half-spread (180° full) codes VRB, not an arc.</summary>
    private const double MaxHalfSpreadDeg = 90.0;

    // --- Altitude taper -----------------------------------------------------------------

    /// <summary>Full perturbation amplitude at or below this height above the surface layer (final, pattern, climb-out).</summary>
    private const double FullAmplitudeTopAglFt = 1000.0;

    /// <summary>Zero perturbation at or above this height (cruise track stays clean); smoothstep between.</summary>
    private const double ZeroAmplitudeTopAglFt = 3000.0;

    // --- Sampling -----------------------------------------------------------------------

    /// <summary>
    /// Span of the per-aircraft phase offset domain. Surface-layer turbulence has a
    /// lateral correlation length of ~100 m, so aircraft even 1 nm apart genuinely see
    /// decorrelated turbulence; a phase offset in [0, span) reproduces that without
    /// per-aircraft noise fields.
    /// </summary>
    public const double PerAircraftPhaseSpanSeconds = 3600.0;

    // Fixed channel seeds. Deliberately NOT per-layer: a single coherent noise field per
    // channel means an aircraft crossing a layer boundary sees continuously varying
    // amplitude (σ interpolates) with no phase discontinuity.
    private const ulong SeedLongitudinal = 0x9E3779B97F4A7C15;
    private const ulong SeedLateral = 0xC2B2AE3D27D4EB4F;
    private const ulong SeedVrbSpeed = 0x165667B19E3779F9;
    private const ulong SeedVrbDirection = 0x27D4EB2F165667C5;

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// Stable per-aircraft phase offset in [0, <see cref="PerAircraftPhaseSpanSeconds"/>).
    /// FNV-1a over the uppercased callsign characters — never <c>string.GetHashCode</c>,
    /// whose value is randomized per process and would break replay determinism.
    /// </summary>
    public static double PhaseSecondsFor(string callsign)
    {
        uint hash = FnvOffsetBasis;
        foreach (char c in callsign)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= FnvPrime;
        }

        return (hash / (double)uint.MaxValue) * PerAircraftPhaseSpanSeconds;
    }

    /// <summary>
    /// Resolves a layer's nullable variability fields into peak excursion amplitudes,
    /// cross-deriving the missing component when only one is authored (they share one
    /// turbulence intensity) and using both verbatim when both are present — gust and
    /// spread measure different components (σ_u vs σ_v), so never cross-derive over an
    /// authored value.
    /// </summary>
    public static (double GustExcessKts, double HalfSpreadDeg) ResolveAmplitudes(
        double meanSpeedKts,
        double? gustKts,
        double? directionVariabilityDeg,
        bool variable
    )
    {
        if (variable || meanSpeedKts < MinPerturbableMeanSpeedKt)
        {
            // VRB uses the isotropic model (amplitudes derive from mean speed internally);
            // calm directional air doesn't perturb.
            return (0, 0);
        }

        double gustExcess = gustKts is { } g && g > meanSpeedKts ? g - meanSpeedKts : 0;
        gustExcess = Math.Min(gustExcess, (MaxGustFactor - 1.0) * meanSpeedKts);

        double halfSpread = Math.Clamp(directionVariabilityDeg ?? 0, 0, MaxHalfSpreadDeg);

        if (gustExcess > 0 && halfSpread <= 0)
        {
            // Gust only: σ_u from the gust, σ_v via the lateral ratio, spread via the range factor.
            double sigmaU = gustExcess / GustPeakFactor;
            double sigmaThetaRad = (LateralToLongitudinalSigmaRatio * sigmaU) / meanSpeedKts;
            halfSpread = Math.Clamp(sigmaThetaRad * RadToDeg * SpreadToSigmaThetaFactor / 2.0, 0, MaxHalfSpreadDeg);
        }
        else if (halfSpread > 0 && gustExcess <= 0)
        {
            // Spread only: σ_θ from the range factor, σ_u via the lateral ratio, gust via the peak factor.
            double sigmaThetaRad = (2.0 * halfSpread) / SpreadToSigmaThetaFactor * DegToRad;
            double sigmaU = (sigmaThetaRad * meanSpeedKts) / LateralToLongitudinalSigmaRatio;
            gustExcess = Math.Min(GustPeakFactor * sigmaU, (MaxGustFactor - 1.0) * meanSpeedKts);
        }

        return (gustExcess, halfSpread);
    }

    /// <summary>
    /// Computes the instantaneous wind for the given inputs at the given sim time and phase.
    /// Pure and closed-form: same inputs → same output, on every replay. Sim time is
    /// quantized to whole seconds so the live per-second path and the 0.25 s sub-tick
    /// replay path evaluate identically within a second.
    /// </summary>
    public static WindAtAltitude Compute(WindPerturbationInputs inputs, double simTimeSeconds, double phaseSeconds)
    {
        double taper = AmplitudeTaper(inputs.HeightAglFt);
        if (taper <= 0)
        {
            return new WindAtAltitude(inputs.MeanDirectionDeg, inputs.MeanSpeedKts);
        }

        double t = Math.Floor(simTimeSeconds) + phaseSeconds;

        if (inputs.Variable)
        {
            return ComputeVrb(inputs, t, taper);
        }

        if ((inputs.GustExcessKts <= 0) && (inputs.HalfSpreadDeg <= 0))
        {
            return new WindAtAltitude(inputs.MeanDirectionDeg, inputs.MeanSpeedKts);
        }

        double basePeriod = Math.Clamp(
            BaseEddyPeriodNumerator / Math.Max(inputs.MeanSpeedKts, 1.0),
            MinBaseEddyPeriodSeconds,
            MaxBaseEddyPeriodSeconds
        );

        double speed = inputs.MeanSpeedKts;
        if (inputs.GustExcessKts > 0)
        {
            double nU = LongitudinalNoise(t, basePeriod);
            double excursion = nU >= 0 ? nU : LullAsymmetryFactor * nU;
            speed = Math.Max(inputs.MeanSpeedKts + (inputs.GustExcessKts * taper * excursion), 0);
        }

        double direction = inputs.MeanDirectionDeg;
        if (inputs.HalfSpreadDeg > 0)
        {
            double nV = LateralNoise(t, basePeriod);
            direction = NormalizeDeg(inputs.MeanDirectionDeg + (inputs.HalfSpreadDeg * taper * nV));
        }

        return new WindAtAltitude(direction, speed);
    }

    private static WindAtAltitude ComputeVrb(WindPerturbationInputs inputs, double t, double taper)
    {
        // Polar formulation with the observation semantics built in: the reported VRB speed
        // is a 2-minute average, so instantaneous speed moves within ±VrbSpeedVariationFraction
        // of it (observed mean equals the report by construction; light wind stays light), and
        // direction wanders around the weak preferred direction with an over-amplitude that
        // wraps the circle. A vector-space model was tried and rejected: bounded noise carries
        // so little variance per unit amplitude that matching the reported mean speed required
        // amplitudes letting a "VRB04" spike to ~5× its reported speed.
        double nSpd = OctaveNoise(t, VrbBaseEddyPeriodSeconds, SeedVrbSpeed, includeMeander: false);
        double nDir = OctaveNoise(t, VrbBaseEddyPeriodSeconds, SeedVrbDirection, includeMeander: false);

        double speed = Math.Max(inputs.MeanSpeedKts * (1.0 + (VrbSpeedVariationFraction * taper * nSpd)), 0);
        double direction = NormalizeDeg(inputs.MeanDirectionDeg + (VrbWanderAmplitudeDeg * taper * nDir));

        return new WindAtAltitude(direction, speed);
    }

    /// <summary>1 at/below the full-amplitude band, 0 at/above the zero band, smoothstep between.</summary>
    public static double AmplitudeTaper(double heightAglFt)
    {
        if (heightAglFt <= FullAmplitudeTopAglFt)
        {
            return 1.0;
        }

        if (heightAglFt >= ZeroAmplitudeTopAglFt)
        {
            return 0.0;
        }

        double x = (heightAglFt - FullAmplitudeTopAglFt) / (ZeroAmplitudeTopAglFt - FullAmplitudeTopAglFt);
        return 1.0 - (x * x * (3.0 - (2.0 * x)));
    }

    private static double LongitudinalNoise(double t, double basePeriodSeconds)
    {
        return OctaveNoise(t, basePeriodSeconds, SeedLongitudinal, includeMeander: false);
    }

    private static double LateralNoise(double t, double basePeriodSeconds)
    {
        return OctaveNoise(t, basePeriodSeconds, SeedLateral, includeMeander: true);
    }

    /// <summary>
    /// Normalized band-limited value noise in [-1, 1]: three octaves at period ratio 3 and
    /// amplitude ratio 3^(-1/3), finest floored at <see cref="MinOctavePeriodSeconds"/>;
    /// the lateral channel adds the slow meander octave. Weights are normalized so the
    /// bound |n| ≤ 1 is exact — which is what makes the reported gust a hard ceiling.
    /// </summary>
    private static double OctaveNoise(double t, double basePeriodSeconds, ulong seed, bool includeMeander)
    {
        double w1 = 1.0;
        double w2 = OctaveAmplitudeRatio;
        double w3 = OctaveAmplitudeRatio * OctaveAmplitudeRatio;

        double sum = (w1 * ValueNoise(t, basePeriodSeconds, seed)) + (w2 * ValueNoise(t, basePeriodSeconds / 3.0, seed ^ 0xA5A5A5A5A5A5A5A5));
        sum += w3 * ValueNoise(t, Math.Max(basePeriodSeconds / 9.0, MinOctavePeriodSeconds), seed ^ 0x5A5A5A5A5A5A5A5A);
        double totalWeight = w1 + w2 + w3;

        if (includeMeander)
        {
            sum += MeanderAmplitudeWeight * ValueNoise(t, MeanderPeriodSeconds, seed ^ 0x3C3C3C3C3C3C3C3C);
            totalWeight += MeanderAmplitudeWeight;
        }

        return sum / totalWeight;
    }

    /// <summary>Smoothly interpolated lattice noise in [-1, 1], period-scaled; pure function of (t, period, seed).</summary>
    private static double ValueNoise(double t, double periodSeconds, ulong seed)
    {
        double x = t / periodSeconds;
        double floor = Math.Floor(x);
        long i0 = (long)floor;
        double f = x - floor;

        double v0 = Hash11(seed, i0);
        double v1 = Hash11(seed, i0 + 1);

        double s = f * f * (3.0 - (2.0 * f));
        return v0 + ((v1 - v0) * s);
    }

    /// <summary>SplitMix64-finalized lattice hash → [-1, 1).</summary>
    private static double Hash11(ulong seed, long lattice)
    {
        unchecked
        {
            ulong x = seed ^ ((ulong)lattice * 0x9E3779B97F4A7C15);
            x ^= x >> 30;
            x *= 0xBF58476D1CE4E5B9;
            x ^= x >> 27;
            x *= 0x94D049BB133111EB;
            x ^= x >> 31;
            return ((x >> 11) * (1.0 / (1UL << 53)) * 2.0) - 1.0;
        }
    }

    private static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    /// <summary>
    /// Applies the time-varying perturbation to an interpolated mean wind. The identity
    /// mapping — every wind lookup threads sim time and phase through this single choke
    /// point so the perturbation model has exactly one seam.
    /// </summary>
    public static WindAtAltitude Perturb(WindAtAltitude meanWind, double simTimeSeconds, double phaseSeconds)
    {
        _ = simTimeSeconds;
        _ = phaseSeconds;
        return meanWind;
    }
}
