using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>Which scope a position works: ERAM, STARS, or neither (a tower cab, ground, ATIS, ramp).</summary>
public enum ArtccPositionKind
{
    /// <summary>The position has a STARS configuration.</summary>
    Stars,

    /// <summary>The position has an ERAM configuration.</summary>
    Eram,

    /// <summary>The position has neither.</summary>
    Other,
}

/// <summary>One position a scenario can name, with the facility that owns it.</summary>
/// <param name="ArtccId">The ARTCC whose config lists the position.</param>
/// <param name="FacilityId">The facility that owns the position (ZOA, NCT, SFO).</param>
/// <param name="FacilityName">That facility's name.</param>
/// <param name="FacilityType">That facility's vNAS type (Artcc, Tracon, Atct, AtctTracon, AtctRapcon).</param>
/// <param name="PositionId">The position's vNAS id, the one a scenario stores.</param>
/// <param name="Callsign">The position's callsign (OAK_14_CTR).</param>
/// <param name="Name">The position's name (Sector 14).</param>
/// <param name="RadioName">The position's radio name (Oakland Center).</param>
/// <param name="FrequencyMhz">The position's frequency in MHz; null when the config gives none.</param>
/// <param name="Kind">Which scope the position works.</param>
/// <param name="IsNeighbour">Whether the position belongs to a neighbouring ARTCC.</param>
public sealed record ArtccPositionRow(
    string ArtccId,
    string FacilityId,
    string FacilityName,
    string FacilityType,
    string PositionId,
    string Callsign,
    string Name,
    string RadioName,
    double? FrequencyMhz,
    ArtccPositionKind Kind,
    bool IsNeighbour
);

/// <summary>
/// Every position of an ARTCC's facility tree and then of its neighbouring centers', in the order each config lists
/// them, for the Scenario Editor's position pickers. A callsign listed under two facilities is two rows.
/// </summary>
public sealed class ArtccPositionCatalog
{
    private const double HertzPerMegahertz = 1_000_000.0;

    private readonly HashSet<(string ArtccId, string FacilityId, string PositionId)> _keys;

    private ArtccPositionCatalog(string artccId, List<ArtccPositionRow> positions, List<string> unreadNeighbours)
    {
        ArtccId = artccId;
        Positions = positions;
        UnreadNeighbours = unreadNeighbours;
        _keys = [.. positions.Select(Key)];
    }

    /// <summary>Gets the ARTCC the catalog is for.</summary>
    public string ArtccId { get; }

    /// <summary>Gets the positions: the ARTCC's own first, then each neighbour's.</summary>
    public IReadOnlyList<ArtccPositionRow> Positions { get; }

    /// <summary>Gets the neighbouring centers whose config could not be read, so their positions are missing.</summary>
    public IReadOnlyList<string> UnreadNeighbours { get; }

    /// <summary>Builds the catalog of an ARTCC from its config and its neighbouring centers' configs.</summary>
    /// <param name="own">The ARTCC's config.</param>
    /// <param name="neighbours">The neighbouring centers' configs, in the order their rows are listed.</param>
    /// <param name="unreadNeighbours">The neighbouring centers whose config could not be read; empty for none.</param>
    /// <returns>The catalog.</returns>
    public static ArtccPositionCatalog Build(ArtccConfigRoot own, IEnumerable<ArtccConfigRoot> neighbours, IEnumerable<string> unreadNeighbours)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(unreadNeighbours);
        var rows = new List<ArtccPositionRow>();
        string ownId = IdOf(own);
        CollectRows(ownId, own.Facility, isNeighbour: false, rows);
        foreach (ArtccConfigRoot neighbour in neighbours)
        {
            CollectRows(IdOf(neighbour), neighbour.Facility, isNeighbour: true, rows);
        }

        return new ArtccPositionCatalog(ownId, rows, [.. unreadNeighbours]);
    }

    /// <summary>Whether the catalog lists a position under that ARTCC and facility.</summary>
    /// <param name="artccId">The ARTCC id, in any case.</param>
    /// <param name="facilityId">The owning facility's id, in any case.</param>
    /// <param name="positionId">The position's vNAS id, exactly.</param>
    /// <returns>True when a row has all three.</returns>
    public bool Contains(string artccId, string facilityId, string positionId) =>
        _keys.Contains((artccId.Trim().ToUpperInvariant(), facilityId.Trim().ToUpperInvariant(), positionId));

    private static (string, string, string) Key(ArtccPositionRow row) =>
        (row.ArtccId.ToUpperInvariant(), row.FacilityId.ToUpperInvariant(), row.PositionId);

    /// <summary>The ARTCC id of a config, upper-cased: its own id, else its facility's; empty when it has neither.</summary>
    /// <param name="config">The config.</param>
    /// <returns>The id.</returns>
    internal static string IdOf(ArtccConfigRoot config) =>
        (string.IsNullOrWhiteSpace(config.Id) ? config.Facility?.Id : config.Id)?.Trim().ToUpperInvariant() ?? "";

    private static void CollectRows(string artccId, FacilityConfig? facility, bool isNeighbour, List<ArtccPositionRow> rows)
    {
        if (facility is null)
        {
            return;
        }

        foreach (PositionConfig position in facility.Positions)
        {
            rows.Add(
                new ArtccPositionRow(
                    artccId,
                    facility.Id,
                    facility.Name,
                    facility.Type,
                    position.Id,
                    position.Callsign,
                    position.Name,
                    position.RadioName,
                    position.Frequency > 0 ? position.Frequency / HertzPerMegahertz : null,
                    KindOf(position),
                    isNeighbour
                )
            );
        }

        foreach (FacilityConfig child in facility.ChildFacilities)
        {
            CollectRows(artccId, child, isNeighbour, rows);
        }
    }

    private static ArtccPositionKind KindOf(PositionConfig position) =>
        position.EramConfiguration is not null ? ArtccPositionKind.Eram
        : position.StarsConfiguration is not null ? ArtccPositionKind.Stars
        : ArtccPositionKind.Other;
}

/// <summary>
/// Reads an ARTCC's neighbouring centers (one hop, the ARTCC ids among its neighbouring facilities) through an
/// <see cref="ArtccConfigSource"/> with the <see cref="ArtccConfigSource.NeighbourConfigTtl"/>, at most
/// <see cref="MaxParallelLoads"/> at a time, and builds the <see cref="ArtccPositionCatalog"/>. A neighbour that cannot
/// be read is listed in <see cref="ArtccPositionCatalog.UnreadNeighbours"/> and the rest are kept.
/// </summary>
public static class ArtccNeighbourLoader
{
    /// <summary>How many neighbouring configs are read at once.</summary>
    public const int MaxParallelLoads = 3;

    private static readonly ILogger Log = SimLog.CreateLogger("ArtccNeighbourLoader");

    /// <summary>The neighbouring centers of an ARTCC: its neighbouring facility ids that are ARTCC ids, upper-cased, once each.</summary>
    /// <param name="own">The ARTCC's config.</param>
    /// <returns>The neighbouring ARTCC ids, in the config's order, without the ARTCC itself.</returns>
    public static IReadOnlyList<string> NeighbourArtccIds(ArtccConfigRoot own)
    {
        ArgumentNullException.ThrowIfNull(own);
        string ownId = ArtccPositionCatalog.IdOf(own);
        return
        [
            .. (own.Facility?.NeighboringFacilityIds ?? [])
                .Select(id => id.Trim().ToUpperInvariant())
                .Where(id => IsArtccId(id) && (id != ownId))
                .Distinct(),
        ];
    }

    /// <summary>Whether a facility id is an ARTCC id: three letters starting with Z (ZOA, ZLA, ZAN).</summary>
    /// <param name="facilityId">The facility id.</param>
    /// <returns>True for an ARTCC id; a neighbour id of any other shape is a terminal facility.</returns>
    public static bool IsArtccId(string facilityId) =>
        (facilityId.Length == 3) && (facilityId[0] is 'Z' or 'z') && facilityId.All(char.IsAsciiLetter);

    /// <summary>Reads the neighbouring centers' configs and builds the catalog of <paramref name="own"/> and them.</summary>
    /// <param name="source">The source the neighbouring configs are read from.</param>
    /// <param name="own">The ARTCC's config.</param>
    /// <param name="ct">Cancels the reads; a cancel is thrown.</param>
    /// <returns>The catalog, with the neighbours that could not be read listed.</returns>
    public static async Task<ArtccPositionCatalog> LoadCatalogAsync(ArtccConfigSource source, ArtccConfigRoot own, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<string> ids = NeighbourArtccIds(own);
        using var gate = new SemaphoreSlim(MaxParallelLoads);
        ArtccConfigLoad[] loads = await Task.WhenAll(ids.Select(id => LoadOneAsync(source, id, gate, ct))).ConfigureAwait(false);
        List<string> unread = [.. loads.Where(l => l.Config is null).Select(l => l.ArtccId)];
        if ((unread.Count > 0) && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Neighbouring centers of {Id} not read: {Unread}", own.Id, string.Join(", ", unread));
        }

        return ArtccPositionCatalog.Build(own, loads.Select(l => l.Config).OfType<ArtccConfigRoot>(), unread);
    }

    private static async Task<ArtccConfigLoad> LoadOneAsync(ArtccConfigSource source, string id, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await source.LoadAsync(id, ArtccConfigSource.NeighbourConfigTtl, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
