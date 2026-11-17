using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minutes.Core;

/// <summary>Minimal log sink; the app routes it to a file. Never log secrets or transcript text.</summary>
public static class MinutesLog
{
    public static Action<string, string> Sink { get; set; } = (_, _) => { };

    public static void Info(string message) => Sink("INFO", message);
    public static void Error(string message) => Sink("ERROR", message);
}

/// <summary>Turns a raw WAV track into the final m4a (Media Foundation on Windows; a fake in tests).</summary>
public interface IFinalAudioExporter
{
    void Export(string rawWav, string destinationM4a);
    double Duration(string path);
}

public sealed record PipelineStatus(int Queued, string? Active, int Failed);

/// <summary>
/// Transcribes finished recordings one at a time with Groq and writes the transcript files.
/// Raw WAV tracks are converted to yo.m4a / ellos.m4a and deleted only after the transcript is written,
/// so a failed meeting can always be retried.
/// </summary>
public sealed class TranscriptionPipeline
{
    public const int ChunkConcurrency = 3;
    public const string AutomaticLanguage = "auto";

    /// <summary>
    /// Whisper invents text ("Gracias.") over silence; calibrated on the Mac: invented segments peaked at
    /// -57…-65 dB, faint real speech at -43 dB. Audio quieter than this is not uploaded either.
    /// </summary>
    public const double SegmentSilenceThresholdDb = -50;

    private static readonly (string Raw, string Final)[] Tracks =
        [(MeetingFile.MicRaw, MeetingFile.MicAudio), (MeetingFile.SystemRaw, MeetingFile.SystemAudio)];

    private readonly string _root;
    private readonly Func<string?> _apiKey;
    private readonly Func<string?> _language;
    private readonly Func<string> _vocabulary;
    private readonly Func<string, GroqClient> _makeClient;
    private readonly IFinalAudioExporter _exporter;
    private readonly Action<PipelineStatus> _onStatus;
    private readonly Action<string, string?> _onFinished;
    private readonly object _lock = new();
    private readonly List<string> _queue = [];
    private bool _isDraining;
    private string? _active;
    private int _failed;

    /// <param name="language">Whisper language code, or null to let Whisper detect it.</param>
    /// <param name="onFinished">Folder and failure message (null on success).</param>
    public TranscriptionPipeline(
        string root,
        Func<string?> apiKey,
        Func<string?> language,
        Func<string> vocabulary,
        Func<string, GroqClient> makeClient,
        IFinalAudioExporter exporter,
        Action<PipelineStatus> onStatus,
        Action<string, string?> onFinished)
    {
        _root = root;
        _apiKey = apiKey;
        _language = language;
        _vocabulary = vocabulary;
        _makeClient = makeClient;
        _exporter = exporter;
        _onStatus = onStatus;
        _onFinished = onFinished;
    }

    /// <summary>Completes when the queue is empty (used by tests and the smoke test).</summary>
    public Task Idle => _idle.Task;
    private TaskCompletionSource _idle = NewIdle(completed: true);

    public void Enqueue(string folder)
    {
        lock (_lock)
        {
            if (_queue.Contains(folder) || _active == folder)
            {
                return;
            }
            _queue.Add(folder);
            if (!_isDraining)
            {
                _isDraining = true;
                _idle = NewIdle(completed: false);
                _ = Task.Run(DrainAsync);
            }
        }
        Publish();
    }

    /// <summary>Re-queues meetings left pending or interrupted by a previous run.</summary>
    public void ResumePending()
    {
        foreach (var folder in MeetingFolders())
        {
            if (MetadataStore.TryRead(folder, out var metadata)
                && metadata.Transcription.State is TranscriptionState.Pending or TranscriptionState.Transcribing)
            {
                Enqueue(folder);
            }
        }
        RefreshFailedCount();
    }

    public void RetryFailed()
    {
        foreach (var folder in MeetingFolders())
        {
            if (MetadataStore.TryRead(folder, out var metadata) && metadata.Transcription.State == TranscriptionState.Failed)
            {
                Enqueue(folder);
            }
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            string folder;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _isDraining = false;
                    _idle.TrySetResult();
                    break;
                }
                folder = _queue[0];
                _queue.RemoveAt(0);
                _active = folder;
            }
            Publish();
            var failure = await ProcessAsync(folder).ConfigureAwait(false);
            lock (_lock)
            {
                _active = null;
            }
            RefreshFailedCount();
            _onFinished(folder, failure);
        }
        Publish();
    }

    /// <summary>Returns null on success or the error message on failure (also stored in meta.json).</summary>
    private async Task<string?> ProcessAsync(string folder)
    {
        if (!MetadataStore.TryRead(folder, out var metadata))
        {
            return $"No se puede leer {MeetingFile.Metadata} en {Path.GetFileName(folder)}.";
        }
        var language = _language();
        var storedLanguage = language ?? AutomaticLanguage;
        var vocabulary = new TranscriptVocabulary(_vocabulary());
        metadata = metadata with { Transcription = new(TranscriptionState.Transcribing, GroqClient.DefaultModel, storedLanguage) };
        TryWrite(metadata, folder);

        var workDirectory = Path.Combine(Path.GetTempPath(), "Minutes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var apiKey = _apiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new PipelineException("Falta la clave de Groq. Añádela en Ajustes (icono de Minutes en la bandeja).");
            }
            Directory.CreateDirectory(workDirectory);
            var client = _makeClient(apiKey);
            var cache = new ChunkCache(folder, storedLanguage);
            var me = await TranscribeTrackAsync(RawPath(folder, MeetingFile.MicRaw), Speaker.Me, language, client, workDirectory, cache).ConfigureAwait(false);
            var them = await TranscribeTrackAsync(RawPath(folder, MeetingFile.SystemRaw), Speaker.Them, language, client, workDirectory, cache).ConfigureAwait(false);
            var segments = vocabulary.Correct(TranscriptAssembler.Assemble(me, them));

            // Raw tracks are deleted last: any failure before this point leaves everything retryable.
            ExportVerifiedAudio(folder);
            metadata = metadata with
            {
                Transcription = new(TranscriptionState.Done, GroqClient.DefaultModel, storedLanguage, completedAt: DateTimeOffset.UtcNow),
            };
            AtomicFile.WriteAllText(Path.Combine(folder, MeetingFile.TranscriptMarkdown), TranscriptRenderer.Markdown(segments, metadata));
            AtomicFile.WriteAllText(Path.Combine(folder, MeetingFile.TranscriptJson), TranscriptRenderer.Json(segments, metadata));
            MetadataStore.Write(metadata, folder);
            RemoveRawAudio(folder);
            cache.Remove();
            MinutesLog.Info($"Transcribed a meeting: {segments.Count} segments");
            return null;
        }
        catch (Exception error)
        {
            var message = error.Message;
            metadata = metadata with
            {
                Transcription = new(TranscriptionState.Failed, GroqClient.DefaultModel, storedLanguage, error: message),
            };
            TryWrite(metadata, folder);
            MinutesLog.Error($"Transcription failed: {error.GetType().Name}: {message}");
            return message;
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static string? RawPath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }

    private static async Task<IReadOnlyList<TranscriptSegment>> TranscribeTrackAsync(
        string? path, Speaker speaker, string? language, GroqClient client, string workDirectory, ChunkCache cache)
    {
        if (path is null)
        {
            return [];
        }
        var wav = WavFile.Open(path);
        var pieces = SpeechPlan.Pieces(wav.WindowLevelsDb(), WavFile.LevelWindowDuration, wav.Duration, SegmentSilenceThresholdDb);
        var chunks = SpeechPlan.Chunks(pieces);
        var results = new IReadOnlyList<TranscriptSegment>[chunks.Count];
        using var gate = new SemaphoreSlim(ChunkConcurrency);
        var tasks = chunks.Select(async chunk =>
        {
            if (cache.Segments(speaker, chunk) is { } cached)
            {
                results[chunk.Index] = cached;
                return;
            }
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var segments = await TranscribeChunkAsync(chunk, wav, speaker, language, client, workDirectory).ConfigureAwait(false);
                cache.Store(segments, speaker, chunk);
                results[chunk.Index] = segments;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(segments => segments ?? []).ToList();
    }

    private static async Task<IReadOnlyList<TranscriptSegment>> TranscribeChunkAsync(
        SpeechChunk chunk, WavFile wav, Speaker speaker, string? language, GroqClient client, string workDirectory)
    {
        var chunkPath = Path.Combine(workDirectory, $"{SpeakerLabel.Of(speaker)}-{chunk.Index}.wav");
        wav.WriteRanges(chunkPath, chunk.Pieces.Select(piece => (piece.Start, piece.Duration)));
        try
        {
            var raw = await client.TranscribeAsync(chunkPath, language: language).ConfigureAwait(false);
            var segments = TranscriptAssembler.Segments(raw, chunk, speaker);
            // A level that cannot be read keeps the segment: losing speech is worse than keeping a guess.
            return segments.Where(segment => IsAudible(segment, wav)).ToList();
        }
        finally
        {
            File.Delete(chunkPath);
        }
    }

    private static bool IsAudible(TranscriptSegment segment, WavFile wav)
    {
        try
        {
            var duration = Math.Max(segment.End - segment.Start, 0.5);
            return wav.PeakWindowLevelDb(segment.Start, duration) > SegmentSilenceThresholdDb;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Converts every raw track to its final m4a and checks the durations match. Deletes nothing.</summary>
    private void ExportVerifiedAudio(string folder)
    {
        foreach (var (raw, final) in Tracks)
        {
            var rawPath = Path.Combine(folder, raw);
            if (!File.Exists(rawPath))
            {
                continue;
            }
            var finalPath = Path.Combine(folder, final);
            _exporter.Export(rawPath, finalPath);
            var rawDuration = WavFile.Open(rawPath).Duration;
            var finalDuration = _exporter.Duration(finalPath);
            if (Math.Abs(rawDuration - finalDuration) >= 1.0)
            {
                throw new PipelineException($"El archivo {final} exportado no coincide con la duración grabada; se ha conservado el audio original.");
            }
        }
    }

    private static void RemoveRawAudio(string folder)
    {
        foreach (var (raw, _) in Tracks)
        {
            try
            {
                File.Delete(Path.Combine(folder, raw));
            }
            catch (Exception)
            {
            }
        }
    }

    private static void TryWrite(MeetingMetadata metadata, string folder)
    {
        try
        {
            MetadataStore.Write(metadata, folder);
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Could not write meta.json: {error.GetType().Name}");
        }
    }

    private IEnumerable<string> MeetingFolders() =>
        Directory.Exists(_root) ? Directory.GetDirectories(_root) : [];

    private void RefreshFailedCount()
    {
        var failed = MeetingFolders().Count(folder =>
            MetadataStore.TryRead(folder, out var metadata) && metadata.Transcription.State == TranscriptionState.Failed);
        lock (_lock)
        {
            _failed = failed;
        }
        Publish();
    }

    private void Publish()
    {
        PipelineStatus status;
        lock (_lock)
        {
            status = new PipelineStatus(_queue.Count, _active is null ? null : Path.GetFileName(_active), _failed);
        }
        _onStatus(status);
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            source.SetResult();
        }
        return source;
    }
}

public sealed class PipelineException(string message) : Exception(message);

/// <summary>
/// Transcribed chunks kept in the meeting folder until the transcript is written, so a retry only sends
/// the audio still missing.
/// </summary>
public sealed class ChunkCache(string folder, string language)
{
    public const string DirectoryName = ".transcripcion-parcial";

    public string Directory { get; } = Path.Combine(folder, DirectoryName);

    public IReadOnlyList<TranscriptSegment>? Segments(Speaker speaker, SpeechChunk chunk)
    {
        try
        {
            var path = PathFor(speaker, chunk);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(path), MinutesJson.Options) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Best effort: a chunk that cannot be cached is transcribed again on a retry.</summary>
    public void Store(IReadOnlyList<TranscriptSegment> segments, Speaker speaker, SpeechChunk chunk)
    {
        try
        {
            var info = System.IO.Directory.CreateDirectory(Directory);
            info.Attributes |= FileAttributes.Hidden;
            AtomicFile.WriteAllText(PathFor(speaker, chunk), JsonSerializer.Serialize(segments, MinutesJson.Options));
        }
        catch (Exception)
        {
        }
    }

    public void Remove()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Language and a fingerprint of every piece (to the millisecond) are part of the name.</summary>
    private string PathFor(Speaker speaker, SpeechChunk chunk)
    {
        var layout = string.Join(",", chunk.Pieces.Select(piece =>
            $"{(long)Math.Round(piece.Start * 1000, MidpointRounding.AwayFromZero)}:{(long)Math.Round(piece.Duration * 1000, MidpointRounding.AwayFromZero)}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(layout));
        var fingerprint = Convert.ToHexStringLower(digest.AsSpan(0, 8));
        return Path.Combine(Directory, $"{language}-{SpeakerLabel.Of(speaker)}-{chunk.Index}-{fingerprint}.json");
    }
}
