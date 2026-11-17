using System.Diagnostics;
using Minutes.App.Capture;
using Minutes.Core;

namespace Minutes.App;

/// <summary>One meeting being recorded: its folder, meta.json and the two audio tracks.</summary>
internal sealed class RecordingSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<TrackRecorder> _tracks = [];
    private MeetingMetadata _metadata;

    public string Folder { get; }
    public DateTimeOffset StartedAt { get; }
    public string AppName => _metadata.AppName;
    public List<string> Warnings { get; } = [];

    private RecordingSession(string folder, MeetingMetadata metadata)
    {
        Folder = folder;
        _metadata = metadata;
        StartedAt = metadata.StartedAt;
    }

    /// <summary>Creates the meeting folder and starts both tracks; throws when neither track can record.</summary>
    public static RecordingSession Start(string appName, string? appKey, string? windowTitle, string? title, string trigger, string root)
    {
        Directory.CreateDirectory(root);
        var startedAt = DateTimeOffset.Now;
        var folder = MeetingFolder.UniquePath(root, MeetingFolder.FolderName(startedAt, title, appName));
        Directory.CreateDirectory(folder);
        var metadata = new MeetingMetadata
        {
            AppName = appName,
            AppBundleId = appKey,
            Title = title,
            WindowTitle = windowTitle,
            Trigger = trigger,
            StartedAt = startedAt,
            HasVideo = false,
        };
        MetadataStore.Write(metadata, folder);

        var session = new RecordingSession(folder, metadata);
        session.StartTrack("Yo (micrófono)", MeetingFile.MicRaw, loopback: false);
        session.StartTrack("Ellos (audio del PC)", MeetingFile.SystemRaw, loopback: true);
        if (session._tracks.Count == 0)
        {
            throw new InvalidOperationException(string.Join(" ", session.Warnings));
        }
        MinutesLog.Info($"Recording started ({trigger}, {appName})");
        return session;
    }

    private void StartTrack(string name, string file, bool loopback)
    {
        TrackRecorder? track = null;
        try
        {
            track = new TrackRecorder(name, Path.Combine(Folder, file), loopback, _clock);
            track.Start();
            _tracks.Add(track);
        }
        catch (Exception error)
        {
            MinutesLog.Error($"{name}: could not start: {error.GetType().Name}: {error.Message}");
            Warnings.Add($"No se pudo grabar {name}: {error.Message}");
            try
            {
                track?.Dispose();
                File.Delete(Path.Combine(Folder, file));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Stops the tracks and writes the end time and duration. Safe to call from a worker thread.</summary>
    public void Stop()
    {
        var total = _clock.Elapsed.TotalSeconds;
        foreach (var track in _tracks)
        {
            try
            {
                track.Stop(total);
            }
            catch (Exception error)
            {
                MinutesLog.Error($"{track.Name}: stop failed: {error.GetType().Name}: {error.Message}");
            }
        }
        _metadata = _metadata with { EndedAt = DateTimeOffset.Now, DurationSeconds = total };
        MetadataStore.Write(_metadata, Folder);
        MinutesLog.Info($"Recording stopped after {total:F0} s");
    }
}
