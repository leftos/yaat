namespace Yaat.Client.Services;

/// <summary>Where the Airport Editor asks for the image it draws under an airport's map.</summary>
public interface IAirportImagerySource
{
    /// <summary>Gets the airport's vNAS tower-cab image.</summary>
    /// <param name="artccId">The ARTCC whose vNAS folder holds the image, e.g. <c>ZOA</c>.</param>
    /// <param name="airportId">The airport's FAA id, e.g. <c>OAK</c>.</param>
    /// <param name="progress">Told how much of the image has downloaded while it downloads (not on a cache hit), or null.</param>
    /// <param name="ct">Cancels the wait; a download already started finishes into the disk cache.</param>
    /// <returns>The image, or null when there is none (no vNAS config, a 404, offline with no cached copy).</returns>
    Task<TowerCabImage?> GetAsync(string artccId, string airportId, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct);
}
