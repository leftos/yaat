using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>One preferred route row: its FAA origin and destination ids, its route string (endpoints excluded) and the FAA's notes.</summary>
internal sealed record PreferredRouteRow(string Origin, string Destination, string RouteText, PreferredRouteInfo Info);

/// <summary>
/// The offered rows of one preferred-routes file, with how many data rows it held and how many of those were malformed.
/// </summary>
internal sealed class PreferredRouteTable(
    IReadOnlyList<PreferredRouteRow> rows,
    int dataRows,
    int malformedRows,
    IReadOnlyList<string> missingColumns
)
{
    private readonly ILookup<string, PreferredRouteRow> _byPair = rows.ToLookup(
        row => PairKey(row.Origin, row.Destination),
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Gets the rows offered, in file order.</summary>
    public IReadOnlyList<PreferredRouteRow> Rows { get; } = rows;

    /// <summary>Gets the number of data rows (header and blank lines excluded).</summary>
    public int DataRows { get; } = dataRows;

    /// <summary>Gets the number of data rows that could not be read.</summary>
    public int MalformedRows { get; } = malformedRows;

    /// <summary>Gets the required columns the header did not name (every row is then malformed); empty for a good header.</summary>
    public IReadOnlyList<string> MissingColumns { get; } = missingColumns;

    /// <summary>Gets a value indicating whether the file held data rows and none of them could be read.</summary>
    public bool IsUnreadable => (DataRows > 0) && (MalformedRows == DataRows);

    /// <summary>The rows for exactly this origin and destination (FAA ids, case-insensitive), never the reverse pair.</summary>
    public IEnumerable<PreferredRouteRow> For(string origin, string destination) => _byPair[PairKey(origin, destination)];

    private static string PairKey(string origin, string destination) => origin + ">" + destination;
}

/// <summary>
/// Reads the FAA NASR <c>PFR_BASE.csv</c> (one quoted CSV row per preferred route, columns named by its header). Rows of
/// the types offered are kept; NAR rows (fix endpoints) and rows with no route string are dropped; rows of an unknown type
/// are dropped and named in one warning; rows that cannot be read are counted and reported in one warning.
/// </summary>
internal static class PreferredRouteParser
{
    private const string NarType = "NAR";

    private const char ByteOrderMark = (char)0xFEFF;

    private static readonly ILogger Log = SimLog.CreateLogger("PreferredRouteParser");

    private static readonly HashSet<string> OfferedTypes = new(["H", "L", "HPD", "LPD", "SLD", "SHD", "TEC"], StringComparer.Ordinal);

    private enum RowKind
    {
        Offered,
        Dropped,
        UnknownType,
        Malformed,
    }

    /// <summary>Parses a whole <c>PFR_BASE.csv</c>; an empty or header-only file gives an empty table.</summary>
    public static PreferredRouteTable Parse(TextReader reader)
    {
        string? header = reader.ReadLine();
        if (header is null)
        {
            return new PreferredRouteTable([], 0, 0, []);
        }

        var columns = Columns.From(header.TrimStart(ByteOrderMark), out List<string> missingColumns);
        LogMissingColumns(missingColumns);
        var rows = new List<PreferredRouteRow>();
        var unknownTypes = new SortedSet<string>(StringComparer.Ordinal);
        int dataRows = 0;
        int malformed = 0;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            dataRows++;
            RowKind kind = ParseRow(line, columns, out PreferredRouteRow? row, out string? type);
            switch (kind)
            {
                case RowKind.Offered when row is not null:
                    rows.Add(row);
                    break;
                case RowKind.Malformed:
                    malformed++;
                    break;
                case RowKind.UnknownType when type is not null:
                    unknownTypes.Add(type);
                    break;
            }
        }

        LogLoad(dataRows, malformed, unknownTypes);
        return new PreferredRouteTable(rows, dataRows, malformed, missingColumns);
    }

    private static void LogMissingColumns(List<string> missingColumns)
    {
        if ((missingColumns.Count > 0) && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Preferred routes: the file's header lacks the columns {Columns}", string.Join(", ", missingColumns));
        }
    }

    private static RowKind ParseRow(string line, Columns? columns, out PreferredRouteRow? row, out string? type)
    {
        row = null;
        type = null;
        List<string>? fields = SplitCsv(line);
        if ((columns is null) || !columns.Fits(fields))
        {
            return RowKind.Malformed;
        }

        type = fields[columns.Type].Trim();
        if (!OfferedTypes.Contains(type))
        {
            return type == NarType ? RowKind.Dropped : RowKind.UnknownType;
        }

        string route = fields[columns.Route].Trim();
        if (route.Length == 0)
        {
            return RowKind.Dropped;
        }

        row = new PreferredRouteRow(fields[columns.Origin].Trim(), fields[columns.Destination].Trim(), route, columns.Info(fields, type));
        return RowKind.Offered;
    }

    private static void LogLoad(int dataRows, int malformed, SortedSet<string> unknownTypes)
    {
        if ((malformed > 0) && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Preferred routes: skipped {Malformed} malformed rows of {Rows}", malformed, dataRows);
        }

        if ((unknownTypes.Count > 0) && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Preferred routes: dropped rows of unknown route types {Types}", string.Join(", ", unknownTypes));
        }
    }

    /// <summary>
    /// Splits one CSV line: fields separated by commas, a field optionally in double quotes with "" for a quote inside.
    /// Null when a quote is left open or text follows a closing quote.
    /// </summary>
    internal static List<string>? SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        int i = 0;
        while (true)
        {
            field.Clear();
            i = ((i < line.Length) && (line[i] == '"')) ? ReadQuoted(line, i + 1, field) : ReadBare(line, i, field);
            if (i < 0)
            {
                return null;
            }

            fields.Add(field.ToString());
            if (i >= line.Length)
            {
                return fields;
            }

            i++;
        }
    }

    /// <summary>Reads an unquoted field from <paramref name="start"/>; returns the index of the comma after it or the line's end.</summary>
    private static int ReadBare(string line, int start, StringBuilder field)
    {
        int comma = line.IndexOf(',', start);
        int end = comma < 0 ? line.Length : comma;
        field.Append(line, start, end - start);
        return end;
    }

    /// <summary>Reads a quoted field whose text starts at <paramref name="start"/>; returns the index after its closing quote, or -1.</summary>
    private static int ReadQuoted(string line, int start, StringBuilder field)
    {
        int i = start;
        while (i < line.Length)
        {
            if (line[i] != '"')
            {
                field.Append(line[i++]);
            }
            else if ((i + 1 < line.Length) && (line[i + 1] == '"'))
            {
                field.Append('"');
                i += 2;
            }
            else
            {
                int after = i + 1;
                return ((after == line.Length) || (line[after] == ',')) ? after : -1;
            }
        }

        return -1;
    }

    /// <summary>Where the columns the parser reads sit in the file's header.</summary>
    private sealed record Columns(
        int Count,
        int Origin,
        int Destination,
        int Type,
        int Area,
        int Altitude,
        int Aircraft,
        int Hours,
        int Direction,
        int Designator,
        int Route
    )
    {
        private static readonly string[] ColumnNames =
        [
            "ORIGIN_ID",
            "DSTN_ID",
            "PFR_TYPE_CODE",
            "SPECIAL_AREA_DESCRIP",
            "ALT_DESCRIP",
            "AIRCRAFT",
            "HOURS",
            "ROUTE_DIR_DESCRIP",
            "DESIGNATOR",
            "ROUTE_STRING",
        ];

        /// <summary>The header's column positions, or null with the required columns it lacks named in <paramref name="missing"/>.</summary>
        public static Columns? From(string header, out List<string> missing)
        {
            List<string> names = SplitCsv(header) ?? [];
            int[] at = [.. ColumnNames.Select(name => names.FindIndex(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))];
            missing = [.. ColumnNames.Where((_, index) => at[index] < 0)];
            return (missing.Count > 0) ? null : new Columns(names.Count, at[0], at[1], at[2], at[3], at[4], at[5], at[6], at[7], at[8], at[9]);
        }

        /// <summary>True when <paramref name="fields"/> has one field per header column and a non-blank origin, destination and type.</summary>
        public bool Fits([NotNullWhen(true)] List<string>? fields) =>
            (fields is not null)
            && (fields.Count == Count)
            && !string.IsNullOrWhiteSpace(fields[Origin])
            && !string.IsNullOrWhiteSpace(fields[Destination])
            && !string.IsNullOrWhiteSpace(fields[Type]);

        public PreferredRouteInfo Info(List<string> fields, string type) =>
            new(
                type,
                Optional(fields[Area]),
                Optional(fields[Altitude]),
                Optional(fields[Aircraft]),
                Optional(fields[Hours]),
                Optional(fields[Direction]),
                Optional(fields[Designator])
            );

        private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
