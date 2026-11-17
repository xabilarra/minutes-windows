using System.Diagnostics;
using Minutes.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Minutes.App.Capture;

/// <summary>
/// Records one track (microphone or what the PC plays) to a 16 kHz mono WAV on the session clock, without
/// depending on which device is configured: every 2 s it looks at which devices other apps are using
/// right now (their active audio sessions) and records all of them, mixed. Microphone: the inputs some
/// other app is capturing (Teams, Chrome…), or the default one when none is. Speakers: every output an
/// app is playing to, plus the default output. Devices that appear mid-call (headphones) are added.
/// </summary>
internal sealed class TrackRecorder : IDisposable
{
    private const int ScanMilliseconds = 2000;
    private const int FlushMilliseconds = 500;
    /// <summary>How far behind the clock the mix is written, so late packets still land in it.</summary>
    private const double MixLatency = 1.0;
    /// <summary>A device nobody else uses any more keeps recording this long before it is dropped.</summary>
    private static readonly TimeSpan UnusedGrace = TimeSpan.FromSeconds(10);

    private readonly bool _loopback;
    private readonly Role _fallbackRole;
    private readonly Stopwatch _clock;
    private readonly WavWriter _writer;
    private readonly TrackMixer _mixer = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceCapture> _captures = [];
    /// <summary>Created and used only on thread-pool (MTA) threads, like every capture.</summary>
    private MMDeviceEnumerator? _enumerator;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private System.Threading.Timer? _scanTimer;
    private System.Threading.Timer? _flushTimer;
    private volatile bool _stopping;
    private float _peak;
    private bool _warnedNothing;
    private readonly IReadOnlyCollection<string> _appProcesses;
    /// <summary>Held by whoever scans; Stop takes it so no scan runs once stopping begins.</summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, DateTime> _lastDeviceError = [];

    public string Name { get; }
    public long FramesWritten => _writer.FramesWritten;

    /// <summary>Raised (on a worker thread) when the track has no device it can record.</summary>
    public event Action<string>? Failed;

    /// <param name="communications">
    /// When no other app is using a device, record the default communications device (call apps)
    /// instead of the default device.
    /// </param>
    /// <param name="appProcesses">
    /// Lowercase exe names of the meeting app (null for a manual recording): the devices its process tree
    /// uses win over everything else, so another app's microphone or a noise-suppression chain is not mixed in.
    /// </param>
    public TrackRecorder(string name, string path, bool loopback, bool communications, IReadOnlyCollection<string>? appProcesses, Stopwatch clock)
    {
        Name = name;
        _appProcesses = appProcesses ?? [];
        _loopback = loopback;
        _fallbackRole = communications ? Role.Communications : Role.Console;
        _clock = clock;
        _writer = new WavWriter(path, 16_000);
    }

    /// <summary>Starts on the devices in use now; throws when no device can be recorded at all.</summary>
    public void Start()
    {
        // The first scan reports a total failure by throwing, not through Failed as well.
        _warnedNothing = true;
        Task.Run(SafeScan).GetAwaiter().GetResult();
        lock (_lock)
        {
            if (_captures.Count == 0)
            {
                throw new InvalidOperationException("no audio device could be opened");
            }
        }
        _scanTimer = new System.Threading.Timer(_ => SafeScan(), null, ScanMilliseconds, ScanMilliseconds);
        _flushTimer = new System.Threading.Timer(_ => Flush(_clock.Elapsed.TotalSeconds - MixLatency), null, FlushMilliseconds, FlushMilliseconds);
    }

    private void SafeScan()
    {
        // One scan at a time, and none once Stop holds the gate.
        if (!_scanGate.Wait(0))
        {
            return;
        }
        try
        {
            if (!_stopping)
            {
                Scan();
            }
        }
        catch (Exception error)
        {
            MinutesLog.Error($"{Name}: device scan failed: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Adds captures for the devices in use, drops the ones nobody has used for a while.</summary>
    private void Scan()
    {
        var wanted = WantedDevices();
        var now = DateTime.UtcNow;
        var toStop = new List<DeviceCapture>();
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            foreach (var capture in _captures.Values)
            {
                if (wanted.ContainsKey(capture.DeviceId))
                {
                    capture.LastWanted = now;
                }
            }
            foreach (var (id, capture) in _captures.ToList())
            {
                // A failed device always goes; an unused one only while another device keeps recording.
                var unused = now - capture.LastWanted > UnusedGrace;
                if (capture.HasFailed || (unused && _captures.Count > 1))
                {
                    _captures.Remove(id);
                    _mixer.RemoveSource(capture.Source);
                    toStop.Add(capture);
                }
            }
        }
        foreach (var capture in toStop)
        {
            MinutesLog.Info($"{Name}: stopped recording \"{capture.DeviceName}\"{(capture.HasFailed ? " (device failed)" : "")}");
            capture.Dispose();
        }
        foreach (var (id, device) in wanted)
        {
            lock (_lock)
            {
                if (_stopping || _captures.ContainsKey(id))
                {
                    continue;
                }
            }
            DeviceCapture? started = null;
            try
            {
                started = new DeviceCapture(device, _loopback, this);
                lock (_lock)
                {
                    if (_stopping)
                    {
                        break;
                    }
                    started.Source = _mixer.AddSource();
                    started.LastWanted = now;
                    _captures[id] = started;
                }
                started.Start();
                MinutesLog.Info($"{Name}: recording \"{started.DeviceName}\" at {started.Format.SampleRate} Hz, {started.Format.Channels} ch, float={started.Format.IsFloat}");
                started = null;
            }
            catch (Exception error)
            {
                LogDeviceError(id, $"{Name}: could not open \"{device.FriendlyName}\": {error.GetType().Name}: {error.Message}");
                lock (_lock)
                {
                    if (started is not null && _captures.TryGetValue(id, out var current) && current == started)
                    {
                        _captures.Remove(id);
                        _mixer.RemoveSource(started.Source);
                    }
                }
            }
            finally
            {
                started?.Dispose();
            }
        }
        lock (_lock)
        {
            if (_captures.Count == 0 && !_stopping && !_warnedNothing)
            {
                _warnedNothing = true;
                Failed?.Invoke($"No se puede grabar {Name}: no hay ningún dispositivo de audio disponible.");
            }
            else if (_captures.Count > 0)
            {
                _warnedNothing = false;
            }
        }
    }

    /// <summary>
    /// Device id → device for every endpoint this track should record now, in order of preference:
    /// 1. the devices the meeting app's process tree is streaming on (Active), or keeps open while muted (Inactive);
    /// 2. otherwise the devices any other app is streaming on, plus (speakers) the default outputs;
    /// 3. otherwise the default device (microphone: communications for call apps).
    /// </summary>
    private Dictionary<string, MMDevice> WantedDevices()
    {
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        _enumerator ??= new MMDeviceEnumerator();
        var appTree = ProcessTree.Of(_appProcesses);
        var appActive = new Dictionary<string, MMDevice>();
        var appInactive = new Dictionary<string, MMDevice>();
        var othersActive = new Dictionary<string, MMDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            foreach (var (processId, state) in Sessions(device))
            {
                if (processId == _ownProcessId)
                {
                    continue;
                }
                var isApp = appTree.Contains(processId);
                if (isApp && state == AudioSessionState.AudioSessionStateActive)
                {
                    appActive[device.ID] = device;
                }
                else if (isApp && state == AudioSessionState.AudioSessionStateInactive)
                {
                    appInactive[device.ID] = device;
                }
                else if (state == AudioSessionState.AudioSessionStateActive)
                {
                    othersActive[device.ID] = device;
                }
            }
        }
        if (appActive.Count > 0)
        {
            return appActive;
        }
        if (appInactive.Count > 0)
        {
            return OneInactiveDevice(appInactive, flow);
        }
        var wanted = othersActive;
        // Speakers: the default outputs always count. Microphone: the default input only when no app uses one.
        if (_loopback || wanted.Count == 0)
        {
            foreach (var role in _loopback ? new[] { Role.Console, _fallbackRole } : [_fallbackRole])
            {
                try
                {
                    var fallback = _enumerator.GetDefaultAudioEndpoint(flow, role);
                    wanted.TryAdd(fallback.ID, fallback);
                }
                catch (Exception)
                {
                    // No default device (none plugged in): nothing to add.
                }
            }
        }
        return wanted;
    }

    /// <summary>
    /// The app keeps Inactive sessions on devices it used earlier, so while it is muted only one of them is
    /// kept: the one already being recorded, else the default device, else the first.
    /// </summary>
    private Dictionary<string, MMDevice> OneInactiveDevice(Dictionary<string, MMDevice> inactive, DataFlow flow)
    {
        if (inactive.Count == 1)
        {
            return inactive;
        }
        lock (_lock)
        {
            var recorded = inactive.Where(pair => _captures.ContainsKey(pair.Key)).ToDictionary();
            if (recorded.Count > 0)
            {
                return recorded;
            }
        }
        try
        {
            var fallback = _enumerator!.GetDefaultAudioEndpoint(flow, _loopback ? Role.Console : _fallbackRole);
            if (inactive.TryGetValue(fallback.ID, out var device))
            {
                return new Dictionary<string, MMDevice> { [fallback.ID] = device };
            }
        }
        catch (Exception)
        {
        }
        var first = inactive.First();
        return new Dictionary<string, MMDevice> { [first.Key] = first.Value };
    }

    /// <summary>Process id and state of every non-system audio session on the device.</summary>
    private List<(uint ProcessId, AudioSessionState State)> Sessions(MMDevice device)
    {
        var result = new List<(uint, AudioSessionState)>();
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (!session.IsSystemSoundsSession)
                {
                    result.Add((session.GetProcessID, session.State));
                }
            }
        }
        catch (Exception error)
        {
            LogDeviceError(device.ID, $"{Name}: reading sessions of \"{device.FriendlyName}\" failed: {error.GetType().Name}");
        }
        return result;
    }

    /// <summary>At most one log line per device and minute, so a broken device does not flood the log.</summary>
    private void LogDeviceError(string deviceId, string message)
    {
        lock (_lastDeviceError)
        {
            if (_lastDeviceError.TryGetValue(deviceId, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1))
            {
                return;
            }
            _lastDeviceError[deviceId] = DateTime.UtcNow;
        }
        MinutesLog.Error(message);
    }

    private void OnSamples(DeviceCapture capture, float[] samples)
    {
        lock (_lock)
        {
            if (_stopping || !_captures.ContainsValue(capture))
            {
                return;
            }
            foreach (var sample in samples)
            {
                _peak = Math.Max(_peak, Math.Abs(sample));
            }
            _mixer.Append(capture.Source, samples, _clock.Elapsed.TotalSeconds);
        }
    }

    private void Flush(double upTo)
    {
        try
        {
            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }
                var mixed = _mixer.Take(upTo);
                if (mixed.Length > 0)
                {
                    _writer.Write(mixed);
                }
            }
        }
        catch (Exception error)
        {
            MinutesLog.Error($"{Name}: writing failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Stops every capture and writes the track up to the session end.</summary>
    public void Stop(double totalElapsed)
    {
        List<DeviceCapture> captures;
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            captures = [.. _captures.Values];
            _captures.Clear();
        }
        _scanTimer?.Dispose();
        _flushTimer?.Dispose();
        // Wait for a scan already running (it may be opening a device) before releasing the devices.
        _scanGate.Wait();
        // Disposed outside the lock: disposing joins each capture thread, which may be waiting for it.
        foreach (var capture in captures)
        {
            capture.Dispose();
        }
        lock (_lock)
        {
            var rest = _mixer.Take(totalElapsed);
            if (rest.Length > 0)
            {
                _writer.Write(rest);
            }
            _writer.Dispose();
        }
        MinutesLog.Info($"{Name}: stopped, {FramesWritten / 16_000.0:F1} s, peak {(_peak > 0 ? 20 * Math.Log10(_peak) : -160):F0} dBFS");
    }

    public void Dispose()
    {
        Stop(_clock.Elapsed.TotalSeconds);
        _enumerator?.Dispose();
    }

    /// <summary>One WASAPI capture of one device, resampled to 16 kHz mono.</summary>
    private sealed class DeviceCapture : IDisposable
    {
        private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly WasapiCapture _capture;
        private readonly StreamingResampler _resampler;
        private readonly TrackRecorder _owner;

        public string DeviceId { get; }
        public string DeviceName { get; }
        public PcmFormat Format { get; }
        public int Source { get; set; }
        public DateTime LastWanted { get; set; }
        public bool HasFailed { get; private set; }

        public DeviceCapture(MMDevice device, bool loopback, TrackRecorder owner)
        {
            _owner = owner;
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
            _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            var wave = _capture.WaveFormat;
            var isFloat = wave.Encoding == WaveFormatEncoding.IeeeFloat
                || (wave is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);
            Format = new PcmFormat(wave.SampleRate, wave.Channels, wave.BitsPerSample, isFloat);
            _resampler = new StreamingResampler(wave.SampleRate, 16_000);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
        }

        public void Start() => _capture.StartRecording();

        private void OnData(object? sender, WaveInEventArgs args)
        {
            if (args.BytesRecorded == 0)
            {
                return;
            }
            float[] samples;
            lock (_resampler)
            {
                samples = _resampler.Process(PcmConverter.ToMono(args.Buffer.AsSpan(0, args.BytesRecorded), Format));
            }
            _owner.OnSamples(this, samples);
        }

        private void OnStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null)
            {
                // Unplugged or invalidated: the next scan drops it and records whatever is in use instead.
                HasFailed = true;
                MinutesLog.Error($"\"{DeviceName}\" stopped: {args.Exception.GetType().Name}: {args.Exception.Message}");
            }
        }

        public void Dispose()
        {
            _capture.DataAvailable -= OnData;
            try
            {
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception error)
            {
                MinutesLog.Error($"Releasing \"{DeviceName}\" failed: {error.GetType().Name}");
            }
        }
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
