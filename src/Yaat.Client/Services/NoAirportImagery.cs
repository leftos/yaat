namespace Yaat.Client.Services;

/// <summary>An imagery source that never has an image: the editor built without the app's vNAS services.</summary>
public sealed class NoAirportImagery : IAirportImagerySource
{
    /// <summary>Gets the one instance.</summary>
    public static NoAirportImagery Instance { get; } = new();

    private NoAirportImagery() { }

    /// <inheritdoc />
    public Task<TowerCabImage?> GetAsync(string artccId, string airportId, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct) =>
        Task.FromResult<TowerCabImage?>(null);
}
