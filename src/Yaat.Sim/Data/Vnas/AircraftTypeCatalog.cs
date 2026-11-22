using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>One aircraft type the catalog offers: its ICAO designator and the label the editor shows beside it.</summary>
/// <param name="Designator">The ICAO type designator, e.g. <c>B738</c>.</param>
/// <param name="Manufacturer">The manufacturer's code, e.g. <c>BOEING</c>.</param>
/// <param name="Model">The model's full name, e.g. <c>737-800</c>.</param>
/// <param name="Wtc">The ICAO wake turbulence category, e.g. <c>M</c>.</param>
public sealed record AircraftTypeEntry(string Designator, string Manufacturer, string Model, string Wtc);

/// <summary>
/// The vNAS AircraftSpecs types, kept after startup for the Scenario Editor's type autocomplete: one entry per designator
/// (its first row in file order), searched by designator prefix. Before it is fed, or fed nothing, it finds no type and
/// knows none, so a type field falls back to free text.
/// </summary>
public static class AircraftTypeCatalog
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftTypeCatalog");

    private static readonly AsyncLocal<Snapshot?> Scoped = new();

    private static Snapshot _snapshot = Snapshot.Empty;

    /// <summary>Replaces the catalog with the types in <paramref name="specs"/>, fed from the same load as the aircraft categories.</summary>
    /// <param name="specs">The AircraftSpecs rows; rows with a blank designator are skipped, and a repeated designator keeps its first row.</param>
    public static void Initialize(IEnumerable<AircraftSpecEntry> specs)
    {
        var built = Snapshot.Build(specs);
        Volatile.Write(ref _snapshot, built);
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Aircraft type catalog initialized: {Count} types", built.Entries.Length);
        }
    }

    /// <summary>
    /// Installs a catalog built from <paramref name="specs"/> for the current async execution context only, until the
    /// returned scope is disposed; other threads and tests keep the process-wide catalog.
    /// </summary>
    /// <param name="specs">The AircraftSpecs rows the scoped catalog holds.</param>
    /// <returns>A scope that restores the catalog it replaced when disposed.</returns>
    public static IDisposable ScopedOverride(IEnumerable<AircraftSpecEntry> specs)
    {
        var scope = new OverrideScope(Scoped.Value);
        Scoped.Value = Snapshot.Build(specs);
        return scope;
    }

    /// <summary>Whether <paramref name="designator"/> is a catalog type, compared ignoring case.</summary>
    /// <param name="designator">The type designator to look up.</param>
    /// <returns><c>true</c> when the catalog holds the type; <c>false</c> for an unknown or blank one, or an empty catalog.</returns>
    public static bool IsKnown(string designator) => (designator is not null) && Current().Designators.Contains(designator.Trim());

    /// <summary>The catalog types whose designator starts with <paramref name="prefix"/>, ignoring case, in ordinal designator order.</summary>
    /// <param name="prefix">The typed text; it is trimmed, and a blank prefix finds nothing.</param>
    /// <param name="limit">The most entries to return; zero or less finds nothing.</param>
    /// <returns>At most <paramref name="limit"/> entries.</returns>
    public static IReadOnlyList<AircraftTypeEntry> Search(string prefix, int limit)
    {
        string trimmed = prefix?.Trim() ?? "";
        if ((trimmed.Length == 0) || (limit <= 0))
        {
            return [];
        }

        var found = new List<AircraftTypeEntry>();
        foreach (AircraftTypeEntry entry in Current().Entries)
        {
            if (entry.Designator.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(entry);
                if (found.Count == limit)
                {
                    break;
                }
            }
        }

        return found;
    }

    private static Snapshot Current() => Scoped.Value ?? Volatile.Read(ref _snapshot);

    private sealed class OverrideScope(Snapshot? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }

    // One immutable catalog: the entries in ordinal designator order and their designators, swapped as one reference.
    private sealed class Snapshot(AircraftTypeEntry[] entries)
    {
        public static readonly Snapshot Empty = new([]);

        public AircraftTypeEntry[] Entries { get; } = entries;

        public FrozenSet<string> Designators { get; } = entries.Select(e => e.Designator).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static Snapshot Build(IEnumerable<AircraftSpecEntry> specs)
        {
            ArgumentNullException.ThrowIfNull(specs);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<AircraftTypeEntry>();
            foreach (AircraftSpecEntry spec in specs)
            {
                if (!string.IsNullOrWhiteSpace(spec.Designator) && seen.Add(spec.Designator))
                {
                    entries.Add(new AircraftTypeEntry(spec.Designator, spec.Manufacturer, spec.Model, spec.Wtc));
                }
            }

            return new Snapshot([.. entries.OrderBy(e => e.Designator, StringComparer.Ordinal)]);
        }
    }
}
