using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Ships locally captured push-to-talk samples to the official YAAT server
/// (<see cref="UserPreferences.OfficialServerUrl"/>) when the user has opted in to speech
/// telemetry; while connected to any other server the samples stay pending. The lifecycle spans
/// two objects: <see cref="SpeechSampleStore"/> writes an empty <c>upload-pending</c> marker
/// beside each sample captured while <see cref="UserPreferences.SpeechTelemetryEnabled"/> is on,
/// and this uploader drains those markers — one single-sample zip per POST to
/// <c>/telemetry/speech</c>, authenticated with the session's bearer token — removing each marker
/// as its sample lands. Each sample re-checks the opt-in and its own marker first, so an opt-out
/// mid-pass sends nothing more. A sample the server rejects permanently (400/413) is dropped by
/// clearing its marker too, so it can never wedge the queue; a transient failure (429 daily cap,
/// 5xx, transport error) stops the pass and leaves the remaining samples pending for the next
/// attempt.
/// </summary>
public sealed class SpeechTelemetryUploader(HttpClient http, SpeechSampleStore store, UserPreferences preferences)
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechTelemetryUploader>();
    private const string TelemetryPath = "/telemetry/speech";
    private const string ZipContentType = "application/zip";

    // Serialises upload passes: a second concurrent call waits for the first and then usually
    // finds nothing pending, which keeps one sample from being sent twice.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Uploads every pending sample, oldest first. Returns the number accepted by the server.
    /// Returns zero without touching the network when telemetry is off, no server URL is known,
    /// the connected server isn't the official one, nothing is pending, or the token provider
    /// yields no token.
    /// </summary>
    /// <param name="serverUrl">Base URL of the connected server; null, blank or non-official short-circuits the pass.</param>
    /// <param name="accessTokenProvider">Fetched once per pass, and only when something is pending.</param>
    /// <param name="ct">Cancellation; a cancel propagates out rather than being logged as a failure.</param>
    public async Task<int> UploadPendingAsync(string? serverUrl, Func<Task<string?>> accessTokenProvider, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!preferences.SpeechTelemetryEnabled || string.IsNullOrWhiteSpace(serverUrl))
            {
                return 0;
            }

            if (!IsOfficialServer(serverUrl))
            {
                Log.LogDebug("Speech telemetry held for the official server; connected to {Server}", serverUrl);
                return 0;
            }

            IReadOnlyList<string> pending = store.PendingUploadIds();
            if (pending.Count == 0)
            {
                return 0;
            }

            string? token = await accessTokenProvider().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                return 0;
            }

            string url = $"{UserPreferences.OfficialServerUrl}{TelemetryPath}";
            int uploaded = 0;
            foreach (string id in pending)
            {
                ct.ThrowIfCancellationRequested();

                // The user can opt out, or Settings can clear the queue, while a POST is in flight.
                if (!preferences.SpeechTelemetryEnabled)
                {
                    break;
                }

                if (!store.IsPendingUpload(id))
                {
                    continue;
                }

                byte[] body = BuildBundle(id);
                if (body.Length == 0)
                {
                    // The sample vanished (evicted or deleted) between the scan and the bundle, or can't be read.
                    store.MarkUploaded(id);
                    continue;
                }

                HttpResult result = await PostAsync(url, token, body, id, ct).ConfigureAwait(false);
                if (result == HttpResult.Accepted)
                {
                    store.MarkUploaded(id);
                    uploaded++;
                }
                else if (result == HttpResult.Rejected)
                {
                    // 400/413 can never succeed on retry, so drop the sample from the queue.
                    store.MarkUploaded(id);
                }
                else
                {
                    // Transient failure: leave this sample and everything after it pending.
                    break;
                }
            }

            return uploaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Scheme, host and port against the official URL; Uri lower-cases the host, and path or trailing slash don't matter.
    private static bool IsOfficialServer(string serverUrl) =>
        Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out Uri? connected)
        && (
            Uri.Compare(
                connected,
                new Uri(UserPreferences.OfficialServerUrl),
                UriComponents.SchemeAndServer,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase
            ) == 0
        );

    // An empty result means the sample can't be sent: gone from the store, or its files unreadable (logged here, so an
    // unreadable sample is dropped rather than failing every later pass).
    private byte[] BuildBundle(string id)
    {
        try
        {
            using var buffer = new MemoryStream();
            return store.WriteBundle([id], buffer) == 0 ? [] : buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Speech telemetry could not read sample {Id}; sample dropped", id);
            return [];
        }
    }

    private async Task<HttpResult> PostAsync(string url, string token, byte[] body, string id, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(ZipContentType);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return HttpResult.Accepted;
            }

            int status = (int)response.StatusCode;
            if (status is 400 or 413)
            {
                Log.LogWarning("Speech telemetry upload of {Id} rejected with {Status}; sample dropped", id, status);
                return HttpResult.Rejected;
            }

            if (status == 429)
            {
                Log.LogInformation("Speech telemetry daily limit reached at {Id}; the rest stay pending for a later pass", id);
                return HttpResult.Transient;
            }

            Log.LogWarning("Speech telemetry upload of {Id} failed with {Status}; will retry later", id, status);
            return HttpResult.Transient;
        }
        catch (HttpRequestException ex)
        {
            Log.LogWarning(ex, "Speech telemetry upload of {Id} failed; will retry later", id);
            return HttpResult.Transient;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.LogWarning(ex, "Speech telemetry upload of {Id} timed out; will retry later", id);
            return HttpResult.Transient;
        }
    }

    private enum HttpResult
    {
        Accepted,
        Rejected,
        Transient,
    }
}
