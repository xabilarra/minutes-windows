using System.Text.Json;
using Minutes.Core;

namespace Minutes.Core.Tests;

public class MeetingFolderTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NamesFolderWithTitleAndApp() =>
        Assert.Equal("2026-10-20 10.30 Weekly sync (Google Meet)", MeetingFolder.FolderName(Start, "Weekly sync", "Google Meet", Madrid));

    [Fact]
    public void NamesFolderWithAppOnlyWhenTitleIsMissing()
    {
        Assert.Equal("2026-10-20 10.30 Zoom", MeetingFolder.FolderName(Start, null, "Zoom", Madrid));
        Assert.Equal("2026-10-20 10.30 Zoom", MeetingFolder.FolderName(Start, " :/ ", "Zoom", Madrid));
    }

    [Fact]
    public void SanitizeRemovesForbiddenCharactersAndCollapsesWhitespace() =>
        Assert.Equal("abc d", MeetingFolder.Sanitize("a/b:c\\\n\td?*"));

    [Fact]
    public void SanitizeCutsAtEightyCharactersWithoutTrailingSpaceOrDot()
    {
        var text = new string('x', 79) + " yyyy";
        Assert.Equal(new string('x', 79), MeetingFolder.Sanitize(text));
        Assert.Equal("Sync", MeetingFolder.Sanitize("Sync..."));
    }

    [Fact]
    public void UniquePathAddsCounter()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "a (2)"));
            Assert.Equal(Path.Combine(root, "a (3)"), MeetingFolder.UniquePath(root, "a"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class MetadataTests
{
    [Fact]
    public void WritesSortedKeysIsoDatesAndOmitsNulls()
    {
        var metadata = new MeetingMetadata
        {
            AppName = "Zoom",
            AppBundleId = "zoom.exe",
            Trigger = "auto",
            StartedAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, 250, TimeSpan.Zero),
        };
        var json = MinutesJson.Serialize(metadata);
        Assert.Contains("\"startedAt\": \"2026-10-20T08:30:00.250Z\"", json);
        Assert.Contains("\"state\": \"pending\"", json);
        Assert.DoesNotContain("endedAt", json);
        Assert.DoesNotContain("null", json);
        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
    }

    [Fact]
    public void RoundTripsThroughTheFolderAndReadsDatesWithoutFraction()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var metadata = new MeetingMetadata
            {
                AppName = "Teams",
                StartedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Transcription = new(TranscriptionState.Failed, "whisper-large-v3", "es", "boom"),
            };
            MetadataStore.Write(metadata, folder);
            Assert.Equal(metadata, MetadataStore.Read(folder));

            File.WriteAllText(Path.Combine(folder, "meta.json"),
                """{"appName":"X","hasVideo":false,"schemaVersion":1,"startedAt":"2026-01-02T03:04:05Z","transcription":{"state":"done"},"trigger":"manual"}""");
            Assert.Equal(TranscriptionState.Done, MetadataStore.Read(folder).Transcription.State);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void WriteRefusesMissingFolder() =>
        Assert.Throws<DirectoryNotFoundException>(() => MetadataStore.Write(new MeetingMetadata(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
}

public class MeetingTitleTests
{
    private static readonly MeetingApp Chrome = MeetingAppCatalog.AppForMicKey("chrome.exe")!;
    private static readonly MeetingApp Edge = MeetingAppCatalog.AppForMicKey("msedge.exe")!;
    private static readonly MeetingApp Teams = MeetingAppCatalog.AppForMicKey("MSTeams_8wekyb3d8bbwe")!;

    [Theory]
    [InlineData("Meet - Weekly sync - Google Chrome", "Weekly sync")]
    [InlineData("Meet - abc-defg-hij 🔊 - Google Chrome", null)]
    [InlineData("Daily | Microsoft Teams", "Daily")]
    [InlineData("Zoom Meeting", null)]
    [InlineData("Weekly - Camera and microphone recording - Google Chrome", "Weekly")]
    public void CleansChromeAndTeamsTitles(string window, string? expected)
    {
        var app = window.Contains("Teams") ? Teams : Chrome;
        Assert.Equal(expected, MeetingTitle.Clean(window, app));
    }

    [Theory]
    [InlineData("Meet - Weekly sync - Personal - Microsoft​ Edge", "Weekly sync")]
    [InlineData("Meet - Weekly sync and 2 more pages - Work - Microsoft Edge", "Weekly sync")]
    public void DropsEdgeProfileAndTabCount(string window, string expected) =>
        Assert.Equal(expected, MeetingTitle.Clean(window, Edge));

    [Theory]
    [InlineData("Meet - abc-defg-hij - Google Chrome", "Google Meet")]
    [InlineData("Launch Meeting - Zoom - Google Chrome", "Zoom")]
    [InlineData("Inbox - Gmail", null)]
    public void NamesBrowserService(string window, string? expected) =>
        Assert.Equal(expected, MeetingTitle.BrowserService(window));
}

public class MeetingAppCatalogTests
{
    [Fact]
    public void MatchesExecutablesAndPackageFamilies()
    {
        Assert.Equal("teams", MeetingAppCatalog.AppForMicKey("MSTeams_8wekyb3d8bbwe")!.Id);
        Assert.Equal("whatsapp", MeetingAppCatalog.AppForMicKey("5319275A.WhatsAppDesktop_cv1g1gvanyjgm")!.Id);
        Assert.Equal("zoom", MeetingAppCatalog.AppForMicKey("Zoom.exe")!.Id);
        Assert.Null(MeetingAppCatalog.AppForMicKey("msteamsx_123"));
        Assert.Null(MeetingAppCatalog.AppForMicKey("notepad.exe"));
    }

    [Fact]
    public void CandidatesPutNativeAppsBeforeBrowsers()
    {
        var candidates = MeetingAppCatalog.Candidates(["chrome.exe", "zoom.exe", "notepad.exe", "zoom.exe"]);
        Assert.Equal(["zoom", "chrome"], candidates.Select(app => app.Id));
    }
}

public class MeetingDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static HashSet<string> Keys(params string[] keys) => [.. keys];

    [Fact]
    public void StartsAfterThreeSecondsAndStopsFortyFiveSecondsAfterRelease()
    {
        var detector = new MeetingDetector();
        Assert.Null(detector.Update(Keys("zoom.exe"), false, T0));
        Assert.Null(detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(2)));
        Assert.IsType<MeetingDetector.Started>(detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(3)));
        Assert.Null(detector.Update(Keys(), false, T0.AddSeconds(10)));
        Assert.Equal(T0.AddSeconds(55), detector.PendingStopDeadline);
        Assert.Null(detector.Update(Keys(), false, T0.AddSeconds(54)));
        Assert.IsType<MeetingDetector.Stopped>(detector.Update(Keys(), false, T0.AddSeconds(55)));
        Assert.Null(detector.ActiveMeeting);
    }

    [Fact]
    public void TakingTheMicAgainCancelsTheStop()
    {
        var detector = new MeetingDetector();
        detector.Adopt(MeetingAppCatalog.AppForMicKey("zoom.exe")!);
        detector.Update(Keys(), false, T0);
        detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(20));
        Assert.Null(detector.Update(Keys(), false, T0.AddSeconds(50)));
        Assert.NotNull(detector.ActiveMeeting);
    }

    [Fact]
    public void BrowsersNeedAMeetingWindow()
    {
        var detector = new MeetingDetector();
        detector.Update(Keys("chrome.exe"), false, T0);
        Assert.Null(detector.Update(Keys("chrome.exe"), false, T0.AddSeconds(5)));
        detector.Update(Keys("chrome.exe"), true, T0.AddSeconds(6));
        Assert.IsType<MeetingDetector.Started>(detector.Update(Keys("chrome.exe"), true, T0.AddSeconds(9)));
    }

    [Fact]
    public void ManualStopSuppressesUntilTheMicIsReleased()
    {
        var detector = new MeetingDetector();
        detector.Update(Keys("zoom.exe"), false, T0);
        detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(3));
        detector.SuppressUntilMicReleased();
        Assert.Null(detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(10)));
        Assert.Null(detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(20)));
        Assert.Null(detector.Update(Keys(), false, T0.AddSeconds(21)));
        detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(22));
        Assert.IsType<MeetingDetector.Started>(detector.Update(Keys("zoom.exe"), false, T0.AddSeconds(25)));
    }
}

public class MeetingWindowSelectorTests
{
    [Fact]
    public void DetectsBrowserMeetingsByTitle()
    {
        Assert.True(MeetingWindowSelector.BrowserMeetingVisible([new WindowInfo(1, "chrome.exe", "Meet - abc-defg-hij - Google Chrome", 1200, 800)]));
        Assert.False(MeetingWindowSelector.BrowserMeetingVisible([new WindowInfo(1, "chrome.exe", "Inbox - Gmail", 1200, 800)]));
        Assert.False(MeetingWindowSelector.BrowserMeetingVisible([new WindowInfo(1, "notepad.exe", "Meet - x", 1200, 800)]));
    }

    [Fact]
    public void PrefersMeetingTitlesThenLargestWindow()
    {
        var zoom = MeetingAppCatalog.AppForMicKey("zoom.exe")!;
        var windows = new[]
        {
            new WindowInfo(1, "zoom.exe", "Zoom Workplace", 1600, 1000),
            new WindowInfo(2, "zoom.exe", "Zoom Meeting", 1000, 700),
            new WindowInfo(3, "zoom.exe", "Zoom Meeting", 200, 100),
            new WindowInfo(4, "chrome.exe", "Zoom Meeting", 2000, 2000),
        };
        Assert.Equal(2, MeetingWindowSelector.Select(zoom, windows)!.Handle);
        Assert.Equal(1, MeetingWindowSelector.Select(zoom, windows.Where(window => window.Handle != 2))!.Handle);
        Assert.Null(MeetingWindowSelector.Select(zoom, []));
    }
}
