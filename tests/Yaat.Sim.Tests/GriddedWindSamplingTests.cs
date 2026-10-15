using Xunit;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests;

/// <summary>
/// Sampling a profile's wind by position: where the profile's GFS grid (<see cref="WeatherProfile.WindGrid"/>, the real NOMADS
/// sample over ZOA) covers the position, the column is the METAR surface layer at the surface datum and, above it, the grid
/// levels that clear the surface's pressure altitude by <see cref="WindInterpolator.GridLevelGroundClearanceFt"/>, turned
/// magnetic; grid levels inside the variability taper band carry the surface layer's gust and spread amplitudes. Everywhere
/// else, and for a profile with no grid, the position overloads return exactly what the overloads without a position return.
/// </summary>
public class GriddedWindSamplingTests
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    private static readonly Lazy<GriddedWindField> SampleField = new(() =>
        GriddedWindField.FromMessages(Grib2Reader.Read(Grib2ReaderTests.LoadSample()))
    );

    private static readonly DateTime ModelDate = new(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);

    private static readonly LatLon Sfo = new(37.62, -122.38);
    private static readonly LatLon Reno = new(39.5, -119.8);
    private static readonly LatLon Krno = new(39.4991, -119.7681);
    private static readonly LatLon Jfk = new(40.64, -73.78);

    private static readonly double[] Altitudes = [0, 200, 1_500, 3_000, 5_000, 10_000, 20_000, 30_000, 45_000];

    public GriddedWindSamplingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The real GFS sample over ZOA (lat 34–42, lon −126 to −117), decoded once and shared.</summary>
    public static GriddedWindField Field => SampleField.Value;

    /// <summary>
    /// A gusty, variable METAR surface layer at 0 ft and FD layers aloft, no METAR text (so the surface datum is the surface
    /// layer's 0 ft), with <paramref name="grid"/> as the profile's grid.
    /// </summary>
    public static WeatherProfile LayersWithGrid(GriddedWindField? grid) => Profile([], 0, grid);

    /// <summary>
    /// <paramref name="metars"/> (which set the surface datum and altimeter), a gusty surface layer (280/15G25, ±30°) authored at
    /// <paramref name="surfaceLayerAltitudeFt"/>, FD layers at 6,000 ft and up, and <paramref name="grid"/>.
    /// </summary>
    public static WeatherProfile Profile(List<string> metars, double surfaceLayerAltitudeFt, GriddedWindField? grid) =>
        new()
        {
            Metars = metars,
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = surfaceLayerAltitudeFt,
                    Direction = 280,
                    Speed = 15,
                    Gusts = 25,
                    DirectionVariabilityDeg = 30,
                },
                new WindLayer
                {
                    Altitude = 6_000,
                    Direction = 260,
                    Speed = 30,
                },
                new WindLayer
                {
                    Altitude = 12_000,
                    Direction = 270,
                    Speed = 45,
                },
                new WindLayer
                {
                    Altitude = 24_000,
                    Direction = 280,
                    Speed = 70,
                },
                new WindLayer
                {
                    Altitude = 39_000,
                    Direction = 290,
                    Speed = 90,
                },
            ],
            WindGrid = grid,
        };

    private static string SfoMetar(string altimeter) => $"KSFO 141756Z 28015G25KT 10SM FEW015 18/12 {altimeter}";

    private static WindSamplePoint PointAt(LatLon position) => new(position, MagneticDeclination.GetDeclination(position, ModelDate));

    private static double Magnetic(GriddedWindSample sample, WindSamplePoint at) =>
        MagneticDeclination.NormalizeDeg(sample.DirectionTrueDeg - at.DeclinationDeg);

    /// <summary>One level of the reference column.</summary>
    private sealed record RefLevel(
        double AltitudeFt,
        double DirectionDeg,
        double SpeedKts,
        double GustExcessKts,
        double HalfSpreadDeg,
        bool Variable
    );

    /// <summary>
    /// The reference: the position overload's wind built the long way, from the whole column <see cref="GriddedWindField.SampleColumn"/>
    /// gives — layer 0 at the surface datum, the grid levels at or above the surface's pressure altitude + 500 ft (and above
    /// the datum) turned magnetic, layer 0's amplitudes on the levels below the taper top — clamped at its ends and lerped
    /// between the bracketing levels, then perturbed.
    /// </summary>
    private static WindAtAltitude ReferenceWind(WeatherProfile profile, WindSamplePoint at, double altitudeFt, double simTimeSeconds, double phase)
    {
        IReadOnlyList<GriddedWindSample>? samples = profile.WindGrid?.SampleColumn(at.Position.Lat, at.Position.Lon);
        double surfaceFt = profile.SurfaceElevationFt();
        double floorFt = surfaceFt + ((29.92 - profile.SurfaceAltimeterInHg()) * 1000.0) + 500.0;

        List<RefLevel> column = [];
        RefLevel? surface = null;
        if (profile.WindLayers.Count > 0)
        {
            WindLayer layer = profile.WindLayers[0];
            (double gust, double spread) = WindVariation.ResolveAmplitudes(
                layer.Speed,
                layer.Gusts,
                layer.DirectionVariabilityDeg,
                layer.Variable ?? false
            );
            surface = new RefLevel(surfaceFt, layer.Direction, layer.Speed, gust, spread, layer.Variable ?? false);
            column.Add(surface);
        }

        int surfaceCount = column.Count;
        foreach (GriddedWindSample sample in samples ?? [])
        {
            if ((sample.AltitudeFt >= floorFt) && (sample.AltitudeFt > surfaceFt))
            {
                bool inBand = (sample.AltitudeFt - surfaceFt) < 3_000;
                (double gust, double spread) = inBand
                    ? Capped(sample.SpeedKts, surface?.GustExcessKts ?? 0, surface?.HalfSpreadDeg ?? 0)
                    : (0.0, 0.0);
                column.Add(new RefLevel(sample.AltitudeFt, Magnetic(sample, at), sample.SpeedKts, gust, spread, false));
            }
        }

        if (column.Count == surfaceCount)
        {
            return WindInterpolator.GetWindAt(profile, altitudeFt, simTimeSeconds, phase);
        }

        double agl = Math.Max(0, altitudeFt - surfaceFt);
        RefLevel clamp = altitudeFt <= column[0].AltitudeFt ? column[0] : column[^1];
        if ((altitudeFt <= column[0].AltitudeFt) || (altitudeFt >= column[^1].AltitudeFt))
        {
            return Perturb(clamp.DirectionDeg, clamp.SpeedKts, clamp.GustExcessKts, clamp.HalfSpreadDeg, clamp.Variable, agl, simTimeSeconds, phase);
        }

        int upper = column.FindIndex(1, l => l.AltitudeFt >= altitudeFt);
        RefLevel low = column[upper - 1];
        RefLevel high = column[upper];
        double t = (altitudeFt - low.AltitudeFt) / (high.AltitudeFt - low.AltitudeFt);
        WindAtAltitude mean = Lerp(low.DirectionDeg, low.SpeedKts, high.DirectionDeg, high.SpeedKts, t);
        return Perturb(
            mean.DirectionDeg,
            mean.SpeedKts,
            low.GustExcessKts + (t * (high.GustExcessKts - low.GustExcessKts)),
            low.HalfSpreadDeg + (t * (high.HalfSpreadDeg - low.HalfSpreadDeg)),
            t < 0.5 ? low.Variable : high.Variable,
            agl,
            simTimeSeconds,
            phase
        );
    }

    /// <summary>
    /// Borrowed amplitudes re-capped against a level's own mean speed, by <see cref="WindVariation.ResolveAmplitudes"/>'s rules
    /// written out (the constants WindCalibrationTests pins): none below a 0.5 kt mean, a gust excess at most 1.5 × the mean
    /// (gust factor 2.5), a half-spread within 0–90°.
    /// </summary>
    private static (double GustExcessKts, double HalfSpreadDeg) Capped(double meanSpeedKts, double gustExcessKts, double halfSpreadDeg) =>
        meanSpeedKts < 0.5 ? (0, 0) : (Math.Clamp(gustExcessKts, 0, 1.5 * meanSpeedKts), Math.Clamp(halfSpreadDeg, 0, 90));

    private static WindAtAltitude Perturb(
        double direction,
        double speed,
        double gust,
        double spread,
        bool variable,
        double agl,
        double simTimeSeconds,
        double phase
    ) => WindVariation.Compute(new WindPerturbationInputs(direction, speed, gust, spread, variable, agl), simTimeSeconds, phase);

    /// <summary>Two winds lerped as unit N/E vectors with a linear speed.</summary>
    private static WindAtAltitude Lerp(double lowDir, double lowSpeed, double highDir, double highSpeed, double t)
    {
        double lowRad = lowDir * DegToRad;
        double highRad = highDir * DegToRad;
        double north = Math.Cos(lowRad) + (t * (Math.Cos(highRad) - Math.Cos(lowRad)));
        double east = Math.Sin(lowRad) + (t * (Math.Sin(highRad) - Math.Sin(lowRad)));
        double direction = Math.Atan2(east, north) * RadToDeg;
        return new WindAtAltitude(direction < 0 ? direction + 360.0 : direction, lowSpeed + (t * (highSpeed - lowSpeed)));
    }

    private static void AssertWind(WindAtAltitude expected, WindAtAltitude actual, string what)
    {
        Assert.True(
            (Math.Abs(expected.DirectionDeg - actual.DirectionDeg) < 1e-9) && (Math.Abs(expected.SpeedKts - actual.SpeedKts) < 1e-9),
            $"{what}: expected {expected}, got {actual}"
        );
    }

    [Fact]
    public void InsideTheGrid_TwoPositionsAtOneAltitude_GetEachTheirOwnGridWind()
    {
        WeatherProfile profile = LayersWithGrid(Field);
        const double AltitudeFt = 10_000;

        WindSamplePoint sfo = PointAt(Sfo);
        WindSamplePoint reno = PointAt(Reno);
        WindAtAltitude atSfo = WindInterpolator.GetWindAt(profile, sfo, AltitudeFt, 37, 11);
        WindAtAltitude atReno = WindInterpolator.GetWindAt(profile, reno, AltitudeFt, 37, 11);

        foreach ((WindSamplePoint at, WindAtAltitude actual) in new[] { (sfo, atSfo), (reno, atReno) })
        {
            List<GriddedWindSample> column = [.. Field.SampleColumn(at.Position.Lat, at.Position.Lon)!];
            int upper = column.FindIndex(s => s.AltitudeFt >= AltitudeFt);
            GriddedWindSample low = column[upper - 1];
            GriddedWindSample high = column[upper];
            double t = (AltitudeFt - low.AltitudeFt) / (high.AltitudeFt - low.AltitudeFt);
            AssertWind(Lerp(Magnetic(low, at), low.SpeedKts, Magnetic(high, at), high.SpeedKts, t), actual, $"{at.Position}");
        }

        bool differ = (Math.Abs(atSfo.DirectionDeg - atReno.DirectionDeg) > 1) || (Math.Abs(atSfo.SpeedKts - atReno.SpeedKts) > 1);
        Assert.True(differ, $"SFO {atSfo} and Reno {atReno} read the same wind");
    }

    /// <summary>
    /// The zero-allocation bracket path is the whole-column semantics: over positions inside the grid (and outside it), profiles
    /// with and without METAR text, at high and low altimeters, and altitudes from the ground to above the top level, the
    /// position overload matches the column built the long way.
    /// </summary>
    [Fact]
    public void Sweep_PositionOverload_MatchesTheWholeColumnReference()
    {
        LatLon[] positions = [Sfo, Reno, Krno, new(35.0, -125.5), new(41.9, -117.1), new(34.0, -126.0), Jfk];
        WeatherProfile[] profiles =
        [
            LayersWithGrid(Field),
            Profile([SfoMetar("A2992")], 0, Field),
            Profile([SfoMetar("A3020")], 0, Field),
            Profile([SfoMetar("A2950")], 13, Field),
            Profile(["KRNO 141755Z 30015G25KT 10SM CLR 15/M05 A3005"], 0, Field),
            new WeatherProfile { WindGrid = Field },
        ];
        double[] altitudes =
        [
            0,
            13,
            100,
            313,
            600,
            700,
            1_000,
            1_500,
            2_500,
            3_100,
            4_415,
            4_600,
            5_000,
            7_000,
            10_000,
            18_000,
            30_000,
            45_000,
            50_000,
        ];

        foreach (WeatherProfile profile in profiles)
        {
            foreach (LatLon position in positions)
            {
                WindSamplePoint at = PointAt(position);
                foreach (double altitudeFt in altitudes)
                {
                    foreach (double second in new[] { 0.0, 37.0, 91.0 })
                    {
                        AssertWind(
                            ReferenceWind(profile, at, altitudeFt, second, 11),
                            WindInterpolator.GetWindAt(profile, at, altitudeFt, second, 11),
                            $"{position} {altitudeFt} ft t={second}"
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// 300 ft above a sea-level field (KSFO, altimeter 29.92) the wind is a blend of the METAR surface layer at the field and the
    /// first grid level kept: 975 hPa, since 1000 hPa (about 360 ft) lies within 500 ft of the field's pressure altitude.
    /// </summary>
    [Fact]
    public void ThreeHundredFeetAboveASeaLevelField_BlendsTheMetarLayerAndTheFirstKeptGridLevel()
    {
        WeatherProfile profile = Profile([SfoMetar("A2992")], 0, Field);
        WindSamplePoint sfo = PointAt(Sfo);
        double fieldFt = profile.SurfaceElevationFt();
        Assert.InRange(fieldFt, 0, 50);

        List<GriddedWindSample> column = [.. Field.SampleColumn(Sfo.Lat, Sfo.Lon)!];
        GriddedWindSample kept = Assert.Single(column, s => s.PressureHpa == 975);
        Assert.Contains(column, s => (s.PressureHpa == 1000) && (s.AltitudeFt < fieldFt + 500));

        double altitudeFt = fieldFt + 300;
        double t = 300 / (kept.AltitudeFt - fieldFt);
        WindAtAltitude mean = Lerp(280, 15, Magnetic(kept, sfo), kept.SpeedKts, t);
        (double gust, double spread) = BlendToLevel(SurfaceAmplitudes, kept, t);
        for (int second = 0; second < 120; second += 10)
        {
            AssertWind(
                Perturb(mean.DirectionDeg, mean.SpeedKts, gust, spread, false, 300, second, 11),
                WindInterpolator.GetWindAt(profile, sfo, altitudeFt, second, 11),
                $"t={second}"
            );
        }
    }

    /// <summary>
    /// The altimeter moves the floor: at KSFO with 30.20 the field's pressure altitude is about 270 ft below it, so 1000 hPa
    /// (about 360 ft) clears it by 500 ft and is kept; with 29.50 the floor rises about 420 ft and 1000 hPa is dropped, so
    /// 700 ft lies between the surface layer and 975 hPa.
    /// </summary>
    [Fact]
    public void LowAltimeter_DropsThe1000HpaLevel_HighAltimeter_KeepsIt()
    {
        WindSamplePoint sfo = PointAt(Sfo);
        List<GriddedWindSample> column = [.. Field.SampleColumn(Sfo.Lat, Sfo.Lon)!];
        GriddedWindSample hpa1000 = Assert.Single(column, s => s.PressureHpa == 1000);
        GriddedWindSample hpa975 = Assert.Single(column, s => s.PressureHpa == 975);
        const double AltitudeFt = 700;

        WeatherProfile high = Profile([SfoMetar("A3020")], 0, Field);
        double tHigh = (AltitudeFt - hpa1000.AltitudeFt) / (hpa975.AltitudeFt - hpa1000.AltitudeFt);
        WindAtAltitude meanHigh = Lerp(Magnetic(hpa1000, sfo), hpa1000.SpeedKts, Magnetic(hpa975, sfo), hpa975.SpeedKts, tHigh);

        WeatherProfile low = Profile([SfoMetar("A2950")], 0, Field);
        double fieldFt = low.SurfaceElevationFt();
        double tLow = (AltitudeFt - fieldFt) / (hpa975.AltitudeFt - fieldFt);
        WindAtAltitude meanLow = Lerp(280, 15, Magnetic(hpa975, sfo), hpa975.SpeedKts, tLow);

        (double gustHigh, double spreadHigh) = BlendToLevel(
            Capped(hpa1000.SpeedKts, SurfaceAmplitudes.GustExcessKts, SurfaceAmplitudes.HalfSpreadDeg),
            hpa975,
            tHigh
        );
        (double gustLow, double spreadLow) = BlendToLevel(SurfaceAmplitudes, hpa975, tLow);
        double agl = AltitudeFt - fieldFt;
        AssertWind(
            Perturb(meanHigh.DirectionDeg, meanHigh.SpeedKts, gustHigh, spreadHigh, false, agl, 0, 11),
            WindInterpolator.GetWindAt(high, sfo, AltitudeFt, 0, 11),
            "A3020"
        );
        AssertWind(
            Perturb(meanLow.DirectionDeg, meanLow.SpeedKts, gustLow, spreadLow, false, agl, 0, 11),
            WindInterpolator.GetWindAt(low, sfo, AltitudeFt, 0, 11),
            "A2950"
        );
    }

    /// <summary>
    /// At KRNO (4,415 ft) under a live-style profile, whose surface layer is authored at 0 MSL, the grid column puts the METAR
    /// layer at the field: at field elevation the position overload is exactly the METAR layer, never a grid level under the
    /// terrain.
    /// </summary>
    [Fact]
    public void AtAHighField_AtFieldElevation_IsTheMetarLayer()
    {
        WeatherProfile profile = Profile(["KRNO 141755Z 30015G25KT 10SM CLR 15/M05 A3005"], 0, Field);
        double fieldFt = profile.SurfaceElevationFt();
        Assert.InRange(fieldFt, 4_000, 4_600);
        var metarLayer = new WeatherProfile { WindLayers = [profile.WindLayers[0]] };

        for (int second = 0; second < 120; second += 10)
        {
            Assert.Equal(
                WindInterpolator.GetWindAt(metarLayer, 0, second, 11),
                WindInterpolator.GetWindAt(profile, PointAt(Krno), fieldFt, second, 11)
            );
        }
    }

    /// <summary>
    /// A gusty METAR keeps its gust and direction-spread amplitudes up through the grid levels of the taper band: 600 ft above
    /// the field under the grid, the perturbation's amplitudes run from the surface layer's toward the same amplitudes carried
    /// by the 975 hPa level (re-capped against that level's own mean speed), not toward none; only the taper fades them.
    /// </summary>
    [Fact]
    public void GustyMetar_600FtAboveTheField_UnderTheGrid_KeepsTheSurfaceGustAmplitude()
    {
        WeatherProfile profile = Profile([SfoMetar("A2992")], 0, Field);
        WindSamplePoint sfo = PointAt(Sfo);
        double fieldFt = profile.SurfaceElevationFt();
        double altitudeFt = fieldFt + 600;

        GriddedWindSample hpa975 = Assert.Single(Field.SampleColumn(Sfo.Lat, Sfo.Lon)!, s => s.PressureHpa == 975);
        Assert.True(altitudeFt < hpa975.AltitudeFt);
        double t = 600 / (hpa975.AltitudeFt - fieldFt);
        WindAtAltitude mean = Lerp(280, 15, Magnetic(hpa975, sfo), hpa975.SpeedKts, t);
        (double gust, double spread) = BlendToLevel(SurfaceAmplitudes, hpa975, t);
        Assert.True(gust > 0);

        var speeds = new HashSet<double>();
        for (int second = 0; second < 300; second += 7)
        {
            WindAtAltitude actual = WindInterpolator.GetWindAt(profile, sfo, altitudeFt, second, 11);
            AssertWind(Perturb(mean.DirectionDeg, mean.SpeedKts, gust, spread, false, 600, second, 11), actual, $"t={second}");
            speeds.Add(Math.Round(actual.SpeedKts, 1));
        }

        Assert.True(speeds.Count > 5, $"the wind 600 ft above the field holds steady: {speeds.Count} distinct speeds");
    }

    /// <summary>The surface layer's resolved amplitudes in <see cref="Profile"/> (280/15G25, ±30°).</summary>
    private static readonly (double GustExcessKts, double HalfSpreadDeg) SurfaceAmplitudes = WindVariation.ResolveAmplitudes(15, 25, 30, false);

    /// <summary>
    /// Amplitudes a fraction <paramref name="t"/> of the way from <paramref name="low"/> to the surface layer's amplitudes as
    /// <paramref name="level"/> carries them, re-capped against its mean speed.
    /// </summary>
    private static (double GustExcessKts, double HalfSpreadDeg) BlendToLevel(
        (double GustExcessKts, double HalfSpreadDeg) low,
        GriddedWindSample level,
        double t
    )
    {
        (double gust, double spread) = Capped(level.SpeedKts, SurfaceAmplitudes.GustExcessKts, SurfaceAmplitudes.HalfSpreadDeg);
        return (low.GustExcessKts + (t * (gust - low.GustExcessKts)), low.HalfSpreadDeg + (t * (spread - low.HalfSpreadDeg)));
    }

    /// <summary>
    /// A strong gusty METAR (20G45, ±30°) under the lightest 975 hPa level in the sample: at that level (about 1,000 ft above the
    /// field) the gust it borrows from the METAR is capped against its own light mean, so its gust factor stays within 2.5 and
    /// it carries a gust only while its mean is perturbable — the METAR's 25 kt excess is never laid on a light level whole.
    /// </summary>
    [Fact]
    public void GustyMetar_UnderALightGridLevel_ReCapsTheBorrowedGustAgainstTheLevelsMean()
    {
        (LatLon position, GriddedWindSample light) = LightestLevel(975);
        Assert.True(light.SpeedKts < 15, $"the lightest 975 hPa wind is {light.SpeedKts:F1} kt, too strong for the cap to bite");
        var profile = new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = 280,
                    Speed = 20,
                    Gusts = 45,
                    DirectionVariabilityDeg = 30,
                },
            ],
            WindGrid = Field,
        };
        WindSamplePoint at = PointAt(position);
        (double surfaceGust, double surfaceSpread) = WindVariation.ResolveAmplitudes(20, 45, 30, false);
        Assert.Equal(25, surfaceGust, 9);
        (double gust, double spread) = Capped(light.SpeedKts, surfaceGust, surfaceSpread);
        Assert.True(gust < surfaceGust);

        double peak = 0;
        for (int second = 0; second < 600; second += 3)
        {
            WindAtAltitude actual = WindInterpolator.GetWindAt(profile, at, light.AltitudeFt, second, 11);
            WindAtAltitude expected = Perturb(Magnetic(light, at), light.SpeedKts, gust, spread, false, light.AltitudeFt, second, 11);
            Assert.Equal(expected.SpeedKts, actual.SpeedKts, 6);
            peak = Math.Max(peak, actual.SpeedKts);
        }

        Assert.True(peak <= light.SpeedKts + (1.45 * 1.5 * light.SpeedKts), $"peak {peak:F1} kt on a {light.SpeedKts:F1} kt mean");
    }

    [Fact]
    public void CapAmplitudes_FollowsResolveAmplitudesRules()
    {
        Assert.Equal((0.0, 0.0), WindVariation.CapAmplitudes(0.4, 10, 20));
        Assert.Equal((6.0, 20.0), WindVariation.CapAmplitudes(4, 10, 20));
        Assert.Equal((10.0, 90.0), WindVariation.CapAmplitudes(20, 10, 120));
        Assert.Equal(WindVariation.ResolveAmplitudes(4, 30, 20, false), WindVariation.CapAmplitudes(4, 30, 20));
    }

    /// <summary>The position on a 0.25° lattice over the sample whose <paramref name="pressureHpa"/> level is lightest.</summary>
    private static (LatLon Position, GriddedWindSample Level) LightestLevel(double pressureHpa)
    {
        (LatLon Position, GriddedWindSample Level)? lightest = null;
        for (double lat = 34.25; lat <= 41.75; lat += 0.25)
        {
            for (double lon = -125.75; lon <= -117.25; lon += 0.25)
            {
                if (Field.SampleColumn(lat, lon)?.FirstOrDefault(s => s.PressureHpa == pressureHpa) is { SpeedKts: > 0 } level)
                {
                    lightest = (lightest is { } best) && (best.Level.SpeedKts <= level.SpeedKts) ? best : (new LatLon(lat, lon), level);
                }
            }
        }

        return lightest ?? throw new InvalidOperationException($"no {pressureHpa} hPa level in the sample");
    }

    [Fact]
    public void InsideTheGrid_AtTheSurfaceDatum_IsTheMetarSurfaceLayer()
    {
        WeatherProfile profile = LayersWithGrid(Field);
        WindSamplePoint sfo = PointAt(Sfo);

        for (int second = 0; second < 120; second += 10)
        {
            Assert.Equal(WindInterpolator.GetWindAt(profile, 0, second, 11), WindInterpolator.GetWindAt(profile, sfo, 0, second, 11));
            Assert.Equal(WindInterpolator.GetWindComponents(profile, 0, second, 11), WindInterpolator.GetWindComponents(profile, sfo, 0, second, 11));
        }
    }

    [Fact]
    public void OutsideTheGrid_IsExactlyTheLayersWithoutAPosition()
    {
        WeatherProfile profile = LayersWithGrid(Field);
        WindSamplePoint jfk = PointAt(Jfk);
        Assert.Null(Field.SampleColumn(Jfk.Lat, Jfk.Lon));
        Assert.Null(WindInterpolator.GetGriddedMeanWindTrueAt(profile, jfk, 10_000));

        AssertSameAsWithoutPosition(profile, jfk);
    }

    [Fact]
    public void NoGrid_IsExactlyTheLayersWithoutAPosition()
    {
        WeatherProfile profile = LayersWithGrid(null);
        Assert.Null(WindInterpolator.GetGriddedMeanWindTrueAt(profile, PointAt(Sfo), 10_000));

        AssertSameAsWithoutPosition(profile, PointAt(Sfo));
    }

    private static void AssertSameAsWithoutPosition(WeatherProfile profile, WindSamplePoint at)
    {
        foreach (double altitudeFt in Altitudes)
        {
            for (int second = 0; second < 60; second += 15)
            {
                Assert.Equal(
                    WindInterpolator.GetWindAt(profile, altitudeFt, second, 11),
                    WindInterpolator.GetWindAt(profile, at, altitudeFt, second, 11)
                );
                Assert.Equal(
                    WindInterpolator.GetWindComponents(profile, altitudeFt, second, 11),
                    WindInterpolator.GetWindComponents(profile, at, altitudeFt, second, 11)
                );
            }
        }
    }
}
