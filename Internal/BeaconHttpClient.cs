using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftAgility.Beacon.Internal.Compat;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Result of a batch flush HTTP request.
/// </summary>
internal sealed class FlushResult
{
    public HttpStatusCode StatusCode { get; init; }
    public bool IsSuccess { get; init; }
    public bool IsRateLimited { get; init; }
    public int RetryAfterSeconds { get; init; }
    public bool IsUnauthorized { get; init; }
    public bool IsHardCapped { get; init; }
    public bool IsServerError { get; init; }
    public bool IsPartialSuccess { get; init; }
    public List<RejectedEvent> RejectedEvents { get; init; } = [];
    public bool IsNetworkError { get; init; }
    public bool IsClientError { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Details of an event rejected by the server in a 207 response.
/// </summary>
internal sealed class RejectedEvent
{
    public string EventId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Wraps HttpClient for all Beacon API communication. Handles events, session start, and session end.
/// </summary>
internal sealed class BeaconHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public BeaconHttpClient(string apiBaseUrl, string apiKey, ILogger? logger)
    {
        _apiKey = apiKey;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Sends a batch of events to POST /v1/events.
    /// Each payload is a pre-serialized JSON string representing a single event.
    /// </summary>
    public async Task<FlushResult> SendEventsAsync(
        IReadOnlyList<string> eventPayloads,
        string? environmentDataBase64,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build JSON array from pre-serialized event payloads
            var jsonArray = "[" + string.Join(",", eventPayloads) + "]";

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/events");
            request.Content = new StringContent(jsonArray, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            if (environmentDataBase64 is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Environment-Data", environmentDataBase64);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            return await ParseResponseAsync(response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: network error during event flush.");
            return new FlushResult
            {
                IsNetworkError = true,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Sends a session start request to POST /v1/events/sessions.
    /// </summary>
    /// <param name="accountId">
    /// Account context set via <c>SetAccount</c>, or null when no account is set.
    /// When null the field is OMITTED from the request body — see account-license-context
    /// PRD §8.1 ("Null/empty/cleared values OMIT the field entirely from the JSON").
    /// </param>
    /// <param name="licenseId">
    /// License context set via <c>SetLicense</c>, or null when no license is set.
    /// When null the field is OMITTED from the request body.
    /// </param>
    public async Task<bool> SendSessionStartAsync(
        string sessionId,
        string actorId,
        string sourceApp,
        string sourceVersion,
        DateTimeOffset startedAt,
        string? accountId = null,
        string? licenseId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Use a Dictionary so account_id / license_id can be omitted (not serialized as null)
            // when no context is set — matches the Track() payload pattern in BeaconTracker.cs.
            var body = new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["actor_id"] = actorId,
                ["product"] = sourceApp,
                ["product_version"] = sourceVersion,
                ["started_at"] = startedAt.ToString("O")
            };

            if (accountId is not null)
                body["account_id"] = accountId;

            if (licenseId is not null)
                body["license_id"] = licenseId;

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/events/sessions");
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await ReadBodyTruncatedAsync(response, cancellationToken);
                _logger?.LogWarning(
                    "Beacon: session start POST returned HTTP {StatusCode} for session {SessionId}{BodySuffix}",
                    (int)response.StatusCode,
                    sessionId,
                    string.IsNullOrEmpty(responseBody) ? "" : $" — {responseBody}");
            }
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to send session start.");
            return false;
        }
    }

    /// <summary>
    /// Sends a session end request to POST /v1/events/sessions/end.
    /// </summary>
    public async Task<bool> SendSessionEndAsync(
        string sessionId,
        DateTimeOffset endedAt,
        string endReason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new
            {
                SessionId = sessionId,
                EndedAt = endedAt.ToString("O"),
                EndReason = endReason
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/events/sessions/end");
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await ReadBodyTruncatedAsync(response, cancellationToken);
                _logger?.LogWarning(
                    "Beacon: session end POST returned HTTP {StatusCode} for session {SessionId}{BodySuffix}",
                    (int)response.StatusCode,
                    sessionId,
                    string.IsNullOrEmpty(responseBody) ? "" : $" — {responseBody}");
            }
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to send session end.");
            return false;
        }
    }

    /// <summary>
    /// Sends an exception report to POST /v1/events/exceptions.
    /// Returns <c>true</c> on HTTP 2xx, <c>false</c> on any other status or network error.
    /// Never throws — all exceptions are caught and logged as warnings.
    /// </summary>
    public async Task<bool> SendExceptionAsync(
        string jsonPayload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/events/exceptions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
                return true;

            var responseBody = await ReadBodyTruncatedAsync(response, cancellationToken);
            _logger?.LogWarning(
                "Beacon: failed to send exception report — HTTP {StatusCode}{BodySuffix}",
                (int)response.StatusCode,
                string.IsNullOrEmpty(responseBody) ? "" : $" — {responseBody}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress — swallow silently
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Beacon: failed to send exception report — {ErrorMessage}.", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends a best-effort identify POST to /v1/actors/identify.
    /// Returns <c>true</c> on HTTP 2xx, <c>false</c> on any other status or network error.
    /// On 409 (already linked), logs a warning and returns <c>false</c>.
    /// </summary>
    public async Task<bool> PostIdentifyAsync(
        string anonymousActorId,
        string identifiedActorId,
        string sourceApp,
        string? sourceVersion)
    {
        try
        {
            var body = new
            {
                AnonymousActorId = anonymousActorId,
                IdentifiedActorId = identifiedActorId,
                IdentifiedAt = DateTimeOffset.UtcNow.ToString("O"),
                Product = sourceApp,
                ProductVersion = sourceVersion
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/actors/identify");
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger?.LogWarning(
                    "Beacon: device ID {DeviceId} is already linked to a different user. Identity link not recorded.",
                    anonymousActorId);
            }
            else if (!response.IsSuccessStatusCode)
            {
                var responseBody = await ReadBodyTruncatedAsync(response, CancellationToken.None);
                _logger?.LogWarning(
                    "Beacon: identify POST returned HTTP {StatusCode} for device {DeviceId}{BodySuffix}",
                    (int)response.StatusCode,
                    anonymousActorId,
                    string.IsNullOrEmpty(responseBody) ? "" : $" — {responseBody}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: identify POST failed for device {DeviceId}.", anonymousActorId);
            return false;
        }
    }

    private async Task<FlushResult> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;

        // 2xx success
        if (response.IsSuccessStatusCode && statusCode != HttpStatus.MultiStatus)
        {
            return new FlushResult
            {
                StatusCode = statusCode,
                IsSuccess = true
            };
        }

        // For all non-2xx and non-207 paths, read the response body once. The
        // body usually contains a structured error from the backend (e.g.
        // "Product 'foo' is not registered", "API key rejected") that the
        // FlushResult.ErrorMessage should surface to the integrator's
        // ILogger. Truncate to 500 chars so a giant 5xx HTML page can't
        // flood the customer's logs.
        var body = string.Empty;
        if (statusCode != HttpStatus.MultiStatus)
        {
            try
            {
                body = await HttpContentCompat.ReadAsStringCompatAsync(response.Content, cancellationToken);
                if (body.Length > 500)
                {
                    body = body[..500] + "...(truncated)";
                }
            }
            catch
            {
                // Body read failed — proceed with empty string.
            }
        }
        var bodySuffix = string.IsNullOrEmpty(body) ? string.Empty : $" — {body}";

        // 207 Multi-Status (partial success)
        if (statusCode == HttpStatus.MultiStatus)
        {
            var rejected = new List<RejectedEvent>();
            try
            {
                var content = await HttpContentCompat.ReadAsStringCompatAsync(response.Content, cancellationToken);
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (item.TryGetProperty("status", out var status) &&
                            status.GetString() == "rejected")
                        {
                            var eventId = item.TryGetProperty("event_id", out var eid)
                                ? eid.GetString() ?? "unknown"
                                : "unknown";
                            var reason = item.TryGetProperty("reason", out var r)
                                ? r.GetString() ?? "unknown"
                                : "unknown";
                            rejected.Add(new RejectedEvent { EventId = eventId, Reason = reason });
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors — treat as success
            }

            return new FlushResult
            {
                StatusCode = statusCode,
                IsSuccess = true,
                IsPartialSuccess = rejected.Count > 0,
                RejectedEvents = rejected
            };
        }

        // 401 Unauthorized
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return new FlushResult
            {
                StatusCode = statusCode,
                IsUnauthorized = true,
                ErrorMessage = $"Beacon: API key rejected (401). Check BeaconOptions.ApiKey. Event delivery halted.{bodySuffix}"
            };
        }

        // 402 Payment Required (Hard Capped)
        if (statusCode == HttpStatusCode.PaymentRequired)
        {
            return new FlushResult
            {
                StatusCode = statusCode,
                IsHardCapped = true,
                ErrorMessage = $"Beacon: Account is hard-capped (402). Events queued to disk. They will be retried after plan upgrade.{bodySuffix}"
            };
        }

        // 429 Too Many Requests (Rate Limited)
        if (statusCode == (HttpStatusCode)429)
        {
            var retryAfter = 60; // default if header absent
            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                retryAfter = (int)Math.Min(delta.TotalSeconds, 300);
            }
            else if (response.Headers.RetryAfter?.Date is { } retryDate)
            {
                retryAfter = (int)Math.Min(Math.Max((retryDate - DateTimeOffset.UtcNow).TotalSeconds, 1), 300);
            }

            return new FlushResult
            {
                StatusCode = statusCode,
                IsRateLimited = true,
                RetryAfterSeconds = retryAfter,
                ErrorMessage = $"Beacon: rate limited (429). Retrying after {retryAfter} seconds.{bodySuffix}"
            };
        }

        // 5xx Server Error
        if ((int)statusCode >= 500)
        {
            return new FlushResult
            {
                StatusCode = statusCode,
                IsServerError = true,
                ErrorMessage = $"Beacon: server error ({(int)statusCode}).{bodySuffix}"
            };
        }

        // 4xx client errors (not 401/402/429) — permanent, no retry
        if ((int)statusCode >= 400 && (int)statusCode < 500)
        {
            return new FlushResult
            {
                StatusCode = statusCode,
                IsClientError = true,
                ErrorMessage = $"Beacon: client error ({(int)statusCode}). Events dropped — this error is permanent.{bodySuffix}"
            };
        }

        // Other unexpected errors
        return new FlushResult
        {
            StatusCode = statusCode,
            IsNetworkError = true,
            ErrorMessage = $"Beacon: unexpected HTTP {(int)statusCode}.{bodySuffix}"
        };
    }

    /// <summary>
    /// Reads the response body and truncates to 500 chars for safe logging.
    /// Used by the per-endpoint helpers (session start/end, identify, exception)
    /// to surface the backend's structured error message to the integrator's logger.
    /// </summary>
    private static async Task<string> ReadBodyTruncatedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await HttpContentCompat.ReadAsStringCompatAsync(response.Content, cancellationToken);
            return body.Length > 500 ? body[..500] + "...(truncated)" : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
