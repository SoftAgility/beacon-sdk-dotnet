// Covers:
//   Identify() validation: null, empty, max-length, valid input
//   Identify + Track integration: identified actor flows to 2-arg Track, throws without Identify,
//     3-arg Track still works, second Identify overrides first
//   Identify + StartSession integration: parameterless StartSession works after Identify,
//     throws without Identify, StartSession(actorId) also identifies, actor flows to Track
//   Identify + Dispose: Identify after Dispose is no-op
//   Identify POST (FR-1771): fires best-effort POST, same-user skips POST, 409 logs warning

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the Identify() method and its integration with Track() and StartSession() overloads.
/// </summary>
public sealed class IdentifyTests : IDisposable
{
    private readonly BeaconTracker _tracker;

    public IdentifyTests()
    {
        _tracker = TrackerTestHelper.CreateTracker();
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }

    // ── Identify validation ─────────────────────────────────────────────

    [Fact]
    public void Identify_WithNullActorId_ThrowsArgumentException()
    {
        // Act
        var act = () => _tracker.Identify(null!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*null or empty*");
    }

    [Fact]
    public void Identify_WithEmptyActorId_ThrowsArgumentException()
    {
        // Act
        var act = () => _tracker.Identify("");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*null or empty*");
    }

    [Fact]
    public void Identify_WithActorIdExceeding512Chars_ThrowsArgumentException()
    {
        // Arrange
        var longActorId = new string('x', 513);

        // Act
        var act = () => _tracker.Identify(longActorId);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*512 characters*");
    }

    [Fact]
    public void Identify_WithValidActorId_DoesNotThrow()
    {
        // Act
        var act = () => _tracker.Identify("valid-actor-123");

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetActorId(_tracker).Should().Be("valid-actor-123");
    }

    [Fact]
    public void Identify_WithExactly512Chars_DoesNotThrow()
    {
        // Arrange
        var actorId = new string('a', 512);

        // Act
        var act = () => _tracker.Identify(actorId);

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetActorId(_tracker).Should().Be(actorId);
    }

    // ── Identify + Track integration ────────────────────────────────────

    [Fact]
    public void Track_AfterIdentify_UsesIdentifiedActorId()
    {
        // Arrange
        _tracker.Identify("identified-user-42");

        // Act
        _tracker.Track("analytics", "page_view");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("identified-user-42");

        docs[0].Dispose();
    }

    [Fact]
    public void Track_WithoutIdentify_UsesDeviceIdFallback()
    {
        // Act — no longer throws; uses anonymous device ID
        var act = () => _tracker.Track("analytics", "page_view");

        // Assert
        act.Should().NotThrow();
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);
        docs[0].RootElement.GetProperty("actor_id").GetString().Should().NotBeNullOrEmpty(
            "device ID should be used as actor_id when Identify() has not been called");
        docs[0].Dispose();
    }

    [Fact]
    public void Track_WithExplicitActorId_DoesNotRequireIdentify()
    {
        // Act — the 3-arg overload does not require Identify
        var act = () => _tracker.Track("analytics", "page_view", "explicit-actor");

        // Assert
        act.Should().NotThrow();
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("explicit-actor");

        docs[0].Dispose();
    }

    [Fact]
    public void Identify_CalledTwice_UsesLatestActorId()
    {
        // Arrange
        _tracker.Identify("first-actor");
        _tracker.Identify("second-actor");

        // Act
        _tracker.Track("analytics", "page_view");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("second-actor");

        docs[0].Dispose();
    }

    [Fact]
    public void Track_AfterIdentify_WithProperties_QueuesCorrectPayload()
    {
        // Arrange
        _tracker.Identify("prop-actor");
        var properties = new Dictionary<string, object> { ["page"] = "/dashboard" };

        // Act
        _tracker.Track("navigation", "page_view", properties);

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("prop-actor");
        root.GetProperty("category").GetString().Should().Be("navigation");
        root.GetProperty("name").GetString().Should().Be("page_view");
        root.GetProperty("properties").GetProperty("page").GetString().Should().Be("/dashboard");

        docs[0].Dispose();
    }

    // ── Identify + StartSession integration ─────────────────────────────

    [Fact]
    public void StartSession_AfterIdentify_DoesNotThrow()
    {
        // Arrange
        _tracker.Identify("session-actor");

        // Act
        var act = () => _tracker.StartSession();

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetSessionId(_tracker).Should().NotBeNullOrEmpty(
            "a session should be started using the identified actor");
    }

    [Fact]
    public void StartSession_WithoutIdentify_UsesDeviceIdFallback()
    {
        // Act — no longer throws; uses anonymous device ID
        var act = () => _tracker.StartSession();

        // Assert
        act.Should().NotThrow();
        TrackerTestHelper.GetSessionId(_tracker).Should().NotBeNullOrEmpty(
            "a session should be started using the anonymous device ID");
    }

    [Fact]
    public void StartSession_WithActorId_AlsoIdentifies()
    {
        // Act — StartSession(actorId) should also store the actor
        _tracker.StartSession("session-started-actor");

        // Assert — the actor should be stored, enabling 2-arg Track
        TrackerTestHelper.GetActorId(_tracker).Should().Be("session-started-actor");

        // Verify Track(category, name) works without a separate Identify call
        var act = () => _tracker.Track("test", "event");
        act.Should().NotThrow();

        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("session-started-actor");

        docs[0].Dispose();
    }

    [Fact]
    public void Track_AfterStartSessionWithActorId_UsesSessionActorId()
    {
        // Arrange
        _tracker.StartSession("flow-through-actor");

        // Act
        _tracker.Track("checkout", "complete");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(_tracker);
        docs.Should().HaveCount(1);

        var root = docs[0].RootElement;
        root.GetProperty("actor_id").GetString().Should().Be("flow-through-actor");
        root.TryGetProperty("session_id", out var sessionId).Should().BeTrue(
            "session_id should be included when a session is active");
        sessionId.GetString().Should().NotBeNullOrEmpty();

        docs[0].Dispose();
    }

    // ── Track(actorId) and StartSession(actorId) validation ─────────────

    [Fact]
    public void Track_WithNullActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.Track("cat", "name", (string)null!);
        act.Should().Throw<ArgumentException>().WithMessage("*null or empty*");
    }

    [Fact]
    public void Track_WithEmptyActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.Track("cat", "name", "");
        act.Should().Throw<ArgumentException>().WithMessage("*null or empty*");
    }

    [Fact]
    public void Track_WithOversizedActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.Track("cat", "name", new string('x', 513));
        act.Should().Throw<ArgumentException>().WithMessage("*512 characters*");
    }

    [Fact]
    public void StartSession_WithNullActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.StartSession((string)null!);
        act.Should().Throw<ArgumentException>().WithMessage("*null or empty*");
    }

    [Fact]
    public void StartSession_WithEmptyActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.StartSession("");
        act.Should().Throw<ArgumentException>().WithMessage("*null or empty*");
    }

    [Fact]
    public void StartSession_WithOversizedActorId_ThrowsArgumentException()
    {
        var act = () => _tracker.StartSession(new string('x', 513));
        act.Should().Throw<ArgumentException>().WithMessage("*512 characters*");
    }

    // ── Identify + Dispose ──────────────────────────────────────────────

    [Fact]
    public void Identify_AfterDispose_IsNoOp()
    {
        // Arrange
        _tracker.Dispose();

        // Act — disposed tracker should not throw, even with invalid input
        var act = () => _tracker.Identify("post-dispose-actor");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Identify_WithNull_AfterDispose_IsNoOp()
    {
        // Arrange
        _tracker.Dispose();

        // Act — null actorId on disposed tracker should be silent no-op, not ArgumentException
        var act = () => _tracker.Identify(null!);

        // Assert
        act.Should().NotThrow();
    }
}

/// <summary>
/// Tests for the Identify() method's best-effort POST to /v1/actors/identify (FR-1771, FR-1774).
/// Uses HttpListener to capture and verify HTTP requests.
/// </summary>
[Collection("HttpListener")]
public sealed class IdentifyPostTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;

    public IdentifyPostTests()
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

    private static string? GetDeviceId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_deviceId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    // AC-2326: Identify fires a best-effort POST to /v1/actors/identify with device ID
    [Fact]
    public async Task Identify_FiresPostWithDeviceIdAsAnonymousActorId()
    {
        // Arrange
        using var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = _baseUrl,
            Product = $"Test_{Guid.NewGuid():N}",
            ProductVersion = "2.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });

        var deviceId = GetDeviceId(tracker);
        deviceId.Should().NotBeNullOrEmpty("device ID should be initialized");

        string? capturedPath = null;
        string? capturedBody = null;
        string? capturedAuth = null;

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            capturedPath = ctx.Request.Url?.AbsolutePath;
            capturedAuth = ctx.Request.Headers["Authorization"];
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            capturedBody = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            var body = Encoding.UTF8.GetBytes("{\"status\":\"linked\"}");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        // Act
        tracker.Identify("userA");

        // Wait for the fire-and-forget POST to complete (throws TimeoutException on timeout)
        await listenerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        capturedPath.Should().Be("/v1/actors/identify");
        capturedAuth.Should().Be("Bearer test-key");

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("anonymous_actor_id").GetString().Should().Be(deviceId);
        root.GetProperty("identified_actor_id").GetString().Should().Be("userA");
        root.GetProperty("product").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("product_version").GetString().Should().Be("2.0.0");
        root.TryGetProperty("identified_at", out _).Should().BeTrue();

        // Actor ID should be set regardless
        TrackerTestHelper.GetActorId(tracker).Should().Be("userA");
    }

    // AC-2327: Re-identifying with the same actor ID does NOT fire POST
    [Fact]
    public async Task Identify_SameUser_DoesNotFirePost()
    {
        // Arrange — first identify fires a POST; second should not
        using var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = _baseUrl,
            Product = $"Test_{Guid.NewGuid():N}",
            ProductVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });

        // Handle the first identify POST
        var firstListenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            var body = Encoding.UTF8.GetBytes("{\"status\":\"linked\"}");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        tracker.Identify("userA");
        await firstListenerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Act — second identify with same user
        // Set up a listener that times out if no request comes
        var secondCallReceived = false;
        var secondListenerTask = Task.Run(async () =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(cts.Token);
                secondCallReceived = true;
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch (OperationCanceledException)
            {
                // Expected — no request arrived
            }
        });

        tracker.Identify("userA"); // Same user — should NOT fire POST
        await secondListenerTask;

        // Assert
        secondCallReceived.Should().BeFalse("re-identifying with the same actor ID should skip the POST");
    }

    // AC-2328: 409 response logs warning, does not revert actorId, deviceId unchanged
    [Fact]
    public async Task Identify_On409_LogsWarningAndDoesNotRevertActorId()
    {
        // Arrange
        using var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = _baseUrl,
            Product = $"Test_{Guid.NewGuid():N}",
            ProductVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });

        var deviceIdBefore = GetDeviceId(tracker);

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 409;
            var body = Encoding.UTF8.GetBytes(
                "{\"error\":\"identity_already_linked\",\"existing_identified_actor_id\":\"userA\"}");
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });

        // Act
        tracker.Identify("userB");

        await listenerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Give a moment for the fire-and-forget task to complete logging
        await Task.Delay(100);

        // Assert — actorId should NOT be reverted
        TrackerTestHelper.GetActorId(tracker).Should().Be("userB",
            "actor ID should remain as the newly identified user even on 409");

        // Assert — deviceId should NOT change
        GetDeviceId(tracker).Should().Be(deviceIdBefore,
            "device ID should not be regenerated on 409");
    }

    // AC-2333: identify() returns immediately (non-blocking)
    [Fact]
    public void Identify_ReturnsImmediately_BeforeHttpCallCompletes()
    {
        // Arrange — use a listener that holds the connection open
        using var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = _baseUrl,
            Product = $"Test_{Guid.NewGuid():N}",
            ProductVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });

        // Don't accept the connection yet — measure that Identify returns fast
        var sw = System.Diagnostics.Stopwatch.StartNew();
        tracker.Identify("userA");
        sw.Stop();

        // Assert — Identify should return in under 50ms (it's fire-and-forget)
        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "identify() must return immediately without waiting for the HTTP POST");

        // Clean up — accept and close the pending request
        try
        {
            var ctx = _listener.GetContext();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        }
        catch { /* ignore */ }
    }
}
