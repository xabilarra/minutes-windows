using System.Text.RegularExpressions;

namespace Minutes.Core;

/// <summary>
/// An application whose microphone use can indicate a video call.
/// Native meeting apps are trusted on microphone use alone; browsers also need a meeting-like window title.
/// </summary>
/// <remarks>
/// Windows identifies microphone users in the registry (CapabilityAccessManager): desktop apps by their
/// executable ("zoom.exe"), Store apps by their package family ("MSTeams_8wekyb3d8bbwe"). Mic keys are
/// lowercase; a key also matches a package family that starts with it plus "_".
/// </remarks>
public sealed class MeetingApp : IEquatable<MeetingApp>
{
    public string Id { get; }
    public string DisplayName { get; }
    public bool IsBrowser { get; }
    public IReadOnlyList<string> MicKeys { get; }
    /// <summary>Lowercase executable names that own the app's windows.</summary>
    public IReadOnlyList<string> WindowProcesses { get; }

    public MeetingApp(string id, string displayName, bool isBrowser, IReadOnlyList<string> micKeys, IReadOnlyList<string> windowProcesses)
    {
        Id = id;
        DisplayName = displayName;
        IsBrowser = isBrowser;
        MicKeys = micKeys.Select(key => key.ToLowerInvariant()).ToArray();
        WindowProcesses = windowProcesses.Select(name => name.ToLowerInvariant()).ToArray();
    }

    /// <summary>Same app, shown under another name (browser meetings are named after the service).</summary>
    public MeetingApp Renamed(string displayName) => new(Id, displayName, IsBrowser, MicKeys, WindowProcesses);

    /// <summary>The longest mic key matching <paramref name="key"/>, or null.</summary>
    public string? MatchingMicKey(string key)
    {
        var lowered = key.ToLowerInvariant();
        return MicKeys
            .Where(candidate => lowered == candidate || lowered.StartsWith(candidate + "_", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
    }

    public bool MatchesMicKey(string key) => MatchingMicKey(key) is not null;

    public bool OwnsWindow(string processName) => WindowProcesses.Contains(processName.ToLowerInvariant());

    public bool Equals(MeetingApp? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => Equals(obj as MeetingApp);
    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => DisplayName;
}

/// <summary>Known meeting apps and browsers on Windows.</summary>
public static class MeetingAppCatalog
{
    private static MeetingApp Native(string id, string name, string[] mic, string[]? windows = null) =>
        new(id, name, false, mic, windows ?? mic.Where(key => key.EndsWith(".exe", StringComparison.Ordinal)).ToArray());

    private static MeetingApp Browser(string id, string name, string[] mic, string[]? windows = null) =>
        new(id, name, true, mic, windows ?? mic.Where(key => key.EndsWith(".exe", StringComparison.Ordinal)).ToArray());

    private static readonly MeetingApp[] NativeApps =
    [
        Native("zoom", "Zoom", ["zoom.exe"]),
        Native("teams", "Microsoft Teams", ["msteams", "ms-teams.exe", "teams.exe"]),
        Native("slack", "Slack", ["slack.exe", "91750d7e.slack"]),
        Native("webex", "Webex", ["ciscocollabhost.exe", "webexhost.exe", "atmgr.exe", "webex.exe", "ciscowebexstart.exe"]),
        Native("whatsapp", "WhatsApp", ["5319275a.whatsappdesktop", "whatsapp.exe", "whatsapp.root.exe"]),
        Native("discord", "Discord", ["discord.exe"]),
        Native("telegram", "Telegram", ["telegram.exe", "telegrammessengerllp.telegramdesktop"]),
        Native("skype", "Skype", ["skype.exe", "microsoft.skypeapp"]),
    ];

    private static readonly MeetingApp[] Browsers =
    [
        Browser("chrome", "Google Chrome", ["chrome.exe"]),
        Browser("edge", "Microsoft Edge", ["msedge.exe"]),
        Browser("brave", "Brave", ["brave.exe"]),
        Browser("firefox", "Firefox", ["firefox.exe"]),
        Browser("opera", "Opera", ["opera.exe"]),
        Browser("vivaldi", "Vivaldi", ["vivaldi.exe"]),
        Browser("arc", "Arc", ["arc.exe", "thebrowsercompany.arc"]),
    ];

    /// <summary>Native meeting apps first, then browsers.</summary>
    public static IReadOnlyList<MeetingApp> All { get; } = [.. NativeApps, .. Browsers];

    /// <summary>The catalog entry for a mic key; the most specific (longest) matching key wins.</summary>
    public static MeetingApp? AppForMicKey(string key)
    {
        MeetingApp? best = null;
        var bestLength = -1;
        foreach (var app in All)
        {
            var match = app.MatchingMicKey(key);
            if (match is not null && match.Length > bestLength)
            {
                best = app;
                bestLength = match.Length;
            }
        }
        return best;
    }

    public static MeetingApp? AppForWindowProcess(string processName) =>
        All.FirstOrDefault(app => app.OwnsWindow(processName));

    /// <summary>Meeting apps among the given mic keys: non-browsers first (catalog order), then browsers. No duplicates.</summary>
    public static IReadOnlyList<MeetingApp> Candidates(IEnumerable<string> activeMicKeys)
    {
        var present = activeMicKeys.Select(AppForMicKey).OfType<MeetingApp>().ToHashSet();
        var ordered = All.Where(present.Contains).ToList();
        return [.. ordered.Where(app => !app.IsBrowser), .. ordered.Where(app => app.IsBrowser)];
    }
}

/// <summary>
/// Debounced meeting start/stop detection from periodic snapshots of microphone users.
/// A pure state machine: time only advances through the <c>now</c> argument.
/// </summary>
public sealed class MeetingDetector
{
    public sealed record Config(double StartDelay = 3, double StopDelay = 45);

    public abstract record Event(MeetingApp App);
    public sealed record Started(MeetingApp App) : Event(App);
    public sealed record Stopped(MeetingApp App) : Event(App);

    private readonly Config _config;
    private (MeetingApp App, DateTimeOffset Since)? _pendingStart;
    private DateTimeOffset? _releasedSince;
    private bool _isSuppressed;

    public MeetingDetector(Config? config = null) => _config = config ?? new Config();

    public MeetingApp? ActiveMeeting { get; private set; }

    /// <summary>When the active meeting will stop unless its app takes the microphone again.</summary>
    public DateTimeOffset? PendingStopDeadline =>
        ActiveMeeting is not null && _releasedSince is { } since ? since.AddSeconds(_config.StopDelay) : null;

    /// <summary>Feeds one snapshot and returns at most one event.</summary>
    public Event? Update(IReadOnlySet<string> activeMicKeys, bool browserMeetingVisible, DateTimeOffset now)
    {
        if (_isSuppressed)
        {
            // Lifted only once no meeting app (browsers included) holds the microphone.
            if (MeetingAppCatalog.Candidates(activeMicKeys).Count > 0)
            {
                return null;
            }
            _isSuppressed = false;
        }

        if (ActiveMeeting is { } active)
        {
            return CheckStop(active, activeMicKeys, now);
        }

        var candidate = MeetingAppCatalog.Candidates(activeMicKeys).FirstOrDefault(app => !app.IsBrowser || browserMeetingVisible);
        return CheckStart(candidate, now);
    }

    /// <summary>User stopped a recording: no auto-start until no candidate holds the mic. Clears the active meeting.</summary>
    public void SuppressUntilMicReleased()
    {
        _isSuppressed = true;
        ActiveMeeting = null;
        _pendingStart = null;
        _releasedSince = null;
    }

    /// <summary>User started recording <paramref name="app"/> by hand: it stops like an automatic one.</summary>
    public void Adopt(MeetingApp app)
    {
        _isSuppressed = false;
        ActiveMeeting = app;
        _pendingStart = null;
        _releasedSince = null;
    }

    private Event? CheckStart(MeetingApp? candidate, DateTimeOffset now)
    {
        if (candidate is null)
        {
            _pendingStart = null;
            return null;
        }
        if (_pendingStart is not { } pending || !pending.App.Equals(candidate))
        {
            _pendingStart = (candidate, now);
        }
        if ((now - _pendingStart!.Value.Since).TotalSeconds < _config.StartDelay)
        {
            return null;
        }
        _pendingStart = null;
        _releasedSince = null;
        ActiveMeeting = candidate;
        return new Started(candidate);
    }

    private Event? CheckStop(MeetingApp active, IReadOnlySet<string> activeMicKeys, DateTimeOffset now)
    {
        // Only releasing the microphone ends a meeting; browser visibility is ignored here.
        if (activeMicKeys.Any(active.MatchesMicKey))
        {
            _releasedSince = null;
            return null;
        }
        var since = _releasedSince ?? now;
        _releasedSince = since;
        if ((now - since).TotalSeconds < _config.StopDelay)
        {
            return null;
        }
        ActiveMeeting = null;
        _releasedSince = null;
        _pendingStart = null;
        return new Stopped(active);
    }
}

/// <summary>A visible top-level window.</summary>
public sealed record WindowInfo(long Handle, string ProcessName, string Title, double Width, double Height)
{
    internal double Area => Width * Height;
}

/// <summary>Window-title heuristics: confirms browser meetings and picks the meeting window.</summary>
public static class MeetingWindowSelector
{
    public const double MinimumWidth = 300;
    public const double MinimumHeight = 200;

    private static readonly string[] MeetingTitlePrefixes = ["Meet - ", "Meet – "];
    private static readonly string[] MeetingTitleKeywords =
        ["google meet", "zoom meeting", "reunión de zoom", "microsoft teams", "whereby", "jitsi", "webex", "huddle", "discord"];
    private static readonly string[] NativeCallKeywords = ["meeting", "reunión", "call", "llamada", "huddle"];

    public static bool IsMeetingTitle(string title) =>
        MeetingTitlePrefixes.Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || ContainsAny(title, MeetingTitleKeywords);

    /// <summary>Any window owned by a catalog browser with a meeting-like title.</summary>
    public static bool BrowserMeetingVisible(IEnumerable<WindowInfo> windows) =>
        windows.Any(window => MeetingAppCatalog.AppForWindowProcess(window.ProcessName)?.IsBrowser == true && IsMeetingTitle(window.Title));

    /// <summary>The meeting window of <paramref name="app"/>: meeting-like titles first, else the largest; null if none.</summary>
    public static WindowInfo? Select(MeetingApp app, IEnumerable<WindowInfo> windows)
    {
        var eligible = windows
            .Where(window => app.OwnsWindow(window.ProcessName) && window.Width >= MinimumWidth && window.Height >= MinimumHeight)
            .ToList();
        var preferred = eligible
            .Where(window => IsMeetingTitle(window.Title) || (!app.IsBrowser && ContainsAny(window.Title, NativeCallKeywords)))
            .ToList();
        return Largest(preferred.Count > 0 ? preferred : eligible);
    }

    private static WindowInfo? Largest(List<WindowInfo> windows)
    {
        WindowInfo? best = null;
        foreach (var window in windows)
        {
            if (best is null || window.Area > best.Area)
            {
                best = window;
            }
        }
        return best;
    }

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Derives a human meeting title from a meeting window title.</summary>
public static partial class MeetingTitle
{
    private static readonly string[] Separators = [" - ", " – ", " — ", " | "];

    private static readonly string[] NoiseSuffixes =
    [
        "Google Chrome", "Chrome", "Arc", "Microsoft Edge", "Edge", "Brave", "Mozilla Firefox", "Firefox", "Opera", "Vivaldi",
        "Audio playing", "Audio muted", "Camera recording", "Microphone recording",
        "Camera and microphone recording", "Camera or microphone recording",
        "Zoom", "Zoom Meeting", "Zoom Workplace", "Microsoft Teams", "Google Meet",
        "Slack", "Webex", "Cisco Webex", "Discord", "WhatsApp", "Skype", "Telegram", "Whereby", "Jitsi Meet",
    ];

    private static readonly string[] MeetPrefixes = ["Meet - ", "Meet – "];

    private static readonly HashSet<string> GenericTitles =
    [
        "zoom meeting", "zoom meetings", "zoom", "zoom workplace", "reunión de zoom",
        "microsoft teams", "teams", "meet", "google meet", "slack", "whatsapp",
        "webex", "cisco webex", "discord", "skype", "telegram", "whereby", "jitsi meet", "huddle",
        "google chrome", "chrome", "arc", "microsoft edge", "brave",
        "firefox", "mozilla firefox", "opera", "vivaldi", "new tab", "nueva pestaña", "untitled",
    ];

    private static readonly char[] InvisibleCharacters = ['​', '‌', '‍', '⁠', '﻿'];

    /// <summary>Cleans a window title into a meeting title; null when generic or empty.</summary>
    public static string? Clean(string? windowTitle, MeetingApp app)
    {
        if (windowTitle is null)
        {
            return null;
        }
        var title = new string(windowTitle.Where(c => !InvisibleCharacters.Contains(c)).ToArray()).Trim();
        title = StripEdgeProfile(title);
        title = StripNoiseSuffixes(title, app.DisplayName);
        title = StripTrailingEmoji(title);
        title = StripMeetPrefix(title).Trim();
        return title.Length == 0 || IsGeneric(title, app.DisplayName) ? null : title;
    }

    private static readonly (string Name, Func<string, bool> Matches)[] BrowserServices =
    [
        ("Google Meet", t => t.StartsWith("meet - ", StringComparison.Ordinal) || t.StartsWith("meet – ", StringComparison.Ordinal) || t.Contains("google meet")),
        ("Microsoft Teams", t => t.Contains("microsoft teams")),
        ("Zoom", t => t.Contains("zoom")),
        ("Whereby", t => t.Contains("whereby")),
        ("Jitsi Meet", t => t.Contains("jitsi")),
        ("Webex", t => t.Contains("webex")),
        ("Discord", t => t.Contains("discord")),
        ("Slack", t => t.Contains("huddle") || t.Contains("slack")),
    ];

    /// <summary>The meeting service shown in a browser window ("Meet - abc-defg-hij" → "Google Meet"), null when none.</summary>
    public static string? BrowserService(string? windowTitle)
    {
        if (windowTitle is null)
        {
            return null;
        }
        var lowered = windowTitle.ToLowerInvariant();
        return BrowserServices.FirstOrDefault(service => service.Matches(lowered)).Name;
    }

    /// <summary>
    /// Edge on Windows titles windows "Page and 2 more pages - Personal - Microsoft Edge": the tab count and
    /// the profile name sit between the page and the browser, so both are dropped.
    /// </summary>
    private static string StripEdgeProfile(string title)
    {
        const string edge = " - Microsoft Edge";
        if (!title.EndsWith(edge, StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }
        var parts = title[..^edge.Length].Split(" - ").ToList();
        if (parts.Count >= 2)
        {
            parts.RemoveAt(parts.Count - 1);
        }
        var page = string.Join(" - ", parts);
        return MorePagesPattern().Replace(page, "") + edge;
    }

    [GeneratedRegex(@"\s+(and \d+ more pages?|y \d+ páginas? más)$", RegexOptions.IgnoreCase)]
    private static partial Regex MorePagesPattern();

    private static string StripNoiseSuffixes(string title, string appName)
    {
        var noise = NoiseSuffixes.Append(appName).ToArray();
        var result = title;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in noise)
            {
                foreach (var separator in Separators)
                {
                    if (result.EndsWith(separator + suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result[..^(separator.Length + suffix.Length)];
                        changed = true;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Browsers append tab-state emoji to the title ("Meet - abc-defg-hij 🔊").</summary>
    private static string StripTrailingEmoji(string title)
    {
        var runes = title.EnumerateRunes().ToList();
        while (runes.Count > 0)
        {
            var last = runes[^1];
            var isEmoji = last.Value >= 0x1F000 || (last.Value >= 0x2600 && last.Value <= 0x27BF) || last.Value == 0xFE0F;
            if (!isEmoji && !System.Text.Rune.IsWhiteSpace(last))
            {
                break;
            }
            runes.RemoveAt(runes.Count - 1);
        }
        return string.Concat(runes.Select(rune => rune.ToString()));
    }

    private static string StripMeetPrefix(string title)
    {
        foreach (var prefix in MeetPrefixes)
        {
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return title[prefix.Length..];
            }
        }
        return title;
    }

    private static bool IsGeneric(string title, string appName)
    {
        var lowered = title.ToLowerInvariant();
        return GenericTitles.Contains(lowered) || lowered == appName.ToLowerInvariant() || MeetCodePattern().IsMatch(lowered);
    }

    [GeneratedRegex("^[a-z]{3}-[a-z]{4}-[a-z]{3}$")]
    private static partial Regex MeetCodePattern();
}
