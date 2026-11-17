using System.Globalization;
using Minutes.App.UI;
using Minutes.Core;

namespace Minutes.App;

internal static class Program
{
    private const string InstanceMutex = @"Local\Minutes.xabilarra.instance";
    private const string ShowEvent = @"Local\Minutes.xabilarra.show";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        FileLogger.Install();
        Application.ThreadException += (_, e) => MinutesLog.Error($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => MinutesLog.Error($"Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => MinutesLog.Error($"Unobserved task exception: {e.Exception}");

        if (SmokeTestSeconds(args) is { } seconds)
        {
            return SmokeTest.Run(seconds);
        }

        using var mutex = new Mutex(initiallyOwned: true, InstanceMutex, out var isFirst);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        if (!isFirst)
        {
            // Opening Minutes again brings the running instance's settings window forward.
            show.Set();
            return 0;
        }

        using var app = new TrayApplication(startedAtLogin: args.Contains(LoginItem.BackgroundArgument));
        var listener = new Thread(() =>
        {
            while (show.WaitOne())
            {
                app.ShowSettingsFromAnyThread();
            }
        }) { IsBackground = true, Name = "Minutes show listener" };
        listener.Start();
        Application.Run(app);
        MinutesLog.Info("Minutes exiting");
        return 0;
    }

    private static int? SmokeTestSeconds(string[] args)
    {
        var index = Array.IndexOf(args, "--smoke-test");
        if (index < 0)
        {
            return null;
        }
        return index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 3, 600)
            : 20;
    }
}

/// <summary>
/// End-to-end check: "Minutes.exe --smoke-test 20" records 20 seconds, transcribes them and shows the result.
/// </summary>
internal static class SmokeTest
{
    public static int Run(int seconds)
    {
        MinutesLog.Info($"Smoke test: {seconds} s");
        using var ui = new Control();
        ui.CreateControl();
        using var coordinator = new RecordingCoordinator(ui);
        coordinator.Start(autoDetect: false);
        coordinator.WaitForStartup();
        coordinator.StartManualRecording();
        if (coordinator.Session is null)
        {
            MessageBox.Show($"No se pudo empezar a grabar.\n\n{coordinator.LastProblem}\n\nRegistro: {AppPaths.LogFile}", "Minutes — prueba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var folder = coordinator.Session.Folder;
        using (var progress = new Form { Text = "Minutes — prueba", Width = 420, Height = 120, StartPosition = FormStartPosition.CenterScreen, TopMost = true })
        {
            var label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10f) };
            progress.Controls.Add(label);
            var done = false;
            // Closing early would leave the recording running and report a missing transcript.
            progress.FormClosing += (_, e) => e.Cancel = !done;
            var started = DateTime.UtcNow;
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) =>
            {
                var left = seconds - (DateTime.UtcNow - started).TotalSeconds;
                if (left > 0)
                {
                    label.Text = $"Grabando {coordinator.Session?.AppName}… habla y pon audio. Quedan {Math.Ceiling(left)} s";
                    return;
                }
                if (coordinator.Session is not null)
                {
                    coordinator.StopRecording(manual: true);
                    label.Text = "Transcribiendo…";
                    coordinator.WhenIdleAsync().ContinueWith(_ => progress.BeginInvoke(() =>
                    {
                        done = true;
                        progress.Close();
                    }));
                }
            };
            timer.Start();
            progress.ShowDialog();
            timer.Dispose();
        }
        var transcript = Path.Combine(folder, MeetingFile.TranscriptMarkdown);
        var ok = File.Exists(transcript);
        var body = ok ? File.ReadAllText(transcript) : MetadataStore.TryRead(folder, out var metadata) ? metadata.Transcription.Error ?? "Sin transcripción." : "Sin transcripción.";
        MessageBox.Show($"{(ok ? "Funciona." : "Algo ha fallado.")}\n\nCarpeta: {folder}\n\n{Truncate(body, 1500)}\n\nRegistro: {AppPaths.LogFile}",
            "Minutes — prueba", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        return ok ? 0 : 1;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
