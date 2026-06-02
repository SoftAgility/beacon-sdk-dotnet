// Regression guard RG-5 — async dispose flushes queued events to disk (R5).
//
// PRD ref: Test plan "New RG-5". IAsyncDisposable / await using / ValueTask DisposeAsync
// (R5) is provided down-level by Microsoft.Bcl.AsyncInterfaces. On net48 this guard
// exercises that polyfilled async-dispose path: `await using` a tracker that has
// in-memory events with an unreachable endpoint must persist them to the SQLite disk
// queue (no network), so they survive for next-launch delivery.

using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG5_AsyncDisposeTests
{
    // RG-5: await using a tracker with queued events persists them to disk via DisposeAsync.
    [Fact]
    public async Task AwaitUsing_WithQueuedEvents_PersistsToDiskQueue()
    {
        // Arrange — unique product so the disk-queue path is isolated; unreachable URL so
        // nothing is delivered over the network and every event lands on disk at dispose.
        var product = $"RG5_AsyncDispose_{Guid.NewGuid():N}";
        DiskQueueLocationHelper.CleanUp(product);

        try
        {
            // Act
            await using (var tracker = new BeaconTracker(new BeaconOptions
            {
                ApiKey = "test-key",
                ApiBaseUrl = "http://192.0.2.1", // RFC 5737 TEST-NET — non-routable
                Product = product,
                ProductVersion = "3.2.0",
                Enabled = true,
                FlushIntervalSeconds = 3600 // no timer-driven flush during the test
            }))
            {
                tracker.Track("rg5", "event_a", "actor-1");
                tracker.Track("rg5", "event_b", "actor-1");
                tracker.Track("rg5", "event_c", "actor-1");
            } // DisposeAsync runs here — R5 path

            // Assert — all three events persisted to the disk queue
            DiskQueueLocationHelper.PersistedCount(product)
                .Should().Be(3, "DisposeAsync must write in-memory events to disk (R5)");
        }
        finally
        {
            DiskQueueLocationHelper.CleanUp(product);
        }
    }

    // RG-5 supplemental: explicit DisposeAsync (not via await using) also persists.
    [Fact]
    public async Task DisposeAsync_WithQueuedEvents_PersistsToDiskQueue()
    {
        // Arrange
        var product = $"RG5_DisposeAsync_{Guid.NewGuid():N}";
        DiskQueueLocationHelper.CleanUp(product);

        try
        {
            var tracker = new BeaconTracker(new BeaconOptions
            {
                ApiKey = "test-key",
                ApiBaseUrl = "http://192.0.2.1",
                Product = product,
                ProductVersion = "3.2.0",
                Enabled = true,
                FlushIntervalSeconds = 3600
            });
            tracker.Track("rg5", "only_event", "actor-1");

            // Act
            await tracker.DisposeAsync();

            // Assert
            DiskQueueLocationHelper.PersistedCount(product).Should().Be(1);
        }
        finally
        {
            DiskQueueLocationHelper.CleanUp(product);
        }
    }
}
