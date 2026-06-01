// Covers:
//   AC-508: Track queues event with correct fields (FR-443)
//   AC-509: product/source_version from options (FR-443)
//   AC-510: timestamp is current UTC (FR-443)
//   AC-522: Track includes session_id when session active (FR-449)
//   AC-544: Each Track() produces distinct event_id (FR-464)
//   AC-537: Track() never throws to host (FR-461)
//   AC-545: Track after Dispose is no-op (EC-425)

using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the Track() method — event construction, queuing, and resilience.
/// </summary>
public sealed class EventTrackingTests : IDisposable
{
    private readonly BeaconTracker _tracker;

    public EventTrackingTests()
    {
        _tracker = TrackerTestHelper.CreateTracker(
            appName: "MyTestApp",
            appVersion: "2.5.0");
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }

    // AC-508: Track queues event with correct category, name, actor_id, properties, and event_id
    [Fact]
    public void Track_WithProperties_QueuesEventWithCorrectFields()
    {
        // Arrange
        var properties = new Dictionary<string, object> { ["quantity"] = 5 };

        // Act
        _tracker.Track("inventory", "item_added", "user-1", properties);

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("category").GetString().Should().Be("inventory");
        root.GetProperty("name").GetString().Should().Be("item_added");
        root.GetProperty("actor_id").GetString().Should().Be("user-1");
        root.GetProperty("event_id").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("properties").GetProperty("quantity").GetInt32().Should().Be(5);

        docs[0].Dispose();
    }

    // AC-509: product and source_version come from BeaconOptions
    [Fact]
    public void Track_QueuesEventWithProductAndSourceVersion()
    {
        // Act
        _tracker.Track("test", "event", "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("product").GetString().Should().Be("MyTestApp");
        root.GetProperty("source_version").GetString().Should().Be("2.5.0");

        docs[0].Dispose();
    }

    // AC-510: timestamp is within 1 second of DateTimeOffset.UtcNow
    [Fact]
    public void Track_QueuesEventWithCurrentUtcTimestamp()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow;

        // Act
        _tracker.Track("test", "event", "actor-1");

        // Assert
        var after = DateTimeOffset.UtcNow;
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var timestampStr = docs[0].RootElement.GetProperty("timestamp").GetString();
        timestampStr.Should().NotBeNull();
        var timestamp = DateTimeOffset.Parse(timestampStr!);
        timestamp.Should().BeOnOrAfter(before.AddSeconds(-1));
        timestamp.Should().BeOnOrBefore(after.AddSeconds(1));

        docs[0].Dispose();
    }

    // AC-522: When a session is active, Track includes session_id
    [Fact]
    public void Track_WithActiveSession_IncludesSessionId()
    {
        // Arrange
        _tracker.StartSession("actor-1");
        // Give a tiny moment for StartSession to set session state
        Thread.Sleep(50);

        // Act
        _tracker.Track("test", "event", "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.TryGetProperty("session_id", out var sessionId).Should().BeTrue(
            "session_id should be included when a session is active");
        sessionId.GetString().Should().NotBeNullOrEmpty();

        docs[0].Dispose();
    }

    // AC-522 supplemental: When no session is active, session_id is omitted
    [Fact]
    public void Track_WithNoActiveSession_OmitsSessionId()
    {
        // Act
        _tracker.Track("test", "event", "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.TryGetProperty("session_id", out _).Should().BeFalse(
            "session_id should not be included when no session is active");

        docs[0].Dispose();
    }

    // AC-544: Two Track() calls produce distinct event_ids
    [Fact]
    public void Track_CalledTwice_ProducesDistinctEventIds()
    {
        // Act
        _tracker.Track("test", "event1", "actor-1");
        _tracker.Track("test", "event2", "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(2);

        var id1 = docs[0].RootElement.GetProperty("event_id").GetString();
        var id2 = docs[1].RootElement.GetProperty("event_id").GetString();

        id1.Should().NotBeNullOrEmpty();
        id2.Should().NotBeNullOrEmpty();
        id1.Should().NotBe(id2, "each event must have a unique event_id");

        // Verify they are valid GUIDs
        Guid.TryParse(id1, out _).Should().BeTrue("event_id must be a valid UUID");
        Guid.TryParse(id2, out _).Should().BeTrue("event_id must be a valid UUID");

        docs.ForEach(d => d.Dispose());
    }

    // AC-537: Track() never throws to host for runtime errors (valid actorId)
    [Fact]
    public void Track_NeverThrowsToHost()
    {
        // Act & Assert — none of these should throw (actorId must be valid)
        var act1 = () => _tracker.Track("", "", "actor");
        var act2 = () => _tracker.Track("category", "name", "actor", null);
        var act3 = () => _tracker.Track("category", "name", "actor",
            new Dictionary<string, object> { ["key"] = "value" });

        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
    }

    // AC-545 / EC-425: Track after Dispose is a silent no-op
    [Fact]
    public void Track_AfterDispose_IsNoOpWithNoException()
    {
        // Arrange
        _tracker.Dispose();

        // Act
        var act = () => _tracker.Track("test", "event", "actor-1");

        // Assert
        act.Should().NotThrow();
        // Queue should be empty since the event was not accepted
        var events = TrackerTestHelper.GetQueuedEvents(_tracker);
        events.Should().BeEmpty("events should not be queued after dispose");
    }

    // AC-508 supplemental: Track with null properties omits properties from payload
    [Fact]
    public void Track_WithNullProperties_OmitsPropertiesField()
    {
        // Act
        _tracker.Track("test", "event", "actor-1", null);

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.TryGetProperty("properties", out _).Should().BeFalse(
            "properties should be omitted when null");

        docs[0].Dispose();
    }

    // Category exceeding 128 chars is truncated, not rejected
    [Fact]
    public void Track_WithLongCategory_TruncatesTo128()
    {
        // Arrange
        var longCategory = new string('c', 200);

        // Act
        _tracker.Track(longCategory, "name", "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);
        var category = docs[0].RootElement.GetProperty("category").GetString()!;
        category.Length.Should().Be(128);
        docs[0].Dispose();
    }

    // Name exceeding 256 chars is truncated, not rejected
    [Fact]
    public void Track_WithLongName_TruncatesTo256()
    {
        // Arrange
        var longName = new string('n', 300);

        // Act
        _tracker.Track("cat", longName, "actor-1");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);
        var name = docs[0].RootElement.GetProperty("name").GetString()!;
        name.Length.Should().Be(256);
        docs[0].Dispose();
    }
}
