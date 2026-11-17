using System.Diagnostics;
using Minutes.Core;

namespace Minutes.App.UI;

/// <summary>Groq key, language, vocabulary and behaviour. Changes apply when saved.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 360 };
    private readonly Label _keyState = new() { AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _vocabulary = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 80 };
    private readonly CheckBox _autoRecord = new() { AutoSize = true, Text = "Grabar automáticamente al detectar una llamada" };
    private readonly CheckBox _launchAtLogin = new() { AutoSize = true, Text = "Abrir Minutes al iniciar sesión en Windows" };
    private readonly Label _privacy = new() { AutoSize = true, ForeColor = Color.FromArgb(185, 28, 28), MaximumSize = new Size(440, 0) };

    public event Action? Saved;

    public SettingsForm()
    {
        Text = "Minutes — Ajustes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);
        if (EmbeddedResources.Open("Minutes.ico") is { } iconStream)
        {
            using (iconStream)
            {
                Icon = new Icon(iconStream);
            }
        }

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(layout);

        layout.Controls.Add(Heading("Clave de API de Groq"));
        layout.Controls.Add(Hint("Se guarda cifrada para tu usuario de Windows. Transcribir cuesta unos 0,11 $ por hora de audio."));
        var keyRow = Row(_key, LinkButton("Obtener clave", () => Open("https://console.groq.com/keys")));
        layout.Controls.Add(keyRow);
        layout.Controls.Add(_keyState);

        layout.Controls.Add(Heading("Idioma de las reuniones"));
        foreach (var (_, name) in TranscriptionLanguages.Options)
        {
            _language.Items.Add(name);
        }
        layout.Controls.Add(_language);

        layout.Controls.Add(Heading("Vocabulario"));
        layout.Controls.Add(Hint("Nombres, marcas y palabras que deben salir bien escritos, separados por comas o líneas."));
        layout.Controls.Add(_vocabulary);

        layout.Controls.Add(Heading("Comportamiento"));
        layout.Controls.Add(_autoRecord);
        layout.Controls.Add(_launchAtLogin);
        layout.Controls.Add(_privacy);

        layout.Controls.Add(Heading("Grabaciones"));
        var settings = AppSettings.Load();
        layout.Controls.Add(Row(Hint(settings.MeetingsRoot), LinkButton("Abrir carpeta", () => OpenFolder(settings.MeetingsRoot))));
        layout.Controls.Add(Row(Hint("Registro de diagnóstico"), LinkButton("Abrir registro", () => OpenFolder(AppPaths.Logs))));

        var save = new Button { Text = "Guardar", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveAndClose();
        AcceptButton = save;
        CancelButton = cancel;
        layout.Controls.Add(Row(save, cancel));

        LoadValues(settings);
    }

    private void LoadValues(AppSettings settings)
    {
        var hasKey = GroqKeyStore.HasKey;
        _key.PlaceholderText = hasKey ? "Clave guardada (escribe otra para cambiarla)" : "gsk_…";
        _keyState.Text = hasKey ? "✓ Clave guardada" : "Falta la clave: sin ella no se puede transcribir.";
        _keyState.ForeColor = hasKey ? Color.FromArgb(21, 128, 61) : Color.FromArgb(185, 28, 28);
        var index = Array.FindIndex(TranscriptionLanguages.Options, option => option.Code == settings.Language);
        _language.SelectedIndex = index >= 0 ? index : 0;
        _vocabulary.Text = settings.Vocabulary.Replace("\n", Environment.NewLine);
        _autoRecord.Checked = settings.AutoRecord;
        _launchAtLogin.Checked = LoginItem.IsEnabled;
        _privacy.Text = MicrophonePrivacy.DesktopAppsAllowed()
            ? ""
            : "Windows no deja a las apps de escritorio usar el micrófono: actívalo en Configuración > Privacidad y seguridad > Micrófono.";
    }

    private void SaveAndClose()
    {
        var key = _key.Text.Trim();
        if (key.Length > 0)
        {
            if (!key.StartsWith("gsk_", StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Las claves de Groq empiezan por \"gsk_\". Revisa que la hayas copiado entera.", "Minutes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            GroqKeyStore.Save(key);
        }
        var settings = AppSettings.Load();
        settings.Language = TranscriptionLanguages.Options[Math.Max(0, _language.SelectedIndex)].Code;
        settings.Vocabulary = _vocabulary.Text.Replace("\r\n", "\n").Trim();
        settings.AutoRecord = _autoRecord.Checked;
        settings.Save();
        try
        {
            LoginItem.Set(_launchAtLogin.Checked);
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Launch at login failed: {error.GetType().Name}");
        }
        Saved?.Invoke();
        Close();
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10f),
        Margin = new Padding(0, 12, 0, 2),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(440, 0),
        Margin = new Padding(0, 0, 0, 4),
    };

    private static Button LinkButton(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    internal static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Opening failed: {error.GetType().Name}");
        }
    }

    internal static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Open(folder);
    }
}
