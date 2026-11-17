using Minutes.App.Capture;
using Minutes.Core;

namespace Minutes.App;

public enum LastMeetingState { Transcribing, Ready, Failed }

/// <summary>
/// Ties detection, recording and transcription together. Runs on the UI thread: every public member
/// must be called there, and every event is raised there.
/// </summary>
internal sealed class RecordingCoordinator : IDisposable
{
    private readonly Control _ui;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly MeetingDetector _detector = new();
    private readonly TranscriptionPipeline _pipeline;
    private readonly WhisperTokenizer? _tokenizer = EmbeddedResources.LoadTokenizer();
    private DateTime _lastBrowserCheck = DateTime.MinValue;
    private bool _browserMeetingVisible;
    private bool _isStarting;
    private bool _sessionStopsWithMic;
    private Task _finalizing = Task.CompletedTask;

    public AppSettings Settings { get; private set; } = AppSettings.Load();
    public RecordingSession? Session { get; private set; }
    public PipelineStatus PipelineStatus { get; private set; } = new(0, null, 0);
    public (string Folder, LastMeetingState State, string? Error)? LastMeeting { get; private set; }
    public string? LastProblem { get; private set; }

    /// <summary>Raised whenever anything shown in the tray or the pill changes.</summary>
    public event Action? Changed;
    /// <summary>Seconds until an automatic stop after the meeting app released the mic, or null.</summary>
    public event Action<int?>? StopCountdown;
    public event Action<string, string>? Problem;

    public RecordingCoordinator(Control ui)
    {
        _ui = ui;
        _pipeline = new TranscriptionPipeline(
            Settings.MeetingsRoot,
            GroqKeyStore.Load,
            () => Settings.WhisperLanguage,
            () => Settings.Vocabulary,
            key => new GroqClient(key, tokenizer: _tokenizer),
            new MediaFoundationExporter(),
            status => OnUi(() => { PipelineStatus = status; Changed?.Invoke(); }),
            (folder, failure) => OnUi(() => TranscriptionFinished(folder, failure)));
        _timer.Tick += (_, _) => Tick();
    }

    public void Start(bool autoDetect = true)
    {
        LoadLastMeeting();
        // Detection starts only after the pending scan, so a new recording is never queued half-written.
        Task.Run(_pipeline.ResumePending).ContinueWith(_ => OnUi(() =>
        {
            if (autoDetect)
            {
                _timer.Start();
            }
        }));
    }

    public void ReloadSettings()
    {
        Settings = AppSettings.Load();
        Changed?.Invoke();
    }

    private void Tick()
    {
        try
        {
            var keys = MicUsageMonitor.ActiveMicKeys();
            if (MeetingAppCatalog.Candidates(keys).Any(app => app.IsBrowser))
            {
                if (DateTime.UtcNow - _lastBrowserCheck >= TimeSpan.FromSeconds(5))
                {
                    _lastBrowserCheck = DateTime.UtcNow;
                    _browserMeetingVisible = MeetingWindowSelector.BrowserMeetingVisible(WindowCatalog.VisibleWindows());
                }
            }
            else
            {
                _browserMeetingVisible = false;
            }

            var meetingEvent = _detector.Update(keys, _browserMeetingVisible, DateTimeOffset.Now);
            if (Session is not null && _sessionStopsWithMic)
            {
                StopCountdown?.Invoke(_detector.PendingStopDeadline is { } deadline
                    ? Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.Now).TotalSeconds))
                    : null);
            }
            switch (meetingEvent)
            {
                case MeetingDetector.Started started when Settings.AutoRecord && Session is null:
                    StartRecording(started.App, keys, "auto");
                    break;
                case MeetingDetector.Stopped when Session is not null && _sessionStopsWithMic:
                    StopRecording(manual: false);
                    break;
            }
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Detection tick failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Records the meeting app using the mic, or else the foreground app (webinars, ad-hoc calls).
    /// A meeting app stops by itself when it hangs up; the foreground fallback never does.
    /// </summary>
    public void StartManualRecording()
    {
        var keys = MicUsageMonitor.ActiveMicKeys();
        if (MeetingAppCatalog.Candidates(keys).FirstOrDefault() is { } app)
        {
            if (StartRecording(app, keys, "manual"))
            {
                _detector.Adopt(app);
                _sessionStopsWithMic = true;
            }
            return;
        }
        var foreground = WindowCatalog.Foreground();
        var isOwnWindow = foreground is { } own && own.Pid == Environment.ProcessId;
        var name = foreground is { } window && !isOwnWindow ? WindowCatalog.ProductName(window.Pid) : "Manual";
        StartRecording(name, isOwnWindow ? null : foreground?.Process, isOwnWindow ? null : foreground?.Title, title: null, "manual");
    }

    private bool StartRecording(MeetingApp app, IReadOnlySet<string> keys, string trigger)
    {
        var window = MeetingWindowSelector.Select(app, WindowCatalog.VisibleWindows());
        // Browser meetings are named after the service ("Google Meet"), not the browser.
        if (app.IsBrowser && MeetingTitle.BrowserService(window?.Title) is { } service)
        {
            app = app.Renamed(service);
        }
        var key = keys.FirstOrDefault(app.MatchesMicKey);
        var started = StartRecording(app.DisplayName, key, window?.Title, MeetingTitle.Clean(window?.Title, app), trigger);
        if (started)
        {
            _sessionStopsWithMic = trigger == "auto";
        }
        return started;
    }

    private bool StartRecording(string appName, string? appKey, string? windowTitle, string? title, string trigger)
    {
        if (Session is not null || _isStarting)
        {
            return false;
        }
        _isStarting = true;
        try
        {
            Session = RecordingSession.Start(appName, appKey, windowTitle, title, trigger, Settings.MeetingsRoot);
            _sessionStopsWithMic = false;
            LastProblem = Session.Warnings.Count == 0 ? null : string.Join(" ", Session.Warnings);
            if (LastProblem is not null)
            {
                Problem?.Invoke("Grabación incompleta", LastProblem);
            }
            Changed?.Invoke();
            return true;
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Recording failed to start: {error.GetType().Name}: {error.Message}");
            LastProblem = $"No se pudo iniciar la grabación: {error.Message}";
            Problem?.Invoke("No se pudo iniciar la grabación", error.Message);
            Changed?.Invoke();
            return false;
        }
        finally
        {
            _isStarting = false;
        }
    }

    public void StopRecording(bool manual)
    {
        if (Session is not { } session)
        {
            return;
        }
        Session = null;
        if (manual)
        {
            _detector.SuppressUntilMicReleased();
        }
        StopCountdown?.Invoke(null);
        LastMeeting = (session.Folder, LastMeetingState.Transcribing, null);
        Changed?.Invoke();
        var previous = _finalizing;
        _finalizing = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                session.Stop();
            }
            catch (Exception error)
            {
                MinutesLog.Error($"Stopping failed: {error.GetType().Name}: {error.Message}");
            }
            _pipeline.Enqueue(session.Folder);
        });
    }

    /// <summary>Stops any recording and waits until its files are written (used on exit).</summary>
    public void PrepareForExit()
    {
        _timer.Stop();
        StopRecording(manual: true);
        _finalizing.Wait(TimeSpan.FromSeconds(15));
    }

    public void RetryFailed() => Task.Run(_pipeline.RetryFailed);

    public void Retry(string folder) => _pipeline.Enqueue(folder);

    /// <summary>Waits for the queued transcriptions (smoke test).</summary>
    public Task WhenIdleAsync() => _finalizing.ContinueWith(_ => _pipeline.Idle).Unwrap();

    private void TranscriptionFinished(string folder, string? failure)
    {
        var state = failure is null ? LastMeetingState.Ready : LastMeetingState.Failed;
        if (LastMeeting is null || LastMeeting.Value.Folder == folder)
        {
            LastMeeting = (folder, state, failure);
        }
        if (failure is not null)
        {
            Problem?.Invoke("No se pudo transcribir", failure);
        }
        Changed?.Invoke();
    }

    private void LoadLastMeeting()
    {
        try
        {
            var root = Settings.MeetingsRoot;
            if (!Directory.Exists(root))
            {
                return;
            }
            var latest = Directory.GetDirectories(root)
                .Select(folder => MetadataStore.TryRead(folder, out var metadata) ? (folder, metadata) : default)
                .Where(item => item.folder is not null)
                .OrderByDescending(item => item.metadata.StartedAt)
                .FirstOrDefault();
            if (latest.folder is null)
            {
                return;
            }
            var state = latest.metadata.Transcription.State switch
            {
                TranscriptionState.Done => LastMeetingState.Ready,
                TranscriptionState.Failed => LastMeetingState.Failed,
                _ => LastMeetingState.Transcribing,
            };
            LastMeeting = (latest.folder, state, latest.metadata.Transcription.Error);
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Reading the last meeting failed: {error.GetType().Name}");
        }
    }

    private void OnUi(Action action)
    {
        if (_ui.IsDisposed)
        {
            return;
        }
        if (_ui.InvokeRequired)
        {
            _ui.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose() => _timer.Dispose();
}
