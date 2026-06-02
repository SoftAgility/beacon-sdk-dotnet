// Regression guard RG-6 — disposal compat across TFMs (R11a + R11b).
//
// PRD ref: Test plan "New RG-6". Dispose a tracker that has a LIVE flush timer and
// queued events; assert no exception and that the events persisted to the disk queue.
//
//   R11a CancellationTokenSource.CancelAsync() — added .NET 8. The #else (synchronous
//        Cancel()) is taken on net6.0 AND net48. net6.0 is the ONLY modern TFM that hits
//        this #else, so the net6.0 run of this guard is what catches a botched CancelAsync
//        shim (it would throw or hang during dispose).
//   R11b Timer.DisposeAsync() — added .NET Core 3.0. The #else (Dispose(WaitHandle) +
//        WaitOne) is taken on net48 only. The net48 run exercises that branch.
//
// Together: net6.0 run guards R11a's #else; net48 run guards both R11a's and R11b's #else.
// The synchronous Dispose() also wraps Task.Run(DisposeAsync).GetAwaiter().GetResult()
// (R-D sync-over-async), so a clean completion here also guards against a dispose deadlock.

using System.Diagnostics;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG6_DisposalCompatTests
{
    // RG-6: synchronous Dispose() with a live flush timer + queued events completes
    // without throwing and persists events to disk on every TFM.
    [Fact]
    public void Dispose_WithLiveTimerAndQueuedEvents_PersistsAndDoesNotThrow()
    {
        // Arrange — a SHORT flush interval keeps the System.Threading.Timer genuinely
        // live during the test, so DisposeAsync's timer-stop branch (R11b) is real.
        // Unreachable URL keeps everything offline so events end up on disk.
        var product = $"RG6_Sync_{Guid.NewGuid():N}";
        DiskQueueLocationHelper.CleanUp(product);

        try
        {
            var tracker = new BeaconTracker(new BeaconOptions
            {
                ApiKey = "test-key",
                ApiBaseUrl = "http://192.0.2.1", // RFC 5737 TEST-NET — non-routable
                Product = product,
                ProductVersion = "3.2.0",
                Enabled = true,
                FlushIntervalSeconds = 1 // live timer
            });
            tracker.Track("rg6", "event_1", "actor-1");
            tracker.Track("rg6", "event_2", "actor-1");

            // Act — dispose must cancel the CTS (R11a #else on net6/net48) and stop the
            // timer (R11b #else on net48) without throwing or deadlocking.
            var sw = Stopwatch.StartNew();
            var act = () => tracker.Dispose();
            act.Should().NotThrow("CancelAsync/Timer.Dispose #else branches must be clean");
            sw.Stop();

            // Assert — no network-I/O hang (well under the TEST-NET connect timeout) and
            // events persisted to disk.
            sw.ElapsedMilliseconds.Should().BeLessThan(5000, "dispose must not block on network I/O");
            DiskQueueLocationHelper.PersistedCount(product)
                .Should().Be(2, "queued events must be persisted to disk on dispose");
        }
        finally
        {
            DiskQueueLocationHelper.CleanUp(product);
        }
    }

    // RG-6: DisposeAsync with a live timer + queued events — the async path. On net6.0
    // this still routes CTS cancel through the synchronous Cancel() #else (R11a), so this
    // is the focused net6.0 guard for that branch.
    [Fact]
    public async Task DisposeAsync_WithLiveTimerAndQueuedEvents_PersistsAndDoesNotThrow()
    {
        // Arrange
        var product = $"RG6_Async_{Guid.NewGuid():N}";
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
                FlushIntervalSeconds = 1
            });
            tracker.Track("rg6", "async_event", "actor-1");

            // Act
            var act = async () => await tracker.DisposeAsync();
            await act.Should().NotThrowAsync();

            // Assert
            DiskQueueLocationHelper.PersistedCount(product).Should().Be(1);
        }
        finally
        {
            DiskQueueLocationHelper.CleanUp(product);
        }
    }
}
