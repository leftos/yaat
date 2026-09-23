namespace Yaat.LayoutInspector.Tick;

/// <summary>Raised when several <c>--ticks</c> recordings cannot be merged into one.</summary>
public sealed class TickMergeException(string message) : Exception(message);

/// <summary>
/// Merges several TickRecorder readings into one recording, so a design review can overlay
/// successive runs of the same case as separate aircraft. A source's LABEL renames its
/// aircraft — a recording holding one aircraft becomes <c>LABEL</c>, one holding several
/// becomes <c>LABEL:CALLSIGN</c> — merged aircraft get distinct palette colours, and the
/// merged ticks are ordered by time.
/// </summary>
public static class TickRecordingMerger
{
    /// <summary>Colours handed to merged aircraft in order, cycling once exhausted.</summary>
    private static readonly string[] Palette = ["#e53935", "#43a047", "#1e88e5", "#fb8c00", "#8e24aa", "#00897b"];

    public static TickRecording Merge(IReadOnlyList<LoadedTickSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("Merge needs at least one recording", nameof(sources));
        }

        ValidateAirports(sources);
        bool palette = sources.Count > 1;
        var aircraft = new List<AircraftMetadata>();
        var ticks = new List<TickEvent>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (LoadedTickSource source in sources)
        {
            Dictionary<string, string> renames = BuildRenames(source.Recording, source.Label);
            foreach (AircraftMetadata meta in source.Recording.Aircraft)
            {
                string callsign = Renamed(renames, meta.Callsign);
                Claim(owners, callsign, source.Path);
                string color = palette ? Palette[aircraft.Count % Palette.Length] : meta.Color;
                aircraft.Add(meta with { Callsign = callsign, Color = color });
            }

            foreach (TickEvent tick in source.Recording.Ticks)
            {
                ticks.Add(tick with { Callsign = Renamed(renames, tick.Callsign) });
            }
        }

        return new TickRecording
        {
            Version = TickRecording.CurrentVersion,
            AirportId = sources[0].Recording.AirportId,
            Aircraft = aircraft,
            Ticks = [.. ticks.OrderBy(t => t.T)],
        };
    }

    /// <summary>
    /// A labelled recording renames its own aircraft; an unlabelled one keeps its callsigns,
    /// so two of those sharing a callsign cannot be told apart.
    /// </summary>
    private static Dictionary<string, string> BuildRenames(TickRecording recording, string? label)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (label is null)
        {
            return renames;
        }

        List<string> callsigns =
        [
            .. recording
                .Aircraft.Select(a => a.Callsign)
                .Concat(recording.Ticks.Select(t => t.Callsign))
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        if (callsigns.Count == 0)
        {
            return renames;
        }

        if (callsigns.Count == 1)
        {
            renames[callsigns[0]] = label;
            return renames;
        }

        foreach (string callsign in callsigns)
        {
            renames[callsign] = $"{label}:{callsign}";
        }

        return renames;
    }

    private static string Renamed(Dictionary<string, string> renames, string callsign) =>
        renames.TryGetValue(callsign, out string? renamed) ? renamed : callsign;

    private static void Claim(Dictionary<string, string> owners, string callsign, string path)
    {
        if (owners.TryGetValue(callsign, out string? owner) && !string.Equals(owner, path, StringComparison.Ordinal))
        {
            throw new TickMergeException($"--ticks recordings {owner} and {path} both contain callsign {callsign}; give each a LABEL= prefix");
        }

        owners[callsign] = path;
    }

    private static void ValidateAirports(IReadOnlyList<LoadedTickSource> sources)
    {
        LoadedTickSource? first = null;
        foreach (LoadedTickSource source in sources)
        {
            if (source.Recording.AirportId is not { } airport)
            {
                continue;
            }

            if (first is null)
            {
                first = source;
            }
            else if (!string.Equals(first.Recording.AirportId, airport, StringComparison.OrdinalIgnoreCase))
            {
                throw new TickMergeException(
                    $"--ticks recordings {first.Path} ({first.Recording.AirportId}) and {source.Path} ({airport}) are from different airports"
                );
            }
        }
    }
}
