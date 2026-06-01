// Covers:
//   AC-518: Authorization header is Bearer <ApiKey> (FR-448)
//   AC-519: First flush includes X-Environment-Data header (FR-448, FR-463)
//   AC-520: Subsequent flushes do not include X-Environment-Data (FR-463)
//   AC-528: 5xx response triggers retry with backoff (FR-458, EC-421)
//   AC-529: After 3 retries, events written to disk (FR-454, EC-421)
//   AC-533: 429 with Retry-After respects wait duration (FR-457, EC-420)
//   AC-534: 429 without Retry-After defaults to 60 seconds (FR-457, EC-420)
//   AC-535: 401 halts delivery and logs error (FR-459, EC-419)
//   AC-536: 402 writes events to disk (FR-460, EC-422)
//   AC-546: 207 with rejected events logs warnings (ED-428)
//   EC-419: HTTP 401 Unauthorized
//   EC-420: HTTP 429 Rate Limited
//   EC-421: HTTP 5xx Server Error
//   EC-422: HTTP 402 Hard Capped
//
// Note: BeaconHttpClient does not accept an injectable HttpMessageHandler.
// These tests verify behavior through BeaconHttpClient's public API using
// a real HTTP listener (HttpListener) that returns controlled responses.
// Tests that would require long waits (429 Retry-After, 5xx backoff) are
// tested at the response parsing level rather than the full retry loop.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for BeaconHttpClient using a local HttpListener to simulate server responses.
/// </summary>
[Collection("HttpListener")]
public sealed class HttpClientTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;

    public HttpClientTests()
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
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
    }

    // AC-518: Authorization header is Bearer <ApiKey>
    [Fact]
    public async Task SendEventsAsync_IncludesBearerAuthorizationHeader()
    {
        // Arrange
        var apiKey = "my-secret-api-key";
        using var client = new BeaconHttpClient(_baseUrl, apiKey, null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        // Set up listener to capture the request and return 200
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var authHeader = ctx.Request.Headers["Authorization"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return authHeader;
        });

        // Act
        await client.SendEventsAsync(payloads, null, CancellationToken.None);
        var authValue = await listenerTask;

        // Assert
        authValue.Should().Be($"Bearer {apiKey}");
    }

    // AC-519: First flush includes X-Environment-Data header when provided
    [Fact]
    public async Task SendEventsAsync_WithEnvironmentData_IncludesXEnvironmentDataHeader()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };
        var envBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"os_name\":\"Windows\"}"));

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var envHeader = ctx.Request.Headers["X-Environment-Data"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return envHeader;
        });

        // Act
        await client.SendEventsAsync(payloads, envBase64, CancellationToken.None);
        var headerValue = await listenerTask;

        // Assert
        headerValue.Should().Be(envBase64);
    }

    // AC-520: When environmentDataBase64 is null, X-Environment-Data header is absent
    [Fact]
    public async Task SendEventsAsync_WithoutEnvironmentData_OmitsXEnvironmentDataHeader()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var envHeader = ctx.Request.Headers["X-Environment-Data"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return envHeader;
        });

        // Act
        await client.SendEventsAsync(payloads, null, CancellationToken.None);
        var headerValue = await listenerTask;

        // Assert
        headerValue.Should().BeNull("X-Environment-Data should not be included when not provided");
    }

    // AC-535 / EC-419: 401 response sets IsUnauthorized with correct error message
    [Fact]
    public async Task SendEventsAsync_With401Response_ReturnsUnauthorizedResult()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "bad-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsUnauthorized.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("API key rejected");
    }

    // AC-536 / EC-422: 402 response sets IsHardCapped
    [Fact]
    public async Task SendEventsAsync_With402Response_ReturnsHardCappedResult()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 402;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsHardCapped.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("hard-capped");
    }

    // AC-533 / EC-420: 429 response sets IsRateLimited with RetryAfterSeconds
    [Fact]
    public async Task SendEventsAsync_With429AndRetryAfter_ReturnsRateLimitedWithSeconds()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 429;
            ctx.Response.AddHeader("Retry-After", "30");
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsRateLimited.Should().BeTrue();
        result.RetryAfterSeconds.Should().Be(30);
    }

    // AC-534 / EC-420: 429 without Retry-After header defaults to 60 seconds
    [Fact]
    public async Task SendEventsAsync_With429WithoutRetryAfter_DefaultsTo60Seconds()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 429;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsRateLimited.Should().BeTrue();
        result.RetryAfterSeconds.Should().Be(60, "default Retry-After should be 60 seconds");
    }

    // AC-528 / EC-421: 5xx response sets IsServerError
    [Fact]
    public async Task SendEventsAsync_With500Response_ReturnsServerErrorResult()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsServerError.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
    }

    // AC-528 supplemental: 503 also sets IsServerError
    [Fact]
    public async Task SendEventsAsync_With503Response_ReturnsServerErrorResult()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsServerError.Should().BeTrue();
    }

    // AC-546 / ED-428: 207 with rejected events returns partial success
    [Fact]
    public async Task SendEventsAsync_With207PartialRejection_ReturnsPartialSuccess()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        // Build response body as raw JSON string to avoid anonymous type array inference issue
        var responseBody = """
            {
                "results": [
                    { "event_id": "ev-001", "status": "accepted" },
                    { "event_id": "ev-002", "status": "rejected", "reason": "Product not registered" }
                ]
            }
            """;

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 207;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsSuccess.Should().BeTrue("207 is treated as successful delivery");
        result.IsPartialSuccess.Should().BeTrue();
        result.RejectedEvents.Should().HaveCount(1);
        result.RejectedEvents[0].EventId.Should().Be("ev-002");
        result.RejectedEvents[0].Reason.Should().Be("Product not registered");
    }

    // AC-546 supplemental: 207 with all accepted returns success without partial flag
    [Fact]
    public async Task SendEventsAsync_With207AllAccepted_ReturnsSuccessNotPartial()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var responseBody = """
            {
                "results": [
                    { "event_id": "ev-001", "status": "accepted" }
                ]
            }
            """;

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 207;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsPartialSuccess.Should().BeFalse();
        result.RejectedEvents.Should().BeEmpty();
    }

    // AC-518 supplemental: Sends to POST /v1/events endpoint
    [Fact]
    public async Task SendEventsAsync_SendsToCorrectEndpoint()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var path = ctx.Request.Url?.AbsolutePath;
            var method = ctx.Request.HttpMethod;
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return (path, method);
        });

        // Act
        await client.SendEventsAsync(payloads, null, CancellationToken.None);
        var (path, method) = await listenerTask;

        // Assert
        path.Should().Be("/v1/events");
        method.Should().Be("POST");
    }

    // AC-521: SendSessionStartAsync sends correct POST to /v1/events/sessions
    [Fact]
    public async Task SendSessionStartAsync_SendsCorrectRequest()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var sessionId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        string? capturedBody = null;
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var path = ctx.Request.Url?.AbsolutePath;
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            capturedBody = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return path;
        });

        // Act
        var result = await client.SendSessionStartAsync(
            sessionId, "user-1", "MyApp", "1.0.0", startedAt);
        var path = await listenerTask;

        // Assert
        path.Should().Be("/v1/events/sessions");
        result.Should().BeTrue();
        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("session_id").GetString().Should().Be(sessionId);
        root.GetProperty("actor_id").GetString().Should().Be("user-1");
        root.GetProperty("product").GetString().Should().Be("MyApp");
        root.GetProperty("product_version").GetString().Should().Be("1.0.0");
        root.TryGetProperty("started_at", out _).Should().BeTrue();
    }

    // AC-524: SendSessionEndAsync sends correct POST to /v1/events/sessions/end
    [Fact]
    public async Task SendSessionEndAsync_SendsCorrectRequest()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var sessionId = Guid.NewGuid().ToString();
        var endedAt = DateTimeOffset.UtcNow;

        string? capturedBody = null;
        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var path = ctx.Request.Url?.AbsolutePath;
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
            capturedBody = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            return path;
        });

        // Act
        var result = await client.SendSessionEndAsync(sessionId, endedAt, "normal");
        var path = await listenerTask;

        // Assert
        path.Should().Be("/v1/events/sessions/end");
        result.Should().BeTrue();
        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("session_id").GetString().Should().Be(sessionId);
        root.GetProperty("end_reason").GetString().Should().Be("normal");
        root.TryGetProperty("ended_at", out _).Should().BeTrue();
    }

    // Success response returns IsSuccess=true
    [Fact]
    public async Task SendEventsAsync_With200Response_ReturnsSuccess()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Network error returns IsNetworkError
    [Fact]
    public async Task SendEventsAsync_WithNetworkError_ReturnsNetworkErrorResult()
    {
        // Arrange — use a port that nothing is listening on
        using var client = new BeaconHttpClient("http://127.0.0.1:1/", "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);

        // Assert
        result.IsNetworkError.Should().BeTrue();
        result.IsSuccess.Should().BeFalse();
    }

    // Bug 2: 400 Bad Request is a permanent client error, not a network error
    [Fact]
    public async Task SendEventsAsync_With400Response_ReturnsClientError()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsClientError.Should().BeTrue();
        result.IsNetworkError.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
    }

    // Bug 2: 403 Forbidden is a permanent client error
    [Fact]
    public async Task SendEventsAsync_With403Response_ReturnsClientError()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsClientError.Should().BeTrue();
        result.IsNetworkError.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
    }

    // Bug 2: 404 Not Found is a permanent client error
    [Fact]
    public async Task SendEventsAsync_With404Response_ReturnsClientError()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert
        result.IsClientError.Should().BeTrue();
        result.IsNetworkError.Should().BeFalse();
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("client error");
    }
}
