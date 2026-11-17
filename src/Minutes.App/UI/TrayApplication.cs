using System.Drawing.Drawing2D;
using Minutes.Core;

namespace Minutes.App.UI;

/// <summary>The tray icon, its menu and the pill; owns the coordinator.</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly PillForm _pill = new();
    private readonly RecordingCoordinator _coordinator;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;
    private SettingsForm? _settings;
    private LastMeetingState? _shownState;
    private string? _shownFolder;

    public TrayApplication(bool startedAtLogin)
    {
        _ui.CreateControl();
        _idleIcon = LoadAppIcon() ?? DrawIcon(recording: false);
        _recordingIcon = DrawIcon(recording: true);
        _coordinator = new RecordingCoordinator(_ui);
        _coordinator.Changed += Render;
        _coordinator.StopCountdown += _pill.UpdateStopCountdown;
        _coordinator.Problem += (title, detail) => _tray.ShowBalloonTip(8000, title, detail, ToolTipIcon.Warning);

        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowSettings();
        _menu.Opening += (_, _) => BuildMenu();
        Render();

        _coordinator.Start();
        LoginItem.Refresh();
        if (!GroqKeyStore.HasKey)
        {
            ShowSettings();
        }
        else if (!startedAtLogin)
        {
            _tray.ShowBalloonTip(4000, "Minutes está activo", "Graba solo cuando empiece una llamada. Lo tienes en la bandeja, junto al reloj.", ToolTipIcon.Info);
        }
    }

    public void ShowSettingsFromAnyThread()
    {
        if (!_ui.IsDisposed)
        {
            _ui.BeginInvoke(ShowSettings);
        }
    }

    public void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm();
        _settings.Saved += () =>
        {
            _coordinator.ReloadSettings();
            if (_coordinator.LastMeeting is { State: LastMeetingState.Failed })
            {
                _coordinator.RetryFailed();
            }
        };
        _settings.Show();
        _settings.Activate();
    }

    private void Render()
    {
        var session = _coordinator.Session;
        _tray.Icon = session is null ? _idleIcon : _recordingIcon;
        _tray.Text = Truncate(session is null ? $"Minutes — {IdleStatus()}" : $"Minutes — grabando {session.AppName}", 63);

        if (session is not null)
        {
            _shownState = null;
            _pill.ShowRecording(session.AppName, session.StartedAt, () => _coordinator.StopRecording(manual: true));
            return;
        }
        if (_coordinator.LastMeeting is not { } last || (last.State == _shownState && last.Folder == _shownFolder))
        {
            return;
        }
        var isNew = _shownFolder is not null || last.State == LastMeetingState.Transcribing;
        _shownState = last.State;
        _shownFolder = last.Folder;
        if (!isNew)
        {
            return;
        }
        switch (last.State)
        {
            case LastMeetingState.Transcribing:
                _pill.ShowTranscribing();
                break;
            case LastMeetingState.Ready:
                _pill.ShowReady(() => CopyTranscript(last.Folder), () => CopyPath(last.Folder), () => SettingsForm.OpenFolder(last.Folder));
                break;
            case LastMeetingState.Failed:
                _pill.ShowFailed(last.Error ?? "", () => _coordinator.Retry(last.Folder));
                break;
        }
    }

    private string IdleStatus()
    {
        var status = _coordinator.PipelineStatus;
        if (status.Active is not null)
        {
            return status.Queued > 0 ? $"transcribiendo (+{status.Queued} en cola)" : "transcribiendo";
        }
        if (status.Failed > 0)
        {
            return $"{status.Failed} sin transcribir";
        }
        return _coordinator.Settings.AutoRecord ? "esperando una llamada" : "grabación automática desactivada";
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var status = new ToolStripMenuItem(_coordinator.Session is { } session ? $"Grabando {session.AppName}" : Capitalize(IdleStatus())) { Enabled = false };
        _menu.Items.Add(status);
        _menu.Items.Add(new ToolStripSeparator());
        if (_coordinator.Session is null)
        {
            _menu.Items.Add("Empezar a grabar ahora", null, (_, _) => _coordinator.StartManualRecording());
        }
        else
        {
            _menu.Items.Add("Detener la grabación", null, (_, _) => _coordinator.StopRecording(manual: true));
        }
        if (_coordinator.LastMeeting is { State: LastMeetingState.Ready } ready)
        {
            _menu.Items.Add("Copiar la última transcripción", null, (_, _) => CopyTranscript(ready.Folder));
            _menu.Items.Add("Copiar la ruta de la última transcripción", null, (_, _) => CopyPath(ready.Folder));
        }
        if (_coordinator.PipelineStatus.Failed > 0)
        {
            _menu.Items.Add("Reintentar transcripciones fallidas", null, (_, _) => _coordinator.RetryFailed());
        }
        _menu.Items.Add("Abrir la carpeta Reuniones", null, (_, _) => SettingsForm.OpenFolder(_coordinator.Settings.MeetingsRoot));
        var auto = new ToolStripMenuItem("Grabar automáticamente") { Checked = _coordinator.Settings.AutoRecord };
        auto.Click += (_, _) =>
        {
            var settings = AppSettings.Load();
            settings.AutoRecord = !settings.AutoRecord;
            settings.Save();
            _coordinator.ReloadSettings();
        };
        _menu.Items.Add(auto);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Ajustes…", null, (_, _) => ShowSettings());
        _menu.Items.Add("Salir", null, (_, _) => Quit());
    }

    private void CopyTranscript(string folder)
    {
        try
        {
            Clipboard.SetText(File.ReadAllText(Path.Combine(folder, MeetingFile.TranscriptMarkdown)));
            _pill.Flash("Transcripción copiada");
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Copy failed: {error.GetType().Name}");
        }
    }

    private void CopyPath(string folder)
    {
        try
        {
            Clipboard.SetText(Path.Combine(folder, MeetingFile.TranscriptMarkdown));
            _pill.Flash("Ruta copiada");
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Copy failed: {error.GetType().Name}");
        }
    }

    private void Quit()
    {
        if (_coordinator.Session is not null
            && MessageBox.Show("Hay una grabación en curso. Si sales, se detiene y se transcribirá la próxima vez que abras Minutes.", "Minutes",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        _coordinator.PrepareForExit();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _pill.Dispose();
            _coordinator.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon? LoadAppIcon()
    {
        using var stream = EmbeddedResources.Open("Minutes.ico");
        return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>A waveform on a purple circle; red while recording.</summary>
    private static Icon DrawIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(recording ? Color.FromArgb(220, 38, 38) : Color.FromArgb(124, 58, 237));
            graphics.FillEllipse(fill, 1, 1, 30, 30);
            using var bar = new SolidBrush(Color.White);
            int[] heights = [8, 16, 12, 18, 8];
            for (var i = 0; i < heights.Length; i++)
            {
                graphics.FillRectangle(bar, 7 + i * 4, 16 - heights[i] / 2, 2, heights[i]);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
