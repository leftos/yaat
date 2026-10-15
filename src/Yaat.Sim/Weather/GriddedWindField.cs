using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Weather;

/// <summary>One isobaric level of a sampled wind column.</summary>
/// <param name="PressureHpa">The level's pressure in hectopascals.</param>
/// <param name="AltitudeFt">The ISA pressure altitude of the level, feet MSL.</param>
/// <param name="DirectionTrueDeg">Direction the wind blows FROM, degrees TRUE, in [0, 360); 0 for a calm.</param>
/// <param name="SpeedKts">Wind speed in knots.</param>
/// <param name="TemperatureC">Air temperature in degrees Celsius.</param>
public readonly record struct GriddedWindSample(double PressureHpa, double AltitudeFt, double DirectionTrueDeg, double SpeedKts, double TemperatureC);

/// <summary>The kept levels either side of an altitude in a sampled column (<see cref="GriddedWindField.SampleBracket"/>).</summary>
/// <param name="Below">The highest kept level strictly below the altitude, or null when there is none.</param>
/// <param name="Above">The lowest kept level at or above the altitude, or null when there is none.</param>
public readonly record struct GriddedWindBracket(GriddedWindSample? Below, GriddedWindSample? Above);

/// <summary>
/// Wind and temperature on isobaric levels over a regular latitude/longitude grid, built from decoded GRIB2 UGRD
/// (0, 2, 2), VGRD (0, 2, 3) and TMP (0, 0, 0) messages of one model run and forecast hour. Immutable after construction.
/// <see cref="SampleColumn"/> interpolates U, V and T bilinearly in latitude/longitude, then converts each level to a
/// FROM direction (TRUE), knots and Celsius, at the level's ISA pressure altitude.
/// </summary>
public sealed class GriddedWindField
{
    private static readonly ILogger Log = SimLog.CreateLogger("GriddedWindField");

    private const int MeteorologicalDiscipline = 0;
    private const int IsobaricSurface = 100;
    private const double MicroDegToDeg = 1e-6;
    private const double MsToKt = 1.0 / 0.514444;
    private const double KelvinOffset = 273.15;
    private const double RadToDeg = 180.0 / Math.PI;
    private const double PaPerHpa = 100.0;

    private readonly Grib2LatLonGrid _grid;
    private readonly Level[] _levels;

    private GriddedWindField(Grib2Message reference, Level[] levels)
    {
        _grid = reference.Grid;
        _levels = levels;
        ReferenceTimeUtc = reference.ReferenceTimeUtc;
        ForecastHours = reference.ForecastHours;
        LevelsHpa = Array.AsReadOnly(levels.Select(l => l.PressureHpa).ToArray());
    }

    /// <summary>The model run (cycle) the field comes from, UTC.</summary>
    public DateTime ReferenceTimeUtc { get; }

    /// <summary>Forecast hour after <see cref="ReferenceTimeUtc"/> (0 = analysis).</summary>
    public int ForecastHours { get; }

    /// <summary>Latitude of the southern grid row, degrees.</summary>
    public double SouthLatDeg => _grid.SouthMicroDeg * MicroDegToDeg;

    /// <summary>Latitude of the northern grid row, degrees.</summary>
    public double NorthLatDeg => _grid.NorthMicroDeg * MicroDegToDeg;

    /// <summary>Longitude of the western grid column, degrees in the grid's 0–360 convention.</summary>
    public double WestLonDeg => _grid.WestMicroDeg * MicroDegToDeg;

    /// <summary>Longitude of the eastern grid column, degrees in the grid's 0–360 convention.</summary>
    public double EastLonDeg => _grid.EastMicroDeg * MicroDegToDeg;

    /// <summary>The isobaric levels in hectopascals, lowest altitude (highest pressure) first.</summary>
    public IReadOnlyList<double> LevelsHpa { get; }

    /// <summary>
    /// Builds the field from a reader's messages. Messages that are not UGRD, VGRD or TMP on an isobaric surface are ignored.
    /// </summary>
    /// <param name="messages">Decoded GRIB2 messages, in any order.</param>
    /// <returns>The field, one level per isobaric surface the three parameters share.</returns>
    /// <exception cref="InvalidOperationException">
    /// No UGRD/VGRD/TMP message is present; the grid has fewer than two rows or columns; the messages disagree on the grid,
    /// reference time or forecast hour; a message's level rounds below 1 Pa, or its value count is not Ni × Nj; a parameter
    /// appears twice at a level; or one is missing at a level the others have. The message names the parameter and level.
    /// </exception>
    public static GriddedWindField FromMessages(IReadOnlyList<Grib2Message> messages)
    {
        List<(Parameter Parameter, Grib2Message Message)> relevant = SelectRelevant(messages);
        Grib2Message reference = relevant[0].Message;
        if ((reference.Grid.Ni < 2) || (reference.Grid.Nj < 2))
        {
            throw new InvalidOperationException($"A wind grid needs at least 2 × 2 points; this one is {reference.Grid.Ni} × {reference.Grid.Nj}");
        }

        foreach ((Parameter parameter, Grib2Message message) in relevant)
        {
            RequireSameRun(reference, relevant[0].Parameter, message, parameter);
            RequireUsable(message, parameter);
        }

        SortedDictionary<long, Grib2Message?[]> byLevel = GroupByLevel(relevant);
        Level[] levels = [.. byLevel.Reverse().Select(entry => BuildLevel(entry.Key, entry.Value))];
        return new GriddedWindField(reference, levels);
    }

    /// <summary>
    /// The wind column at a position, bilinearly interpolated between the four surrounding grid points.
    /// </summary>
    /// <param name="latDeg">Latitude in degrees.</param>
    /// <param name="lonDeg">Longitude in degrees, −180..180 or 0..360.</param>
    /// <returns>
    /// Null when the position lies outside the grid; otherwise one sample per level, lowest altitude first. A level where
    /// any of the four surrounding points is missing (NaN) in U, V or T is left out of the column, so the column is empty
    /// when every level has such a point.
    /// </returns>
    public IReadOnlyList<GriddedWindSample>? SampleColumn(double latDeg, double lonDeg)
    {
        if (Locate(latDeg, lonDeg) is not { } cell)
        {
            return null;
        }

        var column = new List<GriddedWindSample>(_levels.Length);
        foreach (Level level in _levels)
        {
            if (SampleLevel(level, cell) is { } sample)
            {
                column.Add(sample);
            }
        }

        return column;
    }

    /// <summary>
    /// The two levels of the column at a position that bracket an altitude, without building the column: only the levels it
    /// returns (and any level with a missing corner it skips past) are interpolated, and nothing is allocated. Levels below
    /// <paramref name="floorFt"/> are left out, as is a level with a missing (NaN) corner, so the bracket is the one the
    /// column <see cref="SampleColumn"/> gives would have once those levels are removed from it.
    /// </summary>
    /// <param name="latDeg">Latitude in degrees.</param>
    /// <param name="lonDeg">Longitude in degrees, −180..180 or 0..360.</param>
    /// <param name="altitudeFt">The altitude to bracket, feet MSL.</param>
    /// <param name="floorFt">The lowest level altitude kept, feet MSL.</param>
    /// <returns>
    /// Null when the position lies outside the grid. Otherwise <see cref="GriddedWindBracket.Below"/> is the highest kept level
    /// strictly below <paramref name="altitudeFt"/> and <see cref="GriddedWindBracket.Above"/> the lowest kept level at or above
    /// it, each null when there is none; both are null when no level is kept.
    /// </returns>
    public GriddedWindBracket? SampleBracket(double latDeg, double lonDeg, double altitudeFt, double floorFt)
    {
        if (Locate(latDeg, lonDeg) is not { } cell)
        {
            return null;
        }

        int firstAtOrAbove = 0;
        while ((firstAtOrAbove < _levels.Length) && (_levels[firstAtOrAbove].AltitudeFt < altitudeFt))
        {
            firstAtOrAbove++;
        }

        GriddedWindSample? above = null;
        for (int i = firstAtOrAbove; (i < _levels.Length) && (above is null); i++)
        {
            above = _levels[i].AltitudeFt >= floorFt ? SampleLevel(_levels[i], cell) : null;
        }

        GriddedWindSample? below = null;
        for (int i = firstAtOrAbove - 1; (i >= 0) && (below is null) && (_levels[i].AltitudeFt >= floorFt); i--)
        {
            below = SampleLevel(_levels[i], cell);
        }

        return new GriddedWindBracket(below, above);
    }

    private Cell? Locate(double latDeg, double lonDeg)
    {
        double y = (latDeg - SouthLatDeg) / (_grid.DjMicroDeg * MicroDegToDeg);
        double lonOffset = (((lonDeg - WestLonDeg) % 360.0) + 360.0) % 360.0;
        double x = lonOffset / (_grid.DiMicroDeg * MicroDegToDeg);
        if (!((y >= 0) && (y <= _grid.Nj - 1) && (x <= _grid.Ni - 1)))
        {
            return null;
        }

        int i0 = Math.Min((int)Math.Floor(x), _grid.Ni - 2);
        int j0 = Math.Min((int)Math.Floor(y), _grid.Nj - 2);
        return new Cell(i0, j0, x - i0, y - j0);
    }

    private GriddedWindSample? SampleLevel(Level level, Cell cell)
    {
        double u = Bilinear(level.U, cell);
        double v = Bilinear(level.V, cell);
        double t = Bilinear(level.T, cell);
        if (double.IsNaN(u) || double.IsNaN(v) || double.IsNaN(t))
        {
            return null;
        }

        double speedMs = Math.Sqrt((u * u) + (v * v));
        return new GriddedWindSample(level.PressureHpa, level.AltitudeFt, FromDirectionDeg(u, v, speedMs), speedMs * MsToKt, t - KelvinOffset);
    }

    /// <summary>Bilinear interpolation; a NaN corner propagates to the result, whatever its weight.</summary>
    private double Bilinear(float[] values, Cell cell)
    {
        int ni = _grid.Ni;
        int south = (cell.J0 * ni) + cell.I0;
        int north = south + ni;
        double southValue = ((1 - cell.Fx) * values[south]) + (cell.Fx * values[south + 1]);
        double northValue = ((1 - cell.Fx) * values[north]) + (cell.Fx * values[north + 1]);
        return ((1 - cell.Fy) * southValue) + (cell.Fy * northValue);
    }

    /// <summary>The FROM direction of a wind blowing toward east <paramref name="u"/> and north <paramref name="v"/>.</summary>
    private static double FromDirectionDeg(double u, double v, double speedMs)
    {
        if (speedMs == 0)
        {
            return 0;
        }

        // Adding 360 before the remainder keeps the result in [0, 360) and turns atan2's −0 (and a −ε that rounds to 360) into +0.
        return ((Math.Atan2(-u, -v) * RadToDeg) + 360.0) % 360.0;
    }

    private static void RequireUsable(Grib2Message message, Parameter parameter)
    {
        // Checked on the rounded whole-pascal value the level grouping keys on, so a level that rounds to 0 Pa cannot slip through.
        long pa = (long)Math.Round(message.FirstSurfaceValue);
        if (pa <= 0)
        {
            throw new InvalidOperationException($"{Describe(parameter, message)} is not a pressure level above 0 Pa");
        }

        long expected = (long)message.Grid.Ni * message.Grid.Nj;
        if (message.Values.Length != expected)
        {
            throw new InvalidOperationException(
                $"{Describe(parameter, message)} has {message.Values.Length} values; its {message.Grid.Ni} × {message.Grid.Nj} grid needs {expected}"
            );
        }
    }

    private static string Describe(Parameter parameter, Grib2Message message) =>
        $"{parameter.ToString().ToUpperInvariant()} at {FormatLevel(message.FirstSurfaceValue)}";

    private static List<(Parameter Parameter, Grib2Message Message)> SelectRelevant(IReadOnlyList<Grib2Message> messages)
    {
        var relevant = new List<(Parameter, Grib2Message)>();
        foreach (Grib2Message message in messages)
        {
            if (Classify(message) is { } parameter)
            {
                relevant.Add((parameter, message));
            }
        }

        if (relevant.Count == 0)
        {
            throw new InvalidOperationException($"None of the {messages.Count} GRIB2 messages is UGRD, VGRD or TMP on an isobaric surface");
        }

        Log.LogDebug("Gridded wind field: {Used} of {Total} GRIB2 messages used", relevant.Count, messages.Count);
        return relevant;
    }

    private static Parameter? Classify(Grib2Message message)
    {
        if ((message.Discipline != MeteorologicalDiscipline) || (message.FirstSurfaceType != IsobaricSurface))
        {
            return null;
        }

        return (message.ParameterCategory, message.ParameterNumber) switch
        {
            (2, 2) => Parameter.Ugrd,
            (2, 3) => Parameter.Vgrd,
            (0, 0) => Parameter.Tmp,
            _ => null,
        };
    }

    private static void RequireSameRun(Grib2Message reference, Parameter referenceParameter, Grib2Message message, Parameter parameter)
    {
        string what = Describe(parameter, message);
        string against = Describe(referenceParameter, reference);
        if (message.Grid != reference.Grid)
        {
            throw new InvalidOperationException($"{what} is on a different grid from {against}");
        }

        if (message.ReferenceTimeUtc != reference.ReferenceTimeUtc)
        {
            throw new InvalidOperationException(
                $"{what} has reference time {message.ReferenceTimeUtc:u}, {against} has {reference.ReferenceTimeUtc:u}"
            );
        }

        if (message.ForecastHours != reference.ForecastHours)
        {
            throw new InvalidOperationException($"{what} is forecast hour {message.ForecastHours}, {against} is {reference.ForecastHours}");
        }
    }

    /// <summary>Groups the messages by pressure (Pa), one slot per parameter.</summary>
    private static SortedDictionary<long, Grib2Message?[]> GroupByLevel(List<(Parameter Parameter, Grib2Message Message)> relevant)
    {
        var byLevel = new SortedDictionary<long, Grib2Message?[]>();
        foreach ((Parameter parameter, Grib2Message message) in relevant)
        {
            long pa = (long)Math.Round(message.FirstSurfaceValue);
            if (!byLevel.TryGetValue(pa, out Grib2Message?[]? slots))
            {
                slots = new Grib2Message?[3];
                byLevel[pa] = slots;
            }

            if (slots[(int)parameter] is not null)
            {
                throw new InvalidOperationException($"Two {parameter.ToString().ToUpperInvariant()} messages at {FormatLevel(pa)}");
            }

            slots[(int)parameter] = message;
        }

        return byLevel;
    }

    private static Level BuildLevel(long pa, Grib2Message?[] slots)
    {
        Grib2Message u = slots[(int)Parameter.Ugrd] ?? throw MissingAt(Parameter.Ugrd, pa);
        Grib2Message v = slots[(int)Parameter.Vgrd] ?? throw MissingAt(Parameter.Vgrd, pa);
        Grib2Message t = slots[(int)Parameter.Tmp] ?? throw MissingAt(Parameter.Tmp, pa);
        double hpa = pa / PaPerHpa;
        return new Level(hpa, WindInterpolator.PressureToIsaAltitudeFt(hpa), [.. u.Values], [.. v.Values], [.. t.Values]);
    }

    private static InvalidOperationException MissingAt(Parameter parameter, long pa) =>
        new($"{parameter.ToString().ToUpperInvariant()} is missing at {FormatLevel(pa)}, where the other wind-field parameters are present");

    private static string FormatLevel(double pa) => string.Create(CultureInfo.InvariantCulture, $"{pa / PaPerHpa:0.##} hPa");

    private enum Parameter
    {
        Ugrd = 0,
        Vgrd = 1,
        Tmp = 2,
    }

    /// <summary>The grid cell holding a position: its south-west corner indices and the fractions across it.</summary>
    private readonly record struct Cell(int I0, int J0, double Fx, double Fy);

    /// <summary>One isobaric level's grids, values indexed as <see cref="Grib2Message.Values"/>; T in kelvin, U and V in m/s.</summary>
    private sealed record Level(double PressureHpa, double AltitudeFt, float[] U, float[] V, float[] T);
}
