namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>The inputs every airport built in one run shares.</summary>
/// <param name="ArtccsDir">The ARTCCs data directory the sidecar hash reads (<see cref="AirportSidecarHash.For"/>).</param>
/// <param name="Sidecars">The sidecars the movement-area classification is built from, loaded from <paramref name="ArtccsDir"/>.</param>
/// <param name="Envelopes">The design-group envelopes the push targets are planned with.</param>
/// <param name="NavDataSerial">The serial of the loaded navdata the layout is parsed against.</param>
/// <param name="StandLoop">
/// How the stands are planned at once (<see cref="PushTargetPlanner.PlanInParallel{TItem, TResult}"/>); the output is
/// identical for every value.
/// </param>
public sealed record PrecomputeBuildContext(
    string ArtccsDir,
    AirportSidecarCatalog Sidecars,
    DesignGroupEnvelopes Envelopes,
    long NavDataSerial,
    ParallelOptions StandLoop
);

/// <summary>
/// Builds one airport's precompute entry: its key, its layout parsed as the server parses it, and its push targets. This
/// file is in both hashed source sets, so an edit here stales both halves of every committed entry.
/// </summary>
public static class PrecomputeEntryBuild
{
    /// <summary>The key of an entry computed now for <paramref name="faa"/> from <paramref name="geoJson"/>.</summary>
    /// <param name="faa">The airport, FAA id.</param>
    /// <param name="geoJson">The airport's vNAS GeoJSON.</param>
    /// <param name="artccsDir">The ARTCCs data directory its sidecars are read from.</param>
    /// <param name="navDataSerial">The serial of the loaded navdata.</param>
    /// <returns>The key.</returns>
    public static PrecomputeKey CurrentKey(string faa, string geoJson, string artccsDir, long navDataSerial) =>
        PrecomputeKey.Current(GeoJsonMd5.Of(geoJson), navDataSerial, AirportSidecarHash.For(artccsDir, faa));

    /// <summary>
    /// Parses <paramref name="geoJson"/> as the server parses an airport's map, plans its push targets and writes the entry,
    /// keyed as computed now, to <paramref name="store"/> under <paramref name="faa"/>.
    /// </summary>
    /// <param name="store">The store the entry is written to.</param>
    /// <param name="faa">The airport, FAA id.</param>
    /// <param name="geoJson">The airport's vNAS GeoJSON.</param>
    /// <param name="context">The inputs every airport of the run shares.</param>
    /// <param name="standNames">The stands to plan, or null for every stand.</param>
    /// <returns>The entry written.</returns>
    public static PrecomputeEntry Build(
        PrecomputeStore store,
        string faa,
        string geoJson,
        PrecomputeBuildContext context,
        IReadOnlySet<string>? standNames
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);

        PrecomputeKey key = CurrentKey(faa, geoJson, context.ArtccsDir, context.NavDataSerial);
        AirportGroundLayout layout = GeoJsonParser.Parse(faa.ToLowerInvariant(), geoJson, faa);
        IReadOnlyList<PushTargetEntry> targets = standNames is null
            ? PushTargetPlanner.Compute(layout, context.Envelopes, context.Sidecars, context.StandLoop)
            : PushTargetPlanner.ComputeStands(layout, context.Envelopes, context.Sidecars, standNames, context.StandLoop);

        var entry = new PrecomputeEntry(faa, key, layout, targets);
        store.Write(entry);
        return entry;
    }
}
