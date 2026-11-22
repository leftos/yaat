namespace Yaat.Client.Services;

/// <summary>
/// The vNAS tower-cab image, high-res, through the app's shared <see cref="TowerCabImageService"/> (one HTTP client, one
/// disk cache) from the base URL the vNAS config gives; null while the config has no base URL.
/// </summary>
public sealed class TowerCabAirportImagerySource : IAirportImagerySource
{
    private readonly TowerCabImageService _images;
    private readonly VnasConfigService _config;

    /// <summary>Initializes a new instance of the <see cref="TowerCabAirportImagerySource"/> class.</summary>
    /// <param name="images">The app's tower-cab image service.</param>
    /// <param name="config">The app's vNAS config, read at each request.</param>
    public TowerCabAirportImagerySource(TowerCabImageService images, VnasConfigService config)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(config);
        _images = images;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<TowerCabImage?> GetAsync(string artccId, string airportId, CancellationToken ct)
    {
        string baseUrl = _config.TowerCabImagesBaseUrl;
        if ((!_config.IsInitialized) || string.IsNullOrEmpty(baseUrl))
        {
            return null;
        }

        // Off the UI thread: the decode of a high-res image is synchronous and slow.
        return await Task.Run(() => _images.GetImageAsync(baseUrl, artccId, airportId, highRes: true), ct).WaitAsync(ct).ConfigureAwait(false);
    }
}
