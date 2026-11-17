using System.Diagnostics;
using Minutes.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Minutes.App.Capture;

/// <summary>
/// Records one track (microphone or everything the PC plays) to a 16 kHz mono WAV aligned on the session
/// clock. Follows the default device: plugging headphones in restarts capture on the new device, and
/// the gap is filled with silence so both tracks keep one timeline.
/// </summary>
internal sealed class TrackRecorder : IDisposable
{
    private readonly bool _loopback;
    private readonly Stopwatch _clock;
    private readonly AlignedTrackWriter _writer;
    private readonly object _lock = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceChangeListener _listener;
    private WasapiCapture? _capture;
    private PcmFormat? _format;
    private StreamingResampler? _resampler;
    private bool _stopping;
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private float _peak;

    public string Name { get; }
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>Raised (on a worker thread) when the track stopped recording for good.</summary>
    public event Action<string>? Failed;

    public TrackRecorder(string name, string path, bool loopback, Stopwatch clock)
    {
        Name = name;
        _loopback = loopback;
        _clock = clock;
        _writer = new AlignedTrackWriter(path);
        _listener = new DeviceChangeListener(loopback ? DataFlow.Render : DataFlow.Capture, () => Task.Run(Restart));
    }

    public void Start()
    {
        lock (_lock)
        {
            StartCapture();
        }
        _enumerator.RegisterEndpointNotificationCallback(_listener);
    }

    private void StartCapture()
    {
        var capture = _loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
        var wave = capture.WaveFormat;
        var isFloat = wave.Encoding == WaveFormatEncoding.IeeeFloat
            || (wave is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);
        _format = new PcmFormat(wave.SampleRate, wave.Channels, wave.BitsPerSample, isFloat);
        _resampler = new StreamingResampler(wave.SampleRate, 16_000);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        capture.StartRecording();
        _capture = capture;
        MinutesLog.Info($"{Name}: capturing {wave.SampleRate} Hz, {wave.Channels} ch, {wave.BitsPerSample} bit, float={isFloat}");
        LogCommunicationsDevice();
    }

    /// <summary>
    /// Meeting apps often use the default communications device; when it differs from the default device
    /// recorded here, one track may come out silent, so the log says so.
    /// </summary>
    private void LogCommunicationsDevice()
    {
        try
        {
            var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
            var console = _enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            var communications = _enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            if (console.ID != communications.ID)
            {
                MinutesLog.Info($"{Name}: recording the default device; the communications device is a different one");
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        lock (_lock)
        {
            if (sender != _capture || _format is null || _resampler is null || args.BytesRecorded == 0)
            {
                return;
            }
            var mono = PcmConverter.ToMono(args.Buffer.AsSpan(0, args.BytesRecorded), _format);
            var samples = _resampler.Process(mono);
            foreach (var sample in samples)
            {
                _peak = Math.Max(_peak, Math.Abs(sample));
            }
            _writer.Append(samples, _clock.Elapsed.TotalSeconds);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            MinutesLog.Error($"{Name}: capture stopped: {args.Exception.GetType().Name}: {args.Exception.Message}");
            if (!_stopping && sender == _capture)
            {
                _ = Task.Run(Restart);
            }
        }
    }

    private void Restart()
    {
        WasapiCapture? old;
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            old = _capture;
            _capture = null;
        }
        MinutesLog.Info($"{Name}: default device changed, restarting capture");
        // Disposed outside the lock: disposing joins the capture thread, which may be waiting for it in OnData.
        DisposeCapture(old);
        // The new endpoint is often not ready when the notification fires: retry before giving up.
        int[] delays = [0, 1000, 2000, 4000, 8000];
        foreach (var delay in delays)
        {
            Thread.Sleep(delay);
            lock (_lock)
            {
                if (_stopping || _capture is not null)
                {
                    return;
                }
                try
                {
                    StartCapture();
                    return;
                }
                catch (Exception error)
                {
                    MinutesLog.Error($"{Name}: restart failed: {error.GetType().Name}: {error.Message}");
                }
            }
        }
        Failed?.Invoke($"Se ha dejado de grabar {Name} tras cambiar de dispositivo de audio.");
    }

    /// <summary>Stops capturing and pads the track to the session length.</summary>
    public void Stop(double totalElapsed)
    {
        WasapiCapture? old;
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            old = _capture;
            _capture = null;
        }
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_listener);
        }
        catch (Exception)
        {
        }
        DisposeCapture(old);
        lock (_lock)
        {
            _writer.Finish(totalElapsed);
            _writer.Dispose();
        }
        MinutesLog.Info($"{Name}: stopped, {FramesWritten / 16_000.0:F1} s, peak {(_peak > 0 ? 20 * Math.Log10(_peak) : -160):F0} dBFS");
    }

    private void DisposeCapture(WasapiCapture? capture)
    {
        if (capture is null)
        {
            return;
        }
        capture.DataAvailable -= OnData;
        try
        {
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception error)
        {
            MinutesLog.Error($"{Name}: releasing the device failed: {error.GetType().Name}");
        }
    }

    public void Dispose()
    {
        Stop(_clock.Elapsed.TotalSeconds);
        _enumerator.Dispose();
    }

    /// <summary>Calls back when the default device of one direction changes.</summary>
    private sealed class DeviceChangeListener(DataFlow flow, Action onChange) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow changedFlow, Role role, string defaultDeviceId)
        {
            if (changedFlow == flow && role is Role.Console or Role.Multimedia)
            {
                onChange();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

/// <summary>
/// Encodes raw tracks to AAC .m4a with Media Foundation. Its AAC encoder only accepts 44.1/48 kHz input,
/// so the 16 kHz track is upsampled on the fly; 96 kbps is the lowest rate it offers.
/// </summary>
internal sealed class MediaFoundationExporter : IFinalAudioExporter
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFoundationApi.Startup();
        return true;
    });

    public void Export(string rawWav, string destinationM4a)
    {
        _ = Started.Value;
        var temporary = Path.Combine(Path.GetDirectoryName(destinationM4a)!, $".export-{Guid.NewGuid():N}.m4a");
        try
        {
            using (var source = new UpsampledWaveProvider(WavFile.Open(rawWav)))
            {
                MediaFoundationEncoder.EncodeToAac(source, temporary, 96_000);
            }
            File.Move(temporary, destinationM4a, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public double Duration(string path)
    {
        _ = Started.Value;
        using var reader = new MediaFoundationReader(path);
        return reader.TotalTime.TotalSeconds;
    }

    /// <summary>A 16 kHz mono WAV read as 48 kHz mono 16-bit PCM.</summary>
    private sealed class UpsampledWaveProvider(WavFile source) : IWaveProvider, IDisposable
    {
        private const int ReadBlock = 16_000;
        private readonly StreamingResampler _resampler = new(source.SampleRate, 48_000);
        private byte[] _pending = [];
        private int _pendingOffset;
        private long _position;

        public WaveFormat WaveFormat { get; } = new(48_000, 16, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            var copied = 0;
            while (copied < count)
            {
                if (_pendingOffset >= _pending.Length && !Refill())
                {
                    break;
                }
                var take = Math.Min(count - copied, _pending.Length - _pendingOffset);
                Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset + copied, take);
                _pendingOffset += take;
                copied += take;
            }
            return copied;
        }

        private bool Refill()
        {
            while (_position < source.Frames)
            {
                var samples = _resampler.Process(source.ReadFrames(_position, ReadBlock));
                _position += ReadBlock;
                if (samples.Length == 0)
                {
                    continue;
                }
                _pending = new byte[samples.Length * 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                    _pending[i * 2] = (byte)(value & 0xFF);
                    _pending[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
                _pendingOffset = 0;
                return true;
            }
            return false;
        }

        public void Dispose() => _pending = [];
    }
}
