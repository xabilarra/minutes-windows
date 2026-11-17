using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Minutes.Core;

/// <summary>Files inside a meeting folder. Names are part of the on-disk layout shared with the Mac app (Spanish by spec).</summary>
public static class MeetingFile
{
    public const string Video = "video.mp4";
    /// <summary>Microphone track ("Yo").</summary>
    public const string MicAudio = "yo.m4a";
    /// <summary>System audio track ("Ellos").</summary>
    public const string SystemAudio = "ellos.m4a";
    /// <summary>Raw microphone capture, kept until transcription succeeds.</summary>
    public const string MicRaw = "yo.wav";
    /// <summary>Raw system audio capture, kept until transcription succeeds.</summary>
    public const string SystemRaw = "ellos.wav";
    public const string TranscriptMarkdown = "transcripcion.md";
    public const string TranscriptJson = "transcripcion.json";
    public const string Metadata = "meta.json";
}

/// <summary>Locale-independent local date formatting.</summary>
internal static class LocalDateFormat
{
    public static string Format(DateTimeOffset date, string format, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(date, timeZone).ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>Naming and placement of per-meeting folders under the recordings root.</summary>
public static class MeetingFolder
{
    public const int MaximumSanitizedLength = 80;

    /// <summary>Characters that are path separators or forbidden in Windows file names.</summary>
    private static readonly HashSet<char> Forbidden = ['/', ':', '\\', '*', '?', '"', '<', '>', '|'];

    /// <summary>"yyyy-MM-dd HH.mm &lt;title&gt; (&lt;appName&gt;)", or "yyyy-MM-dd HH.mm &lt;appName&gt;" without a usable title.</summary>
    public static string FolderName(DateTimeOffset start, string? title, string appName, TimeZoneInfo? timeZone = null)
    {
        var stamp = LocalDateFormat.Format(start, "yyyy-MM-dd HH.mm", timeZone ?? TimeZoneInfo.Local);
        var app = Sanitize(appName);
        var cleanTitle = title is null ? "" : Sanitize(title);
        if (cleanTitle.Length == 0)
        {
            return app.Length == 0 ? stamp : $"{stamp} {app}";
        }
        return app.Length == 0 ? $"{stamp} {cleanTitle}" : $"{stamp} {cleanTitle} ({app})";
    }

    /// <summary>Removes forbidden and control characters, collapses whitespace, trims, max 80 characters, no trailing dot or space.</summary>
    public static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
            }
            else if (!(rune.IsBmp && Forbidden.Contains((char)rune.Value)) && !Rune.IsControl(rune))
            {
                builder.Append(rune.ToString());
            }
        }
        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var cut = TextElements.Prefix(collapsed, MaximumSanitizedLength);
        // Windows drops trailing dots and spaces from folder names.
        return cut.Trim().TrimEnd('.').Trim();
    }

    /// <summary>root\name, or root\"name (2)", "name (3)"... when it already exists.</summary>
    public static string UniquePath(string root, string name)
    {
        var candidate = Path.Combine(root, name);
        var counter = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{name} ({counter})");
            counter++;
        }
        return candidate;
    }

    /// <summary>Default root: %USERPROFILE%\Reuniones.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Reuniones");
}

internal static class TextElements
{
    /// <summary>The first <paramref name="count"/> user-perceived characters.</summary>
    public static string Prefix(string text, int count)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var builder = new StringBuilder();
        var taken = 0;
        while (taken < count && enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());
            taken++;
        }
        return builder.ToString();
    }
}

public enum TranscriptionState { Pending, Transcribing, Done, Failed }

/// <summary>Where a meeting's transcription stands; failures keep their error so they can be retried.</summary>
public sealed record TranscriptionStatus
{
    [JsonPropertyName("state")] public TranscriptionState State { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    /// <summary>Last failure message. Never contains secrets.</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }

    public TranscriptionStatus() { }

    public TranscriptionStatus(TranscriptionState state, string? model = null, string? language = null, string? error = null, DateTimeOffset? completedAt = null)
    {
        State = state;
        Model = model;
        Language = language;
        Error = error;
        CompletedAt = completedAt;
    }
}

/// <summary>Contents of a meeting's meta.json (same keys as the Mac app).</summary>
public sealed record MeetingMetadata
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("appName")] public string AppName { get; init; } = "";
    /// <summary>Windows: the microphone key of the app (exe name or package family). Key kept for parity with the Mac app.</summary>
    [JsonPropertyName("appBundleID")] public string? AppBundleId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("windowTitle")] public string? WindowTitle { get; init; }
    /// <summary>"auto" (detected meeting) or "manual" (started by the user).</summary>
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "manual";
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; init; }
    /// <summary>Video timeline start minus audio timeline start, in seconds.</summary>
    [JsonPropertyName("videoOffsetSeconds")] public double? VideoOffsetSeconds { get; init; }
    [JsonPropertyName("hasVideo")] public bool HasVideo { get; init; }
    [JsonPropertyName("transcription")] public TranscriptionStatus Transcription { get; init; } = new(TranscriptionState.Pending);
}

/// <summary>
/// Shared JSON conventions for every file Minutes writes: pretty printed, sorted keys, nulls omitted,
/// ISO 8601 UTC dates with milliseconds. Reading accepts dates without fractional seconds.
/// </summary>
public static class MinutesJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new SpeakerConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new IsoDateConverter());
        return options;
    }

    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        return Sorted(node)?.ToJsonString(Options) ?? "null";
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON document.");

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Sorted(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    private sealed class IsoDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString() ?? "";
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }
            throw new JsonException($"Invalid ISO 8601 date: {text}");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}

/// <summary>Reads and writes meta.json inside a meeting folder.</summary>
public static class MetadataStore
{
    /// <summary>Atomic write. The folder must already exist.</summary>
    public static void Write(MeetingMetadata metadata, string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Meeting folder not found: {folder}");
        }
        AtomicFile.WriteAllText(Path.Combine(folder, MeetingFile.Metadata), MinutesJson.Serialize(metadata));
    }

    public static MeetingMetadata Read(string folder) =>
        MinutesJson.Deserialize<MeetingMetadata>(File.ReadAllText(Path.Combine(folder, MeetingFile.Metadata)));

    public static bool TryRead(string folder, out MeetingMetadata metadata)
    {
        try
        {
            metadata = Read(folder);
            return true;
        }
        catch (Exception)
        {
            metadata = new MeetingMetadata();
            return false;
        }
    }
}

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Writes to a temporary sibling, then replaces the destination.</summary>
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
