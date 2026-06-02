using System;
using System.IO;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.Helpers;

/// <summary>
/// Locates the on-disk SQLite queue that <see cref="BeaconTracker"/> creates for a
/// given Product, and reads its persisted event count. Mirrors the SDK's path logic
/// (LocalApplicationData/SoftAgility/Beacon/&lt;sanitized product&gt;/queue.db) so RG-5/RG-6
/// can assert that dispose actually persisted in-memory events to disk on each TFM.
/// RG tests use alphanumeric product names, so sanitization is a no-op in practice.
/// </summary>
internal static class DiskQueueLocationHelper
{
    // Matches BeaconTracker.InvalidPathChars exactly.
    private static readonly char[] InvalidPathChars =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    public static string QueueDbPath(string product)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftAgility",
            "Beacon",
            SanitizeProductForPath(product),
            "queue.db");
    }

    /// <summary>
    /// Opens a fresh DiskQueue against the tracker's persisted db file and returns the
    /// number of rows currently queued. Returns 0 if the file does not exist.
    /// </summary>
    public static long PersistedCount(string product)
    {
        var path = QueueDbPath(product);
        if (!File.Exists(path))
            return 0;

        using var queue = new DiskQueue(path, null);
        return queue.GetCount();
    }

    /// <summary>
    /// Best-effort removal of a product's disk-queue directory so RG tests start clean
    /// and do not leak state across runs.
    /// </summary>
    public static void CleanUp(string product)
    {
        try
        {
            var path = QueueDbPath(product);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Test teardown best-effort only.
        }
    }

    private static string SanitizeProductForPath(string product)
    {
        var chars = product.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidPathChars, chars[i]) >= 0)
                chars[i] = '_';
        }

        return new string(chars);
    }
}
