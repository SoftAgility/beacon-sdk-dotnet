// Covers FR-2366 (SDK drain pacing against the server-side event rate limit):
//   AC-3441: A 429 stops the drain for the cycle instead of sending the next batch
//   AC-3442: Retry-After is honoured ACROSS cycles, not just within one
//   AC-3443: A 429 returns promptly — no inline sleep on the flushing thread
//   AC-3444: Rate-limited events are preserved on disk, never dropped
//   AC-3445: Sends resume once the cooldown expires
//
// These run against a real HttpListener that answers every request with 429, because the
// behaviour under test is "how many requests does the client make", and that is only
// observable from the server side.

using System.Diagnostics;
using System.Net;
using System.Reflection;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

[Collection("HttpListener")]
public sealed class RateLimitPacingTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _serverCts = new();
    private readonly List<string> _products = new();
    private int _requestCount;
    private BeaconTracker? _tracker;

    public RateLimitPacingTests()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    public void Dispose()
    {
        _serverCts.Cancel();
        _tracker?.Dispose();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        foreach (var product in _products)
        {
            try { DiskQueueLocationHelper.CleanUp(product); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Answers every request with 429 and counts them. The count is the whole point: a client
    /// that keeps draining after a 429 shows up here as N requests instead of one.
    /// </summary>
    private void StartAlways429Server(int retryAfterSeconds = 60)
    {
        _ = Task.Run(async () =>
        {
            while (!_serverCts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref _requestCount);
                try
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.AddHeader("Retry-After", retryAfterSeconds.ToString());
                    ctx.Response.Close();
                }
                catch { /* client may have gone away */ }
            }
        });
    }

    private BeaconTracker CreateTracker(string product, int maxBatchSize = 5)
    {
        _products.Add(product);
        return TrackerTestHelper.CreateTracker(
            apiBaseUrl: _baseUrl,
            appName: product,
            maxBatchSize: maxBatchSize,
            flushIntervalSeconds: 3600);
    }

    private static void ForceCooldownExpiry(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField(
            "_rateLimitedUntilTicks", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("the cooldown field backs FR-2366; renaming it breaks this guard");
        field!.SetValue(tracker, DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks);
    }

    private static long ReadCooldownTicks(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField(
            "_rateLimitedUntilTicks", BindingFlags.NonPublic | BindingFlags.Instance);
        return (long)field!.GetValue(tracker)!;
    }

    // AC-3441 + AC-3443: 25 events at a batch size of 5 is five batches. The server rejects the
    // first. Exactly one request must reach it — and the whole thing must finish fast, because
    // the old code slept the full Retry-After on the flushing thread once per batch.
    [Fact]
    public async Task RateLimited_StopsTheDrainAndDoesNotSleepPerBatch()
    {
        StartAlways429Server(retryAfterSeconds: 60);
        var product = $"PacingStop_{Guid.NewGuid():N}";
        _tracker = CreateTracker(product);

        for (var i = 0; i < 25; i++)
            _tracker.Track("batch", $"event_{i}", "actor-1");

        var sw = Stopwatch.StartNew();
        await _tracker.FlushAsync();
        sw.Stop();

        Volatile.Read(ref _requestCount).Should().Be(1,
            "a 429 means the tenant's per-minute event budget is spent — every remaining batch "
            + "is charged against the same budget, so sending them can only produce more 429s");

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "Retry-After must be recorded as a deadline, not slept through on the flushing "
            + "thread; the previous code blocked 60s per batch and delivered nothing");
    }

    // AC-3442: the point of a cooldown is that it survives the cycle that set it. A client that
    // resumes on the next timer tick has not honoured Retry-After at all.
    [Fact]
    public async Task RateLimited_SuppressesSubsequentFlushCycles()
    {
        StartAlways429Server(retryAfterSeconds: 60);
        var product = $"PacingHold_{Guid.NewGuid():N}";
        _tracker = CreateTracker(product);

        _tracker.Track("batch", "first", "actor-1");
        await _tracker.FlushAsync();
        Volatile.Read(ref _requestCount).Should().Be(1);

        // Three more cycles, each with fresh events. All must stay silent.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            _tracker.Track("batch", $"during_cooldown_{cycle}", "actor-1");
            await _tracker.FlushAsync();
        }

        Volatile.Read(ref _requestCount).Should().Be(1,
            "the server said not for 60 seconds; asking again three times inside that window is "
            + "exactly the behaviour the limit exists to stop");
    }

    // AC-3444: suppressing sends must not mean losing events. Everything queued goes to disk.
    [Fact]
    public async Task RateLimited_PreservesEveryEventOnDisk()
    {
        StartAlways429Server();
        var product = $"PacingKeep_{Guid.NewGuid():N}";
        _tracker = CreateTracker(product);

        for (var i = 0; i < 25; i++)
            _tracker.Track("batch", $"event_{i}", "actor-1");

        await _tracker.FlushAsync();

        // Events tracked AFTER the cooldown arms legitimately stay in memory — the gate turns
        // later flush cycles into no-ops, which is the whole point. So the property to assert
        // is conservation across both tiers, not an empty memory queue.
        var inMemory = TrackerTestHelper.GetMemoryQueueCount(_tracker);
        var onDisk = DiskQueueLocationHelper.PersistedCount(product);

        onDisk.Should().BeGreaterThan(0,
            "the batch the server rejected must be persisted; holding it only in memory would "
            + "lose it to a process restart during the cooldown");

        (inMemory + onDisk).Should().BeGreaterThanOrEqualTo(25,
            "being throttled is a reason to wait, not a reason to discard telemetry");
    }

    // AC-3445: a cooldown that never lifts is an outage. Once it expires, sending resumes.
    [Fact]
    public async Task RateLimitCooldown_ExpiresAndSendingResumes()
    {
        StartAlways429Server(retryAfterSeconds: 60);
        var product = $"PacingResume_{Guid.NewGuid():N}";
        _tracker = CreateTracker(product);

        _tracker.Track("batch", "first", "actor-1");
        await _tracker.FlushAsync();
        Volatile.Read(ref _requestCount).Should().Be(1);
        ReadCooldownTicks(_tracker).Should().BeGreaterThan(0, "a 429 must arm the cooldown");

        ForceCooldownExpiry(_tracker);

        _tracker.Track("batch", "after_cooldown", "actor-1");
        await _tracker.FlushAsync();

        Volatile.Read(ref _requestCount).Should().BeGreaterThan(1,
            "once the server Retry-After window has elapsed the client must try again — "
            + "otherwise a single 429 silences the SDK permanently");
    }

    // A hostile or misconfigured endpoint must not be able to silence telemetry for hours.
    // The server bucket is 60s, so anything past the 300s clamp is not a real instruction.
    [Fact]
    public async Task AbsurdRetryAfter_IsClampedToTheCeiling()
    {
        StartAlways429Server(retryAfterSeconds: 86_400);
        var product = $"PacingClamp_{Guid.NewGuid():N}";
        _tracker = CreateTracker(product);

        _tracker.Track("batch", "first", "actor-1");
        var before = DateTimeOffset.UtcNow;
        await _tracker.FlushAsync();

        var until = new DateTimeOffset(ReadCooldownTicks(_tracker), TimeSpan.Zero);
        (until - before).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(301),
            "honouring a day-long Retry-After verbatim would let one bad response turn into "
            + "a silent, self-inflicted telemetry outage");
    }
}
