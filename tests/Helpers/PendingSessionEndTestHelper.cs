using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.Helpers;

/// <summary>
/// Helpers for the session-end-durability tests. Reads the tracker's private durable
/// <see cref="PendingSessionEndStore"/> via reflection (mirrors <see cref="TrackerTestHelper"/>)
/// and provides a tiny recording HTTP listener so a tracker can be pointed at a controllable
/// session-end endpoint without inventing a new fake (the SDK's BeaconHttpClient takes no
/// injectable handler — the existing HttpClientTests use the same HttpListener approach).
/// </summary>
internal static class PendingSessionEndTestHelper
{
    /// <summary>
    /// Reads the tracker's private <c>_pendingSessionEndStore</c> and returns its current rows
    /// (oldest first). Returns an empty list if the store was not initialized.
    /// </summary>
    public static IReadOnlyList<PendingSessionEnd> GetPendingEnds(BeaconTracker tracker)
    {
        var store = GetStore(tracker);
        return store is null ? [] : store.DequeueUpTo(1000);
    }

    /// <summary>Count of durable pending session-end records the tracker currently holds.</summary>
    public static long GetPendingEndCount(BeaconTracker tracker)
    {
        var store = GetStore(tracker);
        return store?.GetCount() ?? 0;
    }

    private static PendingSessionEndStore? GetStore(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_pendingSessionEndStore",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (PendingSessionEndStore?)field!.GetValue(tracker);
    }
}

/// <summary>
/// A recording HTTP listener that captures the request bodies/paths POSTed to it and returns a
/// caller-controlled status code (optionally after a delay, to drive a ShutdownFlushTimeout).
/// Modeled on the HttpListener usage already in <c>HttpClientTests</c> — no new fake type, just
/// a small reusable harness so each durability test does not re-roll its own listener loop.
/// </summary>
internal sealed class RecordingSessionEndServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(string Path, string Body)> _requests = [];
    private readonly object _lock = new();
    private readonly Task _loop;

    /// <summary>Status code to return for /v1/events/sessions/end. Default 200.</summary>
    public int SessionEndStatusCode { get; set; } = 200;

    /// <summary>Optional delay before responding to a session-end (drives a timeout). Default none.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    public string BaseUrl { get; }

    public RecordingSessionEndServer()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped
            }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                string body;
                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                lock (_lock)
                {
                    _requests.Add((path, body));
                }

                var status = 200;
                if (path.EndsWith("/sessions/end", StringComparison.Ordinal))
                {
                    if (ResponseDelay > TimeSpan.Zero)
                        await Task.Delay(ResponseDelay, _cts.Token).ConfigureAwait(false);
                    status = SessionEndStatusCode;
                }

                ctx.Response.StatusCode = status;
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>All requests captured so far (path + raw JSON body), in arrival order.</summary>
    public IReadOnlyList<(string Path, string Body)> Requests
    {
        get { lock (_lock) { return _requests.ToList(); } }
    }

    /// <summary>The session-end requests captured so far.</summary>
    public IReadOnlyList<(string Path, string Body)> SessionEndRequests
    {
        get
        {
            lock (_lock)
            {
                return _requests
                    .Where(r => r.Path.EndsWith("/sessions/end", StringComparison.Ordinal))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Polls until at least <paramref name="count"/> session-end requests have arrived, or the
    /// timeout elapses. Avoids sleeps in the test body — used after a fire-and-forget send.
    /// </summary>
    public async Task<bool> WaitForSessionEndRequestsAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (SessionEndRequests.Count >= count)
                return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return SessionEndRequests.Count >= count;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }
    }
}
