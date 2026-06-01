// Covers (.NET SDK only):
//   AC-1893: Reset with active session clears session ID (FR-1122)
//   AC-1894: Reset clears in-memory queue with 3 events (FR-1122)
//   AC-1895: Reset writes new device ID to file (FR-1122)
//   AC-1896: Track after Reset uses new device ID (FR-1122, FR-1124)
//   AC-1897: Reset clears breadcrumbs (FR-1122)
//   AC-1898: Reset with no active session does not crash (FR-1122, ED-733)
//   AC-1899: Reset when opted out clears state but stays opted out (FR-1122, ED-734)

using System.Reflection;
using FluentAssertions;
using SoftAgility.Beacon.Internal;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the Reset() method — identity clearing, queue draining, device ID rotation,
/// and breadcrumb clearing. Each test uses a unique Product for directory isolation.
/// </summary>
public sealed class ResetTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* ignore */ }
        }
    }

    private BeaconTracker CreateTrackerWithUniqueDir()
    {
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"ResetTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        return tracker;
    }

    private string GetDataDirectory(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_dataDirectory",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)field!.GetValue(tracker)!;
    }

    private string? GetDeviceId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_deviceId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    private BreadcrumbBuffer? GetBreadcrumbBuffer(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_breadcrumbs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (BreadcrumbBuffer?)field!.GetValue(tracker);
    }

    // ── AC-1893: Reset with active session clears session ID ──

    [Fact]
    public void Reset_WithActiveSession_ClearsSessionId()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.StartSession("user-1");
        TrackerTestHelper.GetSessionId(tracker).Should().NotBeNullOrEmpty(
            "session should be active before reset");

        // Act
        tracker.Reset();

        // Assert
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull(
            "session ID should be cleared after Reset()");
    }

    // ── AC-1894: Reset clears in-memory queue ──

    [Fact]
    public void Reset_ClearsInMemoryQueue()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        tracker.Track("a", "event_1");
        tracker.Track("a", "event_2");
        tracker.Track("a", "event_3");
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(3);

        // Act
        tracker.Reset();

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "in-memory queue should be cleared by Reset()");
    }

    // ── AC-1895: Reset writes new device ID to file ──

    [Fact]
    public void Reset_WritesNewDeviceIdToFile()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        var dataDir = GetDataDirectory(tracker);
        var deviceIdPath = Path.Combine(dataDir, "beacon_device_id");

        var originalDeviceId = File.ReadAllText(deviceIdPath).Trim();
        originalDeviceId.Should().NotBeNullOrEmpty();

        // Act
        tracker.Reset();

        // Assert
        var newDeviceId = File.ReadAllText(deviceIdPath).Trim();
        newDeviceId.Should().NotBeNullOrEmpty();
        newDeviceId.Should().NotBe(originalDeviceId,
            "Reset should generate a new device ID different from the original");

        // The in-memory _deviceId should match the file
        GetDeviceId(tracker).Should().Be(newDeviceId);
    }

    // ── AC-1896: Track after Reset uses new device ID ──

    [Fact]
    public void Track_AfterReset_UsesNewDeviceId()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        var originalDeviceId = GetDeviceId(tracker);
        tracker.Identify("user-1");

        // Act
        tracker.Reset(); // Clears _actorId, generates new device ID
        tracker.Track("feature", "export"); // Should use new device ID

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(tracker);
        docs.Should().HaveCount(1);

        var actorId = docs[0].RootElement.GetProperty("actor_id").GetString();
        actorId.Should().NotBe(originalDeviceId,
            "Track after Reset should use the new device ID, not the original");
        actorId.Should().Be(GetDeviceId(tracker),
            "Track after Reset should use the current _deviceId");

        docs[0].Dispose();
    }

    // ── AC-1897: Reset clears breadcrumbs ──

    [Fact]
    public void Reset_ClearsBreadcrumbs()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        tracker.Track("nav", "page_1");
        tracker.Track("nav", "page_2");
        tracker.Track("nav", "page_3");

        var breadcrumbs = GetBreadcrumbBuffer(tracker);
        breadcrumbs.Should().NotBeNull("breadcrumb buffer should be initialized with default MaxBreadcrumbs > 0");
        breadcrumbs!.Snapshot().Should().HaveCount(3, "3 breadcrumbs should exist before Reset");

        // Act
        tracker.Reset();

        // Assert
        breadcrumbs.Snapshot().Should().BeEmpty(
            "breadcrumb buffer should be cleared by Reset()");
    }

    // ── AC-1898: Reset with no active session does not crash (ED-733) ──

    [Fact]
    public void Reset_WithNoActiveSession_DoesNotCrash()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull(
            "no session should be active before Reset");

        // Act
        var act = () => tracker.Reset();

        // Assert
        act.Should().NotThrow();
    }

    // ── AC-1899: Reset when opted out clears state but stays opted out (ED-734) ──

    [Fact]
    public void Reset_WhenOptedOut_ClearsStateButStaysOptedOut()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        var originalDeviceId = GetDeviceId(tracker);
        tracker.OptOut();
        tracker.LastFlushStatus.Should().Be(FlushStatus.OptedOut);

        // Act
        tracker.Reset();

        // Assert -- opt-out state is unchanged
        tracker.LastFlushStatus.Should().Be(FlushStatus.OptedOut,
            "Reset should NOT change opt-out state");

        // Assert -- device ID has changed
        var newDeviceId = GetDeviceId(tracker);
        newDeviceId.Should().NotBe(originalDeviceId,
            "Reset should generate a new device ID even when opted out");

        // Assert -- actor ID is cleared
        TrackerTestHelper.GetActorId(tracker).Should().BeNull(
            "actor ID should be cleared by Reset even when opted out");
    }

    // ── AC-1893 supplemental: Reset also clears actor ID ──

    [Fact]
    public void Reset_ClearsActorId()
    {
        // Arrange
        var tracker = CreateTrackerWithUniqueDir();
        tracker.Identify("user-1");
        TrackerTestHelper.GetActorId(tracker).Should().Be("user-1");

        // Act
        tracker.Reset();

        // Assert
        TrackerTestHelper.GetActorId(tracker).Should().BeNull(
            "actor ID should be cleared by Reset()");
    }
}
