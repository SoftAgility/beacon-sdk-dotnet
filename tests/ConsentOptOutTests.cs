// Covers (.NET SDK only):
//   AC-1881: Init with opt-out file -> FlushStatus.OptedOut, Track is no-op (FR-1119)
//   AC-1882: Init without opt-out file -> normal tracking (FR-1119)
//   AC-1883: OptOut sets status, creates file, clears queue (FR-1120)
//   AC-1884: OptOut with 5 queued events -> queue empty (FR-1120)
//   AC-1885: Track after OptOut is no-op (FR-1120, FR-1127)
//   AC-1886: StartSession after OptOut is no-op (FR-1120, FR-1127)
//   AC-1887: EndSession after OptOut is no-op (FR-1120, FR-1127)
//   AC-1888: FlushAsync after OptOut returns completed task (FR-1120, FR-1127)
//   AC-1889: Identify after OptOut is no-op (FR-1120, FR-1127)
//   AC-1890: ExportEventManifest works when opted out (FR-1120)
//   AC-1891: OptIn after OptOut removes file, tracking resumes (FR-1121)
//   AC-1892: OptIn when not opted out is idempotent (FR-1121, ED-732)
//   AC-1906: FlushStatus.OptedOut enum value exists (FR-1137)
//   ED-735: OptOut called twice is idempotent (FR-1120)

using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for runtime consent toggling: OptOut(), OptIn(), and FlushStatus.OptedOut.
/// Each test uses a unique Product to isolate data directory side effects.
/// </summary>
public sealed class ConsentOptOutTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* ignore */ }
        }
    }

    private BeaconTracker CreateTrackerWithUniqueDir(bool enabled = true)
    {
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"ConsentTest_{Guid.NewGuid():N}",
            enabled: enabled);
        _disposables.Add(tracker);
        return tracker;
    }

    private string GetDataDirectory(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_dataDirectory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)field!.GetValue(tracker)!;
    }

    private string? GetDeviceId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_deviceId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    private BeaconTracker CreateTrackerWithOptOutFile()
    {
        // Create a tracker to set up the directory, then dispose it,
        // plant the opt-out file, and create a new tracker with the same Product.
        var appName = $"ConsentTest_{Guid.NewGuid():N}";
        var tempTracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });
        var dataDir = GetDataDirectory(tempTracker);
        tempTracker.Dispose();

        // Create the sentinel file
        File.Create(Path.Combine(dataDir, "beacon_opted_out")).Dispose();

        // Create a new tracker with the same Product -- it should read the opt-out file
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });
        _disposables.Add(tracker);
        return tracker;
    }

    // ── AC-1881: Init with opt-out file present -> FlushStatus.OptedOut ──

    [Fact]
    public void Init_WithOptOutFilePresent_SetsFlushStatusToOptedOut()
    {
        // Arrange & Act
        var tracker = CreateTrackerWithOptOutFile();

        // Assert
        tracker.LastFlushStatus.Should().Be(FlushStatus.OptedOut);
    }

    [Fact]
    public void Track_WhenInitOptedOut_IsNoOp()
    {
        // Arrange
        var tracker = CreateTrackerWithOptOutFile();

        // Act -- Track should be a no-op, not throw
        var act = () => tracker.Track("feature", "export", "user-1");

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "no events should be queued when SDK started opted out");
    }

    // ── AC-1882: Init without opt-out file -> normal tracking ──

    [Fact]
    public void Init_WithoutOptOutFile_TracksNormally()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();

        // Act
        tracker.Identify("user-1");
        tracker.Track("feature", "export");

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(1);
        tracker.LastFlushStatus.Should().NotBe(FlushStatus.OptedOut);
    }

    // ── AC-1883: OptOut sets status, creates file, clears queue ──

    [Fact]
    public void OptOut_SetsStatusCreatesFileAndClearsQueue()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        tracker.Track("analytics", "page_view");
        tracker.Track("analytics", "click");
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(2);

        // Act
        tracker.OptOut();

        // Assert
        tracker.LastFlushStatus.Should().Be(FlushStatus.OptedOut);
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "in-memory queue should be cleared by OptOut");

        var dataDir = GetDataDirectory(tracker);
        File.Exists(Path.Combine(dataDir, "beacon_opted_out")).Should().BeTrue(
            "beacon_opted_out sentinel file should be created");
    }

    // ── AC-1884: OptOut with 5 queued events -> queue empty ──

    [Fact]
    public void OptOut_With5QueuedEvents_ClearsQueue()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        for (var i = 0; i < 5; i++)
            tracker.Track("analytics", $"event_{i}");
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(5);

        // Act
        tracker.OptOut();

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty();
    }

    // ── AC-1885: Track after OptOut is no-op ──

    [Fact]
    public void Track_AfterOptOut_IsNoOp()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.OptOut();

        // Act -- both overloads should be no-op
        var act1 = () => tracker.Track("analytics", "click", "user-1");
        var act2 = () => tracker.Track("analytics", "click");

        // Assert
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty();
    }

    // ── AC-1886: StartSession after OptOut is no-op ──

    [Fact]
    public void StartSession_AfterOptOut_IsNoOp()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.OptOut();

        // Act
        var act1 = () => tracker.StartSession("user-1");
        var act2 = () => tracker.StartSession();

        // Assert
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull(
            "no session should be started when opted out");
    }

    // ── AC-1887: EndSession after OptOut is no-op ──

    [Fact]
    public void EndSession_AfterOptOut_IsNoOp()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.OptOut();

        // Act
        var act = () => tracker.EndSession();

        // Assert
        act.Should().NotThrow();
    }

    // ── AC-1888: FlushAsync after OptOut returns completed task ──

    [Fact]
    public async Task FlushAsync_AfterOptOut_ReturnsCompletedTask()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.OptOut();

        // Act
        var task = tracker.FlushAsync();

        // Assert
        task.IsCompleted.Should().BeTrue(
            "FlushAsync should return a completed task when opted out");
        await task; // Should not throw
    }

    // ── AC-1889: Identify after OptOut is no-op ──

    [Fact]
    public void Identify_AfterOptOut_IsNoOp()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.OptOut();

        // Act
        var act = () => tracker.Identify("user-1");

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetActorId(tracker).Should().BeNull(
            "actor ID should not be set when opted out");
    }

    // ── AC-1890: ExportEventManifest works when opted out ──

    [Fact]
    public void ExportEventManifest_WhenOptedOut_WritesValidManifest()
    {
        // Arrange
        var appName = $"ManifestTest_{Guid.NewGuid():N}";
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600,
            Events = { }
        });
        _disposables.Add(tracker);

        // Add an event definition before opting out
        // (Events are registered at configure time, not at Track time)
        tracker.OptOut();

        var manifestPath = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid():N}.json");

        try
        {
            // Act
            var act = () => tracker.ExportEventManifest(manifestPath);

            // Assert
            act.Should().NotThrow();
            File.Exists(manifestPath).Should().BeTrue();
            var content = File.ReadAllText(manifestPath);
            content.Should().Contain("schema_version");
            content.Should().Contain("product");
        }
        finally
        {
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
        }
    }

    // ── AC-1891: OptIn after OptOut removes file and resumes tracking ──

    [Fact]
    public void OptIn_AfterOptOut_RemovesFileAndResumesTracking()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        tracker.OptOut();

        var dataDir = GetDataDirectory(tracker);
        File.Exists(Path.Combine(dataDir, "beacon_opted_out")).Should().BeTrue(
            "opt-out file should exist after OptOut");

        // Act
        tracker.OptIn();

        // Assert -- file removed
        File.Exists(Path.Combine(dataDir, "beacon_opted_out")).Should().BeFalse(
            "beacon_opted_out file should be removed after OptIn");

        // Assert -- tracking resumes (need to re-identify since OptOut cleared nothing;
        // the Identify before OptOut was accepted, so we need to re-identify post-OptIn)
        tracker.Identify("user-1");
        tracker.Track("analytics", "click");
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(1,
            "events should be queued normally after OptIn");
    }

    // ── AC-1892: OptIn when not opted out is idempotent (ED-732) ──

    [Fact]
    public void OptIn_WhenNotOptedOut_IsIdempotent()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();

        // Act -- calling OptIn when not opted out
        var act = () => tracker.OptIn();

        // Assert
        act.Should().NotThrow();
        tracker.LastFlushStatus.Should().NotBe(FlushStatus.OptedOut,
            "OptIn on non-opted-out tracker should not change state");
    }

    // ── AC-1906: FlushStatus.OptedOut enum value exists (FR-1137) ──

    [Fact]
    public void FlushStatus_OptedOut_EnumValueExists()
    {
        // Assert
        Enum.IsDefined(typeof(FlushStatus), FlushStatus.OptedOut).Should().BeTrue(
            "FlushStatus enum must contain an OptedOut value");
    }

    // ── ED-735: OptOut called twice is idempotent ──

    [Fact]
    public void OptOut_CalledTwice_IsIdempotent()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();

        // Act
        var act = () =>
        {
            tracker.OptOut();
            tracker.OptOut();
        };

        // Assert
        act.Should().NotThrow();
        tracker.LastFlushStatus.Should().Be(FlushStatus.OptedOut);
    }
}
