using System.Net;
using System.Text;
using Minutes.Core;

namespace Minutes.Core.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("minutes-tests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class AudioTests
{
    private static float[] Tone(int frames, float amplitude) =>
        Enumerable.Range(0, frames).Select(i => amplitude * MathF.Sin(i * 0.3f)).ToArray();

    [Fact]
    public void WavRoundTripsLevelsAndRanges()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "a.wav");
        using (var writer = new WavWriter(path, 16_000))
        {
            writer.WriteSilence(16_000);
            writer.Write(Tone(16_000, 0.5f));
        }
        var wav = WavFile.Open(path);
        Assert.Equal(2.0, wav.Duration, 3);
        var levels = wav.WindowLevelsDb();
        Assert.Equal(4, levels.Count);
        Assert.Equal(WavFile.SilenceDb, levels[0]);
        Assert.InRange(levels[3], -10, -5);
        Assert.Equal(WavFile.SilenceDb, wav.PeakWindowLevelDb(0, 1));
        Assert.InRange(wav.PeakWindowLevelDb(0.5, 1), -10, -5);

        var slice = System.IO.Path.Combine(temp.Path, "b.wav");
        wav.WriteRanges(slice, [(0.5, 0.25), (1.5, 9)]);
        Assert.Equal(0.75, WavFile.Open(slice).Duration, 3);
    }

    [Fact]
    public void ReadsAWavLeftOpenByACrash()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "crash.wav");
        var writer = new WavWriter(path, 16_000);
        writer.Write(Tone(8_000, 0.5f));
        // Header still says zero bytes: copy the file without disposing the writer.
        var copy = System.IO.Path.Combine(temp.Path, "copy.wav");
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var target = File.Create(copy))
        {
            source.CopyTo(target);
        }
        writer.Dispose();
        Assert.Equal(0.5, WavFile.Open(copy).Duration, 3);
    }

    [Fact]
    public void AlignedWriterFillsCaptureGapsAndPadsTheEnd()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "ellos.wav");
        using (var writer = new AlignedTrackWriter(path))
        {
            writer.Append(Tone(1_600, 0.5f), 0.1);   // 0.0-0.1 s
            writer.Append(Tone(1_600, 0.5f), 2.1);   // loopback silent until 2.0 s
            writer.Append(Tone(1_600, 0.5f), 2.21);  // 10 ms jitter: no padding
            writer.Finish(3.0);
        }
        var wav = WavFile.Open(path);
        Assert.Equal(3.0, wav.Duration, 2);
        Assert.Equal(WavFile.SilenceDb, wav.PeakWindowLevelDb(0.5, 1.0));
        Assert.True(wav.PeakWindowLevelDb(2.0, 0.2) > -20);
    }

    [Fact]
    public void ResamplerKeepsDurationAndToneLevel()
    {
        var resampler = new StreamingResampler(48_000, 16_000);
        var output = new List<float>();
        var tone = Enumerable.Range(0, 48_000).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48_000)).ToArray();
        for (var i = 0; i < tone.Length; i += 480)
        {
            output.AddRange(resampler.Process(tone.AsSpan(i, 480)));
        }
        Assert.InRange(output.Count, 15_990, 16_000);
        Assert.InRange(WavFile.LevelDb(output.Skip(1000).ToArray()), -10, -8);

        var up = new StreamingResampler(16_000, 48_000).Process(new float[1600]);
        Assert.InRange(up.Length, 4790, 4800);
    }

    [Fact]
    public void ConvertsInterleavedFormatsToMono()
    {
        var stereoFloat = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(stereoFloat, 0);
        BitConverter.GetBytes(0.25f).CopyTo(stereoFloat, 4);
        BitConverter.GetBytes(-1f).CopyTo(stereoFloat, 8);
        BitConverter.GetBytes(1f).CopyTo(stereoFloat, 12);
        Assert.Equal([0.375f, 0f], PcmConverter.ToMono(stereoFloat, new PcmFormat(48_000, 2, 32, true)));

        var mono16 = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(mono16, 0);
        BitConverter.GetBytes(short.MinValue).CopyTo(mono16, 2);
        Assert.Equal([0.5f, -1f], PcmConverter.ToMono(mono16, new PcmFormat(16_000, 1, 16, false)));
    }
}

/// <summary>Replays queued responses and records the requests it receives.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<(string Body, string? Authorization)> Requests { get; } = [];

    public FakeHandler Reply(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is { } delay)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }
            return response;
        });
        return this;
    }

    public FakeHandler Fail()
    {
        _responses.Enqueue(() => throw new HttpRequestException("offline"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((body, request.Headers.Authorization?.ToString()));
        return _responses.Count == 0 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":""}""") } : _responses.Dequeue()();
    }
}

public class GroqClientTests
{
    private const string Segments = """{"segments":[{"start":0.5,"end":2,"text":" Hola","avg_logprob":-0.2,"no_speech_prob":0.01,"compression_ratio":1.1}]}""";

    private static (GroqClient Client, FakeHandler Handler, List<double> Waits, string File) Make(TempDirectory temp, string key = " gsk_secret ")
    {
        var handler = new FakeHandler();
        var waits = new List<double>();
        var client = new GroqClient(key, new HttpClient(handler), sleeper: (seconds, _) => { waits.Add(seconds); return Task.CompletedTask; });
        var file = System.IO.Path.Combine(temp.Path, "chunk.wav");
        File.WriteAllBytes(file, [1, 2, 3]);
        return (client, handler, waits, file);
    }

    [Fact]
    public async Task SendsTheMultipartFormAndParsesSegments()
    {
        using var temp = new TempDirectory();
        var (client, handler, _, file) = Make(temp);
        handler.Reply(HttpStatusCode.OK, Segments);
        var segments = await client.TranscribeAsync(file, language: "es");
        Assert.Equal([new RawSegment(0.5, 2, " Hola", -0.2, 0.01, 1.1)], segments);
        var (body, authorization) = handler.Requests.Single();
        Assert.Equal("Bearer gsk_secret", authorization);
        foreach (var field in new[] { "name=file; filename=chunk.wav", "audio/wav", "whisper-large-v3", "verbose_json", "timestamp_granularities[]", "name=language" })
        {
            Assert.Contains(field, body);
        }
    }

    [Fact]
    public async Task WaitsOnRateLimitsAndBacksOffOnServerAndNetworkErrors()
    {
        using var temp = new TempDirectory();
        var (client, handler, waits, file) = Make(temp);
        handler
            .Reply(HttpStatusCode.TooManyRequests, """{"error":{"message":"Please try again in 1m12.5s"}}""", TimeSpan.FromSeconds(30))
            .Reply(HttpStatusCode.InternalServerError, "oops")
            .Fail()
            .Reply(HttpStatusCode.OK, Segments);
        Assert.Single(await client.TranscribeAsync(file));
        Assert.Equal([72.5, 2, 4], waits);
    }

    [Fact]
    public async Task ThrowsClientErrorsWithTheKeyRedacted()
    {
        using var temp = new TempDirectory();
        var (client, handler, waits, file) = Make(temp);
        handler.Reply(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key gsk_secret"}}""");
        var error = await Assert.ThrowsAsync<GroqHttpException>(() => client.TranscribeAsync(file));
        Assert.Equal(401, error.Status);
        Assert.Equal("Invalid API Key [redacted]", error.ServerMessage);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task MissingKeyFailsBeforeAnyRequest()
    {
        using var temp = new TempDirectory();
        var (client, handler, _, file) = Make(temp, "  ");
        await Assert.ThrowsAsync<GroqMissingApiKeyException>(() => client.TranscribeAsync(file));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ParsesTextOnlyResponsesAndRejectsOthers()
    {
        Assert.Equal([new RawSegment(0, 0, "Hola")], GroqClient.ParseSegments(Encoding.UTF8.GetBytes("""{"text":"Hola"}""")));
        Assert.Empty(GroqClient.ParseSegments(Encoding.UTF8.GetBytes("""{"text":"  "}""")));
        Assert.Throws<GroqInvalidResponseException>(() => GroqClient.ParseSegments(Encoding.UTF8.GetBytes("[1]")));
        Assert.Throws<GroqInvalidResponseException>(() => GroqClient.ParseSegments(Encoding.UTF8.GetBytes("nope")));
    }

    [Theory]
    [InlineData("try again in 345ms", 0.345)]
    [InlineData("Please try again in 1h2m3.5s.", 3723.5)]
    public void ReadsNamedWaits(string message, double seconds) => Assert.Equal(seconds, GroqClient.WaitNamed(message)!.Value, 6);
}

public class TranscriptionPipelineTests
{
    private sealed class CopyExporter : IFinalAudioExporter
    {
        public void Export(string rawWav, string destinationM4a) => File.Copy(rawWav, destinationM4a, overwrite: true);
        public double Duration(string path) => WavFile.Open(path).Duration;
    }

    private static string MakeMeeting(string root)
    {
        var folder = System.IO.Path.Combine(root, "2026-10-20 10.30 Zoom");
        Directory.CreateDirectory(folder);
        MetadataStore.Write(new MeetingMetadata { AppName = "Zoom", StartedAt = DateTimeOffset.UtcNow, DurationSeconds = 4 }, folder);
        foreach (var (name, speechAt) in new[] { (MeetingFile.MicRaw, 0), (MeetingFile.SystemRaw, 2) })
        {
            using var writer = new WavWriter(System.IO.Path.Combine(folder, name), 16_000);
            writer.WriteSilence(speechAt * 16_000);
            writer.Write(Enumerable.Range(0, 32_000).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray());
            writer.WriteSilence((2 - speechAt) * 16_000);
        }
        return folder;
    }

    private sealed class BrokenExporter : IFinalAudioExporter
    {
        public void Export(string rawWav, string destinationM4a)
        {
            File.WriteAllText(destinationM4a, "partial");
            throw new InvalidOperationException("no AAC encoder");
        }

        public double Duration(string path) => 0;
    }

    private static TranscriptionPipeline Make(string root, FakeHandler handler, string? key, List<string?> failures, IFinalAudioExporter? exporter = null) => new(
        root,
        () => key,
        () => "es",
        () => "Mobilia",
        apiKey => new GroqClient(apiKey, new HttpClient(handler), sleeper: (_, _) => Task.CompletedTask),
        exporter ?? new CopyExporter(),
        _ => { },
        (_, failure) => failures.Add(failure));

    [Fact]
    public async Task TranscribesBothTracksWritesFilesAndDeletesRawAudio()
    {
        using var temp = new TempDirectory();
        var folder = MakeMeeting(temp.Path);
        var handler = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Hola, soy de Movilia"}]}""")
            .Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Encantado"}]}""");
        var failures = new List<string?>();
        var pipeline = Make(temp.Path, handler, "gsk_x", failures);
        pipeline.ResumePending();
        await pipeline.Idle;

        Assert.Equal([null], failures);
        var markdown = File.ReadAllText(System.IO.Path.Combine(folder, MeetingFile.TranscriptMarkdown));
        Assert.True(markdown.Contains("] Yo: Hola, soy de Mobilia"), markdown);
        Assert.Contains("[00:00:01] Ellos: Encantado", markdown);
        Assert.True(File.Exists(System.IO.Path.Combine(folder, MeetingFile.MicAudio)));
        Assert.False(File.Exists(System.IO.Path.Combine(folder, MeetingFile.MicRaw)));
        Assert.False(Directory.Exists(System.IO.Path.Combine(folder, ChunkCache.DirectoryName)));
        Assert.Equal(TranscriptionState.Done, MetadataStore.Read(folder).Transcription.State);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FailureKeepsRawAudioAndRetryOnlySendsMissingChunks()
    {
        using var temp = new TempDirectory();
        var folder = MakeMeeting(temp.Path);
        var handler = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Hola"}]}""")
            .Reply(HttpStatusCode.BadRequest, """{"error":{"message":"bad audio"}}""");
        var failures = new List<string?>();
        var pipeline = Make(temp.Path, handler, "gsk_x", failures);
        pipeline.Enqueue(folder);
        await pipeline.Idle;

        Assert.Contains("bad audio", failures.Single());
        var metadata = MetadataStore.Read(folder);
        Assert.Equal(TranscriptionState.Failed, metadata.Transcription.State);
        Assert.True(File.Exists(System.IO.Path.Combine(folder, MeetingFile.MicRaw)));

        handler.Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Adiós"}]}""");
        pipeline.RetryFailed();
        await pipeline.Idle;
        Assert.Null(failures[^1]);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task MissingKeyFailsWithASpanishMessage()
    {
        using var temp = new TempDirectory();
        MakeMeeting(temp.Path);
        var failures = new List<string?>();
        var pipeline = Make(temp.Path, new FakeHandler(), null, failures);
        pipeline.ResumePending();
        await pipeline.Idle;
        Assert.StartsWith("Falta la clave de Groq", failures.Single());
    }

    [Fact]
    public async Task AFailingAudioExportStillWritesTheTranscriptAndKeepsTheRawTracks()
    {
        using var temp = new TempDirectory();
        var folder = MakeMeeting(temp.Path);
        var handler = new FakeHandler()
            .Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Hola"}]}""")
            .Reply(HttpStatusCode.OK, """{"segments":[{"start":0.2,"end":1.5,"text":"Adiós"}]}""");
        var failures = new List<string?>();
        var pipeline = Make(temp.Path, handler, "gsk_x", failures, new BrokenExporter());
        pipeline.Enqueue(folder);
        await pipeline.Idle;

        Assert.Equal([null], failures);
        Assert.Equal(TranscriptionState.Done, MetadataStore.Read(folder).Transcription.State);
        Assert.True(File.Exists(System.IO.Path.Combine(folder, MeetingFile.MicRaw)));
        Assert.False(File.Exists(System.IO.Path.Combine(folder, MeetingFile.MicAudio)));
        Assert.Contains("- Audio: yo.wav (Yo), ellos.wav (Ellos)", File.ReadAllText(System.IO.Path.Combine(folder, MeetingFile.TranscriptMarkdown)));
    }
}

public class TrackMixerTests
{
    private static float[] Ones(int count, float value = 0.25f) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void MixesSourcesOnOneTimelineAndFillsSilence()
    {
        var mixer = new TrackMixer(sampleRate: 100);
        var mic = mixer.AddSource();
        var headset = mixer.AddSource();
        mixer.Append(mic, Ones(10), elapsedAtEnd: 0.1);      // samples 0-9
        mixer.Append(headset, Ones(10), elapsedAtEnd: 0.15); // samples 5-14
        var mixed = mixer.Take(upToElapsed: 0.2);
        Assert.Equal(20, mixed.Length);
        Assert.Equal(0.25f, mixed[0]);
        Assert.Equal(0.5f, mixed[7]);
        Assert.Equal(0.25f, mixed[12]);
        Assert.Equal(0f, mixed[17]);
        Assert.Equal(20, mixer.Taken);
    }

    [Fact]
    public void KeepsEachSourceContiguousUnderJitterAndJumpsRealGaps()
    {
        var mixer = new TrackMixer(sampleRate: 100);
        var source = mixer.AddSource();
        mixer.Append(source, Ones(10), 0.10);
        mixer.Append(source, Ones(10), 0.22); // 2 samples of jitter: stays contiguous (10-19)
        mixer.Append(source, Ones(10), 0.60); // real gap: placed at 50-59
        var mixed = mixer.Take(0.6);
        Assert.All(mixed[..20], sample => Assert.Equal(0.25f, sample));
        Assert.All(mixed[20..50], sample => Assert.Equal(0f, sample));
        Assert.All(mixed[50..60], sample => Assert.Equal(0.25f, sample));
    }

    [Fact]
    public void DropsLateSamplesAndClipsTheSum()
    {
        var mixer = new TrackMixer(sampleRate: 100);
        var a = mixer.AddSource();
        var b = mixer.AddSource();
        Assert.Equal(10, mixer.Take(0.1).Length);
        mixer.Append(a, Ones(20, 0.8f), 0.15);  // samples -5..14: the first 5 are already written
        mixer.Append(b, Ones(5, 0.8f), 0.15);   // samples 10-14
        mixer.RemoveSource(b);
        var mixed = mixer.Take(0.15);
        Assert.Equal(5, mixed.Length);
        Assert.All(mixed, sample => Assert.Equal(1f, sample));
    }
}

public class TrackMixerDriftTests
{
    [Fact]
    public void ReanchorsASourceThatRunsAheadOfTheClock()
    {
        var mixer = new TrackMixer(sampleRate: 100);
        var source = mixer.AddSource();
        mixer.Append(source, Enumerable.Repeat(0.25f, 50).ToArray(), 0.5);  // 0-49
        mixer.Append(source, Enumerable.Repeat(0.25f, 50).ToArray(), 0.8);  // device ran fast: expected 30-79, cursor at 50
        var mixed = mixer.Take(0.8);
        Assert.Equal(80, mixed.Length);
        Assert.All(mixed[..30], sample => Assert.Equal(0.25f, sample));
        Assert.All(mixed[30..50], sample => Assert.Equal(0.5f, sample));
        Assert.All(mixed[50..80], sample => Assert.Equal(0.25f, sample));
    }
}

public class DeviceSelectionTests
{
    private const uint Own = 1, Teams = 10, TeamsWebView = 11, Krisp = 20, Chrome = 30;
    private static readonly HashSet<uint> TeamsTree = [Teams, TeamsWebView];

    private static IReadOnlyList<string> Choose(
        AudioSessionInfo[] sessions, HashSet<uint>? app = null, string[]? recorded = null, string[]? defaults = null, bool alwaysDefaults = false) =>
        DeviceSelection.Choose(sessions, app ?? [], Own, recorded ?? [], defaults ?? ["default"], alwaysDefaults);

    [Fact]
    public void TheMeetingAppsActiveDevicesWin()
    {
        var sessions = new[]
        {
            new AudioSessionInfo("krisp-virtual", TeamsWebView, IsActive: true),
            new AudioSessionInfo("headset", Krisp, IsActive: true),
            new AudioSessionInfo("webcam", Chrome, IsActive: true),
            new AudioSessionInfo("laptop", Teams, IsActive: false),
        };
        Assert.Equal(["krisp-virtual"], Choose(sessions, TeamsTree, alwaysDefaults: true));
    }

    [Fact]
    public void WhileTheAppIsMutedOneInactiveDeviceIsKept()
    {
        var sessions = new[]
        {
            new AudioSessionInfo("headset", Teams, IsActive: false),
            new AudioSessionInfo("laptop", TeamsWebView, IsActive: false),
            new AudioSessionInfo("webcam", Chrome, IsActive: true),
        };
        Assert.Equal(["laptop"], Choose(sessions, TeamsTree, recorded: ["laptop"]));
        Assert.Equal(["headset"], Choose(sessions, TeamsTree, defaults: ["headset"]));
        Assert.Equal(["headset"], Choose(sessions, TeamsTree, defaults: ["other"]));
    }

    [Fact]
    public void WithoutTheAppOtherAppsActiveDevicesCountAndOwnSessionsDoNot()
    {
        var sessions = new[]
        {
            new AudioSessionInfo("webcam", Chrome, IsActive: true),
            new AudioSessionInfo("headset", Own, IsActive: true),
            new AudioSessionInfo("laptop", Chrome, IsActive: false),
        };
        Assert.Equal(["webcam"], Choose(sessions));
        Assert.Equal(["webcam", "default", "comms"], Choose(sessions, defaults: ["default", "comms"], alwaysDefaults: true));
    }

    [Fact]
    public void NothingInUseFallsBackToTheDefaults()
    {
        Assert.Equal(["default"], Choose([new AudioSessionInfo("headset", Own, IsActive: true)]));
        Assert.Empty(Choose([], defaults: []));
    }
}
