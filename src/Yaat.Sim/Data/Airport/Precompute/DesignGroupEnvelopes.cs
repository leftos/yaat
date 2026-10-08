using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>The ICAO designator of the FAA record that set each maximum of a design group's envelope.</summary>
public sealed record DesignGroupSources
{
    /// <summary>The record with the greatest length.</summary>
    public required string LengthFt { get; init; }

    /// <summary>The record with the greatest span, before the cap at the group's ceiling.</summary>
    public required string WingspanFt { get; init; }

    /// <summary>The record with the greatest wheelbase, or null when no record of the group has one.</summary>
    public required string? WheelbaseFt { get; init; }

    /// <summary>The record with the greatest main gear width, or null when no record of the group has one.</summary>
    public required string? MainGearWidthFt { get; init; }
}

/// <summary>
/// One design group's envelope: the greatest length, span, wheelbase and main gear width over the group's fixed-wing FAA
/// records, each taken separately, the span capped at the group's ceiling. A footprint is built from it alone.
/// </summary>
public sealed record DesignGroupEnvelope
{
    /// <summary>The design group, roman (<c>I</c> to <c>VI</c>).</summary>
    public required string Group { get; init; }

    /// <summary>The synthetic type code the footprint carries, <c>ADG-I</c> to <c>ADG-VI</c>.</summary>
    public required string EnvelopeCode { get; init; }

    /// <summary>The greatest fuselage length, feet.</summary>
    public required double LengthFt { get; init; }

    /// <summary>
    /// The greatest wingspan, feet, capped at the group's span ceiling (<see cref="AirplaneDesignGroups.MaxWingspanFt"/>).
    /// An envelope capped there lies on the next group's boundary under the exclusive bound
    /// (<see cref="AirplaneDesignGroups.SmallestCoveringSpan"/>), and nothing re-derives a group from a footprint span.
    /// </summary>
    public required double WingspanFt { get; init; }

    /// <summary>
    /// The greatest wheelbase, feet, over the group's records other than a non-jet taildragger's and a broken row's (see
    /// <see cref="DesignGroupEnvelopes"/>), or null when no other record of the group has one.
    /// </summary>
    public required double? WheelbaseFt { get; init; }

    /// <summary>
    /// The greatest main gear width, feet, or null when no record of the group has one. Kept for review: neither the
    /// footprint nor the tug planner reads a gear width.
    /// </summary>
    public required double? MainGearWidthFt { get; init; }

    /// <summary>The performance category of the record that set the wheelbase maximum, else of the one that set the length.</summary>
    public required AircraftCategory Category { get; init; }

    /// <summary>The record that set each maximum.</summary>
    public required DesignGroupSources Sources { get; init; }
}

/// <summary>
/// The envelope per Airplane Design Group the precompute cache plans push targets with, as shipped in
/// <c>Data/PrecomputeCache/design-group-envelopes.json</c>. <see cref="Build"/> makes them from FAA records: the fixed-wing
/// records in the group <see cref="AirplaneDesignGroups.OfRecord"/> gives, each dimension's maximum taken separately. The
/// wheelbase maximum leaves out a non-jet whose wheelbase is at least <see cref="TurnAboutFit.TaildraggerWheelbaseRatio"/>
/// of its length (a taildragger's wheelbase is measured to the tailwheel) and any record at or over
/// <see cref="TurnAboutFit.BrokenRowWheelbaseRatio"/> (a broken row). A group with no record giving a length
/// and a span is left out and gets no push targets.
/// </summary>
public sealed class DesignGroupEnvelopes
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private DesignGroupEnvelopes(IReadOnlyList<DesignGroupEnvelope> envelopes) => Envelopes = envelopes;

    /// <summary>The envelopes, in group order I to VI, one per group at most.</summary>
    public IReadOnlyList<DesignGroupEnvelope> Envelopes { get; }

    /// <summary>The path of the shipped file under the application's base directory.</summary>
    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "Data", "PrecomputeCache", "design-group-envelopes.json");

    /// <summary>Reads the envelopes from <paramref name="path"/>.</summary>
    /// <param name="path">The JSON file.</param>
    /// <returns>The envelopes.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not JSON, is not an array, is <c>null</c>, has a null element, misses a field, or has an unknown or
    /// repeated group.
    /// </exception>
    public static DesignGroupEnvelopes Load(string path)
    {
        List<DesignGroupEnvelope?>? read;
        try
        {
            read = JsonSerializer.Deserialize<List<DesignGroupEnvelope?>>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Design group envelopes file {path} is not an array of design group envelopes: {ex.Message}", ex);
        }

        if (read is null)
        {
            throw new InvalidDataException($"Design group envelopes file {path} holds no array");
        }

        var seen = new HashSet<AirplaneDesignGroup>();
        for (int i = 0; i < read.Count; i++)
        {
            if (read[i] is not { } envelope)
            {
                throw new InvalidDataException($"Design group envelopes file {path} has a null element at index {i}");
            }

            if (!AirplaneDesignGroups.TryParseRoman(envelope.Group, out AirplaneDesignGroup group))
            {
                throw new InvalidDataException($"Design group envelopes file {path} has an unknown group '{envelope.Group}' at index {i}");
            }

            if (!seen.Add(group))
            {
                throw new InvalidDataException($"Design group envelopes file {path} repeats group {group} at index {i}");
            }
        }

        return new DesignGroupEnvelopes([.. read.OfType<DesignGroupEnvelope>().OrderBy(GroupOf)]);
    }

    /// <summary>Reads the shipped file (<see cref="ShippedPath"/>).</summary>
    /// <returns>The envelopes.</returns>
    public static DesignGroupEnvelopes LoadShipped() => Load(ShippedPath);

    /// <summary>Builds the envelope per group from <paramref name="records"/> by the rule in the class summary.</summary>
    /// <param name="records">The FAA records.</param>
    /// <param name="warnings">One line per record whose group came from a fallback worth a warning, and per group left out.</param>
    /// <returns>The envelopes.</returns>
    public static DesignGroupEnvelopes Build(IEnumerable<FaaAircraftRecord> records, out IReadOnlyList<string> warnings)
    {
        var lines = new List<string>();
        var members = new Dictionary<AirplaneDesignGroup, List<FaaAircraftRecord>>();
        foreach (FaaAircraftRecord record in records)
        {
            if (!string.Equals(record.Class, "Fixed-wing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AirplaneDesignGroup? group = AirplaneDesignGroups.OfRecord(record, out string? warning);
            if (warning is not null)
            {
                lines.Add(warning);
            }

            if (group is not { } g)
            {
                continue;
            }

            if (!members.TryGetValue(g, out List<FaaAircraftRecord>? list))
            {
                list = [];
                members[g] = list;
            }

            list.Add(record);
        }

        var envelopes = new List<DesignGroupEnvelope>();
        foreach (AirplaneDesignGroup group in Enum.GetValues<AirplaneDesignGroup>())
        {
            if (Envelope(group, members.GetValueOrDefault(group) ?? []) is { } envelope)
            {
                envelopes.Add(envelope);
            }
            else
            {
                lines.Add($"No FAA record qualifies for design group {group}; it gets no push targets");
            }
        }

        warnings = lines;
        return new DesignGroupEnvelopes(envelopes);
    }

    /// <summary>
    /// The footprint the group's push targets are planned with, built from its envelope alone, never from
    /// <see cref="FaaAircraftDatabase"/>.
    /// </summary>
    /// <param name="group">The design group.</param>
    /// <returns>The footprint.</returns>
    /// <exception cref="KeyNotFoundException">The group has no envelope.</exception>
    public AircraftFootprint FootprintOf(AirplaneDesignGroup group) =>
        FootprintOf(Envelopes.FirstOrDefault(e => GroupOf(e) == group) ?? throw new KeyNotFoundException($"Design group {group} has no envelope"));

    /// <summary>The footprint built from <paramref name="envelope"/> alone, never from <see cref="FaaAircraftDatabase"/>.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The footprint.</returns>
    public static AircraftFootprint FootprintOf(DesignGroupEnvelope envelope) =>
        new()
        {
            TypeCode = envelope.EnvelopeCode,
            LengthFt = envelope.LengthFt,
            WingspanFt = envelope.WingspanFt,
            WheelbaseFt = envelope.WheelbaseFt,
            Category = envelope.Category,
        };

    /// <summary>The design group <paramref name="envelope"/> names.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The group.</returns>
    /// <exception cref="InvalidDataException">The envelope names no group I to VI.</exception>
    public static AirplaneDesignGroup GroupOf(DesignGroupEnvelope envelope) =>
        AirplaneDesignGroups.TryParseRoman(envelope.Group, out AirplaneDesignGroup group)
            ? group
            : throw new InvalidDataException($"Design group envelope {envelope.EnvelopeCode} has an unknown group '{envelope.Group}'");

    /// <summary>
    /// The shipped file's text: an indented array in group order, keys in a fixed order, LF line ends and a final LF, so
    /// the same envelopes always give the same bytes.
    /// </summary>
    /// <param name="envelopes">The envelopes.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(DesignGroupEnvelopes envelopes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartArray();
            foreach (DesignGroupEnvelope envelope in envelopes.Envelopes)
            {
                WriteEnvelope(writer, envelope);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Writes <see cref="ToJson"/>'s text to <paramref name="path"/>, UTF-8 without a byte-order mark.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="envelopes">The envelopes.</param>
    public static void Write(string path, DesignGroupEnvelopes envelopes) => File.WriteAllText(path, ToJson(envelopes), new UTF8Encoding(false));

    private static DesignGroupEnvelope? Envelope(AirplaneDesignGroup group, List<FaaAircraftRecord> members)
    {
        if ((Max(members, r => r.LengthFt) is not { } length) || (Max(members, r => r.WingspanFt) is not { } span))
        {
            return null;
        }

        (double Value, string Icao)? wheelbase = MaxWheelbase(members);
        (double Value, string Icao)? gearWidth = Max(members, r => r.MainGearWidthFt);
        return new DesignGroupEnvelope
        {
            Group = group.ToString(),
            EnvelopeCode = $"ADG-{group}",
            LengthFt = length.Value,
            WingspanFt = Math.Min(span.Value, AirplaneDesignGroups.MaxWingspanFt(group)),
            WheelbaseFt = wheelbase?.Value,
            MainGearWidthFt = gearWidth?.Value,
            Category = AircraftCategorization.Categorize(wheelbase?.Icao ?? length.Icao),
            Sources = new DesignGroupSources
            {
                LengthFt = length.Icao,
                WingspanFt = span.Icao,
                WheelbaseFt = wheelbase?.Icao,
                MainGearWidthFt = gearWidth?.Icao,
            },
        };
    }

    /// <summary>
    /// Whether the record's wheelbase may set the envelope's: not a broken row (wheelbase at least
    /// <see cref="TurnAboutFit.BrokenRowWheelbaseRatio"/> of the length), and not a non-jet taildragger (at least
    /// <see cref="TurnAboutFit.TaildraggerWheelbaseRatio"/>). A record with no length or wheelbase is not judged.
    /// </summary>
    private static bool HasTricycleWheelbase(FaaAircraftRecord record)
    {
        if ((record.WheelbaseFt is not { } wheelbaseFt) || (record.LengthFt is not { } lengthFt) || (lengthFt <= 0.0))
        {
            return true;
        }

        double ratio = wheelbaseFt / lengthFt;
        bool taildragger =
            (ratio >= TurnAboutFit.TaildraggerWheelbaseRatio) && (AircraftCategorization.Categorize(record.IcaoCode) != AircraftCategory.Jet);
        return (ratio < TurnAboutFit.BrokenRowWheelbaseRatio) && !taildragger;
    }

    /// <summary>
    /// The greatest wheelbase among <paramref name="members"/>' tricycle-wheelbase records, and the record giving it. A tie
    /// goes to a record whose cockpit-to-main-gear is unknown or at least its wheelbase — a row whose cockpit figure sits
    /// under its wheelbase is a copy error, so it may not label the envelope — then to the ordinal-first ICAO code.
    /// </summary>
    private static (double Value, string Icao)? MaxWheelbase(List<FaaAircraftRecord> members)
    {
        FaaAircraftRecord? best = members
            .Where(HasTricycleWheelbase)
            .Where(r => r.WheelbaseFt is > 0.0)
            .OrderByDescending(r => r.WheelbaseFt)
            .ThenByDescending(HasConsistentCockpitToMainGear)
            .ThenBy(r => r.IcaoCode, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : (best.WheelbaseFt!.Value, best.IcaoCode);
    }

    /// <summary>
    /// Whether the record's cockpit-to-main-gear is consistent with its wheelbase: unknown, or at least the wheelbase. Used
    /// only to break a tie between records sharing the greatest wheelbase.
    /// </summary>
    private static bool HasConsistentCockpitToMainGear(FaaAircraftRecord record) =>
        (record.CockpitToMainGearFt is not { } cockpit) || ((record.WheelbaseFt is { } wheelbase) && (cockpit >= wheelbase));

    /// <summary>
    /// The greatest positive figure among <paramref name="members"/> and the record giving it; a tie goes to the
    /// ordinal-first ICAO code.
    /// </summary>
    private static (double Value, string Icao)? Max(IEnumerable<FaaAircraftRecord> members, Func<FaaAircraftRecord, double?> figure)
    {
        FaaAircraftRecord? best = members
            .Where(r => figure(r) is > 0.0)
            .OrderByDescending(r => figure(r))
            .ThenBy(r => r.IcaoCode, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? null : (figure(best)!.Value, best.IcaoCode);
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, DesignGroupEnvelope envelope)
    {
        writer.WriteStartObject();
        writer.WriteString("group", envelope.Group);
        writer.WriteString("envelopeCode", envelope.EnvelopeCode);
        writer.WriteNumber("lengthFt", envelope.LengthFt);
        writer.WriteNumber("wingspanFt", envelope.WingspanFt);
        WriteNullableNumber(writer, "wheelbaseFt", envelope.WheelbaseFt);
        WriteNullableNumber(writer, "mainGearWidthFt", envelope.MainGearWidthFt);
        writer.WriteString("category", envelope.Category.ToString());
        writer.WriteStartObject("sources");
        writer.WriteString("lengthFt", envelope.Sources.LengthFt);
        writer.WriteString("wingspanFt", envelope.Sources.WingspanFt);
        writer.WriteString("wheelbaseFt", envelope.Sources.WheelbaseFt);
        writer.WriteString("mainGearWidthFt", envelope.Sources.MainGearWidthFt);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
