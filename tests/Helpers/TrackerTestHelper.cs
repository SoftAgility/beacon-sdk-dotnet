using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace SoftAgility.Beacon.Tests.Helpers;

/// <summary>
/// Helper methods for accessing BeaconTracker internals in test scenarios.
/// Uses reflection to read private state for assertion purposes only.
/// </summary>
internal static class TrackerTestHelper
{
    /// <summary>
    /// Creates a BeaconTracker with valid defaults for testing.
    /// The HTTP client will point to a non-routable address — the tracker
    /// is intended for queuing tests only, not HTTP delivery tests.
    /// </summary>
    public static BeaconTracker CreateTracker(
        string apiKey = "test-api-key",
        string apiBaseUrl = "https://beacon.test.local",
        string? appName = null,
        string appVersion = "1.0.0",
        bool enabled = true,
        int maxBatchSize = 1000,
        int flushIntervalSeconds = 3600)
    {
        return new BeaconTracker(new BeaconOptions
        {
            ApiKey = apiKey,
            ApiBaseUrl = apiBaseUrl,
            AppName = appName ?? $"Test_{Guid.NewGuid():N}",
            AppVersion = appVersion,
            Enabled = enabled,
            MaxBatchSize = maxBatchSize,
            FlushIntervalSeconds = flushIntervalSeconds
        });
    }

    /// <summary>
    /// Reads the in-memory queue contents from the tracker via reflection.
    /// Returns the raw JSON strings queued by Track() calls.
    /// </summary>
    public static List<string> GetQueuedEvents(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_memoryQueue",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var queue = (ConcurrentQueue<string>)field!.GetValue(tracker)!;
        return queue.ToArray().ToList();
    }

    /// <summary>
    /// Reads the in-memory queue and parses each event as a JsonDocument.
    /// </summary>
    public static List<JsonDocument> GetQueuedEventDocuments(BeaconTracker tracker)
    {
        return GetQueuedEvents(tracker)
            .Select(json => JsonDocument.Parse(json))
            .ToList();
    }

    /// <summary>
    /// Gets the current session ID from the tracker via reflection.
    /// </summary>
    public static string? GetSessionId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_sessionId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    /// <summary>
    /// Gets the stored actor ID from the tracker via reflection.
    /// </summary>
    public static string? GetActorId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_actorId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    /// <summary>
    /// Gets the _disposed flag from the tracker via reflection.
    /// </summary>
    public static bool GetDisposed(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)field!.GetValue(tracker)!;
    }

    /// <summary>
    /// Gets the _environmentSent flag from the tracker via reflection.
    /// </summary>
    public static bool GetEnvironmentSent(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_environmentSent",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)field!.GetValue(tracker)!;
    }

    /// <summary>
    /// Gets the approximate memory queue count from the tracker.
    /// </summary>
    public static int GetMemoryQueueCount(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_memoryQueueCount",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)field!.GetValue(tracker)!;
    }

    /// <summary>
    /// Gets the _halted flag from the tracker via reflection.
    /// </summary>
    public static bool GetHalted(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_halted",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)field!.GetValue(tracker)!;
    }
}
