using Minutes.Core;

namespace Minutes.Core.Tests;

public class SegmentFilterTests
{
    [Fact]
    public void DropsSilenceLoopsHallucinationsAndBlankText()
    {
        var segments = new[]
        {
            new RawSegment(0, 1, "  Hola, ¿qué tal?  "),
            new RawSegment(1, 2, "Gracias por ver el vídeo."),
            new RawSegment(2, 3, "Subtítulos realizados por la comunidad de Amara.org"),
            new RawSegment(3, 4, "texto", AvgLogprob: -1.5, NoSpeechProb: 0.8),
            new RawSegment(4, 5, "la la la", CompressionRatio: 3),
            new RawSegment(5, 6, "..."),
            new RawSegment(6, 7, "O, o."),
            new RawSegment(7, 8, "Vale", AvgLogprob: -1.5, NoSpeechProb: 0.5),
        };
        Assert.Equal(["Hola, ¿qué tal?", "Vale"], SegmentFilter.Clean(segments).Select(segment => segment.Text));
    }

    [Fact]
    public void NormalizesCaseAccentsAndPunctuation() =>
        Assert.Equal("ano nino que tal", SegmentFilter.Normalize("¡Año,  NIÑO! ¿Qué\ttal?"));
}

public class EchoDeduplicatorTests
{
    [Fact]
    public void RemovesMicrophoneEchoesOfOverlappingRemoteSpeech()
    {
        var segments = new[]
        {
            new TranscriptSegment(Speaker.Them, 0, 4, "Vamos a revisar el presupuesto del trimestre"),
            new TranscriptSegment(Speaker.Me, 0.2, 4, "vamos a revisar el presupuesto del trimestre"),
            new TranscriptSegment(Speaker.Me, 5, 6, "Perfecto, comparto la hoja"),
        };
        Assert.Equal([segments[0], segments[2]], EchoDeduplicator.Dedupe(segments));
    }

    [Fact]
    public void ShortSegmentsNeedAnExactMatch()
    {
        var segments = new[]
        {
            new TranscriptSegment(Speaker.Them, 0, 1, "Sí."),
            new TranscriptSegment(Speaker.Me, 0, 1, "sí"),
            new TranscriptSegment(Speaker.Them, 2, 3, "Vale, sí"),
            new TranscriptSegment(Speaker.Me, 2, 3, "sí"),
        };
        Assert.Equal([segments[0], segments[2], segments[3]], EchoDeduplicator.Dedupe(segments));
    }
}

public class SpeechPlanTests
{
    [Fact]
    public void FindsSpeechWithPaddingAndBridgesShortSilences()
    {
        // 0.5 s windows: speech at 2-3 s and 4-4.5 s (bridged, gap < 3 s), then at 10-10.5 s.
        var levels = Enumerable.Repeat(-90.0, 24).ToArray();
        levels[4] = levels[5] = levels[8] = levels[20] = -20;
        var pieces = SpeechPlan.Pieces(levels, 0.5, 12);
        Assert.Equal([new AudioPiece(1, 4.5), new AudioPiece(9, 2.5)], pieces);
    }

    [Fact]
    public void ChunksSplitLongPiecesAndKeepShortTails()
    {
        var chunks = SpeechPlan.Chunks([new AudioPiece(0, 300), new AudioPiece(400, 400), new AudioPiece(900, 601)]);
        Assert.Equal(3, chunks.Count);
        Assert.Equal([new AudioPiece(0, 300)], chunks[0].Pieces);
        Assert.Equal([new AudioPiece(400, 400), new AudioPiece(900, 200)], chunks[1].Pieces);
        Assert.Equal([new AudioPiece(1100, 401)], chunks[2].Pieces);
    }

    [Fact]
    public void MapsUploadTimesBackToTheTrack()
    {
        var chunk = new SpeechChunk(0, [new AudioPiece(10, 5), new AudioPiece(30, 5)]);
        Assert.Equal(12, chunk.TrackTime(2));
        Assert.Equal(30, chunk.TrackTime(5));
        Assert.Equal(15, chunk.TrackTime(5, isEnd: true));
        Assert.Equal(35, chunk.TrackTime(99));
    }

    [Fact]
    public void AssemblerMapsTimesAndOrdersSpeakers()
    {
        var chunk = new SpeechChunk(0, [new AudioPiece(10, 5), new AudioPiece(30, 5)]);
        var me = TranscriptAssembler.Segments([new RawSegment(4, 6, "Cruza el corte")], chunk, Speaker.Me);
        Assert.Equal(new TranscriptSegment(Speaker.Me, 14, 16, "Cruza el corte"), me[0]);
        var them = new[] { new TranscriptSegment(Speaker.Them, 14, 15, "Hola") };
        Assert.Equal([Speaker.Them, Speaker.Me], TranscriptAssembler.Assemble(me, them).Select(segment => segment.Speaker));
    }
}

public class TranscriptRendererTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    [Fact]
    public void RendersTheMacMarkdownFormat()
    {
        var metadata = new MeetingMetadata
        {
            AppName = "Google Meet",
            Title = "Weekly sync",
            StartedAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero),
            DurationSeconds = 2533.9,
        };
        var segments = new[]
        {
            new TranscriptSegment(Speaker.Them, 3, 5, "Morning!"),
            new TranscriptSegment(Speaker.Them, 6, 7, "Shall we\nstart?"),
            new TranscriptSegment(Speaker.Me, 7.5, 9, "Sure."),
        };
        Assert.Equal(
            "# Weekly sync — 2026-10-20 10:30\n\n- App: Google Meet\n- Duración: 00:42:13\n- Audio: yo.m4a (Yo), ellos.m4a (Ellos)\n\n## Transcripción\n\n"
            + "[00:00:03] Ellos: Morning! Shall we start?\n[00:00:07] Yo: Sure.\n",
            TranscriptRenderer.Markdown(segments, metadata, Madrid));
    }

    [Fact]
    public void RendersEmptyTranscriptAndVideoLine()
    {
        var metadata = new MeetingMetadata { AppName = "Zoom", HasVideo = true, VideoOffsetSeconds = 0.44 };
        var markdown = TranscriptRenderer.Markdown([], metadata, Madrid);
        Assert.Contains("- Duración: desconocida", markdown);
        Assert.Contains("- Vídeo: video.mp4 (desfase +0.4 s)", markdown);
        Assert.EndsWith("_Sin transcripción (no se detectó voz)._\n", markdown);
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(-3, "00:00:00")]
    [InlineData(double.NaN, "00:00:00")]
    [InlineData(3725.9, "01:02:05")]
    [InlineData(90000, "25:00:00")]
    public void FormatsTimestamps(double seconds, string expected) => Assert.Equal(expected, TranscriptRenderer.Timestamp(seconds));

    [Fact]
    public void JsonHasMeetingAndSegmentsWithSpeakerLabels()
    {
        var json = TranscriptRenderer.Json([new TranscriptSegment(Speaker.Me, 1, 2, "Hola")], new MeetingMetadata { AppName = "Zoom" });
        Assert.Contains("\"speaker\": \"Yo\"", json);
        Assert.StartsWith("{\n  \"meeting\": {", json.Replace("\r\n", "\n"));
    }
}

public class VocabularyTests
{
    [Fact]
    public void CorrectsNamesBySoundKeepingPunctuation()
    {
        var vocabulary = new TranscriptVocabulary("Mobilia, Aitor García\nNexoBrokers");
        Assert.Equal(["Mobilia", "Aitor García", "NexoBrokers"], vocabulary.Terms);
        Assert.Equal("Hablé con Mobilia y con Aitor García-López.", vocabulary.Correct("Hablé con Movilia y con aitor garcia-López."));
        Assert.Equal("Usamos NexoBrokers.", vocabulary.Correct("Usamos nexobrokers."));
        Assert.Equal("Usamos NexoBrokers.", vocabulary.Correct("Usamos Nexobrokrs."));
    }

    [Fact]
    public void ShortTermsNeedTheSameSoundsAndLowercaseTermsKeepCase()
    {
        var vocabulary = new TranscriptVocabulary("reel; Ana");
        Assert.Equal("Reel nuevo con Ana, no con Anabel.", vocabulary.Correct("Reel nuevo con ana, no con Anabel."));
        Assert.Equal("Un Real y Ana.", vocabulary.Correct("Un Real y ana."));
    }
}

public class WhisperTokenizerTests
{
    private static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void DecodesTokensAndSkipsSpecialOnes()
    {
        var tokenizer = new WhisperTokenizer($"{B64("seg")} 0\n{B64("ún")} 1\n\n= 50256\n");
        Assert.Equal("según", tokenizer.Text([50364, 0, 1, 50400]));
        Assert.Null(tokenizer.Text([50364]));
        Assert.Null(tokenizer.Text([7]));
    }

    [Fact]
    public void RejectsMalformedLinesAndSplitCharacters()
    {
        Assert.Throws<FormatException>(() => new WhisperTokenizer("abc\n"));
        var tokenizer = new WhisperTokenizer($"{Convert.ToBase64String([0xC3])} 0\n");
        Assert.Null(tokenizer.Text([0]));
    }
}
