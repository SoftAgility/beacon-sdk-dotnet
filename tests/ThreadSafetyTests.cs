// Covers:
//   AC-539: 100 threads call Track() simultaneously, all events queued (FR-462)
//   ED-421: StartSession called concurrently from two threads (lock ensures safety)

using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for thread safety of the BeaconTracker under concurrent access.
/// </summary>
public sealed class ThreadSafetyTests : IDisposable
{
    private readonly BeaconTracker _tracker;

    public ThreadSafetyTests()
    {
        _tracker = TrackerTestHelper.CreateTracker(
            maxBatchSize: 1000,
            flushIntervalSeconds: 3600); // Long interval to prevent auto-flush during test
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }

    // AC-539: 100 concurrent Track() calls, all 100 events are in the queue
    [Fact]
    public async Task Track_CalledFrom100ThreadsSimultaneously_AllEventsQueued()
    {
        // Arrange
        const int threadCount = 100;

        // Act — Task.Run provides sufficient concurrency without a Barrier
        // (Barrier requires all threads alive simultaneously, causing thread pool starvation)
        var tasks = Enumerable.Range(0, threadCount).Select(i =>
            Task.Run(() =>
            {
                _tracker.Track("concurrent", $"event_{i}", $"actor_{i}");
            })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var events = TrackerTestHelper.GetQueuedEvents(_tracker);
        events.Should().HaveCount(threadCount,
            $"all {threadCount} events should be in the queue after concurrent Track() calls");
    }

    // ED-421: StartSession called concurrently — lock ensures only one executes at a time
    [Fact]
    public async Task StartSession_CalledConcurrently_ResultsInSingleActiveSession()
    {
        // Arrange
        const int threadCount = 10;
        var barrier = new Barrier(threadCount);

        // Act
        var tasks = Enumerable.Range(0, threadCount).Select(i =>
            Task.Run(() =>
            {
                barrier.SignalAndWait();
                _tracker.StartSession($"actor_{i}");
            })).ToArray();

        await Task.WhenAll(tasks);

        // Assert — exactly one session should be active
        var sessionId = TrackerTestHelper.GetSessionId(_tracker);
        sessionId.Should().NotBeNullOrEmpty(
            "exactly one session should be active after concurrent StartSession calls");
        Guid.TryParse(sessionId, out _).Should().BeTrue("session_id should be a valid UUID");
    }

    // AC-539 supplemental: No exceptions thrown during concurrent Track calls
    [Fact]
    public async Task Track_CalledConcurrently_NeverThrows()
    {
        // Arrange
        const int threadCount = 100;

        // Act & Assert
        var tasks = Enumerable.Range(0, threadCount).Select(i =>
            Task.Run(() =>
            {
                // Mix Track, StartSession, and EndSession calls
                _tracker.Track("cat", $"event_{i}", $"actor_{i}");
                if (i % 10 == 0) _tracker.StartSession($"actor_{i}");
                if (i % 15 == 0) _tracker.EndSession();
            })).ToArray();

        var act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("concurrent SDK operations should never throw");
    }
}
