using Yaat.Sim.Weather;

namespace Yaat.Sim;

/// <summary>Wind at a specific altitude after interpolation.</summary>
public readonly record struct WindAtAltitude(double DirectionDeg, double SpeedKts);

/// <summary>
/// Where a wind is sampled: the position, and the magnetic declination there (degrees, east positive, as
/// <see cref="MagneticDeclination.GetDeclination(LatLon, DateTime)"/> gives it) that turns the profile's gridded winds, which
/// are true, magnetic as its layers are.
/// </summary>
public readonly record struct WindSamplePoint(LatLon Position, double DeclinationDeg);

public static class WindInterpolator
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    // ISA standard atmosphere constants
    private const double T0 = 288.15; // Sea-level temperature (K)
    private const double LapseRate = 0.0065; // Tropospheric lapse rate (K/m)
    private const double G = 9.80665; // Standard gravity (m/s²)
    private const double RGas = 287.05287; // Specific gas constant for dry air (J/(kg·K))
    private const double Gamma = 1.4; // Ratio of specific heats
    private const double KtToMs = 0.514444;
    private const double MsToKt = 1.0 / KtToMs;
    private const double FtToM = 0.3048;
    private const double GOverLR = G / (LapseRate * RGas); // ~5.255876
    private const double GammaRatio = Gamma / (Gamma - 1.0); // 3.5
    private const double InvGammaRatio = 1.0 / GammaRatio; // 2/7
    private const double TropopauseM = 11_000.0; // Tropopause altitude (m)
    private const double TropopauseK = 216.65; // Tropopause temperature (K)
    private const double SeaLevelPressureHpa = 1013.25; // ISA sea-level pressure P₀ (hPa)
    private static readonly double A0 = Math.Sqrt(Gamma * RGas * T0); // Sea-level speed of sound (~340.294 m/s)
    private static readonly double DeltaTropopause = Math.Pow(TropopauseK / T0, GOverLR); // ~0.22336

    /// <summary>
    /// Returns interpolated wind at the given altitude, perturbed by
    /// <see cref="WindVariation"/> for the given sim time and phase offset.
    /// Null profile or empty layers returns zero wind.
    /// Altitudes outside the layer range are clamped to the nearest layer.
    /// Interpolation uses N/E vector components to handle the 0/360 boundary correctly.
    /// <paramref name="simTimeSeconds"/> is the scenario's elapsed sim time;
    /// <paramref name="phaseSeconds"/> is the sampler's phase offset — 0 for
    /// observation/reporting surfaces, <see cref="WindVariation.PhaseSecondsFor"/>
    /// for a specific aircraft.
    /// </summary>
    public static WindAtAltitude GetWindAt(WeatherProfile? profile, double altitudeFt, double simTimeSeconds, double phaseSeconds)
    {
        if (profile is null || profile.WindLayers.Count == 0)
        {
            return new WindAtAltitude(0, 0);
        }

        return SampleLayers(profile.WindLayers, profile.SurfaceElevationFt(), altitudeFt, simTimeSeconds, phaseSeconds);
    }

    /// <summary>
    /// <see cref="GetWindAt(WeatherProfile?, double, double, double)"/> at a position. Where the profile's
    /// <see cref="WeatherProfile.WindGrid"/> covers <paramref name="at"/>, the wind comes from a column of the METAR surface
    /// layer (layer 0), placed at the surface datum (<see cref="WeatherProfile.SurfaceElevationFt"/>) rather than its authored
    /// altitude, and above it the grid's levels there, each turned magnetic with <see cref="WindSamplePoint.DeclinationDeg"/>;
    /// the FD layers are not used. Grid levels lower than <see cref="GridLevelGroundClearanceFt"/> above the surface's pressure
    /// altitude (<see cref="SurfacePressureAltitudeFt"/>) are left out: they lie under or too near the ground. A grid level
    /// inside the variability taper band (below <see cref="WindVariation.ZeroAmplitudeTopAglFt"/> above the surface) carries
    /// layer 0's gust and direction-spread amplitudes, so the taper alone fades them with height. The altitude interpolation
    /// and the perturbation then run as they do over the layers. With no grid, outside it, or with no grid level kept, the
    /// result is exactly the overload without a position. Nothing is allocated.
    /// </summary>
    public static WindAtAltitude GetWindAt(WeatherProfile? profile, WindSamplePoint at, double altitudeFt, double simTimeSeconds, double phaseSeconds)
    {
        if ((profile is null) || (GridBracket(profile, at, altitudeFt, ColumnFrame.Magnetic) is not { } bracket))
        {
            return GetWindAt(profile, altitudeFt, simTimeSeconds, phaseSeconds);
        }

        double heightAglFt = Math.Max(0, altitudeFt - profile.SurfaceElevationFt());
        WindPerturbationInputs inputs = bracket.High is { } high
            ? InputsBetween(bracket.Low, high, bracket.T, heightAglFt)
            : InputsAt(bracket.Low, heightAglFt);
        return WindVariation.Compute(inputs, simTimeSeconds, phaseSeconds);
    }

    /// <summary>
    /// How far above the surface's pressure altitude the lowest GFS level a column keeps must lie: a model level nearer the
    /// ground than this is an extrapolation through (or a boundary layer under) the terrain, not the wind an aircraft flies in.
    /// </summary>
    public const double GridLevelGroundClearanceFt = 500.0;

    /// <summary>
    /// The surface's pressure altitude, feet: <see cref="WeatherProfile.SurfaceElevationFt"/> plus 1,000 ft per inch of mercury
    /// the profile's altimeter setting (<see cref="WeatherProfile.SurfaceAltimeterInHg"/>) lies below standard (29.92).
    /// </summary>
    public static double SurfacePressureAltitudeFt(WeatherProfile profile) =>
        profile.SurfaceElevationFt() + ((WeatherProfile.StandardAltimeterInHg - profile.SurfaceAltimeterInHg()) * 1000.0);

    /// <summary>
    /// The perturbed wind at <paramref name="altitudeFt"/> over a non-empty, altitude-ascending layer list:
    /// clamped to the end layers outside their range, interpolated between the bracketing layers inside it.
    /// </summary>
    private static WindAtAltitude SampleLayers(
        List<WindLayer> layers,
        double surfaceElevationFt,
        double altitudeFt,
        double simTimeSeconds,
        double phaseSeconds
    )
    {
        // Height above the ground drives the variability taper. The datum is the profile's
        // resolved field elevation, NOT the lowest layer's altitude — real profiles author
        // their lowest layer well above the field, and live weather authors it at 0 MSL
        // regardless of terrain, either of which would misplace (or entirely disable) the
        // taper at high-elevation airports.
        double heightAglFt = Math.Max(0, altitudeFt - surfaceElevationFt);

        if (altitudeFt <= layers[0].Altitude)
        {
            return WindVariation.Compute(InputsAt(ColumnLevel.Of(layers[0]), heightAglFt), simTimeSeconds, phaseSeconds);
        }

        if (altitudeFt >= layers[^1].Altitude)
        {
            return WindVariation.Compute(InputsAt(ColumnLevel.Of(layers[^1]), heightAglFt), simTimeSeconds, phaseSeconds);
        }

        (WindLayer low, WindLayer high, double t) = Bracket(layers, altitudeFt);
        return WindVariation.Compute(InputsBetween(ColumnLevel.Of(low), ColumnLevel.Of(high), t, heightAglFt), simTimeSeconds, phaseSeconds);
    }

    /// <summary>
    /// One level of a wind column as the perturbation sees it: altitude, mean FROM direction and speed, and the resolved
    /// variability amplitudes (<see cref="WindVariation.ResolveAmplitudes"/>) with the VRB flag.
    /// </summary>
    private readonly record struct ColumnLevel(
        double AltitudeFt,
        double DirectionDeg,
        double SpeedKts,
        double GustExcessKts,
        double HalfSpreadDeg,
        bool Variable
    )
    {
        /// <summary>An authored layer, its amplitudes resolved from its gusts and direction variability.</summary>
        public static ColumnLevel Of(WindLayer layer)
        {
            bool variable = layer.Variable ?? false;
            (double gustExcess, double halfSpread) = WindVariation.ResolveAmplitudes(
                layer.Speed,
                layer.Gusts,
                layer.DirectionVariabilityDeg,
                variable
            );
            return new ColumnLevel(layer.Altitude, layer.Direction, layer.Speed, gustExcess, halfSpread, variable);
        }
    }

    /// <summary>The perturbation inputs of one level, used as it stands (an altitude clamped to an end of the column).</summary>
    private static WindPerturbationInputs InputsAt(ColumnLevel level, double heightAglFt) =>
        new(level.DirectionDeg, level.SpeedKts, level.GustExcessKts, level.HalfSpreadDeg, level.Variable, heightAglFt);

    /// <summary>
    /// The perturbation inputs a fraction <paramref name="t"/> of the way from <paramref name="low"/> to
    /// <paramref name="high"/>. Variability amplitudes interpolate between the bracketing levels with the same scheme as the
    /// mean vector, so a level boundary never produces a turbulence discontinuity; the AGL taper applies last, inside
    /// <see cref="WindVariation.Compute"/>. The discrete VRB flag steps at t=0.5, matching the cloud-cover convention.
    /// </summary>
    private static WindPerturbationInputs InputsBetween(ColumnLevel low, ColumnLevel high, double t, double heightAglFt)
    {
        WindAtAltitude mean = InterpolateMean(low.DirectionDeg, low.SpeedKts, high.DirectionDeg, high.SpeedKts, t);
        double gustExcess = low.GustExcessKts + (t * (high.GustExcessKts - low.GustExcessKts));
        double halfSpread = low.HalfSpreadDeg + (t * (high.HalfSpreadDeg - low.HalfSpreadDeg));
        bool variable = t < 0.5 ? low.Variable : high.Variable;
        return new WindPerturbationInputs(mean.DirectionDeg, mean.SpeedKts, gustExcess, halfSpread, variable, heightAglFt);
    }

    /// <summary>
    /// Returns the mean wind at the given altitude: interpolated between the bracketing layers exactly as
    /// <see cref="GetWindAt"/> does, without the <see cref="WindVariation"/> perturbation. Direction is magnetic, as the
    /// layers are. Null profile or empty layers returns zero wind; altitudes outside the layer range clamp to the nearest
    /// layer.
    /// </summary>
    public static WindAtAltitude GetMeanWindAt(WeatherProfile? profile, double altitudeFt)
    {
        if (profile is null || profile.WindLayers.Count == 0)
        {
            return new WindAtAltitude(0, 0);
        }

        return MeanOverLayers(profile.WindLayers, altitudeFt);
    }

    /// <summary>
    /// The mean wind at <paramref name="altitudeFt"/> over the grid column at <paramref name="at"/>, TRUE: the column
    /// <see cref="GetWindAt(WeatherProfile?, WindSamplePoint, double, double, double)"/> samples, with the grid's levels as the
    /// grid gives them and the METAR surface layer turned true with <see cref="WindSamplePoint.DeclinationDeg"/>. Null where
    /// that overload falls back to the layers: no grid, a position outside it, or no grid level kept.
    /// </summary>
    public static WindAtAltitude? GetGriddedMeanWindTrueAt(WeatherProfile profile, WindSamplePoint at, double altitudeFt)
    {
        if (GridBracket(profile, at, altitudeFt, ColumnFrame.True) is not { } bracket)
        {
            return null;
        }

        ColumnLevel low = bracket.Low;
        return bracket.High is { } high
            ? InterpolateMean(low.DirectionDeg, low.SpeedKts, high.DirectionDeg, high.SpeedKts, bracket.T)
            : new WindAtAltitude(low.DirectionDeg, low.SpeedKts);
    }

    /// <summary>The unperturbed wind at <paramref name="altitudeFt"/> over a non-empty, altitude-ascending layer list.</summary>
    private static WindAtAltitude MeanOverLayers(List<WindLayer> layers, double altitudeFt)
    {
        if (altitudeFt <= layers[0].Altitude)
        {
            return new WindAtAltitude(layers[0].Direction, layers[0].Speed);
        }

        if (altitudeFt >= layers[^1].Altitude)
        {
            return new WindAtAltitude(layers[^1].Direction, layers[^1].Speed);
        }

        (WindLayer low, WindLayer high, double t) = Bracket(layers, altitudeFt);
        return InterpolateMean(low.Direction, low.Speed, high.Direction, high.Speed, t);
    }

    /// <summary>Which north a grid column's directions are measured from.</summary>
    private enum ColumnFrame
    {
        Magnetic,
        True,
    }

    /// <summary>
    /// Where <paramref name="altitudeFt"/> falls in the grid column at a position: the level below (or the level an altitude
    /// clamps to, with no <c>High</c>), the level above, and the fraction of the way between. The column is the METAR surface
    /// layer (layer 0) at the surface datum, when the profile has one, then the grid levels kept above it (see
    /// <see cref="GetWindAt(WeatherProfile?, WindSamplePoint, double, double, double)"/>). In <see cref="ColumnFrame.Magnetic"/>
    /// the grid's true directions turn magnetic and layer 0 stays as authored; in <see cref="ColumnFrame.True"/> layer 0 turns
    /// true and the grid's directions stay. Null without a grid, outside it, or with no grid level kept. Only the two
    /// bracketing grid levels are sampled (<see cref="GriddedWindField.SampleBracket"/>).
    /// </summary>
    private static (ColumnLevel Low, ColumnLevel? High, double T)? GridBracket(
        WeatherProfile profile,
        WindSamplePoint at,
        double altitudeFt,
        ColumnFrame frame
    )
    {
        if (profile.WindGrid is not { } grid)
        {
            return null;
        }

        double surfaceFt = profile.SurfaceElevationFt();
        double floorFt = Math.Max(SurfacePressureAltitudeFt(profile) + GridLevelGroundClearanceFt, Math.BitIncrement(surfaceFt));
        if (
            (grid.SampleBracket(at.Position.Lat, at.Position.Lon, altitudeFt, floorFt) is not { } bracket)
            || ((bracket.Below is null) && (bracket.Above is null))
        )
        {
            return null;
        }

        ColumnLevel? surface = profile.WindLayers.Count > 0 ? SurfaceLevel(profile.WindLayers[0], surfaceFt, at, frame) : null;
        ColumnLevel? low = bracket.Below is { } below ? GridLevel(below, surface, surfaceFt, at, frame) : surface;
        ColumnLevel? high = bracket.Above is { } above ? GridLevel(above, surface, surfaceFt, at, frame) : null;
        if (low is not { } lowLevel)
        {
            return (high!.Value, null, 0);
        }

        if ((high is not { } highLevel) || (altitudeFt <= lowLevel.AltitudeFt))
        {
            return (lowLevel, null, 0);
        }

        return (lowLevel, highLevel, (altitudeFt - lowLevel.AltitudeFt) / (highLevel.AltitudeFt - lowLevel.AltitudeFt));
    }

    /// <summary>Layer 0 as the bottom of a grid column: at the surface datum, turned true in the true frame.</summary>
    private static ColumnLevel SurfaceLevel(WindLayer layer, double surfaceFt, WindSamplePoint at, ColumnFrame frame)
    {
        var level = ColumnLevel.Of(layer);
        double direction = frame == ColumnFrame.True ? MagneticDeclination.NormalizeDeg(level.DirectionDeg + at.DeclinationDeg) : level.DirectionDeg;
        return level with { AltitudeFt = surfaceFt, DirectionDeg = direction };
    }

    /// <summary>
    /// A grid level in a column: turned magnetic in the magnetic frame; inside the variability taper band it carries layer 0's
    /// amplitudes re-capped against its own mean speed (<see cref="WindVariation.CapAmplitudes"/>; none without a layer 0),
    /// above it none. It is never VRB.
    /// </summary>
    private static ColumnLevel GridLevel(GriddedWindSample sample, ColumnLevel? surface, double surfaceFt, WindSamplePoint at, ColumnFrame frame)
    {
        double direction =
            frame == ColumnFrame.Magnetic ? MagneticDeclination.NormalizeDeg(sample.DirectionTrueDeg - at.DeclinationDeg) : sample.DirectionTrueDeg;
        bool inTaperBand = (sample.AltitudeFt - surfaceFt) < WindVariation.ZeroAmplitudeTopAglFt;
        (double gustExcess, double halfSpread) =
            (inTaperBand && (surface is { } s)) ? WindVariation.CapAmplitudes(sample.SpeedKts, s.GustExcessKts, s.HalfSpreadDeg) : (0.0, 0.0);
        return new ColumnLevel(sample.AltitudeFt, direction, sample.SpeedKts, gustExcess, halfSpread, false);
    }

    /// <summary>
    /// The two layers bracketing <paramref name="altitudeFt"/>, which lies strictly inside the layer range, and the
    /// fraction of the way from the lower to the upper.
    /// </summary>
    private static (WindLayer Low, WindLayer High, double T) Bracket(List<WindLayer> layers, double altitudeFt)
    {
        int upper = 1;
        while (upper < layers.Count && layers[upper].Altitude < altitudeFt)
        {
            upper++;
        }

        WindLayer low = layers[upper - 1];
        WindLayer high = layers[upper];
        return (low, high, (altitudeFt - low.Altitude) / (high.Altitude - low.Altitude));
    }

    /// <summary>
    /// The mean wind a fraction <paramref name="t"/> of the way from the low wind to the high: the wind FROM directions
    /// decompose into unit N/E components before the lerp, which handles the 0/360 wraparound (350° and 010° → 000°); the
    /// speed lerps linearly.
    /// </summary>
    private static WindAtAltitude InterpolateMean(double lowDirectionDeg, double lowSpeedKts, double highDirectionDeg, double highSpeedKts, double t)
    {
        double lowRad = lowDirectionDeg * DegToRad;
        double highRad = highDirectionDeg * DegToRad;

        double interpN = Math.Cos(lowRad) + t * (Math.Cos(highRad) - Math.Cos(lowRad));
        double interpE = Math.Sin(lowRad) + t * (Math.Sin(highRad) - Math.Sin(lowRad));
        double interpSpeed = lowSpeedKts + t * (highSpeedKts - lowSpeedKts);

        double direction = Math.Atan2(interpE, interpN) * RadToDeg;
        if (direction < 0)
        {
            direction += 360.0;
        }

        return new WindAtAltitude(direction, interpSpeed);
    }

    /// <summary>
    /// Returns the wind effect vector in knots: the direction the wind blows TOWARD
    /// (reversed from the "wind FROM" convention) as (northKts, eastKts) components.
    /// Example: wind FROM 270 at 10 kts → (northKts: 0, eastKts: +10).
    /// Time/phase semantics match <see cref="GetWindAt"/>.
    /// </summary>
    public static (double NorthKts, double EastKts) GetWindComponents(
        WeatherProfile? profile,
        double altitudeFt,
        double simTimeSeconds,
        double phaseSeconds
    ) => ToComponents(GetWindAt(profile, altitudeFt, simTimeSeconds, phaseSeconds));

    /// <summary>
    /// <see cref="GetWindComponents(WeatherProfile?, double, double, double)"/> at a position, from
    /// <see cref="GetWindAt(WeatherProfile?, WindSamplePoint, double, double, double)"/>.
    /// </summary>
    public static (double NorthKts, double EastKts) GetWindComponents(
        WeatherProfile? profile,
        WindSamplePoint at,
        double altitudeFt,
        double simTimeSeconds,
        double phaseSeconds
    ) => ToComponents(GetWindAt(profile, at, altitudeFt, simTimeSeconds, phaseSeconds));

    /// <summary>The (northKts, eastKts) vector a wind blowing FROM <see cref="WindAtAltitude.DirectionDeg"/> pushes toward.</summary>
    private static (double NorthKts, double EastKts) ToComponents(WindAtAltitude wind)
    {
        if (wind.SpeedKts <= 0)
        {
            return (0, 0);
        }

        // Wind FROM direction + 180° gives the direction wind blows toward.
        double toRad = ((wind.DirectionDeg + 180.0) % 360.0) * DegToRad;
        double northKts = wind.SpeedKts * Math.Cos(toRad);
        double eastKts = wind.SpeedKts * Math.Sin(toRad);
        return (northKts, eastKts);
    }

    /// <summary>
    /// Converts indicated airspeed (IAS/CAS) to true airspeed (TAS) using ISA compressible
    /// flow equations. CAS → Mach → TAS via isentropic impact pressure relations.
    /// </summary>
    public static double IasToTas(double ias, double altitudeFt)
    {
        (double tempK, double _) = GetAtmosphere(altitudeFt);
        double mach = IasToMach(ias, altitudeFt);
        double aLocal = Math.Sqrt(Gamma * RGas * tempK);
        return mach * aLocal * MsToKt;
    }

    /// <summary>
    /// Converts true airspeed (TAS) to indicated airspeed (IAS/CAS) using the inverse
    /// of the ISA compressible flow equations.
    /// </summary>
    public static double TasToIas(double tas, double altitudeFt)
    {
        (double tempK, double _) = GetAtmosphere(altitudeFt);
        double aLocal = Math.Sqrt(Gamma * RGas * tempK);
        double mach = tas * KtToMs / aLocal;
        return MachToIas(mach, altitudeFt);
    }

    /// <summary>
    /// Converts a Mach number to indicated airspeed (IAS/CAS) at the given altitude
    /// using ISA compressible flow: Mach → impact pressure at altitude → CAS at sea level.
    /// </summary>
    public static double MachToIas(double mach, double altitudeFt)
    {
        (double _, double delta) = GetAtmosphere(altitudeFt);
        double qcOverP0 = delta * (Math.Pow(1.0 + 0.2 * mach * mach, GammaRatio) - 1.0);
        double vcMs = A0 * Math.Sqrt(5.0 * (Math.Pow(qcOverP0 + 1.0, InvGammaRatio) - 1.0));
        return vcMs * MsToKt;
    }

    /// <summary>
    /// Converts indicated airspeed (IAS/CAS) to Mach number at the given altitude
    /// using ISA compressible flow: CAS → impact pressure at sea level → Mach at altitude.
    /// 280 KIAS at FL350 ≈ M0.82.
    /// </summary>
    public static double IasToMach(double ias, double altitudeFt)
    {
        (double _, double delta) = GetAtmosphere(altitudeFt);
        double vcRatio = ias * KtToMs / A0;
        double qcOverP0 = Math.Pow(1.0 + 0.2 * vcRatio * vcRatio, GammaRatio) - 1.0;
        double qcOverP = qcOverP0 / delta;
        return Math.Sqrt(5.0 * (Math.Pow(qcOverP + 1.0, InvGammaRatio) - 1.0));
    }

    /// <summary>
    /// ISA speed of sound at the given altitude in knots.
    /// </summary>
    public static double SpeedOfSoundKts(double altitudeFt)
    {
        (double tempK, double _) = GetAtmosphere(altitudeFt);
        return Math.Sqrt(Gamma * RGas * tempK) * MsToKt;
    }

    /// <summary>
    /// Returns ISA temperature (K) and pressure ratio δ = P/P₀ at the given altitude.
    /// Below tropopause (36,089 ft): standard lapse rate. Above: isothermal stratosphere.
    /// </summary>
    internal static (double TempK, double Delta) GetAtmosphere(double altitudeFt)
    {
        double h = altitudeFt * FtToM;
        if (h <= TropopauseM)
        {
            double t = T0 - LapseRate * h;
            double delta = Math.Pow(t / T0, GOverLR);
            return (t, delta);
        }

        double stratDelta = DeltaTropopause * Math.Exp(-G * (h - TropopauseM) / (RGas * TropopauseK));
        return (TropopauseK, stratDelta);
    }

    /// <summary>
    /// ISA pressure altitude in feet for a static pressure: the inverse of the pressure ratio <see cref="GetAtmosphere"/>
    /// gives, with the standard lapse rate up to the tropopause (36,089 ft) and an isothermal stratosphere above.
    /// 500 hPa ≈ 18,289 ft.
    /// </summary>
    /// <param name="pressureHpa">Static pressure in hectopascals.</param>
    /// <returns>The altitude in feet at which the ISA pressure equals <paramref name="pressureHpa"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pressureHpa"/> is zero, negative or NaN.</exception>
    public static double PressureToIsaAltitudeFt(double pressureHpa)
    {
        if (!(pressureHpa > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pressureHpa), pressureHpa, "Pressure must be a positive number of hectopascals");
        }

        double delta = pressureHpa / SeaLevelPressureHpa;
        if (delta >= DeltaTropopause)
        {
            double tempK = T0 * Math.Pow(delta, 1.0 / GOverLR);
            return (T0 - tempK) / LapseRate / FtToM;
        }

        double h = TropopauseM - (Math.Log(delta / DeltaTropopause) * RGas * TropopauseK / G);
        return h / FtToM;
    }

    /// <summary>
    /// Component of a wind along <paramref name="trueHeadingDeg"/>, in knots.
    /// Positive = headwind, negative = tailwind. On the ground the gear prevents lateral
    /// translation, so only this component affects groundspeed; the crosswind component
    /// produces a side force, not drift.
    /// </summary>
    public static double HeadwindComponentKts(double windFromDeg, double windSpeedKts, double trueHeadingDeg) =>
        windSpeedKts * Math.Cos((windFromDeg - trueHeadingDeg) * DegToRad);

    /// <summary>
    /// Computes the Wind Correction Angle (WCA) in degrees.
    /// Positive WCA means correction is to the right of the desired track.
    /// Apply as: heading = desiredTrack + WCA.
    /// Returns 0 if TAS or wind speed is zero.
    /// </summary>
    public static double ComputeWindCorrectionAngle(double desiredTrackDeg, double tasKts, double windFromDeg, double windSpeedKts)
    {
        if (tasKts <= 0 || windSpeedKts <= 0)
        {
            return 0;
        }

        // Wind blows toward: FROM + 180°
        double windToRad = ((windFromDeg + 180.0) % 360.0) * DegToRad;
        double windN = windSpeedKts * Math.Cos(windToRad);
        double windE = windSpeedKts * Math.Sin(windToRad);

        double trackRad = desiredTrackDeg * DegToRad;

        // Cross-track wind component (positive = wind pushes left of track → crab right).
        // Derived from: sin(WCA) = (windN·sin(track) - windE·cos(track)) / TAS
        double sinWca = (windN * Math.Sin(trackRad) - windE * Math.Cos(trackRad)) / tasKts;
        sinWca = Math.Clamp(sinWca, -1.0, 1.0);

        return Math.Asin(sinWca) * RadToDeg;
    }
}
