using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.SpeechSandbox;

/// <summary>
/// A cloud STT model behind the corpus seam: Gemini's <c>generateContent</c> with the clip inline, or
/// OpenAI's audio transcription endpoint. Each sends a fixed ATC instruction rather than the Whisper
/// biasing prompt, retries rate limits and server errors with a growing wait, and caches every successful
/// transcript under the YAAT cache keyed by the audio, the model and the instruction, so a re-run costs no
/// API call. The API key comes from <c>GEMINI_API_KEY</c> or <c>OPENAI_API_KEY</c> in the process or the
/// user environment and is never logged.
/// </summary>
public sealed class CloudSttEngine : ICorpusSttEngine
{
    /// <summary>The instruction every cloud model gets: OpenAI as its prompt, Gemini as the start of its system instruction.</summary>
    public const string Instruction =
        "US air traffic control radio, FAA phraseology. Transcribe exactly what is said, words only, numbers spelled as spoken "
        + "(two eight right, one two thousand), NATO alphabet spelled alfa bravo ... juliett xray. Lowercase, no punctuation.";

    private const string GeminiSuffix = " Output only the transcript, nothing else. If there is no speech, output an empty line.";

    private const string GeminiUserText = "Transcribe this clip verbatim.";

    private const int MaxAttempts = 6;

    private const int ExcerptChars = 300;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly ILogger Log = AppLog.CreateLogger<CloudSttEngine>();

    private readonly SttKind _kind;
    private readonly string _model;
    private readonly string _keyName;
    private readonly string? _apiKey;
    private readonly string _instruction;

    /// <summary>One attempt's result: the transcript when <paramref name="Failure"/> is null, else why it failed.</summary>
    private sealed record AttemptOutcome(string? Text, string? Failure, bool Retryable);

    /// <summary>Builds the engine for one cloud model.</summary>
    /// <param name="kind"><see cref="SttKind.Gemini"/> or <see cref="SttKind.OpenAi"/>.</param>
    /// <param name="model">The provider's model id, such as <c>gemini-4-argon</c>.</param>
    public CloudSttEngine(SttKind kind, string model)
    {
        _keyName = kind switch
        {
            SttKind.Gemini => "GEMINI_API_KEY",
            SttKind.OpenAi => "OPENAI_API_KEY",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a cloud STT engine is Gemini or OpenAI"),
        };
        _kind = kind;
        _model = model;
        _instruction = (kind == SttKind.Gemini) ? Instruction + GeminiSuffix : Instruction;
        _apiKey = ReadKey(_keyName);
    }

    public string? SetupError() => string.IsNullOrEmpty(_apiKey) ? $"{_keyName} is not set (process or user environment)" : null;

    /// <summary>
    /// The transcript of the clip, from the cache when this clip, model and instruction were sent before;
    /// no entry when the provider refused it or every retry failed. The bias is ignored.
    /// </summary>
    public async Task<SttHypotheses> TranscribeAsync(float[] samples16k, SttBias bias, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException(SetupError());
        }
        using MemoryStream wavStream = WavHeader.WritePcm16(samples16k, AudioCaptureService.SampleRate);
        byte[] wav = wavStream.ToArray();
        string cachePath = YaatPaths.Combine("cache", "cloud-stt", CacheKey(wav, _kind, _model, RequestSignature()) + ".json");
        string? cached = ReadCache(cachePath);
        if (cached is not null)
        {
            return SttHypotheses.Single(cached, null, true);
        }
        string? text = await RequestAsync(wav, _apiKey, ct).ConfigureAwait(false);
        if (text is not null)
        {
            WriteCache(cachePath, text);
        }
        return SttHypotheses.Single(text, null, false);
    }

    /// <summary>Nothing to load: the provider holds the model.</summary>
    public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() { }

    /// <summary>
    /// The cache file name for a request: the SHA-256 hex of the WAV bytes followed by <c>\n</c>, the kind's
    /// <c>--stt</c> spelling, <c>\n</c>, the model id, <c>\n</c> and the request signature: everything sent
    /// besides the audio.
    /// </summary>
    public static string CacheKey(byte[] wav, SttKind kind, string model, string signature)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(wav);
        hash.AppendData(Encoding.UTF8.GetBytes($"\n{CorpusSttEngines.KindText(kind)}\n{model}\n{signature}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The language field an OpenAI transcription model takes: <c>gpt-transcribe</c> takes the
    /// <c>languages[]</c> list, every other model the single <c>language</c> field.
    /// </summary>
    public static (string Field, string Value) OpenAiLanguageField(string model) =>
        (model == "gpt-transcribe") ? ("languages[]", "en") : ("language", "en");

    /// <summary>
    /// The transcript in a Gemini <c>generateContent</c> response: the first candidate's text parts joined,
    /// skipping thought parts. A candidate that stopped normally with no parts is an empty transcript.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The body is not JSON, has no candidates, finished for any reason but <c>STOP</c>, or holds only thought
    /// parts without a <c>STOP</c> finish.
    /// </exception>
    public static string ParseGemini(string json)
    {
        JsonNode? root = ParseBody(json);
        if (Field(root, "candidates") is not JsonArray { Count: > 0 } candidates)
        {
            throw Invalid("the Gemini response has no candidates", json);
        }
        JsonNode? candidate = candidates[0];
        string? finish = StringField(candidate, "finishReason");
        if ((finish is not null) && (finish != "STOP"))
        {
            throw Invalid($"the Gemini candidate finished with {finish}", json);
        }
        JsonNode? parts = Field(Field(candidate, "content"), "parts");
        if (parts is null)
        {
            return string.Empty;
        }
        if (parts is not JsonArray partList)
        {
            throw Invalid("the Gemini candidate's parts are not a list", json);
        }
        return JoinText(partList, stopped: finish is not null, json);
    }

    /// <summary>The transcript in an OpenAI audio transcription response: its <c>text</c> field.</summary>
    /// <exception cref="InvalidDataException">The body is not JSON or has no string <c>text</c>.</exception>
    public static string ParseOpenAi(string json) => StringField(ParseBody(json), "text") ?? throw Invalid("the OpenAI response has no text", json);

    /// <summary>Sends the clip until it succeeds, fails without a retry, or runs out of attempts; null on failure.</summary>
    private async Task<string?> RequestAsync(byte[] wav, string apiKey, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            AttemptOutcome outcome = await SendOnceAsync(wav, apiKey, ct).ConfigureAwait(false);
            if (outcome.Failure is null)
            {
                return outcome.Text;
            }
            if (!outcome.Retryable || (attempt == MaxAttempts))
            {
                Log.LogWarning(
                    "{Kind} model {Model}: transcription failed after {Attempts} attempt(s): {Failure}",
                    CorpusSttEngines.KindText(_kind),
                    _model,
                    attempt,
                    outcome.Failure
                );
                return null;
            }
            double waitSeconds = Math.Min(60.0, Math.Pow(2, attempt)) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
        }
    }

    /// <summary>One request: 429 and 5xx statuses, transport errors and timeouts are retryable; other failures are not.</summary>
    private async Task<AttemptOutcome> SendOnceAsync(byte[] wav, string apiKey, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = (_kind == SttKind.Gemini) ? GeminiRequest(wav, apiKey) : OpenAiRequest(wav, apiKey);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new AttemptOutcome(null, $"HTTP {status}: {Excerpt(body)}", (status == 429) || (status >= 500));
            }
            return ParseSuccess(body);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptOutcome(null, $"{ex.GetType().Name}: {ex.Message}", Retryable: true);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new AttemptOutcome(null, $"timed out: {ex.Message}", Retryable: true);
        }
    }

    private AttemptOutcome ParseSuccess(string body)
    {
        try
        {
            return new AttemptOutcome((_kind == SttKind.Gemini) ? ParseGemini(body) : ParseOpenAi(body), null, Retryable: false);
        }
        catch (InvalidDataException ex)
        {
            return new AttemptOutcome(null, ex.Message, Retryable: false);
        }
    }

    private HttpRequestMessage GeminiRequest(byte[] wav, string apiKey)
    {
        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = _instruction }) },
            ["contents"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(
                        new JsonObject
                        {
                            ["inlineData"] = new JsonObject { ["mimeType"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) },
                        },
                        new JsonObject { ["text"] = GeminiUserText }
                    ),
                }
            ),
        };
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    private HttpRequestMessage OpenAiRequest(byte[] wav, string apiKey)
    {
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        (string languageField, string language) = OpenAiLanguageField(_model);
        var form = new MultipartFormDataContent
        {
            { file, "file", "clip.wav" },
            { new StringContent(_model), "model" },
            { new StringContent(_instruction), "prompt" },
            { new StringContent(language), languageField },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static string? ReadKey(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : value;
    }

    /// <summary>The cached transcript, or null on a miss. An unreadable cache file is logged and counts as a miss.</summary>
    private static string? ReadCache(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return StringField(JsonNode.Parse(File.ReadAllText(path)), "text") ?? throw new InvalidDataException("no text field");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Cloud STT cache file {Path} is unreadable; sending the clip again", path);
            return null;
        }
    }

    /// <summary>Caches a transcript; a failed write is logged and the transcript still returned.</summary>
    private static void WriteCache(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, new JsonObject { ["text"] = text }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not write the cloud STT cache file {Path}", path);
        }
    }

    private static JsonNode? ParseBody(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the response is not JSON: {Excerpt(json)}", ex);
        }
    }

    private static JsonNode? Field(JsonNode? node, string name) =>
        ((node is JsonObject obj) && obj.TryGetPropertyValue(name, out JsonNode? value)) ? value : null;

    private static string? StringField(JsonNode? node, string name) =>
        ((Field(node, name) is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;

    /// <summary>The non-thought parts' text joined; only thought parts without a <c>STOP</c> finish is invalid.</summary>
    private static string JoinText(JsonArray parts, bool stopped, string json)
    {
        if (!stopped && (parts.Count > 0) && parts.All(IsThought))
        {
            throw Invalid("the Gemini candidate holds only thought parts and did not finish with STOP", json);
        }
        var text = new StringBuilder();
        foreach (JsonNode? part in parts.Where(p => !IsThought(p)))
        {
            text.Append(StringField(part, "text") ?? string.Empty);
        }
        return text.ToString();
    }

    /// <summary>Everything a request sends besides the audio, so a change to any of it misses the cache.</summary>
    private string RequestSignature()
    {
        if (_kind == SttKind.Gemini)
        {
            return $"{_instruction}\n{GeminiUserText}";
        }
        (string field, string value) = OpenAiLanguageField(_model);
        return $"{_instruction}\n{field}={value}";
    }

    private static bool IsThought(JsonNode? part) => (Field(part, "thought") is JsonValue value) && value.TryGetValue(out bool thought) && thought;

    private static InvalidDataException Invalid(string message, string body) => new($"{message}: {Excerpt(body)}");

    private static string Excerpt(string body) => (body.Length <= ExcerptChars) ? body : body[..ExcerptChars];
}
