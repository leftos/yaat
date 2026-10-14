using System.Buffers.Binary;
using Xunit;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests;

/// <summary>
/// Decodes a real NOMADS GFS 0.25° subset: cycle 2026-10-14 12Z, analysis hour f000, UGRD/VGRD/TMP at the 20 levels
/// 1000–150 hPa over ZOA (lat 34–42, lon 234–243). Fetched 2026-10-14 17:47Z from
/// https://nomads.ncep.noaa.gov/cgi-bin/filter_gfs_0p25.pl?dir=%2Fgfs.20261014%2F12%2Fatmos&amp;file=gfs.t12z.pgrb2.0p25.f000&amp;var_UGRD=on&amp;var_VGRD=on&amp;var_TMP=on&amp;lev_1000_mb=on&amp;lev_975_mb=on&amp;lev_950_mb=on&amp;lev_925_mb=on&amp;lev_900_mb=on&amp;lev_850_mb=on&amp;lev_800_mb=on&amp;lev_750_mb=on&amp;lev_700_mb=on&amp;lev_650_mb=on&amp;lev_600_mb=on&amp;lev_550_mb=on&amp;lev_500_mb=on&amp;lev_450_mb=on&amp;lev_400_mb=on&amp;lev_350_mb=on&amp;lev_300_mb=on&amp;lev_250_mb=on&amp;lev_200_mb=on&amp;lev_150_mb=on&amp;subregion=&amp;toplat=42&amp;leftlon=234&amp;rightlon=243&amp;bottomlat=34
/// (HTTP 200, 94,551 bytes). Expected values were decoded independently with ecCodes (the <c>eccodes</c> Python wheel):
/// grid points (j, i) = (0, 0) south-west corner, (16, 18) interior, (32, 35) near the north-east edge.
/// </summary>
public class Grib2ReaderTests
{
    internal const string SamplePath = "TestData/gfs-zoa-sample.grib2";

    // Decoded values are float32; this is the float representation limit at ~300 K, far below the 0.01 packing step.
    private const double Tolerance = 1e-4;

    private static readonly (int J, int I)[] Points = [(0, 0), (16, 18), (32, 35)];

    internal static byte[] LoadSample() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, SamplePath));

    [Fact]
    public void Read_Sample_DecodesSixtyMessages()
    {
        IReadOnlyList<Grib2Message> messages = Grib2Reader.Read(LoadSample());

        Assert.Equal(60, messages.Count);
    }

    [Fact]
    public void Read_Sample_FirstMessageHeaderMatchesEcCodes()
    {
        Grib2Message first = Grib2Reader.Read(LoadSample())[0];

        Assert.Equal(0, first.Discipline);
        Assert.Equal(0, first.ParameterCategory);
        Assert.Equal(0, first.ParameterNumber);
        Assert.Equal(100, first.FirstSurfaceType);
        Assert.Equal(15000.0, first.FirstSurfaceValue, 6);
        Assert.Equal(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), first.ReferenceTimeUtc);
        Assert.Equal(DateTimeKind.Utc, first.ReferenceTimeUtc.Kind);
        Assert.Equal(0, first.ForecastHours);
    }

    [Fact]
    public void Read_Sample_GridGeometryMatchesEcCodes()
    {
        Grib2LatLonGrid grid = Grib2Reader.Read(LoadSample())[0].Grid;

        Assert.Equal(37, grid.Ni);
        Assert.Equal(33, grid.Nj);
        Assert.Equal(34_000_000, grid.La1MicroDeg);
        Assert.Equal(234_000_000, grid.Lo1MicroDeg);
        Assert.Equal(42_000_000, grid.La2MicroDeg);
        Assert.Equal(243_000_000, grid.Lo2MicroDeg);
        Assert.Equal(250_000, grid.DiMicroDeg);
        Assert.Equal(250_000, grid.DjMicroDeg);
        Assert.Equal(64, grid.ScanningMode);
    }

    [Fact]
    public void Read_Sample_EveryMessageSharesGridAndTime()
    {
        IReadOnlyList<Grib2Message> messages = Grib2Reader.Read(LoadSample());

        Assert.All(messages, m => Assert.Equal(messages[0].Grid, m.Grid));
        Assert.All(messages, m => Assert.Equal(37 * 33, m.Values.Length));
    }

    [Theory]
    [InlineData(1000, 287.94734375, 287.99734375, 289.70734375)]
    [InlineData(500, 267.531015625, 264.221015625, 259.471015625)]
    [InlineData(150, 210.70955078125002, 214.47955078125, 218.10955078125)]
    public void Read_Sample_TemperatureMatchesEcCodes(int levelHpa, double p0, double p1, double p2) => AssertValues(0, 0, levelHpa, [p0, p1, p2]);

    [Theory]
    [InlineData(1000, -2.42605224609375, 0.59394775390625, 2.77394775390625)]
    [InlineData(500, 6.2741027832031255, 6.574102783203125, 2.374102783203125)]
    [InlineData(150, 17.67057556152344, 16.290575561523436, 11.550575561523438)]
    public void Read_Sample_UWindMatchesEcCodes(int levelHpa, double p0, double p1, double p2) => AssertValues(2, 2, levelHpa, [p0, p1, p2]);

    [Theory]
    [InlineData(1000, -6.65585205078125, -0.58585205078125, 0.25414794921875)]
    [InlineData(500, -8.663365173339844, -14.463365173339845, -17.563365173339843)]
    [InlineData(150, -14.14416015625, -21.04416015625, -14.76416015625)]
    public void Read_Sample_VWindMatchesEcCodes(int levelHpa, double p0, double p1, double p2) => AssertValues(2, 3, levelHpa, [p0, p1, p2]);

    [Fact]
    public void Read_DataRepresentationTemplate52_ThrowsNamingTemplate()
    {
        byte[] data = LoadSample();
        int section5 = FindSection(data, 5);
        data[section5 + 9] = 0;
        data[section5 + 10] = 2;

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains("5.2", ex.Message);
    }

    [Fact]
    public void Read_TruncatedFile_Throws()
    {
        byte[] data = LoadSample();

        Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data.AsSpan(0, data.Length - 100)));
    }

    [Fact]
    public void Read_Edition1_ThrowsNamingEdition()
    {
        byte[] data = LoadSample();
        data[7] = 1;

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains("edition 1", ex.Message);
    }

    [Fact]
    public void Read_InvalidReferenceMonth_KeepsDateErrorAsInnerException()
    {
        byte[] data = LoadSample();
        data[FindSection(data, 1) + 14] = 13;

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains("Section 1", ex.Message);
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    /// <summary>
    /// The sample is stored with mode 0x40 (rows south to north, columns west to east). Patching the mode reinterprets the
    /// same bytes: without 0x40 the first stored row is the northern one, with 0x80 the first stored column is the eastern
    /// one. La1/La2 and Lo1/Lo2 are swapped to match, as the reader requires.
    /// </summary>
    [Theory]
    [InlineData(0x00, true, false)]
    [InlineData(0x80, true, true)]
    [InlineData(0xC0, false, true)]
    public void Read_PatchedScanningMode_ReordersToSouthWestOrigin(int mode, bool flipRows, bool flipColumns)
    {
        byte[] original = FirstMessage();
        byte[] patched = [.. original];
        int section3 = FindSection(patched, 3);
        patched[section3 + 71] = (byte)mode;
        if (flipRows)
        {
            SwapInt32(patched, section3 + 46, section3 + 55); // La1 <-> La2
        }

        if (flipColumns)
        {
            SwapInt32(patched, section3 + 50, section3 + 59); // Lo1 <-> Lo2
        }

        AssertReordered(Grib2Reader.Read(original)[0], Grib2Reader.Read(patched)[0], flipRows, flipColumns);
    }

    [Fact]
    public void Read_NegativeBinaryScale_ReadsSignMagnitude()
    {
        byte[] original = FirstMessage();
        byte[] patched = [.. original];
        int section5 = FindSection(patched, 5);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(section5 + 15))); // E = 0 in the sample
        patched[section5 + 15] = 0x80; // E = −1 in sign-magnitude; a two's-complement read gives −32767.
        patched[section5 + 16] = 0x01;
        float r = BinaryPrimitives.ReadSingleBigEndian(original.AsSpan(section5 + 11));
        int d = BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(section5 + 17));
        Assert.True(d < 0x8000, "the sample's D is positive");
        double decimalScale = Math.Pow(10, d);

        float[] before = Grib2Reader.Read(original)[0].Values;
        float[] after = Grib2Reader.Read(patched)[0].Values;

        // value = (R + X·2^E) / 10^D: recover X from the E = 0 decode, then apply E = −1.
        for (int k = 0; k < before.Length; k++)
        {
            double x = Math.Round((before[k] * decimalScale) - r);
            Assert.Equal((r + (x / 2.0)) / decimalScale, after[k], 1e-4);
        }
    }

    [Fact]
    public void Read_BitmapWithFirstPointMissing_DecodesNaNAndShiftsValues()
    {
        byte[] original = FirstMessage();
        Grib2Message source = Grib2Reader.Read(original)[0];
        int n = source.Values.Length;
        int section5 = FindSection(original, 5);
        int section6 = FindSection(original, 6);
        int section7 = FindSection(original, 7);
        Assert.Equal(255, original[section6 + 5]); // the sample has no bitmap
        Assert.Equal(0x40, source.Grid.ScanningMode); // rows already south to north: storage order is the Values order

        byte[] bitmap = new byte[(n + 7) / 8];
        for (int k = 1; k < n; k++)
        {
            bitmap[k >> 3] |= (byte)(0x80 >> (k & 7));
        }

        byte[] newSection6 = [0, 0, 0, 0, 6, 0, .. bitmap];
        BinaryPrimitives.WriteUInt32BigEndian(newSection6, (uint)newSection6.Length);
        byte[] rebuilt = [.. original[..section6], .. newSection6, .. original[section7..]];
        BinaryPrimitives.WriteUInt64BigEndian(rebuilt.AsSpan(8), (ulong)rebuilt.Length);
        BinaryPrimitives.WriteUInt32BigEndian(rebuilt.AsSpan(section5 + 5), (uint)(n - 1));

        Grib2Message decoded = Grib2Reader.Read(rebuilt)[0];

        Assert.True(float.IsNaN(decoded.Values[0]));
        Assert.Equal(source.Values[..(n - 1)], decoded.Values[1..]);
    }

    [Theory]
    [InlineData(67, "Dj")]
    [InlineData(63, "Di")]
    public void Read_IncrementDisagreeingWithSpan_ThrowsNamingIncrement(int offset, string name)
    {
        byte[] data = LoadSample();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(FindSection(data, 3) + offset), 200_000);

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Read_LatitudeOrderDisagreeingWithScanningMode_Throws()
    {
        byte[] data = LoadSample();
        int section3 = FindSection(data, 3);
        SwapInt32(data, section3 + 46, section3 + 55); // La1 north of La2 while 0x40 says rows run north

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains("scanning mode 0x40", ex.Message);
    }

    [Fact]
    public void Read_PointCountAboveArrayLimit_Throws()
    {
        byte[] data = LoadSample();
        int section3 = FindSection(data, 3);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(section3 + 6), 2_500_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(section3 + 30), 50_000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(section3 + 34), 50_000);

        Grib2FormatException ex = Assert.Throws<Grib2FormatException>(() => Grib2Reader.Read(data));

        Assert.Contains("2500000000 data points", ex.Message);
    }

    /// <summary>The sample's first message (TMP at 150 hPa) on its own.</summary>
    private static byte[] FirstMessage()
    {
        byte[] data = LoadSample();
        return data[..(int)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(8))];
    }

    private static void SwapInt32(byte[] data, int a, int b)
    {
        uint first = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(a));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(a), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(b)));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(b), first);
    }

    private static void AssertReordered(Grib2Message before, Grib2Message after, bool flipRows, bool flipColumns)
    {
        int ni = before.Grid.Ni;
        int nj = before.Grid.Nj;
        Assert.Equal(before.Grid.SouthMicroDeg, after.Grid.SouthMicroDeg);
        Assert.Equal(before.Grid.WestMicroDeg, after.Grid.WestMicroDeg);
        for (int j = 0; j < nj; j++)
        {
            for (int i = 0; i < ni; i++)
            {
                int jj = flipRows ? nj - 1 - j : j;
                int ii = flipColumns ? ni - 1 - i : i;
                Assert.Equal(before.Values[(j * ni) + i], after.Values[(jj * ni) + ii]);
            }
        }
    }

    private static void AssertValues(int category, int number, int levelHpa, double[] expected)
    {
        IReadOnlyList<Grib2Message> messages = Grib2Reader.Read(LoadSample());
        Grib2Message message = Assert.Single(
            messages,
            m => (m.ParameterCategory == category) && (m.ParameterNumber == number) && (Math.Abs(m.FirstSurfaceValue - (levelHpa * 100.0)) < 1e-6)
        );

        for (int k = 0; k < Points.Length; k++)
        {
            (int j, int i) = Points[k];
            Assert.Equal(expected[k], message.Values[(j * message.Grid.Ni) + i], Tolerance);
        }
    }

    /// <summary>Offset of the first message's section <paramref name="number"/>, walking section lengths from octet 17.</summary>
    private static int FindSection(byte[] data, int number)
    {
        int offset = 16;
        while (data[offset + 4] != number)
        {
            offset += (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        return offset;
    }
}
