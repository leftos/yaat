using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Yaat.Client.ViewModels;
using Yaat.Sim.Weather;

namespace Yaat.Client.Services;

/// <summary>
/// What Save Weather writes: the file's <paramref name="Contents"/>, the grid fetched from the room for it (null when none was
/// fetched or the fetch failed), and why the grid hash was stripped, when it was.
/// </summary>
public sealed record WeatherSaveContents(string Contents, ActiveWindGrid? FetchedGrid, string? StrippedReason);

/// <summary>
/// The GFS grid a saved weather file carries: the held bytes when this client holds the grid the weather names, else the room's
/// copy fetched by hash, so a member who did not load the weather (or the loader after a restart) saves it with its grid. A
/// fetch that fails leaves the file without the hash, so it reloads on its FD winds.
/// </summary>
public static class WeatherSaveGrid
{
    /// <summary>The strip reason when the client is not connected to a server.</summary>
    public const string NotConnected = "not connected to the server";

    /// <summary>The strip reason when the fetched bytes hash to something other than the hash the weather names.</summary>
    public const string HashMismatch = "the server's grid did not match its hash";

    /// <summary>The strip reason when the fetched bytes are not decodable GRIB2.</summary>
    public const string Undecodable = "the server's grid could not be decoded";

    /// <summary>
    /// Builds the file contents for <paramref name="json"/>: unchanged when it names no grid; with <paramref name="held"/>'s bytes
    /// embedded when they are the grid it names; otherwise with the bytes <paramref name="fetchGrid"/> returns for its hash,
    /// once they pass the size cap, hash to that hash and decode.
    /// </summary>
    /// <param name="json">The active weather JSON, captured before the file picker opened.</param>
    /// <param name="held">The grid this client holds, if any.</param>
    /// <param name="fetchGrid">Fetches the room's grid bytes by hash (<c>ServerConnection.GetWindGridAsync</c>).</param>
    /// <param name="log">Where each failed fetch is logged as a warning.</param>
    /// <returns>
    /// The contents, the fetched grid on a successful fetch, and on a failed one the reason, with the contents naming no hash.
    /// </returns>
    public static async Task<WeatherSaveContents> PrepareAsync(string json, ActiveWindGrid? held, Func<string, Task<byte[]>> fetchGrid, ILogger log)
    {
        if (SavedWeatherGrid.NamedHash(json) is not { } hash)
        {
            return new WeatherSaveContents(json, null, null);
        }

        if ((held is not null) && string.Equals(held.Hash, hash, StringComparison.Ordinal))
        {
            return new WeatherSaveContents(SavedWeatherGrid.Embed(json, held.Grib2), null, null);
        }

        (ActiveWindGrid? fetched, string? reason) = await FetchAsync(hash, fetchGrid, log);
        return fetched is null
            ? new WeatherSaveContents(SavedWeatherGrid.WithHash(json, null), null, reason)
            : new WeatherSaveContents(SavedWeatherGrid.Embed(json, fetched.Grib2), fetched, null);
    }

    private static async Task<(ActiveWindGrid? Grid, string? Reason)> FetchAsync(string hash, Func<string, Task<byte[]>> fetchGrid, ILogger log)
    {
        byte[] bytes;
        try
        {
            bytes = await fetchGrid(hash);
        }
        catch (HubException ex)
        {
            log.LogWarning(ex, "Fetching GFS wind grid {Hash} for Save Weather was refused", hash);
            return (null, GfsGridFetcher.RefusalReason(ex.Message));
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or OperationCanceledException or IOException or TimeoutException or WebSocketException)
        {
            // Not connected, or the connection was lost (closed, timed out, transport error) while the fetch was in flight.
            log.LogWarning(ex, "Fetching GFS wind grid {Hash} for Save Weather failed: connection lost or not connected", hash);
            return (null, NotConnected);
        }

        if (bytes.Length > WindGridRegistry.MaxGridBytes)
        {
            log.LogWarning("GFS wind grid {Hash} fetched for Save Weather is {Bytes} bytes, over the limit", hash, bytes.Length);
            return (null, GfsGridFetcher.OverUploadLimit);
        }

        string fetchedHash = WindGridRegistry.HashOf(bytes);
        if (!string.Equals(fetchedHash, hash, StringComparison.Ordinal))
        {
            log.LogWarning("GFS wind grid {Hash} fetched for Save Weather hashed {FetchedHash} here", hash, fetchedHash);
            return (null, HashMismatch);
        }

        try
        {
            return (ActiveWindGrid.FromGrib2(bytes, hash), null);
        }
        catch (Grib2FormatException ex)
        {
            log.LogWarning(ex, "GFS wind grid {Hash} fetched for Save Weather did not decode", hash);
            return (null, Undecodable);
        }
    }
}
