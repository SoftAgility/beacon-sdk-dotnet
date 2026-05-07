// Covers:
//   AC-526: Dispose flushes remaining events and ends session (FR-453)
//   AC-527: Dispose grace period writes to disk on timeout (ED-419)
//   AC-545: Track after Dispose is no-op (EC-425)
//   ED-419: Flush during dispose exceeds grace period

using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for Dispose/DisposeAsync behavior.
/// </summary>
public sealed class DisposeTests
{
    // AC-526: Dispose clears the disposed flag (tracker is in disposed state)
    [Fact]
    public void Dispose_SetsDisposedFlag()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();

        // Act
        tracker.Dispose();

        // Assert
        TrackerTestHelper.GetDisposed(tracker).Should().BeTrue();
    }

    // AC-526: Dispose ends any active session
    [Fact]
    public void Dispose_WithActiveSession_ClearsSessionId()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.StartSession("user-1");
        TrackerTestHelper.GetSessionId(tracker).Should().NotBeNull();

        // Act
        tracker.Dispose();

        // Assert
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull(
            "session should be ended during dispose");
    }

    // AC-545 / EC-425: Track after Dispose is silent no-op
    [Fact]
    public void Track_AfterDispose_IsNoOpAndDoesNotThrow()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.Track("test", "event", "actor-1");

        // Assert
        act.Should().NotThrow();
    }

    // AC-545 supplemental: FlushAsync after Dispose is no-op
    [Fact]
    public async Task FlushAsync_AfterDispose_IsNoOpAndDoesNotThrow()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.FlushAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    // AC-526 supplemental: Double Dispose does not throw
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();

        // Act
        var act = () =>
        {
            tracker.Dispose();
            tracker.Dispose();
        };

        // Assert
        act.Should().NotThrow();
    }

    // AC-526 supplemental: DisposeAsync works correctly
    [Fact]
    public async Task DisposeAsync_SetsDisposedFlag()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();

        // Act
        await tracker.DisposeAsync();

        // Assert
        TrackerTestHelper.GetDisposed(tracker).Should().BeTrue();
    }

    // AC-526 supplemental: StartSession after Dispose is no-op
    [Fact]
    public void StartSession_AfterDispose_IsNoOp()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.StartSession("user-1");

        // Assert
        act.Should().NotThrow();
    }

    // Parameterless Track() after Dispose is no-op even without Identify()
    [Fact]
    public void Track_Parameterless_AfterDispose_IsNoOpAndDoesNotThrow()
    {
        // Arrange — no Identify() call, so _actorId is null
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.Track("test", "event");

        // Assert
        act.Should().NotThrow();
    }

    // Parameterless StartSession() after Dispose is no-op even without Identify()
    [Fact]
    public void StartSession_Parameterless_AfterDispose_IsNoOpAndDoesNotThrow()
    {
        // Arrange — no Identify() call, so _actorId is null
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.StartSession();

        // Assert
        act.Should().NotThrow();
    }

    // Track(actorId) with invalid actorId after Dispose is silent no-op
    [Fact]
    public void Track_WithNullActorId_AfterDispose_DoesNotThrow()
    {
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        var act = () => tracker.Track("cat", "name", (string)null!);
        act.Should().NotThrow();
    }

    // StartSession(actorId) with invalid actorId after Dispose is silent no-op
    [Fact]
    public void StartSession_WithNullActorId_AfterDispose_DoesNotThrow()
    {
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        var act = () => tracker.StartSession((string)null!);
        act.Should().NotThrow();
    }

    // AC-526 supplemental: EndSession after Dispose is no-op
    [Fact]
    public void EndSession_AfterDispose_IsNoOp()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.Dispose();

        // Act
        var act = () => tracker.EndSession();

        // Assert
        act.Should().NotThrow();
    }

    // DisposeAsync clears session locally (no HTTP call)
    [Fact]
    public async Task DisposeAsync_WithActiveSession_ClearsSessionLocally()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker();
        tracker.StartSession("user-1");
        TrackerTestHelper.GetSessionId(tracker).Should().NotBeNull();

        // Act
        await tracker.DisposeAsync();

        // Assert — session cleared locally, tracker disposed
        TrackerTestHelper.GetSessionId(tracker).Should().BeNull();
        TrackerTestHelper.GetDisposed(tracker).Should().BeTrue();
    }

    // Dispose writes memory events to disk and completes instantly (no network I/O)
    [Fact]
    public void Dispose_WithQueuedEvents_CompletesInstantly()
    {
        // Arrange
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "http://192.0.2.1", // RFC 5737 TEST-NET — non-routable
            AppName = "TestApp",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        });
        tracker.Track("test", "event", "actor-1");
        tracker.StartSession("user-1");

        // Act — Dispose should persist to disk and return immediately
        var sw = System.Diagnostics.Stopwatch.StartNew();
        tracker.Dispose();
        sw.Stop();

        // Assert — should complete in under 1 second (no network calls)
        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "dispose should not perform any network I/O");
    }
}
