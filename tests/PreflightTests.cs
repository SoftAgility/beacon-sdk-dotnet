// Covers:
//   Preflight: background empty-batch POST detects invalid API key early
//   401 data preservation: memory-queued events written to disk on halt

using System.Net;
using FluentAssertions;
using SoftAgility.Beacon.Internal;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the preflight API key validation and 401 data preservation.
/// Uses a local HttpListener to simulate server responses.
/// </summary>
[Collection("HttpListener")]
public sealed class PreflightTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly string _tempDir;
    private readonly string _dbPath;

    public PreflightTests()
    {
        // Find an available port
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();

        _tempDir = Path.Combine(Path.GetTempPath(), "beacon-preflight-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test-queue.db");
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* ignore cleanup errors */ }
    }

    // Preflight: Track() triggers a background empty-batch POST that detects 401
    [Fact]
    public async Task Track_TriggersPreflightThatDetects401()
    {
        // Arrange — respond 401 to the preflight empty-batch POST
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
        });

        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "bad-key",
            ApiBaseUrl = _baseUrl,
            AppName = $"Preflight_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600 // Prevent timer-driven flush
        });

        // Act — Track() fires the preflight in the background
        tracker.Track("test", "event", "actor-1");

        // Wait for preflight to complete
        await listenerTask;
        await Task.Delay(100); // Let the background task set _halted

        // Assert — tracker should be halted
        TrackerTestHelper.GetHalted(tracker).Should().BeTrue(
            "preflight should halt the tracker on 401");

        tracker.Dispose();
    }

    // Preflight: 200 response does not halt
    [Fact]
    public async Task Track_PreflightWith200_DoesNotHalt()
    {
        // Arrange — respond 200 to the preflight
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "good-key",
            ApiBaseUrl = _baseUrl,
            AppName = $"Preflight_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        });

        // Act
        tracker.Track("test", "event", "actor-1");

        await listenerTask;
        await Task.Delay(100);

        // Assert
        TrackerTestHelper.GetHalted(tracker).Should().BeFalse();

        tracker.Dispose();
    }

    // Preflight fires only once even with multiple Track() calls
    [Fact]
    public async Task Track_CalledMultipleTimes_PreflightFiresOnlyOnce()
    {
        // Arrange — only accept one request (second would hang)
        var requestCount = 0;
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            Interlocked.Increment(ref requestCount);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = _baseUrl,
            AppName = $"Preflight_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        });

        // Act — multiple Track() calls
        tracker.Track("test", "e1", "actor-1");
        tracker.Track("test", "e2", "actor-1");
        tracker.Track("test", "e3", "actor-1");

        await listenerTask;
        await Task.Delay(200);

        // Assert — only 1 preflight request
        requestCount.Should().Be(1);

        tracker.Dispose();
    }

    // 401 during flush writes memory events to disk for recovery after key rotation
    [Fact]
    public async Task Flush_On401_WritesEventsToDiskQueue()
    {
        // Arrange — create a disk queue we can inspect
        var diskQueue = new DiskQueue(_dbPath, null);

        // Respond 401 to the preflight, then 401 again to the flush
        var listenerTask = Task.Run(async () =>
        {
            // Handle preflight
            var ctx1 = await _listener.GetContextAsync();
            ctx1.Response.StatusCode = 200; // Preflight passes
            ctx1.Response.Close();

            // Handle flush attempt
            var ctx2 = await _listener.GetContextAsync();
            ctx2.Response.StatusCode = 401; // Flush gets 401
            ctx2.Response.Close();
        });

        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "rotating-key",
            ApiBaseUrl = _baseUrl,
            AppName = $"Preflight401_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        });

        // Queue some events
        tracker.Track("test", "event1", "actor-1");
        tracker.Track("test", "event2", "actor-1");

        // Wait for preflight
        await Task.Delay(200);

        // Act — trigger a manual flush that will get 401
        await tracker.FlushAsync();

        // Wait for listener
        await listenerTask;

        // Assert — tracker halted and events should have been written to disk
        TrackerTestHelper.GetHalted(tracker).Should().BeTrue();
        // Memory queue should be empty (written to disk)
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "events should be moved to disk on 401 halt");

        tracker.Dispose();
        diskQueue.Dispose();
    }
}
