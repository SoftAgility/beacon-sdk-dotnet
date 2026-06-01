// Covers:
//   AC-530: Disk events replayed on flush (FR-455) — tested via DiskQueue.DequeueUpTo
//   AC-531: Successful delivery deletes disk rows (FR-455) — tested via DiskQueue.Delete
//   AC-532: Max size enforcement drops oldest events (FR-456, ED-425)
//   EC-423: SQLite disk queue failure handling (partially)

using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the DiskQueue SQLite offline event queue.
/// Each test creates a temporary SQLite database and cleans up after itself.
/// </summary>
public sealed class DiskQueueTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private DiskQueue? _queue;

    public DiskQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "beacon-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test-queue.db");
        _queue = new DiskQueue(_dbPath, null);
    }

    public void Dispose()
    {
        _queue?.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in test teardown
        }
    }

    // Disk-queue contention hardening (busy_timeout): two instances sharing the same
    // Product (same app as several processes on one host) can coexist on one db file
    // without erroring. busy_timeout makes a contending writer wait for the lock.
    [Fact]
    public void TwoInstances_OnSameFile_BothEnqueueWithoutThrowing()
    {
        using var second = new DiskQueue(_dbPath, null);

        var act = () =>
        {
            _queue!.Enqueue(["{\"a\":1}"]);
            second.Enqueue(["{\"b\":2}"]);
        };

        act.Should().NotThrow();
    }

    // AC-530: Enqueued events can be dequeued in order
    [Fact]
    public void Enqueue_ThenDequeue_ReturnsEventsInOrder()
    {
        // Arrange
        var payloads = new[]
        {
            "{\"event_id\":\"aaa\",\"name\":\"event1\"}",
            "{\"event_id\":\"bbb\",\"name\":\"event2\"}",
            "{\"event_id\":\"ccc\",\"name\":\"event3\"}"
        };

        // Act
        _queue!.Enqueue(payloads);
        var dequeued = _queue.DequeueUpTo(10);

        // Assert
        dequeued.Should().HaveCount(3);
        dequeued[0].Payload.Should().Contain("event1");
        dequeued[1].Payload.Should().Contain("event2");
        dequeued[2].Payload.Should().Contain("event3");
    }

    // AC-530: DequeueUpTo respects the count limit
    [Fact]
    public void DequeueUpTo_WithLimit_ReturnsOnlyRequestedCount()
    {
        // Arrange
        var payloads = new[]
        {
            "{\"event_id\":\"a\",\"name\":\"e1\"}",
            "{\"event_id\":\"b\",\"name\":\"e2\"}",
            "{\"event_id\":\"c\",\"name\":\"e3\"}"
        };
        _queue!.Enqueue(payloads);

        // Act
        var dequeued = _queue.DequeueUpTo(2);

        // Assert
        dequeued.Should().HaveCount(2);
    }

    // AC-531: Delete removes specified rows from the queue
    [Fact]
    public void Delete_RemovesSpecifiedRows()
    {
        // Arrange
        var payloads = new[]
        {
            "{\"event_id\":\"a\",\"name\":\"e1\"}",
            "{\"event_id\":\"b\",\"name\":\"e2\"}",
            "{\"event_id\":\"c\",\"name\":\"e3\"}"
        };
        _queue!.Enqueue(payloads);
        var batch = _queue.DequeueUpTo(10);
        batch.Should().HaveCount(3);

        // Act — delete the first two rows
        _queue.Delete(batch.Take(2).Select(b => b.Id));

        // Assert — only the third event remains
        var remaining = _queue.DequeueUpTo(10);
        remaining.Should().HaveCount(1);
        remaining[0].Payload.Should().Contain("e3");
    }

    // AC-531 supplemental: GetCount returns correct count
    [Fact]
    public void GetCount_ReturnsCorrectCount()
    {
        // Arrange
        _queue!.Enqueue(new[]
        {
            "{\"event_id\":\"a\",\"name\":\"e1\"}",
            "{\"event_id\":\"b\",\"name\":\"e2\"}"
        });

        // Act
        var count = _queue.GetCount();

        // Assert
        count.Should().Be(2);
    }

    // AC-531: After deleting all, count is zero
    [Fact]
    public void Delete_AllRows_ResultsInEmptyQueue()
    {
        // Arrange
        _queue!.Enqueue(new[] { "{\"event_id\":\"x\",\"name\":\"e\"}" });
        var batch = _queue.DequeueUpTo(10);

        // Act
        _queue.Delete(batch.Select(b => b.Id));

        // Assert
        _queue.GetCount().Should().Be(0);
        _queue.DequeueUpTo(10).Should().BeEmpty();
    }

    // AC-530: DequeueUpTo from empty queue returns empty list
    [Fact]
    public void DequeueUpTo_EmptyQueue_ReturnsEmptyList()
    {
        // Act
        var result = _queue!.DequeueUpTo(10);

        // Assert
        result.Should().BeEmpty();
    }

    // AC-530 supplemental: DiskQueue persists across instances (startup replay)
    [Fact]
    public void DiskQueue_PersistsAcrossInstances()
    {
        // Arrange — write events with one instance
        _queue!.Enqueue(new[] { "{\"event_id\":\"persist\",\"name\":\"persisted_event\"}" });
        _queue.Dispose();

        // Act — create a new instance pointing to the same file
        _queue = new DiskQueue(_dbPath, null);
        var events = _queue.DequeueUpTo(10);

        // Assert
        events.Should().HaveCount(1);
        events[0].Payload.Should().Contain("persisted_event");
    }

    // AC-532 / ED-425: DeleteOldest removes the oldest events
    [Fact]
    public void DeleteOldest_RemovesOldestEvents()
    {
        // Arrange
        var payloads = new[]
        {
            "{\"event_id\":\"oldest\",\"name\":\"e1\"}",
            "{\"event_id\":\"middle\",\"name\":\"e2\"}",
            "{\"event_id\":\"newest\",\"name\":\"e3\"}"
        };
        _queue!.Enqueue(payloads);

        // Act
        _queue.DeleteOldest(2);

        // Assert
        var remaining = _queue.DequeueUpTo(10);
        remaining.Should().HaveCount(1);
        remaining[0].Payload.Should().Contain("newest");
    }

    // AC-532: GetFileSizeBytes returns a positive value for non-empty queue
    [Fact]
    public void GetFileSizeBytes_WithData_ReturnsPositiveValue()
    {
        // Arrange
        _queue!.Enqueue(new[] { "{\"event_id\":\"a\",\"name\":\"e\"}" });

        // Act
        var size = _queue.GetFileSizeBytes();

        // Assert
        size.Should().BeGreaterThan(0);
    }

    // AC-532 / ED-425: EnforceMaxSize drops oldest events when over limit
    [Fact]
    public void EnforceMaxSize_WhenOverLimit_DropsOldestEvents()
    {
        // Arrange — add many events to make the file grow
        var payloads = Enumerable.Range(1, 100)
            .Select(i => $"{{\"event_id\":\"ev{i}\",\"name\":\"event_{i}\",\"data\":\"{new string('x', 500)}\"}}")
            .ToArray();
        _queue!.Enqueue(payloads);

        var sizeBeforeEnforce = _queue.GetFileSizeBytes();
        // Set a small cap (should be less than current size)
        var smallCap = sizeBeforeEnforce / 2;

        // Act
        var dropped = _queue.EnforceMaxSize(smallCap);

        // Assert
        dropped.Should().BeGreaterThan(0, "some events should have been dropped");
        _queue.GetCount().Should().BeLessThan(100, "fewer events should remain after enforcement");
    }

    // Bug 3: EnforceMaxSize actually reduces file size (vacuum works)
    [Fact]
    public void EnforceMaxSize_ReducesFileSize()
    {
        // Arrange — add enough data to make the file meaningful
        var payloads = Enumerable.Range(1, 200)
            .Select(i => $"{{\"event_id\":\"ev{i}\",\"name\":\"event_{i}\",\"data\":\"{new string('x', 500)}\"}}")
            .ToArray();
        _queue!.Enqueue(payloads);

        var sizeBeforeEnforce = _queue.GetFileSizeBytes();
        var smallCap = sizeBeforeEnforce / 4;

        // Act
        _queue.EnforceMaxSize(smallCap);
        var sizeAfterEnforce = _queue.GetFileSizeBytes();

        // Assert
        sizeAfterEnforce.Should().BeLessThan(sizeBeforeEnforce,
            "VACUUM should reclaim disk space after deleting events");
    }

    // Bug 3: EnforceMaxSize loop stops once under the cap (doesn't delete everything)
    [Fact]
    public void EnforceMaxSize_StopsWhenUnderCap()
    {
        // Arrange — add events
        var payloads = Enumerable.Range(1, 50)
            .Select(i => $"{{\"event_id\":\"ev{i}\",\"name\":\"event_{i}\",\"data\":\"{new string('x', 200)}\"}}")
            .ToArray();
        _queue!.Enqueue(payloads);

        // Set cap larger than file size — nothing should be dropped
        var currentSize = _queue.GetFileSizeBytes();
        var largeCap = currentSize * 2;

        // Act
        var dropped = _queue.EnforceMaxSize(largeCap);

        // Assert
        dropped.Should().Be(0, "no events should be dropped when under the cap");
        _queue.GetCount().Should().Be(50);
    }

    // EC-423 supplemental: Operations after Dispose return defaults without throwing
    [Fact]
    public void DequeueUpTo_AfterDispose_ReturnsEmptyList()
    {
        // Arrange
        _queue!.Enqueue(new[] { "{\"event_id\":\"a\",\"name\":\"e\"}" });
        _queue.Dispose();

        // Act
        var result = _queue.DequeueUpTo(10);

        // Assert
        result.Should().BeEmpty();
    }

    // EC-423 supplemental: GetCount after Dispose returns 0
    [Fact]
    public void GetCount_AfterDispose_ReturnsZero()
    {
        // Arrange
        _queue!.Enqueue(new[] { "{\"event_id\":\"a\",\"name\":\"e\"}" });
        _queue.Dispose();

        // Act
        var count = _queue.GetCount();

        // Assert
        count.Should().Be(0);
    }
}
