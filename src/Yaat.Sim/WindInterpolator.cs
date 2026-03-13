namespace Yaat.Sim;

/// <summary>Wind at a specific altitude after interpolation.</summary>
public readonly record struct WindAtAltitude(double DirectionDeg, double SpeedKts);

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
    private static readonly double A0 = Math.Sqrt(Gamma * RGas * T0); // Sea-level speed of sound (~340.294 m/s)
    private static readonly double DeltaTropopause = Math.Pow(TropopauseK / T0, GOverLR); // ~0.22336

    /// <summary>
    /// Returns interpolated wind at the given altitude.
    /// Null profile or empty layers returns zero wind.
    /// Altitudes outside the layer range are clamped to the nearest layer.
    /// Interpolation uses N/E vector components to handle the 0/360 boundary correctly.
    /// </summary>
    public static WindAtAltitude GetWindAt(WeatherProfile? profile, double altitudeFt)
    {
        if (profile is null || profile.WindLayers.Count == 0)
        {
            return new WindAtAltitude(0, 0);
        }

        var layers = profile.WindLayers;

        if (altitudeFt <= layers[0].Altitude)
        {
            return new WindAtAltitude(layers[0].Direction, layers[0].Speed);
        }

        if (altitudeFt >= layers[^1].Altitude)
        {
            return new WindAtAltitude(layers[^1].Direction, layers[^1].Speed);
        }

        int upper = 1;
        while (upper < layers.Count && layers[upper].Altitude < altitudeFt)
        {
            upper++;
        }

        var low = layers[upper - 1];
        var high = layers[upper];
        double t = (altitudeFt - low.Altitude) / (high.Altitude - low.Altitude);

        // Decompose wind FROM direction into unit N/E components, then lerp.
        // This correctly handles the 0/360 wraparound (e.g., 350° and 010° → 000°).
        double lowRad = low.Direction * DegToRad;
        double highRad = high.Direction * DegToRad;

        double interpN = Math.Cos(lowRad) + t * (Math.Cos(highRad) - Math.Cos(lowRad));
        double interpE = Math.Sin(lowRad) + t * (Math.Sin(highRad) - Math.Sin(lowRad));
        double interpSpeed = low.Speed + t * (high.Speed - low.Speed);

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
    /// </summary>
    public static (double NorthKts, double EastKts) GetWindComponents(WeatherProfile? profile, double altitudeFt)
    {
        var wind = GetWindAt(profile, altitudeFt);
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
        var (tempK, _) = GetAtmosphere(altitudeFt);
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
        var (tempK, _) = GetAtmosphere(altitudeFt);
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
        var (_, delta) = GetAtmosphere(altitudeFt);
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
        var (_, delta) = GetAtmosphere(altitudeFt);
        double vcRatio = ias * KtToMs / A0;
        double qcOverP0 = Math.Pow(1.0 + 0.2 * vcRatio * vcRatio, GammaRatio) - 1.0;
        double qcOverP = qcOverP0 / delta;
        return Math.Sqrt(5.0 * (Math.Pow(qcOverP + 1.0, InvGammaRatio) - 1.0));
    }

    /// <summary>
    /// ISA speed of sound at the given altitude in knots.
    /// </summary>
    internal static double SpeedOfSoundKts(double altitudeFt)
    {
        var (tempK, _) = GetAtmosphere(altitudeFt);
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
