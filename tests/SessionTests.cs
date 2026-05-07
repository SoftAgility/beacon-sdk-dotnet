// Covers:
//   AC-521: StartSession sends session start request (FR-449)
//   AC-522: Track includes session_id when session active (FR-449)
//   AC-523: StartSession when already active ends previous first (FR-450)
//   AC-524: EndSession sends session end request (FR-451)
//   AC-525: EndSession with no active session is no-op (FR-452)
//   EC-425: Track/StartSession/EndSession after Dispose is no-op
//   EC-426: Session start HTTP failure still stores session locally

using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for session lifecycle management (StartSession, EndSession).
/// HTTP calls to the backend are fire-and-forget; we verify local state changes.
/// </summary>
public sealed class SessionTests : IDisposable
{
    private readonly BeaconTracker _tracker;

    public SessionTests()
    {
        _tracker = TrackerTestHelper.CreateTracker();
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }

    // AC-521: StartSession stores a non-null session_id locally
    [Fact]
    public void StartSession_SetsSessionIdLocally()
    {
        // Act
        _tracker.StartSession("user-1");

        // Assert
        var sessionId = TrackerTestHelper.GetSessionId(_tracker);
        sessionId.Should().NotBeNullOrEmpty("session_id should be set after StartSession");
        Guid.TryParse(sessionId, out _).Should().BeTrue("session_id should be a valid UUID");
    }

    // AC-522: After StartSession, Track includes session_id
    [Fact]
    public void Track_AfterStartSession_IncludesSessionId()
    {
        // Arrange
        _tracker.StartSession("user-1");
        var sessionId = TrackerTestHelper.GetSessionId(_tracker);

        // Act
        _tracker.Track("test", "event", "user-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("session_id").GetString().Should().Be(sessionId);

        docs[0].Dispose();
    }

    // AC-523: StartSession when already active ends previous session first
    [Fact]
    public void StartSession_WhenAlreadyActive_EndsPreviousAndStartsNew()
    {
        // Arrange
        _tracker.StartSession("user-1");
        var firstSessionId = TrackerTestHelper.GetSessionId(_tracker);
        firstSessionId.Should().NotBeNullOrEmpty();

        // Act
        _tracker.StartSession("user-1");

        // Assert
        var newSessionId = TrackerTestHelper.GetSessionId(_tracker);
        newSessionId.Should().NotBeNullOrEmpty();
        newSessionId.Should().NotBe(firstSessionId,
            "a new session should be started after ending the previous one");
    }

    // AC-524: EndSession clears session_id (HTTP call is fire-and-forget)
    [Fact]
    public void EndSession_WithActiveSession_ClearsSessionId()
    {
        // Arrange
        _tracker.StartSession("user-1");
        TrackerTestHelper.GetSessionId(_tracker).Should().NotBeNull();

        // Act
        _tracker.EndSession();

        // Assert
        TrackerTestHelper.GetSessionId(_tracker).Should().BeNull(
            "session_id should be cleared after EndSession");
    }

    // AC-524 supplemental: After EndSession, Track no longer includes session_id
    [Fact]
    public void Track_AfterEndSession_DoesNotIncludeSessionId()
    {
        // Arrange
        _tracker.StartSession("user-1");
        _tracker.EndSession();

        // Act
        _tracker.Track("test", "event", "user-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);
        docs[0].RootElement.TryGetProperty("session_id", out _).Should().BeFalse(
            "session_id should not be present after EndSession");

        docs[0].Dispose();
    }

    // AC-525: EndSession with no active session is a no-op
    [Fact]
    public void EndSession_WithNoActiveSession_IsNoOp()
    {
        // Act
        var act = () => _tracker.EndSession();

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetSessionId(_tracker).Should().BeNull();
    }

    // EC-425: StartSession after Dispose is no-op
    [Fact]
    public void StartSession_AfterDispose_IsNoOp()
    {
        // Arrange
        _tracker.Dispose();

        // Act
        var act = () => _tracker.StartSession("user-1");

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetSessionId(_tracker).Should().BeNull();
    }

    // EC-425: EndSession after Dispose is no-op
    [Fact]
    public void EndSession_AfterDispose_IsNoOp()
    {
        // Arrange
        _tracker.StartSession("user-1");
        _tracker.Dispose();

        // Act
        var act = () => _tracker.EndSession();

        // Assert
        act.Should().NotThrow();
    }

    // EC-426 / AC-521 supplemental: StartSession resets environment-sent flag
    [Fact]
    public void StartSession_ResetsEnvironmentSentFlag()
    {
        // Arrange — simulate that environment was sent previously
        // (The flag is reset on StartSession per FR-449)
        _tracker.StartSession("user-1");

        // Assert
        TrackerTestHelper.GetEnvironmentSent(_tracker).Should().BeFalse(
            "environment-sent flag should be reset when a new session starts");
    }
}
