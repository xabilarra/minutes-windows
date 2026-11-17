using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minutes.Core;

public class GroqException(string message) : Exception(message);

public sealed class GroqMissingApiKeyException() : GroqException("The Groq API key is missing.");

public sealed class GroqHttpException(int status, string serverMessage)
    : GroqException(serverMessage.Length == 0 ? $"Groq returned HTTP {status}." : $"Groq returned HTTP {status}: {serverMessage}")
{
    public int Status { get; } = status;
    public string ServerMessage { get; } = serverMessage;
}

public sealed class GroqInvalidResponseException() : GroqException("Groq returned an unexpected response.");

/// <summary>Client for Groq's OpenAI-compatible audio transcription endpoint.</summary>
public sealed partial class GroqClient
{
    public static readonly Uri DefaultEndpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    public const string DefaultModel = "whisper-large-v3";

    internal const int MaximumErrorMessageLength = 500;
    internal const double DefaultRetryAfter = 5;
    /// <summary>Groq's hourly audio limit can ask for waits of several minutes.</summary>
    internal const double MaximumRetryAfter = 900;
    /// <summary>Rate-limit waits allowed per file, on top of the attempts.</summary>
    internal const int MaximumRateLimitWaits = 20;
    internal const double InitialBackoff = 2;
    internal const double MaximumBackoff = 60;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(300);

    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly int _maxAttempts;
    private readonly WhisperTokenizer? _tokenizer;
    private readonly Func<double, CancellationToken, Task> _sleeper;

    /// <param name="apiKey">Groq API key; surrounding whitespace is ignored.</param>
    /// <param name="http">Shared HTTP client (tests inject a fake handler).</param>
    /// <param name="maxAttempts">Total attempts per file, including the first.</param>
    /// <param name="tokenizer">Rebuilds each segment's text from its tokens; null keeps Groq's text.</param>
    /// <param name="sleeper">Waits the given seconds between attempts (injected in tests).</param>
    public GroqClient(
        string apiKey,
        HttpClient? http = null,
        Uri? endpoint = null,
        int maxAttempts = 4,
        WhisperTokenizer? tokenizer = null,
        Func<double, CancellationToken, Task>? sleeper = null)
    {
        _apiKey = apiKey;
        _http = http ?? SharedHttp.Value;
        _endpoint = endpoint ?? DefaultEndpoint;
        _maxAttempts = maxAttempts;
        _tokenizer = tokenizer;
        _sleeper = sleeper ?? ((seconds, token) => Task.Delay(TimeSpan.FromSeconds(seconds), token));
    }

    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = RequestTimeout });

    /// <summary>
    /// Uploads one audio file. Retries: 429 waits (Retry-After or the wait named in the message, default 5 s, max 900 s,
    /// up to 20 waits that do not count as attempts); 5xx and network failures back off 2, 4, 8 s up to the attempts;
    /// other 4xx throw immediately. A cancelled token is never retried.
    /// </summary>
    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(
        string filePath, string model = DefaultModel, string? language = "es", string? prompt = null, CancellationToken cancellationToken = default)
    {
        var key = _apiKey.Trim();
        if (key.Length == 0)
        {
            throw new GroqMissingApiKeyException();
        }
        var fileData = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(filePath);
        var attempts = Math.Max(1, _maxAttempts);
        var attempt = 0;
        var rateLimitWaits = 0;
        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();
            var isLastAttempt = attempt >= attempts;

            HttpResponseMessage response;
            try
            {
                using var request = MakeRequest(key, _endpoint, fileData, fileName, model, language, prompt);
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsNetworkFailure(error, cancellationToken) && !isLastAttempt)
            {
                await _sleeper(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 200 and < 300)
                {
                    return ParseSegments(body, _tokenizer);
                }
                if (status == 429 && rateLimitWaits < MaximumRateLimitWaits)
                {
                    rateLimitWaits++;
                    attempt--;
                    await _sleeper(RetryAfterDelay(response.Headers.RetryAfter, body), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (status is >= 500 and <= 599 && !isLastAttempt)
                {
                    await _sleeper(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new GroqHttpException(status, ErrorMessage(body, key));
            }
        }
    }

    private static bool IsNetworkFailure(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    internal static HttpRequestMessage MakeRequest(
        string apiKey, Uri endpoint, byte[] fileData, string fileName, string model, string? language, string? prompt)
    {
        var form = new MultipartFormDataContent("minutes-" + Guid.NewGuid().ToString("N"));
        var file = new ByteArrayContent(fileData);
        file.Headers.ContentType = new MediaTypeHeaderValue(ContentType(fileName));
        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");
        if (language is not null)
        {
            form.Add(new StringContent(language), "language");
        }
        if (prompt is not null)
        {
            form.Add(new StringContent(prompt), "prompt");
        }
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>Parses verbose_json segments; a response with only "text" becomes one segment [0, 0].</summary>
    public static IReadOnlyList<RawSegment> ParseSegments(byte[] data, WhisperTokenizer? tokenizer = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw new GroqInvalidResponseException();
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GroqInvalidResponseException();
            }
            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    return segments.EnumerateArray().Select(segment =>
                    {
                        var text = segment.GetProperty("text").GetString() ?? "";
                        if (tokenizer is not null && segment.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                        {
                            text = tokenizer.Text(tokens.EnumerateArray().Select(token => token.GetInt32())) ?? text;
                        }
                        return new RawSegment(
                            segment.GetProperty("start").GetDouble(),
                            segment.GetProperty("end").GetDouble(),
                            text,
                            OptionalDouble(segment, "avg_logprob"),
                            OptionalDouble(segment, "no_speech_prob"),
                            OptionalDouble(segment, "compression_ratio"));
                    }).ToList();
                }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    throw new GroqInvalidResponseException();
                }
            }
            if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString() ?? "";
                return text.Trim().Length == 0 ? [] : [new RawSegment(0, 0, text)];
            }
            throw new GroqInvalidResponseException();
        }
    }

    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>2, 4, 8 … seconds after the 1st, 2nd, 3rd … failed attempt, capped.</summary>
    internal static double BackoffDelay(int attempt) =>
        Math.Min(InitialBackoff * Math.Pow(2, Math.Max(0, attempt - 1)), MaximumBackoff);

    /// <summary>The longer of Retry-After and the wait named in the error message, clamped to [0, 900]; 5 when neither.</summary>
    internal static double RetryAfterDelay(RetryConditionHeaderValue? retryAfter, byte[] body)
    {
        double? header = retryAfter?.Delta?.TotalSeconds;
        var named = WaitNamed(ErrorMessage(body, ""));
        var values = new[] { header, named }.Where(value => value is not null && double.IsFinite(value.Value)).Select(value => value!.Value).ToList();
        return values.Count == 0 ? DefaultRetryAfter : Math.Min(Math.Max(values.Max(), 0), MaximumRetryAfter);
    }

    /// <summary>Seconds in "try again in 1h2m3.5s" or "345ms", null when absent.</summary>
    internal static double? WaitNamed(string message)
    {
        var match = TryAgainPattern().Match(message);
        if (!match.Success)
        {
            return null;
        }
        var total = 0.0;
        foreach (Match part in DurationPartPattern().Matches(match.Groups[1].Value))
        {
            var value = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value switch
            {
                "h" => value * 3600,
                "m" => value * 60,
                "ms" => value / 1000,
                _ => value,
            };
        }
        return total;
    }

    [GeneratedRegex(@"try again in ((?:\d+(?:\.\d+)?(?:ms|[hms]))+)")]
    private static partial Regex TryAgainPattern();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|[hms])")]
    private static partial Regex DurationPartPattern();

    /// <summary>The JSON error.message when present, else the body; API key redacted before truncation.</summary>
    internal static string ErrorMessage(byte[] data, string apiKey)
    {
        string? decoded = null;
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                decoded = message.GetString();
            }
        }
        catch (JsonException)
        {
        }
        var text = (decoded ?? System.Text.Encoding.UTF8.GetString(data)).Trim();
        if (apiKey.Length > 0)
        {
            text = text.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }
        return text.Length > MaximumErrorMessageLength ? text[..MaximumErrorMessageLength] : text;
    }

    internal static string ContentType(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() switch
    {
        "m4a" or "mp4" => "audio/mp4",
        "mp3" or "mpga" or "mpeg" => "audio/mpeg",
        "wav" => "audio/wav",
        "flac" => "audio/flac",
        "ogg" or "opus" => "audio/ogg",
        "webm" => "audio/webm",
        _ => "application/octet-stream",
    };

}
