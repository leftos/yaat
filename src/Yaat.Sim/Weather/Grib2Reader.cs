using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Weather;

/// <summary>
/// Decodes GRIB edition 2 files in the subset NOMADS' GRIB filter serves: grid definition template 3.0 (regular
/// latitude/longitude), product definition template 4.0 with the forecast time in hours, data representation template 5.0
/// (simple packing: value = (R + X·2^E) / 10^D) and a section 6 bitmap that is either present (indicator 0) or absent (255).
/// Anything else throws <see cref="Grib2FormatException"/> naming the section and the template or value found: a template
/// outside the subset, a truncated or overrunning section, a non-finite reference value or computed value, or a scale
/// factor outside ±127 (binary) or ±38 (decimal).
/// </summary>
public static class Grib2Reader
{
    private static readonly ILogger Log = SimLog.CreateLogger("Grib2Reader");

    private const int IndicatorLength = 16;
    private const int EndMarkerLength = 4;
    private const int SectionHeaderLength = 5;
    private const int ScanWestward = 0x80;
    private const int ScanNorthward = 0x40;
    private const int SupportedScanBits = ScanWestward | ScanNorthward;
    private const uint MissingUInt32 = 0xFFFFFFFF;
    private const long MicroDegPerTurn = 360_000_000;

    // Section 5's scales, bounded so Unpack's double arithmetic stays finite: |X·2^E| ≤ 2^159 and 10^±38 leave a double
    // far from its own range, so no Inf or NaN arises before the cast to float. 127 and 38 are float32's exponent limits;
    // a value whose cast still overflows float32 is refused per value in Unpack.
    private const int MaxBinaryScale = 127;
    private const int MaxDecimalScale = 38;

    /// <summary>
    /// The most points one message's grid may declare. A CONUS-sized 0.25° box (20–55° N, 130–60° W) is 141 × 281 =
    /// 39,621 points per level; the cap is four times that, rounded up. It is checked before any value array is
    /// allocated, because a file with 0 bits per value declares any number of points in a few bytes.
    /// </summary>
    public const int MaxGridPoints = 160_000;

    /// <summary>
    /// The most points one file may declare across all its fields, refused before the field that passes it is allocated:
    /// 3 variables (UGRD, VGRD, TMP) × the 20 isobaric levels 1000–150 hPa the client fetches (the NOMADS request in
    /// docs/research/2026-10-11-gridded-winds-aloft.md) × <see cref="MaxGridPoints"/> (four CONUS-sized 0.25° boxes).
    /// </summary>
    public const int MaxDecodedPoints = 3 * 20 * MaxGridPoints;

    /// <summary>
    /// Decodes every message in <paramref name="data"/>, which holds one or more GRIB2 messages back to back.
    /// </summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The messages in file order; a message whose sections 3–7 repeat yields one entry per section 7.</returns>
    /// <exception cref="Grib2FormatException">The data is empty, truncated, malformed or outside the supported subset.</exception>
    public static IReadOnlyList<Grib2Message> Read(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new Grib2FormatException("GRIB2 data is empty");
        }

        var messages = new List<Grib2Message>();
        var budget = new DecodeBudget();
        int offset = 0;
        while (offset < data.Length)
        {
            int length = ReadMessageLength(data, offset);
            ReadMessage(data.Slice(offset, length), offset, budget, messages);
            offset += length;
        }

        Log.LogDebug("Decoded {Count} GRIB2 messages from {Bytes} bytes", messages.Count, data.Length);
        return messages;
    }

    /// <summary>Validates section 0 at <paramref name="offset"/> and returns the message's total length.</summary>
    private static int ReadMessageLength(ReadOnlySpan<byte> data, int offset)
    {
        ReadOnlySpan<byte> rest = data[offset..];
        if (rest.Length < IndicatorLength)
        {
            throw new Grib2FormatException($"Section 0 at byte {offset}: {rest.Length} bytes left, a GRIB indicator needs {IndicatorLength}");
        }

        if (!rest[..4].SequenceEqual("GRIB"u8))
        {
            throw new Grib2FormatException($"Section 0 at byte {offset}: no 'GRIB' marker");
        }

        int edition = rest[7];
        if (edition != 2)
        {
            throw new Grib2FormatException($"Section 0 at byte {offset}: GRIB edition {edition} is not supported (only edition 2)");
        }

        ulong length = BinaryPrimitives.ReadUInt64BigEndian(rest[8..]);
        if ((length < IndicatorLength + EndMarkerLength) || (length > (ulong)rest.Length))
        {
            throw new Grib2FormatException(
                $"Section 0 at byte {offset}: message length {length} does not fit the {rest.Length} bytes left (truncated file?)"
            );
        }

        return (int)length;
    }

    private static void ReadMessage(ReadOnlySpan<byte> message, int fileOffset, DecodeBudget budget, List<Grib2Message> output)
    {
        if (!message[^EndMarkerLength..].SequenceEqual("7777"u8))
        {
            throw new Grib2FormatException($"Section 8 of the message at byte {fileOffset}: no '7777' end marker");
        }

        var state = new MessageState(message[6]) { Budget = budget };
        int end = message.Length - EndMarkerLength;
        int pos = IndicatorLength;
        while (pos < end)
        {
            ReadOnlySpan<byte> section = SliceSection(message, pos, end, fileOffset);
            ReadSection(section, state, output);
            pos += section.Length;
        }
    }

    private static ReadOnlySpan<byte> SliceSection(ReadOnlySpan<byte> message, int pos, int end, int fileOffset)
    {
        if (end - pos < SectionHeaderLength)
        {
            throw new Grib2FormatException($"Section header at byte {fileOffset + pos}: {end - pos} bytes left before the end marker");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(message[pos..]);
        int number = message[pos + 4];
        if ((length < SectionHeaderLength) || (length > (uint)(end - pos)))
        {
            throw new Grib2FormatException($"Section {number} at byte {fileOffset + pos}: length {length} does not fit the message");
        }

        return message.Slice(pos, (int)length);
    }

    private static void ReadSection(ReadOnlySpan<byte> section, MessageState state, List<Grib2Message> output)
    {
        switch (section[4])
        {
            case 1:
                state.ReferenceTimeUtc = ReadReferenceTime(section);
                break;
            case 2:
                break;
            case 3:
                state.Grid = ReadGrid(section);
                break;
            case 4:
                state.Product = ReadProduct(section);
                state.HasProductSinceData = true;
                break;
            case 5:
                state.Packing = ReadPacking(section);
                break;
            case 6:
                state.Bitmap = ReadBitmap(section);
                state.HasBitmapSection = true;
                break;
            case 7:
                output.Add(Decode(section, state));
                break;
            default:
                throw new Grib2FormatException($"Section {section[4]} is not a GRIB2 section number");
        }
    }

    private static DateTime ReadReferenceTime(ReadOnlySpan<byte> section)
    {
        RequireLength(section, 19, 1);
        int year = BinaryPrimitives.ReadUInt16BigEndian(section[12..]);
        int month = section[14];
        int day = section[15];
        int hour = section[16];
        int minute = section[17];
        int second = section[18];
        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new Grib2FormatException(
                $"Section 1: reference time {year:D4}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{second:D2} is not a date",
                ex
            );
        }
    }

    private static Grib2LatLonGrid ReadGrid(ReadOnlySpan<byte> section)
    {
        RequireLength(section, 14, 3);
        int template = BinaryPrimitives.ReadUInt16BigEndian(section[12..]);
        if (template != 0)
        {
            throw new Grib2FormatException($"Section 3: grid definition template 3.{template} is not supported (only 3.0, regular lat/lon)");
        }

        RequireLength(section, 72, 3);
        if (section[10] != 0)
        {
            throw new Grib2FormatException($"Section 3: a list of {section[10]}-octet point counts (quasi-regular grid) is not supported");
        }

        uint basicAngle = BinaryPrimitives.ReadUInt32BigEndian(section[38..]);
        if ((basicAngle != 0) && (basicAngle != MissingUInt32))
        {
            throw new Grib2FormatException($"Section 3: basic angle {basicAngle} is not supported (only micro-degrees)");
        }

        var grid = new Grib2LatLonGrid
        {
            Ni = ReadPositiveInt32(section, 30, "Ni"),
            Nj = ReadPositiveInt32(section, 34, "Nj"),
            La1MicroDeg = ReadSignMagnitudeInt32(section[46..]),
            Lo1MicroDeg = ReadSignMagnitudeInt32(section[50..]),
            La2MicroDeg = ReadSignMagnitudeInt32(section[55..]),
            Lo2MicroDeg = ReadSignMagnitudeInt32(section[59..]),
            DiMicroDeg = ReadPositiveInt32(section, 63, "Di"),
            DjMicroDeg = ReadPositiveInt32(section, 67, "Dj"),
            ScanningMode = section[71],
        };
        ValidateGridShape(grid, BinaryPrimitives.ReadUInt32BigEndian(section[6..]));
        return grid;
    }

    private static void ValidateGridShape(Grib2LatLonGrid grid, uint pointCount)
    {
        if ((grid.ScanningMode & ~SupportedScanBits) != 0)
        {
            throw new Grib2FormatException(
                $"Section 3: scanning mode 0x{grid.ScanningMode:X2} is not supported (only the i-direction 0x80 and j-direction 0x40 bits)"
            );
        }

        long points = (long)grid.Ni * grid.Nj;
        if (points != pointCount)
        {
            throw new Grib2FormatException($"Section 3: {pointCount} data points do not match Ni {grid.Ni} × Nj {grid.Nj}");
        }

        if (points > MaxGridPoints)
        {
            throw new Grib2FormatException($"Section 3: {pointCount} data points exceed the {MaxGridPoints} a wind grid may hold");
        }

        ValidateLatitudes(grid);
        ValidateLongitudes(grid);
    }

    /// <summary>La1 → La2 runs the way the 0x40 bit says, and spans (Nj − 1) rows of Dj.</summary>
    private static void ValidateLatitudes(Grib2LatLonGrid grid)
    {
        bool northward = (grid.ScanningMode & ScanNorthward) != 0;
        long step = (long)grid.La2MicroDeg - grid.La1MicroDeg;
        if (northward ? (step < 0) : (step > 0))
        {
            string rows = northward ? "north" : "south";
            throw new Grib2FormatException(
                $"Section 3: La1 {grid.La1MicroDeg} and La2 {grid.La2MicroDeg} disagree with scanning mode 0x{grid.ScanningMode:X2} (rows run {rows})"
            );
        }

        RequireSpan(Math.Abs(step), (long)(grid.Nj - 1) * grid.DjMicroDeg, grid.Nj, "(Nj − 1) × Dj", "|La2 − La1|");
    }

    /// <summary>Lo1 → Lo2, taken in the scan direction modulo 360°, spans (Ni − 1) columns of Di.</summary>
    private static void ValidateLongitudes(Grib2LatLonGrid grid)
    {
        bool westward = (grid.ScanningMode & ScanWestward) != 0;
        long raw = westward ? (long)grid.Lo1MicroDeg - grid.Lo2MicroDeg : (long)grid.Lo2MicroDeg - grid.Lo1MicroDeg;
        long span = ((raw % MicroDegPerTurn) + MicroDegPerTurn) % MicroDegPerTurn;
        RequireSpan(span, (long)(grid.Ni - 1) * grid.DiMicroDeg, grid.Ni, "(Ni − 1) × Di", "the longitude span in the scan direction");
    }

    /// <summary>Each stored increment is rounded to a micro-degree, so the span may drift by one micro-degree per step.</summary>
    private static void RequireSpan(long span, long expected, int points, string expectedName, string spanName)
    {
        if (Math.Abs(span - expected) > points - 1)
        {
            throw new Grib2FormatException($"Section 3: {expectedName} = {expected} µdeg does not match {spanName} = {span} µdeg");
        }
    }

    private static ProductHeader ReadProduct(ReadOnlySpan<byte> section)
    {
        RequireLength(section, 9, 4);
        int template = BinaryPrimitives.ReadUInt16BigEndian(section[7..]);
        if (template != 0)
        {
            throw new Grib2FormatException($"Section 4: product definition template 4.{template} is not supported (only 4.0)");
        }

        RequireLength(section, 34, 4);
        int timeUnit = section[17];
        if (timeUnit != 1)
        {
            throw new Grib2FormatException($"Section 4: forecast time unit {timeUnit} is not supported (only 1, hours)");
        }

        int scaleFactor = ReadSignMagnitudeInt8(section[23]);
        int scaledValue = ReadSignMagnitudeInt32(section[24..]);
        return new ProductHeader(
            section[9],
            section[10],
            section[22],
            scaledValue / Math.Pow(10, scaleFactor),
            ReadSignMagnitudeInt32(section[18..])
        );
    }

    private static PackingHeader ReadPacking(ReadOnlySpan<byte> section)
    {
        RequireLength(section, 11, 5);
        int template = BinaryPrimitives.ReadUInt16BigEndian(section[9..]);
        if (template != 0)
        {
            throw new Grib2FormatException($"Section 5: data representation template 5.{template} is not supported (only 5.0, simple packing)");
        }

        RequireLength(section, 20, 5);
        int bitsPerValue = section[19];
        if (bitsPerValue > 32)
        {
            throw new Grib2FormatException($"Section 5: {bitsPerValue} bits per value is not supported (at most 32)");
        }

        float referenceValue = BinaryPrimitives.ReadSingleBigEndian(section[11..]);
        if (!float.IsFinite(referenceValue))
        {
            throw new Grib2FormatException("Section 5: reference value is not finite");
        }

        int binaryScale = ReadSignMagnitudeInt16(section[15..]);
        if ((binaryScale < -MaxBinaryScale) || (binaryScale > MaxBinaryScale))
        {
            throw new Grib2FormatException($"Section 5: binary scale factor {binaryScale} is outside ±{MaxBinaryScale}");
        }

        int decimalScale = ReadSignMagnitudeInt16(section[17..]);
        if ((decimalScale < -MaxDecimalScale) || (decimalScale > MaxDecimalScale))
        {
            throw new Grib2FormatException($"Section 5: decimal scale factor {decimalScale} is outside ±{MaxDecimalScale}");
        }

        return new PackingHeader(BinaryPrimitives.ReadUInt32BigEndian(section[5..]), referenceValue, binaryScale, decimalScale, bitsPerValue);
    }

    private static byte[]? ReadBitmap(ReadOnlySpan<byte> section)
    {
        RequireLength(section, 6, 6);
        int indicator = section[5];
        return indicator switch
        {
            0 => section[6..].ToArray(),
            255 => null,
            _ => throw new Grib2FormatException($"Section 6: bitmap indicator {indicator} is not supported (only 0, present, or 255, none)"),
        };
    }

    private static Grib2Message Decode(ReadOnlySpan<byte> section, MessageState state)
    {
        if ((state.ReferenceTimeUtc is not { } referenceTime) || (state.Grid is not { } grid) || (state.Product is not { } product))
        {
            throw new Grib2FormatException("Section 7 arrived before sections 1, 3 and 4");
        }

        if ((state.Packing is not { } packing) || !state.HasBitmapSection)
        {
            throw new Grib2FormatException("Section 7 arrived before sections 5 and 6");
        }

        if (!state.HasProductSinceData)
        {
            throw new Grib2FormatException("Section 7 repeated without a new section 4 (a message repeats sections 2–7, 3–7 or 4–7)");
        }

        state.HasProductSinceData = false;
        state.Budget.Spend((long)grid.Ni * grid.Nj);
        float[] stored = Unpack(section[SectionHeaderLength..], grid.Ni * grid.Nj, packing, state.Bitmap);
        return new Grib2Message
        {
            Discipline = state.Discipline,
            ParameterCategory = product.Category,
            ParameterNumber = product.Number,
            FirstSurfaceType = product.SurfaceType,
            FirstSurfaceValue = product.SurfaceValue,
            ReferenceTimeUtc = referenceTime,
            ForecastHours = product.ForecastHours,
            Grid = grid,
            Values = ToSouthWestOrigin(stored, grid),
        };
    }

    /// <summary>Unpacks the simple-packed values in storage order, NaN where the bitmap marks a point missing.</summary>
    private static float[] Unpack(ReadOnlySpan<byte> packed, int pointCount, PackingHeader packing, byte[]? bitmap)
    {
        int present = bitmap is null ? pointCount : CountPresent(bitmap, pointCount);
        if (present != packing.DataPointCount)
        {
            throw new Grib2FormatException($"Section 5: {packing.DataPointCount} packed values, but the grid and bitmap give {present}");
        }

        long neededBytes = (((long)present * packing.BitsPerValue) + 7) / 8;
        if (packed.Length < neededBytes)
        {
            throw new Grib2FormatException($"Section 7: {packed.Length} bytes of data, {present} values need {neededBytes}");
        }

        double binaryScale = Math.Pow(2, packing.BinaryScale);
        double decimalScale = Math.Pow(10, packing.DecimalScale);
        float[] values = new float[pointCount];
        long bitPos = 0;
        for (int k = 0; k < pointCount; k++)
        {
            if ((bitmap is not null) && !IsBitSet(bitmap, k))
            {
                values[k] = float.NaN;
                continue;
            }

            uint packedValue = ReadBits(packed, bitPos, packing.BitsPerValue);
            bitPos += packing.BitsPerValue;
            float value = (float)((packing.ReferenceValue + (packedValue * binaryScale)) / decimalScale);
            if (!float.IsFinite(value))
            {
                throw new Grib2FormatException($"Section 7: value {k} is not finite");
            }

            values[k] = value;
        }

        return values;
    }

    private static int CountPresent(byte[] bitmap, int pointCount)
    {
        if (bitmap.Length < (pointCount + 7) / 8)
        {
            throw new Grib2FormatException($"Section 6: {bitmap.Length} bitmap bytes cover fewer than {pointCount} points");
        }

        int present = 0;
        for (int k = 0; k < pointCount; k++)
        {
            if (IsBitSet(bitmap, k))
            {
                present++;
            }
        }

        return present;
    }

    private static bool IsBitSet(ReadOnlySpan<byte> bytes, long bit) => ((bytes[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1) != 0;

    private static uint ReadBits(ReadOnlySpan<byte> bytes, long bitPos, int count)
    {
        uint value = 0;
        for (int b = 0; b < count; b++)
        {
            value = (value << 1) | (IsBitSet(bytes, bitPos + b) ? 1u : 0u);
        }

        return value;
    }

    /// <summary>Reorders storage-order values to rows south to north, columns west to east.</summary>
    private static float[] ToSouthWestOrigin(float[] stored, Grib2LatLonGrid grid)
    {
        bool westward = (grid.ScanningMode & ScanWestward) != 0;
        bool northward = (grid.ScanningMode & ScanNorthward) != 0;
        if (!westward && northward)
        {
            return stored;
        }

        float[] values = new float[stored.Length];
        for (int row = 0; row < grid.Nj; row++)
        {
            int j = northward ? row : grid.Nj - 1 - row;
            for (int col = 0; col < grid.Ni; col++)
            {
                int i = westward ? grid.Ni - 1 - col : col;
                values[(j * grid.Ni) + i] = stored[(row * grid.Ni) + col];
            }
        }

        return values;
    }

    private static void RequireLength(ReadOnlySpan<byte> section, int octets, int number)
    {
        if (section.Length < octets)
        {
            throw new Grib2FormatException($"Section {number}: {section.Length} octets, the fields read need {octets}");
        }
    }

    private static int ReadPositiveInt32(ReadOnlySpan<byte> section, int index, string name)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(section[index..]);
        if ((value == 0) || (value > int.MaxValue))
        {
            throw new Grib2FormatException($"Section 3: {name} {value} is missing or out of range");
        }

        return (int)value;
    }

    /// <summary>GRIB2 signed integers are sign-magnitude: the top bit is the sign, the rest the magnitude.</summary>
    private static int ReadSignMagnitudeInt32(ReadOnlySpan<byte> bytes)
    {
        uint raw = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        int magnitude = (int)(raw & 0x7FFFFFFF);
        return (raw & 0x80000000) != 0 ? -magnitude : magnitude;
    }

    private static int ReadSignMagnitudeInt16(ReadOnlySpan<byte> bytes)
    {
        ushort raw = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        int magnitude = raw & 0x7FFF;
        return (raw & 0x8000) != 0 ? -magnitude : magnitude;
    }

    private static int ReadSignMagnitudeInt8(byte raw)
    {
        int magnitude = raw & 0x7F;
        return (raw & 0x80) != 0 ? -magnitude : magnitude;
    }

    private readonly record struct ProductHeader(int Category, int Number, int SurfaceType, double SurfaceValue, int ForecastHours);

    private readonly record struct PackingHeader(uint DataPointCount, float ReferenceValue, int BinaryScale, int DecimalScale, int BitsPerValue);

    /// <summary>The sections read so far in one message; sections 3–7 may repeat, each section 7 emitting a field.</summary>
    private sealed class MessageState(int discipline)
    {
        public int Discipline { get; } = discipline;
        public DateTime? ReferenceTimeUtc { get; set; }
        public Grib2LatLonGrid? Grid { get; set; }
        public ProductHeader? Product { get; set; }
        public PackingHeader? Packing { get; set; }
        public byte[]? Bitmap { get; set; }
        public bool HasBitmapSection { get; set; }

        /// <summary>A section 4 has arrived since the last section 7: GRIB2 repeats sections 2–7, 3–7 or 4–7, never a bare 7.</summary>
        public bool HasProductSinceData { get; set; }

        /// <summary>The points every field of the file declared so far, shared by all its messages.</summary>
        public required DecodeBudget Budget { get; init; }
    }

    /// <summary>The running count of points a file's fields declare, held against <see cref="MaxDecodedPoints"/>.</summary>
    private sealed class DecodeBudget
    {
        private long _declaredPoints;

        /// <summary>Counts a field of <paramref name="points"/> points, refusing it when the file's total passes the budget.</summary>
        public void Spend(long points)
        {
            _declaredPoints += points;
            if (_declaredPoints > MaxDecodedPoints)
            {
                throw new Grib2FormatException(
                    $"Section 7: the file's fields declare {_declaredPoints} data points, more than the {MaxDecodedPoints} a wind grid file may hold"
                );
            }
        }
    }
}
