namespace Yaat.Sim.Weather;

/// <summary>
/// One decoded GRIB2 field: its parameter, its first fixed surface, its times, its grid and its values.
/// </summary>
public sealed class Grib2Message
{
    /// <summary>Section 0 discipline (0 = meteorological products).</summary>
    public required int Discipline { get; init; }

    /// <summary>Section 4 parameter category (0 = temperature, 2 = momentum within discipline 0).</summary>
    public required int ParameterCategory { get; init; }

    /// <summary>Section 4 parameter number within the category (UGRD = 2, VGRD = 3 in category 2; TMP = 0 in category 0).</summary>
    public required int ParameterNumber { get; init; }

    /// <summary>Section 4 type of the first fixed surface (100 = isobaric surface).</summary>
    public required int FirstSurfaceType { get; init; }

    /// <summary>
    /// Value of the first fixed surface with its scale factor applied, in the surface type's SI unit (pascals for an isobaric
    /// surface: 50000 is 500 hPa).
    /// </summary>
    public required double FirstSurfaceValue { get; init; }

    /// <summary>Section 1 reference time (the model cycle), UTC.</summary>
    public required DateTime ReferenceTimeUtc { get; init; }

    /// <summary>Section 4 forecast time after the reference time, in hours (0 = analysis).</summary>
    public required int ForecastHours { get; init; }

    /// <summary>The regular latitude/longitude grid the values lie on.</summary>
    public required Grib2LatLonGrid Grid { get; init; }

    /// <summary>
    /// Decoded values, <see cref="Grib2LatLonGrid.Ni"/> × <see cref="Grib2LatLonGrid.Nj"/> of them, row-major from the
    /// south-west corner whatever the file's scanning mode: index = j·Ni + i, with j counting rows south to north from
    /// <see cref="Grib2LatLonGrid.SouthMicroDeg"/> and i counting columns west to east from
    /// <see cref="Grib2LatLonGrid.WestMicroDeg"/>. A point the section 6 bitmap marks missing is <see cref="float.NaN"/>.
    /// </summary>
    public required float[] Values { get; init; }
}

/// <summary>
/// A GRIB2 grid definition template 3.0 (regular latitude/longitude) as stored in section 3. Angles are micro-degrees;
/// longitudes are in the file's 0–360 convention.
/// </summary>
public sealed record Grib2LatLonGrid
{
    /// <summary>Points along a parallel (columns).</summary>
    public required int Ni { get; init; }

    /// <summary>Points along a meridian (rows).</summary>
    public required int Nj { get; init; }

    /// <summary>Latitude of the first grid point as stored.</summary>
    public required int La1MicroDeg { get; init; }

    /// <summary>Longitude of the first grid point as stored.</summary>
    public required int Lo1MicroDeg { get; init; }

    /// <summary>Latitude of the last grid point as stored.</summary>
    public required int La2MicroDeg { get; init; }

    /// <summary>Longitude of the last grid point as stored.</summary>
    public required int Lo2MicroDeg { get; init; }

    /// <summary>Increment between columns.</summary>
    public required int DiMicroDeg { get; init; }

    /// <summary>Increment between rows.</summary>
    public required int DjMicroDeg { get; init; }

    /// <summary>
    /// Scanning-mode flags as stored: 0x80 set means points run west from the first, 0x40 set means rows run north from the
    /// first. No other bit is ever set on a grid the reader returns.
    /// </summary>
    public required int ScanningMode { get; init; }

    /// <summary>Latitude of the southern row.</summary>
    public int SouthMicroDeg => Math.Min(La1MicroDeg, La2MicroDeg);

    /// <summary>Latitude of the northern row.</summary>
    public int NorthMicroDeg => Math.Max(La1MicroDeg, La2MicroDeg);

    /// <summary>Longitude (0–360) of the western column.</summary>
    public int WestMicroDeg => ScansWestward ? Lo2MicroDeg : Lo1MicroDeg;

    /// <summary>Longitude (0–360) of the eastern column.</summary>
    public int EastMicroDeg => ScansWestward ? Lo1MicroDeg : Lo2MicroDeg;

    private bool ScansWestward => (ScanningMode & 0x80) != 0;
}
