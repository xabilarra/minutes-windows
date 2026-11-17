using System.Drawing.Drawing2D;

namespace Minutes.App.UI;

/// <summary>
/// The small floating pill under the top edge of the screen. It stays above full-screen meetings and never
/// takes the focus from the call (WS_EX_NOACTIVATE).
/// </summary>
internal sealed class PillForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    private static readonly Color Background = Color.FromArgb(32, 28, 40);
    private static readonly Color Accent = Color.FromArgb(139, 92, 246);

    private readonly Label _dot = new() { AutoSize = true, Text = "●", ForeColor = Color.FromArgb(239, 68, 68), Font = new Font("Segoe UI", 11f) };
    private readonly Label _text = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f) };
    private readonly FlowLayoutPanel _buttons = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _row = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(14, 8, 10, 8) };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _autoHide = new();
    private DateTimeOffset? _recordingSince;
    private string _appName = "";
    private int? _stopCountdown;

    public PillForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Text = "Minutes";

        _dot.Margin = new Padding(0, 3, 4, 0);
        _text.Margin = new Padding(0, 6, 10, 0);
        _row.Controls.AddRange([_dot, _text, _buttons]);
        Controls.Add(_row);

        _clock.Tick += (_, _) => RenderRecording();
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            Hide();
        };
        SizeChanged += (_, _) => Reposition();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
            return parameters;
        }
    }

    public void ShowRecording(string appName, DateTimeOffset since, Action onStop)
    {
        _recordingSince = since;
        _appName = appName;
        _stopCountdown = null;
        SetButtons(("Detener", onStop, true));
        _dot.Visible = true;
        _clock.Start();
        RenderRecording();
        Present(autoHideAfter: null);
    }

    public void UpdateStopCountdown(int? seconds)
    {
        if (_recordingSince is null || _stopCountdown == seconds)
        {
            return;
        }
        _stopCountdown = seconds;
        RenderRecording();
    }

    public void ShowTranscribing()
    {
        StopClock();
        _dot.Visible = false;
        _text.Text = "Transcribiendo la reunión…";
        SetButtons();
        Present(autoHideAfter: TimeSpan.FromSeconds(8));
    }

    public void ShowReady(Action copyText, Action copyPath, Action openFolder)
    {
        StopClock();
        _dot.Visible = false;
        _text.Text = "Transcripción lista";
        SetButtons(("Copiar texto", copyText, true), ("Copiar ruta", copyPath, false), ("Abrir", openFolder, false), ("✕", Hide, false));
        Present(autoHideAfter: TimeSpan.FromMinutes(2));
    }

    public void ShowFailed(string message, Action retry)
    {
        StopClock();
        _dot.Visible = false;
        _text.Text = message.Length > 70 ? "No se pudo transcribir" : $"No se pudo transcribir: {message}";
        SetButtons(("Reintentar", retry, true), ("✕", Hide, false));
        Present(autoHideAfter: TimeSpan.FromMinutes(2));
    }

    public void Flash(string message)
    {
        _text.Text = message;
        Present(autoHideAfter: TimeSpan.FromSeconds(2));
    }

    private void RenderRecording()
    {
        if (_recordingSince is not { } since)
        {
            return;
        }
        var elapsed = DateTimeOffset.Now - since;
        var time = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        _text.Text = _stopCountdown is { } seconds
            ? $"¿Has colgado? Se detiene en {seconds} s"
            : $"{_appName} · {time}";
        if (_stopCountdown is not null && _buttons.Controls.Count == 1 && _buttons.Controls[0] is Button stop)
        {
            stop.Text = "Detener ya";
        }
        else if (_stopCountdown is null && _buttons.Controls.Count == 1 && _buttons.Controls[0] is Button running)
        {
            running.Text = "Detener";
        }
    }

    private void StopClock()
    {
        _clock.Stop();
        _recordingSince = null;
        _stopCountdown = null;
    }

    private void SetButtons(params (string Text, Action OnClick, bool Primary)[] buttons)
    {
        _buttons.SuspendLayout();
        foreach (Control control in _buttons.Controls)
        {
            control.Dispose();
        }
        _buttons.Controls.Clear();
        foreach (var (text, onClick, primary) in buttons)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = primary ? Accent : Color.FromArgb(60, 56, 70),
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(4, 0, 0, 0),
                Padding = new Padding(6, 0, 6, 0),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => onClick();
            _buttons.Controls.Add(button);
        }
        _buttons.ResumeLayout();
    }

    private void Present(TimeSpan? autoHideAfter)
    {
        _autoHide.Stop();
        if (autoHideAfter is { } delay)
        {
            _autoHide.Interval = (int)delay.TotalMilliseconds;
            _autoHide.Start();
        }
        if (!Visible)
        {
            Show();
        }
        Reposition();
    }

    private void Reposition()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 8);
        using var path = RoundedRectangle(new Rectangle(Point.Empty, Size), Height / 2);
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            _autoHide.Dispose();
        }
        base.Dispose(disposing);
    }
}
