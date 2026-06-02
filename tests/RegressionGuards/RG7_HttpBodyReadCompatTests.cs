// Regression guard RG-7 — HTTP body-read compat (R12).
//
// PRD ref: Test plan "New RG-7". HttpContent.ReadAsStringAsync(CancellationToken) was
// added in .NET 5 and is absent on net48/netstandard2.0. The SDK routes every body read
// through Internal/Compat HttpContentCompat.ReadAsStringCompatAsync, which uses the token
// overload on modern TFMs and ReadAsStringAsync() + ct.ThrowIfCancellationRequested()
// down-level. This guard drives:
//   (a) a non-2xx (400) response carrying a server error body  -> error-body read path
//   (b) a 207 Multi-Status response with a rejected event       -> 207 body read path
// and asserts the body content surfaced. On net48 a broken shim would fail to compile or
// would drop the body, so ErrorMessage / RejectedEvents would be empty.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

[Collection("HttpListener")]
public sealed class RG7_HttpBodyReadCompatTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;

    public RG7_HttpBodyReadCompatTests()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
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

    // RG-7(a): non-2xx error body is read via the compat shim and surfaced in ErrorMessage.
    [Fact]
    public async Task SendEventsAsync_NonSuccessBody_IsReadViaCompatShim()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };
        const string serverMessage = "Product 'beacon-demo' is not registered";

        var listenerTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "text/plain";
            var bytes = Encoding.UTF8.GetBytes(serverMessage);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        // Act
        var result = await client.SendEventsAsync(payloads, null, CancellationToken.None);
        await listenerTask;

        // Assert — the body was read (down-level via ReadAsStringCompatAsync) and appended
        result.IsSuccess.Should().BeFalse();
        result.IsClientError.Should().BeTrue();
        result.ErrorMessage.Should().Contain(serverMessage,
            "the non-2xx body must be read via the compat shim and surfaced");
    }

    // RG-7(b): 207 Multi-Status body is read via the compat shim and parsed into
    // RejectedEvents. This is the second ReadAsStringCompatAsync call site.
    [Fact]
    public async Task SendEventsAsync_207MultiStatusBody_IsReadAndParsedViaCompatShim()
    {
        // Arrange
        using var client = new BeaconHttpClient(_baseUrl, "test-key", null);
        var payloads = new[] { "{\"event_id\":\"test\",\"name\":\"e1\"}" };
        const string responseBody =
            "{\"results\":[" +
            "{\"event_id\":\"ev-001\",\"status\":\"accepted\"}," +
            "{\"event_id\":\"ev-002\",\"status\":\"rejected\",\"reason\":\"Schema mismatch\"}" +
            "]}";

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

        // Assert — 207 treated as success, body read+parsed via the shim
        result.IsSuccess.Should().BeTrue("207 is partial success");
        result.IsPartialSuccess.Should().BeTrue();
        result.RejectedEvents.Should().ContainSingle();
        result.RejectedEvents[0].EventId.Should().Be("ev-002");
        result.RejectedEvents[0].Reason.Should().Be("Schema mismatch");
    }
}
