using System.Buffers.Binary;

namespace Minutes.Core;

/// <summary>Sample layout of a capture buffer.</summary>
public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
}

/// <summary>Converts interleaved capture buffers to mono float samples.</summary>
public static class PcmConverter
{
    public static float[] ToMono(ReadOnlySpan<byte> buffer, PcmFormat format)
    {
        if (format.BlockAlign <= 0 || format.Channels <= 0)
        {
            return [];
        }
        var frames = buffer.Length / format.BlockAlign;
        var output = new float[frames];
        var bytesPerSample = format.BitsPerSample / 8;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * bytesPerSample;
                sum += ReadSample(buffer.Slice(offset, bytesPerSample), format);
            }
            output[frame] = sum / format.Channels;
        }
        return output;
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, PcmFormat format) => (format.BitsPerSample, format.IsFloat) switch
    {
        (32, true) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (64, true) => (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        (16, false) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        (24, false) => ((bytes[0] | (bytes[1] << 8) | ((sbyte)bytes[2] << 16))) / 8388608f,
        (32, false) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        (8, false) => (bytes[0] - 128) / 128f,
        _ => 0f,
    };
}

/// <summary>
/// Streaming sample-rate converter for mono audio: a windowed-sinc low-pass when downsampling,
/// then linear interpolation. Good enough for speech sent to Whisper.
/// </summary>
public sealed class StreamingResampler
{
    private const int Taps = 63;

    private readonly double _step;
    private readonly float[]? _filter;
    private readonly float[] _history = new float[Taps - 1];
    private readonly List<float> _pending = [];
    private double _position;

    public int InputRate { get; }
    public int OutputRate { get; }

    public StreamingResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Sample rates must be positive.");
        }
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        if (outputRate < inputRate)
        {
            _filter = LowPass(0.45 * outputRate / inputRate);
        }
    }

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (InputRate == OutputRate)
        {
            return input.ToArray();
        }
        foreach (var sample in input)
        {
            _pending.Add(_filter is null ? sample : Filter(sample));
        }
        var output = new List<float>((int)(input.Length / _step) + 2);
        while (_position + 1 < _pending.Count)
        {
            var index = (int)_position;
            var fraction = (float)(_position - index);
            output.Add(_pending[index] + (_pending[index + 1] - _pending[index]) * fraction);
            _position += _step;
        }
        var consumed = Math.Min((int)_position, _pending.Count);
        _pending.RemoveRange(0, consumed);
        _position -= consumed;
        return output.ToArray();
    }

    private float Filter(float sample)
    {
        var filter = _filter!;
        var sum = filter[0] * sample;
        for (var k = 1; k < Taps; k++)
        {
            sum += filter[k] * _history[k - 1];
        }
        Array.Copy(_history, 0, _history, 1, _history.Length - 1);
        _history[0] = sample;
        return sum;
    }

    /// <summary>Hamming-windowed sinc with unity gain; cutoff as a fraction of the input rate.</summary>
    private static float[] LowPass(double cutoff)
    {
        var filter = new double[Taps];
        var middle = (Taps - 1) / 2.0;
        for (var n = 0; n < Taps; n++)
        {
            var x = n - middle;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (Taps - 1));
            filter[n] = sinc * window;
        }
        var sum = filter.Sum();
        return filter.Select(value => (float)(value / sum)).ToArray();
    }
}

/// <summary>A 16-bit PCM WAV file being written; the header is kept valid so a crash leaves a readable file.</summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 44;
    private readonly FileStream _stream;
    private readonly object _lock = new();
    private long _dataBytes;
    private DateTime _lastHeaderUpdate = DateTime.UtcNow;

    public int SampleRate { get; }
    public long FramesWritten => _dataBytes / 2;

    public WavWriter(string path, int sampleRate)
    {
        SampleRate = sampleRate;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        WriteHeader();
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(clamped * 32767f));
        }
        lock (_lock)
        {
            _stream.Seek(HeaderSize + _dataBytes, SeekOrigin.Begin);
            _stream.Write(bytes);
            _dataBytes += bytes.Length;
            if (DateTime.UtcNow - _lastHeaderUpdate > TimeSpan.FromSeconds(5))
            {
                WriteHeader();
                _stream.Flush();
                _lastHeaderUpdate = DateTime.UtcNow;
            }
        }
    }

    public void WriteSilence(long frames)
    {
        const int block = 16_000;
        while (frames > 0)
        {
            var count = (int)Math.Min(frames, block);
            Write(new float[count]);
            frames -= count;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            WriteHeader();
            _stream.Flush();
            _stream.Dispose();
        }
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)Math.Min(uint.MaxValue, 36 + _dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(SampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)Math.Min(uint.MaxValue, _dataBytes));
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(header);
    }
}

/// <summary>
/// Writes a track aligned to a shared session clock: gaps in the capture (WASAPI loopback sends nothing
/// while no sound plays) are filled with silence, so both tracks share one timeline.
/// </summary>
public sealed class AlignedTrackWriter : IDisposable
{
    /// <summary>Gaps shorter than this are capture jitter, not silence.</summary>
    public const double GapTolerance = 0.1;

    private readonly WavWriter _writer;

    public AlignedTrackWriter(string path, int sampleRate = 16_000) => _writer = new WavWriter(path, sampleRate);

    public long FramesWritten => _writer.FramesWritten;

    /// <param name="samples">Mono samples at the track rate.</param>
    /// <param name="elapsedAtEnd">Session clock, in seconds, when the last of these samples was captured.</param>
    public void Append(ReadOnlySpan<float> samples, double elapsedAtEnd)
    {
        var expectedStart = (long)Math.Round(elapsedAtEnd * _writer.SampleRate) - samples.Length;
        var gap = expectedStart - _writer.FramesWritten;
        if (gap > GapTolerance * _writer.SampleRate)
        {
            _writer.WriteSilence(gap);
        }
        _writer.Write(samples);
    }

    /// <summary>Pads the track with silence up to the session end.</summary>
    public void Finish(double totalElapsed)
    {
        var missing = (long)Math.Round(totalElapsed * _writer.SampleRate) - _writer.FramesWritten;
        if (missing > 0)
        {
            _writer.WriteSilence(missing);
        }
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Offline helpers over 16-bit PCM WAV files written by Minutes.</summary>
public sealed class WavFile
{
    public const double SilenceDb = -160;
    public const double LevelWindowDuration = 0.5;

    public int SampleRate { get; }
    public int Channels { get; }
    public long Frames { get; }
    public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    private readonly string _path;
    private readonly long _dataOffset;

    private WavFile(string path, int sampleRate, int channels, long dataOffset, long frames)
    {
        _path = path;
        SampleRate = sampleRate;
        Channels = channels;
        _dataOffset = dataOffset;
        Frames = frames;
    }

    /// <summary>Opens a 16-bit PCM WAV. A data size of zero or past the file end (a crash) is read up to the file end.</summary>
    public static WavFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a WAV file.");
        }
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAV file.");
        }
        int? sampleRate = null, channels = null, bits = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format != 1 && format != 0xFFFE)
                {
                    throw new InvalidDataException("Only PCM WAV files are supported.");
                }
                stream.Seek(size - 16 + (size % 2), SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (sampleRate is null || channels is null || bits != 16)
                {
                    throw new InvalidDataException("Only 16-bit PCM WAV files are supported.");
                }
                var available = stream.Length - stream.Position;
                var dataBytes = size == 0 || size > available ? available : size;
                return new WavFile(path, sampleRate.Value, channels.Value, stream.Position, dataBytes / (2 * channels.Value));
            }
            else
            {
                stream.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("WAV file without audio data.");
    }

    /// <summary>Mono samples of [startFrame, startFrame + count), clamped to the file.</summary>
    public float[] ReadFrames(long startFrame, long count)
    {
        startFrame = Math.Clamp(startFrame, 0, Frames);
        count = Math.Clamp(count, 0, Frames - startFrame);
        var bytes = new byte[count * 2 * Channels];
        using (var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(_dataOffset + startFrame * 2 * Channels, SeekOrigin.Begin);
            stream.ReadExactly(bytes);
        }
        return PcmConverter.ToMono(bytes, new PcmFormat(SampleRate, Channels, 16, false));
    }

    /// <summary>RMS level in dBFS of each consecutive 0.5 s window (the last may be shorter).</summary>
    public IReadOnlyList<double> WindowLevelsDb()
    {
        var window = HalfSecondFrames();
        var levels = new List<double>();
        for (long start = 0; start < Frames; start += window)
        {
            levels.Add(LevelDb(ReadFrames(start, window)));
        }
        return levels;
    }

    /// <summary>Loudest 0.5 s window level of [start, start + duration), -160 for silence or an empty range.</summary>
    public double PeakWindowLevelDb(double start, double duration)
    {
        var (first, count) = FrameRange(start, duration);
        var window = HalfSecondFrames();
        var peak = SilenceDb;
        for (long offset = 0; offset < count; offset += window)
        {
            peak = Math.Max(peak, LevelDb(ReadFrames(first + offset, Math.Min(window, count - offset))));
        }
        return peak;
    }

    /// <summary>Writes the given ranges, back to back, as a new mono 16-bit WAV at the same rate.</summary>
    public void WriteRanges(string destination, IEnumerable<(double Start, double Duration)> ranges)
    {
        using var writer = new WavWriter(destination, SampleRate);
        foreach (var (start, duration) in ranges)
        {
            var (first, count) = FrameRange(start, duration);
            const long block = 160_000;
            for (long offset = 0; offset < count; offset += block)
            {
                writer.Write(ReadFrames(first + offset, Math.Min(block, count - offset)));
            }
        }
    }

    private long HalfSecondFrames() => Math.Max(1, (long)Math.Round(SampleRate * LevelWindowDuration));

    private (long First, long Count) FrameRange(double start, double duration)
    {
        long ToFrames(double seconds) => double.IsFinite(seconds) && seconds > 0 ? (long)Math.Round(seconds * SampleRate) : 0;
        var first = Math.Min(Frames, ToFrames(start));
        var last = Math.Min(Frames, first + ToFrames(duration));
        return (first, Math.Max(0, last - first));
    }

    internal static double LevelDb(float[] samples)
    {
        if (samples.Length == 0)
        {
            return SilenceDb;
        }
        var sumOfSquares = 0.0;
        foreach (var sample in samples)
        {
            sumOfSquares += sample * sample;
        }
        var rms = Math.Sqrt(sumOfSquares / samples.Length);
        return rms > 0 ? Math.Max(SilenceDb, 20 * Math.Log10(rms)) : SilenceDb;
    }
}
