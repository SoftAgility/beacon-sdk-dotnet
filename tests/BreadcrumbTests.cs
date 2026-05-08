// Covers:
//   AC-591: 30 Track() calls with MaxBreadcrumbs=25 -> buffer has exactly 25, oldest 5 discarded (FR-488, ED-436)
//   AC-592: 5 Track() calls then TrackException -> breadcrumbs field has 5 entries in order (FR-489)
//   AC-593: MaxBreadcrumbs=0 -> no breadcrumbs field in payload (FR-490)
//   AC-594: MaxBreadcrumbs=500 -> effective capacity is 200 (clamped) (ED-435)
//   AC-595: Track("cat", "name", "actor-1") -> breadcrumb has category "cat", name "name" (FR-488)
//   AC-596: Concurrent Track() calls -> no exceptions or data corruption (FR-488 thread safety)
//   AC-600: Track with properties -> breadcrumb entry contains those properties (FR-488)

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the breadcrumb ring buffer — capacity, ordering, properties, and thread safety.
/// HTTP listener tests capture the exception payload to inspect breadcrumb contents.
/// </summary>
[Collection("HttpListener")]
public sealed class BreadcrumbTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;

    public BreadcrumbTests()
    {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
    }

    private BeaconTracker CreateTrackerWithListener(int maxBreadcrumbs = 25)
    {
        return new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = _baseUrl,
            AppName = $"Test_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600,
            MaxBreadcrumbs = maxBreadcrumbs,
            MaxBatchSize = 1000 // Prevent size-triggered flushes from interfering
        });
    }

    /// <summary>
    /// Keeps accepting HTTP requests (responding 200) until one arrives at /v1/events/exceptions,
    /// then returns the body. This handles any batch flush requests that Track() may trigger
    /// before TrackException fires its POST.
    /// </summary>
    private async Task<string> CaptureExceptionBodyAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var ctx = await _listener.GetContextAsync();
            var path = ctx.Request.Url?.AbsolutePath;

            if (path == "/v1/events/exceptions")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                return body;
            }

            // Not the exception endpoint — respond 200 and keep waiting
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        }

        throw new OperationCanceledException("Timed out waiting for exception request");
    }

    // AC-591: 30 Track() calls with MaxBreadcrumbs=25 -> buffer has exactly 25, oldest 5 discarded
    [Fact]
    public async Task BreadcrumbBuffer_At30EntriesWithMax25_HasExactly25OldestDiscarded()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Make 30 Track() calls — each adds a breadcrumb
        for (var i = 0; i < 30; i++)
        {
            tracker.Track("cat", $"event_{i}", "actor-1");
        }

        // Now call TrackException to capture the breadcrumbs in the payload
        var captureTask = CaptureExceptionBodyAsync(cts.Token);
        tracker.TrackException(new Exception("test"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out var breadcrumbs).Should().BeTrue();

        var breadcrumbArray = breadcrumbs.EnumerateArray().ToList();
        breadcrumbArray.Should().HaveCount(25,
            "ring buffer should retain exactly MaxBreadcrumbs entries");

        // The oldest 5 (event_0 through event_4) should be discarded
        // First entry should be event_5
        breadcrumbArray[0].GetProperty("name").GetString().Should().Be("event_5",
            "oldest entries should be discarded from the front");

        // Last entry should be event_29
        breadcrumbArray[24].GetProperty("name").GetString().Should().Be("event_29");

        tracker.Dispose();
    }

    // AC-592: 5 Track() calls then TrackException -> breadcrumbs has 5 entries in insertion order
    [Fact]
    public async Task TrackException_After5TrackCalls_IncludesBreadcrumbsInOrder()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        tracker.Track("nav", "page_1", "actor-1");
        tracker.Track("nav", "page_2", "actor-1");
        tracker.Track("btn", "click_1", "actor-1");
        tracker.Track("api", "fetch_1", "actor-1");
        tracker.Track("nav", "page_3", "actor-1");

        var captureTask = CaptureExceptionBodyAsync(cts.Token);

        // Act
        tracker.TrackException(new Exception("crash"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out var breadcrumbs).Should().BeTrue();

        var breadcrumbArray = breadcrumbs.EnumerateArray().ToList();
        breadcrumbArray.Should().HaveCount(5);

        // Verify insertion order (oldest first)
        breadcrumbArray[0].GetProperty("name").GetString().Should().Be("page_1");
        breadcrumbArray[1].GetProperty("name").GetString().Should().Be("page_2");
        breadcrumbArray[2].GetProperty("name").GetString().Should().Be("click_1");
        breadcrumbArray[3].GetProperty("name").GetString().Should().Be("fetch_1");
        breadcrumbArray[4].GetProperty("name").GetString().Should().Be("page_3");

        // Verify categories
        breadcrumbArray[0].GetProperty("category").GetString().Should().Be("nav");
        breadcrumbArray[2].GetProperty("category").GetString().Should().Be("btn");
        breadcrumbArray[3].GetProperty("category").GetString().Should().Be("api");

        // Verify each has a timestamp
        foreach (var crumb in breadcrumbArray)
        {
            crumb.TryGetProperty("timestamp", out var ts).Should().BeTrue();
            ts.GetString().Should().NotBeNullOrEmpty();
        }

        tracker.Dispose();
    }

    // AC-593: MaxBreadcrumbs=0 -> no breadcrumbs field in payload
    [Fact]
    public async Task TrackException_WithMaxBreadcrumbs0_OmitsBreadcrumbs()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Track 10 events — breadcrumbs should be disabled
        for (var i = 0; i < 10; i++)
        {
            tracker.Track("cat", $"event_{i}", "actor-1");
        }

        var captureTask = CaptureExceptionBodyAsync(cts.Token);

        // Act
        tracker.TrackException(new Exception("test"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out _).Should().BeFalse(
            "breadcrumbs should be omitted when MaxBreadcrumbs is 0");

        tracker.Dispose();
    }

    // AC-594: MaxBreadcrumbs=500 -> effective capacity is 200 (clamped)
    [Fact]
    public async Task Constructor_WithMaxBreadcrumbs500_ClampsTo200()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 500);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Track 250 events — if clamped to 200, buffer should hold exactly 200
        for (var i = 0; i < 250; i++)
        {
            tracker.Track("cat", $"event_{i}", "actor-1");
        }

        var captureTask = CaptureExceptionBodyAsync(cts.Token);

        // Act
        tracker.TrackException(new Exception("test"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out var breadcrumbs).Should().BeTrue();

        var count = breadcrumbs.EnumerateArray().Count();
        count.Should().Be(200,
            "MaxBreadcrumbs should be clamped to 200 when set above cap");

        // First retained entry should be event_50 (oldest 50 discarded: 250 - 200 = 50)
        var first = breadcrumbs.EnumerateArray().First();
        first.GetProperty("name").GetString().Should().Be("event_50");

        tracker.Dispose();
    }

    // AC-595: Track("cat", "name", "actor-1") -> breadcrumb has correct category and name
    [Fact]
    public async Task TrackException_AfterTrack_BreadcrumbHasCorrectCategoryAndName()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        tracker.Track("cat", "name", "actor-1");

        var captureTask = CaptureExceptionBodyAsync(cts.Token);

        // Act
        tracker.TrackException(new Exception("test"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out var breadcrumbs).Should().BeTrue();

        var entry = breadcrumbs.EnumerateArray().First();
        entry.GetProperty("category").GetString().Should().Be("cat");
        entry.GetProperty("name").GetString().Should().Be("name");

        tracker.Dispose();
    }

    // AC-596: Concurrent Track() calls -> no exceptions or data corruption
    [Fact]
    public void Track_CalledConcurrently_NoBreadcrumbCorruption()
    {
        // Arrange — use a non-routable address since we only care about
        // breadcrumb buffer integrity, not HTTP delivery
        var tracker = TrackerTestHelper.CreateTracker(
            maxBatchSize: 1000,
            flushIntervalSeconds: 3600);

        // Act — fire 100 Track() calls from 10 concurrent threads
        var tasks = Enumerable.Range(0, 10)
            .Select(threadIdx => Task.Run(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    tracker.Track("cat", $"event_{threadIdx}_{i}", $"actor-{threadIdx}");
                }
            }))
            .ToArray();

        // Assert — no exceptions should be thrown
        var act = () => Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
        act.Should().NotThrow("concurrent Track() calls should not corrupt the breadcrumb buffer");

        // Verify the breadcrumb buffer can still be accessed (via reflection)
        var breadcrumbField = typeof(BeaconTracker).GetField("_breadcrumbs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var buffer = breadcrumbField?.GetValue(tracker);
        buffer.Should().NotBeNull("breadcrumb buffer should still be intact after concurrent access");

        tracker.Dispose();
    }

    // AC-600: Track with properties -> breadcrumb entry contains those properties
    [Fact]
    public async Task TrackException_AfterTrackWithProperties_BreadcrumbContainsProperties()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        tracker.Track("purchase", "item_added", "actor-1",
            new Dictionary<string, object> { ["item"] = "x", ["qty"] = 3 });

        var captureTask = CaptureExceptionBodyAsync(cts.Token);

        // Act
        tracker.TrackException(new Exception("test"), "actor-1");

        // Assert
        var body = await captureTask;
        body.Should().NotBeNull();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.TryGetProperty("breadcrumbs", out var breadcrumbs).Should().BeTrue();

        var entry = breadcrumbs.EnumerateArray().First();
        entry.TryGetProperty("properties", out var props).Should().BeTrue(
            "breadcrumb should contain properties from Track() call");
        props.GetProperty("item").GetString().Should().Be("x");
        props.GetProperty("qty").GetInt32().Should().Be(3);

        tracker.Dispose();
    }

    // AC-592 supplemental: Breadcrumbs are not cleared after TrackException snapshot
    [Fact]
    public async Task TrackException_CalledTwice_BreadcrumbsRetainedBetweenCalls()
    {
        // Arrange
        var tracker = CreateTrackerWithListener(maxBreadcrumbs: 25);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        tracker.Track("nav", "page_1", "actor-1");
        tracker.Track("nav", "page_2", "actor-1");

        // First exception call — captures 2 breadcrumbs
        var captureTask1 = CaptureExceptionBodyAsync(cts.Token);
        tracker.TrackException(new Exception("first"), "actor-1");
        var body1 = await captureTask1;

        using var doc1 = JsonDocument.Parse(body1);
        doc1.RootElement.GetProperty("breadcrumbs").EnumerateArray().Count().Should().Be(2);

        // Add one more Track() call
        tracker.Track("nav", "page_3", "actor-1");

        // Second exception call — should have all 3 breadcrumbs (buffer not cleared)
        var captureTask2 = CaptureExceptionBodyAsync(cts.Token);
        tracker.TrackException(new Exception("second"), "actor-1");
        var body2 = await captureTask2;

        using var doc2 = JsonDocument.Parse(body2);
        var breadcrumbs2 = doc2.RootElement.GetProperty("breadcrumbs").EnumerateArray().ToList();
        breadcrumbs2.Should().HaveCount(3,
            "breadcrumbs should not be cleared after TrackException — subsequent calls see the same trail plus new entries");

        tracker.Dispose();
    }

    // AC-593 supplemental: MaxBreadcrumbs=0 means no breadcrumb buffer is allocated
    [Fact]
    public void Constructor_WithMaxBreadcrumbs0_DoesNotAllocateBuffer()
    {
        // Arrange
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.test.local",
            AppName = $"Test_{Guid.NewGuid():N}",
            AppVersion = "1.0.0",
            MaxBreadcrumbs = 0,
            FlushIntervalSeconds = 3600
        });

        // Assert — breadcrumb buffer should be null via reflection
        var breadcrumbField = typeof(BeaconTracker).GetField("_breadcrumbs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var buffer = breadcrumbField?.GetValue(tracker);
        buffer.Should().BeNull("no breadcrumb buffer should be allocated when MaxBreadcrumbs is 0");

        tracker.Dispose();
    }
}
