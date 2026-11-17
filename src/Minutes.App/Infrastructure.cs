using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Minutes.Core;

namespace Minutes.App;

/// <summary>Where Minutes keeps its own files (never the recordings).</summary>
internal static class AppPaths
{
    public static string Config { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minutes");
    public static string Logs { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Minutes", "logs");
    public static string LogFile => Path.Combine(Logs, "minutes.log");
}

/// <summary>Appends timestamped lines to %LOCALAPPDATA%\Minutes\logs\minutes.log (rotated at 5 MB).</summary>
internal static class FileLogger
{
    private const long MaximumSize = 5 * 1024 * 1024;
    private static readonly object Lock = new();

    public static void Install()
    {
        MinutesLog.Sink = Write;
        MinutesLog.Info($"Minutes {typeof(FileLogger).Assembly.GetName().Version} starting on {Environment.OSVersion}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var info = new FileInfo(AppPaths.LogFile);
                if (info.Exists && info.Length > MaximumSize)
                {
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1", overwrite: true);
                }
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Languages offered for transcription (Whisper codes); "auto" lets Whisper detect it.</summary>
internal static class TranscriptionLanguages
{
    public static readonly (string Code, string Name)[] Options =
    [
        ("es", "Español"), ("en", "English"), ("ca", "Català"), ("eu", "Euskara"), ("gl", "Galego"),
        ("pt", "Português"), ("fr", "Français"), ("de", "Deutsch"), ("it", "Italiano"),
        (TranscriptionPipeline.AutomaticLanguage, "Detectar automáticamente"),
    ];

    public static string SystemDefault
    {
        get
        {
            var code = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Options.Any(option => option.Code == code) ? code : TranscriptionPipeline.AutomaticLanguage;
        }
    }
}

/// <summary>User preferences in %APPDATA%\Minutes\settings.json.</summary>
internal sealed class AppSettings
{
    [JsonPropertyName("autoRecord")] public bool AutoRecord { get; set; } = true;
    [JsonPropertyName("language")] public string Language { get; set; } = TranscriptionLanguages.SystemDefault;
    [JsonPropertyName("vocabulary")] public string Vocabulary { get; set; } = "";
    [JsonPropertyName("recordingsRoot")] public string? RecordingsRoot { get; set; }

    [JsonIgnore] public string MeetingsRoot => string.IsNullOrWhiteSpace(RecordingsRoot) ? MeetingFolder.DefaultRoot : RecordingsRoot;

    /// <summary>The Whisper language, or null to let Whisper detect it.</summary>
    [JsonIgnore] public string? WhisperLanguage => Language == TranscriptionPipeline.AutomaticLanguage ? null : Language;

    private static string FilePath => Path.Combine(AppPaths.Config, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Settings unreadable, using defaults: {error.GetType().Name}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Config);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}

/// <summary>The Groq API key, encrypted for the current Windows user with DPAPI.</summary>
internal static class GroqKeyStore
{
    private static readonly byte[] Entropy = "Minutes.GroqKey.v1"u8.ToArray();
    private static string FilePath => Path.Combine(AppPaths.Config, "groq.key");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Groq key unreadable: {error.GetType().Name}");
            return null;
        }
    }

    public static bool HasKey => !string.IsNullOrWhiteSpace(Load());

    public static void Save(string key)
    {
        Directory.CreateDirectory(AppPaths.Config);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllBytes(FilePath, protectedBytes);
    }

    public static void Remove()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Launch at login through HKCU\...\Run.</summary>
internal static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Minutes";
    public const string BackgroundArgument = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Keeps the registered path current when the exe was moved or updated.</summary>
    public static void Refresh()
    {
        if (IsEnabled)
        {
            Set(true);
        }
    }
}

/// <summary>Windows privacy switch for microphone access by desktop apps.</summary>
internal static class MicrophonePrivacy
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>False when the user or the device turned off microphone access for desktop apps.</summary>
    public static bool DesktopAppsAllowed()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(Store);
            if (key?.GetValue("Value") as string == "Deny")
            {
                return false;
            }
            using var nonPackaged = hive.OpenSubKey(Store + @"\NonPackaged");
            if (nonPackaged?.GetValue("Value") as string == "Deny")
            {
                return false;
            }
        }
        return true;
    }
}

internal static class EmbeddedResources
{
    public static Stream? Open(string name) => typeof(EmbeddedResources).Assembly.GetManifestResourceStream(name);

    public static WhisperTokenizer? LoadTokenizer()
    {
        try
        {
            using var stream = Open("whisper-multilingual.tiktoken");
            if (stream is null)
            {
                MinutesLog.Error("Whisper vocabulary missing; keeping Groq's segment text");
                return null;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return new WhisperTokenizer(reader.ReadToEnd());
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Whisper vocabulary unreadable: {error.GetType().Name}");
            return null;
        }
    }
}
