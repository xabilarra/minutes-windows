using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minutes.Core;

/// <summary>Who spoke a segment, inferred from its track. Stored labels are Spanish by spec.</summary>
[JsonConverter(typeof(SpeakerConverter))]
public enum Speaker { Me, Them }

public static class SpeakerLabel
{
    public static string Of(Speaker speaker) => speaker == Speaker.Me ? "Yo" : "Ellos";
}

internal sealed class SpeakerConverter : JsonConverter<Speaker>
{
    public override Speaker Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "Yo" => Speaker.Me,
            "Ellos" => Speaker.Them,
            var other => throw new JsonException($"Unknown speaker: {other}"),
        };

    public override void Write(Utf8JsonWriter writer, Speaker value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SpeakerLabel.Of(value));
}

/// <summary>One Whisper segment as returned by the API, times relative to the uploaded chunk.</summary>
public sealed record RawSegment(
    double Start, double End, string Text, double? AvgLogprob = null, double? NoSpeechProb = null, double? CompressionRatio = null);

/// <summary>A cleaned, speaker-labelled segment with times in seconds from the start of the recording.</summary>
public sealed record TranscriptSegment(
    [property: JsonPropertyName("speaker")] Speaker Speaker,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);

/// <summary>Removes Whisper output that is not speech: silence guesses, repetition loops and known hallucinations.</summary>
public static class SegmentFilter
{
    internal const double NoSpeechProbabilityLimit = 0.6;
    internal const double AverageLogProbabilityLimit = -1.0;
    internal const double CompressionRatioLimit = 2.4;
    internal const string HallucinationMarker = "amaraorg";

    internal static readonly HashSet<string> Hallucinations =
    [
        "subtitulos realizados por la comunidad de amaraorg",
        "subtitulos por la comunidad de amaraorg",
        "gracias por ver el video",
        "gracias por ver",
        "por ver el video",
        "suscribete",
        "suscribete al canal",
        "musica",
        "thank you for watching",
        "thanks for watching",
        "amaraorg",
    ];

    public static IReadOnlyList<RawSegment> Clean(IEnumerable<RawSegment> segments)
    {
        var kept = new List<RawSegment>();
        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            var normalized = Normalize(text);
            if (normalized.Length == 0 || IsLikelySilence(segment) || IsRepetitionLoop(segment) || IsHallucination(normalized))
            {
                continue;
            }
            kept.Add(segment with { Text = text });
        }
        return kept;
    }

    /// <summary>Lowercases, removes diacritics, punctuation and symbols, and collapses whitespace.</summary>
    public static string Normalize(string text)
    {
        var folded = TextFolding.RemoveDiacritics(text).ToLowerInvariant();
        var builder = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
            }
            else if (!TextFolding.IsPunctuationOrSymbol(rune))
            {
                builder.Append(rune.ToString());
            }
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsLikelySilence(RawSegment segment) =>
        segment.NoSpeechProb is { } noSpeech && segment.AvgLogprob is { } logprob
        && noSpeech > NoSpeechProbabilityLimit && logprob < AverageLogProbabilityLimit;

    private static bool IsRepetitionLoop(RawSegment segment) => (segment.CompressionRatio ?? 0) > CompressionRatioLimit;

    /// <summary>A segment of nothing but "o" ("o", "o o o") is Whisper guessing at background noise.</summary>
    private static bool IsHallucination(string normalized) =>
        Hallucinations.Contains(normalized)
        || normalized.Contains(HallucinationMarker, StringComparison.Ordinal)
        || normalized.Split(' ').All(word => word == "o");
}

internal static class TextFolding
{
    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool IsPunctuationOrSymbol(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol => true,
        _ => false,
    };
}

/// <summary>Drops microphone segments that merely repeat the remote side (speakers heard by the mic).</summary>
public static class EchoDeduplicator
{
    internal const int MinimumTokensForOverlapRatio = 4;

    public static IReadOnlyList<TranscriptSegment> Dedupe(
        IReadOnlyList<TranscriptSegment> segments, double threshold = 0.6, double minimumOverlap = 0.5)
    {
        var remote = segments
            .Where(segment => segment.Speaker == Speaker.Them)
            .OrderBy(segment => segment.Start)
            .Select(segment => (Segment: segment, Normalized: SegmentFilter.Normalize(segment.Text)))
            .ToList();

        return segments.Where(segment =>
        {
            if (segment.Speaker != Speaker.Me)
            {
                return true;
            }
            var overlapping = remote.Where(item => Overlap(segment, item.Segment) > 0).ToList();
            if (overlapping.Count == 0 || OverlapRatio(segment, overlapping.Select(item => item.Segment)) < minimumOverlap)
            {
                return true;
            }
            return !IsEcho(SegmentFilter.Normalize(segment.Text), overlapping.Select(item => item.Normalized).ToList(), threshold);
        }).ToList();
    }

    private static double Overlap(TranscriptSegment a, TranscriptSegment b) =>
        Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));

    private static double OverlapRatio(TranscriptSegment microphone, IEnumerable<TranscriptSegment> remote)
    {
        var duration = microphone.End - microphone.Start;
        if (duration <= 0)
        {
            return 1;
        }
        return Math.Min(1, remote.Sum(segment => Overlap(microphone, segment)) / duration);
    }

    private static bool IsEcho(string microphone, List<string> remote, double threshold)
    {
        var tokens = microphone.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < MinimumTokensForOverlapRatio)
        {
            return remote.Contains(microphone);
        }
        var remoteTokens = remote.SelectMany(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return (double)LongestCommonSubsequence(tokens, remoteTokens) / tokens.Length >= threshold;
    }

    private static int LongestCommonSubsequence(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        var previous = new int[b.Length + 1];
        foreach (var tokenA in a)
        {
            var current = new int[b.Length + 1];
            for (var j = 0; j < b.Length; j++)
            {
                current[j + 1] = tokenA == b[j] ? previous[j] + 1 : Math.Max(previous[j + 1], current[j]);
            }
            previous = current;
        }
        return previous[b.Length];
    }
}

/// <summary>A stretch of a track, in seconds from the start of the track.</summary>
public sealed record AudioPiece(double Start, double Duration)
{
    public double End => Start + Duration;
}

/// <summary>One upload: the pieces of a track that contain speech, played back to back.</summary>
public sealed record SpeechChunk(int Index, IReadOnlyList<AudioPiece> Pieces)
{
    public double Duration => Pieces.Sum(piece => piece.Duration);

    /// <summary>Maps a time in the uploaded audio back to the track.</summary>
    public double TrackTime(double time, bool isEnd = false)
    {
        var offset = 0.0;
        foreach (var piece in Pieces)
        {
            var pieceEnd = offset + piece.Duration;
            if (time < pieceEnd || (isEnd && time <= pieceEnd))
            {
                return piece.Start + Math.Max(0, time - offset);
            }
            offset = pieceEnd;
        }
        return Pieces.Count > 0 ? Pieces[^1].End : 0;
    }
}

/// <summary>Finds the speech in a track from its loudness and packs it into uploads.</summary>
public static class SpeechPlan
{
    public static IReadOnlyList<AudioPiece> Pieces(
        IReadOnlyList<double> levels, double windowDuration, double totalDuration,
        double thresholdDb = -50, double minimumSilence = 3, double padding = 1)
    {
        if (windowDuration <= 0 || totalDuration <= 0)
        {
            return [];
        }
        var ranges = new List<(double Start, double End)>();
        for (var index = 0; index < levels.Count; index++)
        {
            if (levels[index] <= thresholdDb)
            {
                continue;
            }
            var start = index * windowDuration;
            var end = Math.Min(totalDuration, start + windowDuration);
            if (start >= end)
            {
                break;
            }
            if (ranges.Count > 0 && start - ranges[^1].End < minimumSilence)
            {
                ranges[^1] = (ranges[^1].Start, end);
            }
            else
            {
                ranges.Add((start, end));
            }
        }
        var padded = new List<(double Start, double End)>();
        foreach (var range in ranges)
        {
            var start = Math.Max(0, range.Start - padding);
            var end = Math.Min(totalDuration, range.End + padding);
            if (padded.Count > 0 && start <= padded[^1].End)
            {
                padded[^1] = (padded[^1].Start, end);
            }
            else
            {
                padded.Add((start, end));
            }
        }
        return padded.Select(range => new AudioPiece(range.Start, range.End - range.Start)).ToList();
    }

    /// <summary>Packs pieces into chunks of at most <paramref name="chunkLength"/> seconds (Groq's 25 MB limit).</summary>
    public static IReadOnlyList<SpeechChunk> Chunks(IReadOnlyList<AudioPiece> pieces, double chunkLength = 600, double minimumTail = 2)
    {
        if (chunkLength <= 0)
        {
            return [];
        }
        var chunks = new List<List<AudioPiece>>();
        var current = new List<AudioPiece>();
        var currentDuration = 0.0;
        void Close()
        {
            if (current.Count > 0)
            {
                chunks.Add(current);
            }
            current = [];
            currentDuration = 0;
        }
        foreach (var piece in pieces.Where(piece => piece.Duration > 0))
        {
            if (currentDuration + piece.Duration > chunkLength && piece.Duration <= chunkLength)
            {
                Close();
            }
            var rest = piece;
            while (currentDuration + rest.Duration > chunkLength)
            {
                var fits = Math.Max(0, chunkLength - currentDuration);
                if (rest.Duration - fits < minimumTail)
                {
                    break;
                }
                if (fits > 0)
                {
                    current.Add(new AudioPiece(rest.Start, fits));
                }
                Close();
                rest = new AudioPiece(rest.Start + fits, rest.Duration - fits);
            }
            if (rest.Duration > 0)
            {
                current.Add(rest);
                currentDuration += rest.Duration;
            }
        }
        Close();
        return chunks.Select((list, index) => new SpeechChunk(index, list)).ToList();
    }
}

/// <summary>Turns per-chunk Whisper output of both tracks into one speaker-labelled timeline.</summary>
public static class TranscriptAssembler
{
    public static IReadOnlyList<TranscriptSegment> Segments(IEnumerable<RawSegment> raw, SpeechChunk chunk, Speaker speaker) =>
        SegmentFilter.Clean(raw).Select(segment =>
        {
            var start = chunk.TrackTime(segment.Start);
            var end = Math.Min(chunk.TrackTime(segment.End, isEnd: true), start + Math.Max(0, segment.End - segment.Start));
            return new TranscriptSegment(speaker, start, end, segment.Text);
        }).ToList();

    /// <summary>Merges both speakers sorted by start (ties: them first, stable), then removes echoes.</summary>
    public static IReadOnlyList<TranscriptSegment> Assemble(IEnumerable<TranscriptSegment> me, IEnumerable<TranscriptSegment> them)
    {
        var merged = them.Concat(me)
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.Speaker == Speaker.Them ? 0 : 1)
            .ToList();
        return EchoDeduplicator.Dedupe(merged);
    }
}

/// <summary>Renders transcripcion.md and transcripcion.json.</summary>
public static class TranscriptRenderer
{
    internal const double TurnJoinGap = 2.0;
    internal const string EmptyTranscriptLine = "_Sin transcripción (no se detectó voz)._";

    public static string Markdown(IReadOnlyList<TranscriptSegment> segments, MeetingMetadata metadata, TimeZoneInfo? timeZone = null)
    {
        var title = metadata.Title is { } raw && SingleLine(raw) is { Length: > 0 } clean ? clean : metadata.AppName;
        var started = LocalDateFormat.Format(metadata.StartedAt, "yyyy-MM-dd HH:mm", timeZone ?? TimeZoneInfo.Local);
        var lines = new List<string>
        {
            $"# {title} — {started}",
            "",
            $"- App: {metadata.AppName}",
            $"- Duración: {(metadata.DurationSeconds is { } seconds ? Timestamp(seconds) : "desconocida")}",
        };
        if (metadata.HasVideo)
        {
            lines.Add($"- Vídeo: {MeetingFile.Video} (desfase {SignedTenths(metadata.VideoOffsetSeconds)} s)");
        }
        lines.Add($"- Audio: {MeetingFile.MicAudio} ({SpeakerLabel.Of(Speaker.Me)}), {MeetingFile.SystemAudio} ({SpeakerLabel.Of(Speaker.Them)})");
        lines.Add("");
        lines.Add("## Transcripción");
        lines.Add("");

        var turns = SpeakerTurns(segments);
        if (turns.Count == 0)
        {
            lines.Add(EmptyTranscriptLine);
        }
        else
        {
            lines.AddRange(turns.Select(turn => $"[{Timestamp(turn.Start)}] {SpeakerLabel.Of(turn.Speaker)}: {string.Join(' ', turn.Texts)}"));
        }
        return string.Join('\n', lines) + "\n";
    }

    public static string Json(IReadOnlyList<TranscriptSegment> segments, MeetingMetadata metadata) =>
        MinutesJson.Serialize(new TranscriptDocument(metadata, segments));

    /// <summary>"HH:MM:SS" (hours not wrapped), floor of seconds, negative and non-finite as zero.</summary>
    public static string Timestamp(double seconds)
    {
        var total = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Floor(Math.Min(seconds, int.MaxValue)) : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}");
    }

    private sealed record TranscriptDocument(
        [property: JsonPropertyName("meeting")] MeetingMetadata Meeting,
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments);

    private sealed class Turn(Speaker speaker, double start, double end, string text)
    {
        public Speaker Speaker { get; } = speaker;
        public double Start { get; } = start;
        public double End { get; set; } = end;
        public List<string> Texts { get; } = [text];
    }

    private static List<Turn> SpeakerTurns(IEnumerable<TranscriptSegment> segments)
    {
        var turns = new List<Turn>();
        foreach (var segment in segments)
        {
            var text = SingleLine(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }
            if (turns.Count > 0 && turns[^1].Speaker == segment.Speaker && segment.Start - turns[^1].End <= TurnJoinGap)
            {
                turns[^1].Texts.Add(text);
                turns[^1].End = segment.End;
            }
            else
            {
                turns.Add(new Turn(segment.Speaker, segment.Start, segment.End, text));
            }
        }
        return turns;
    }

    private static string SingleLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SignedTenths(double? seconds)
    {
        var tenths = Math.Round((seconds ?? 0) * 10, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(tenths) || tenths == 0)
        {
            return "+0.0";
        }
        return (tenths / 10).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
    }
}
