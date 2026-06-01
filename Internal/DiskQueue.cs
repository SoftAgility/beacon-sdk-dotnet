using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Manages the SQLite offline event queue. Events that fail to send are persisted here
/// and replayed on subsequent flush cycles or application restarts.
/// Thread-safe: all operations are synchronized internally.
/// </summary>
internal sealed class DiskQueue : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private bool _disposed;

    public DiskQueue(string dbPath, ILogger? logger)
    {
        _dbPath = dbPath;
        _logger = logger;

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        // Set a busy timeout so that multiple BeaconTracker instances sharing the same
        // Product on one host (e.g. the same app running as several processes, or a
        // multi-worker server) degrade gracefully under disk-queue contention: a
        // contending writer WAITS up to 5s for the lock instead of failing immediately
        // with SQLITE_BUSY (the default busy_timeout is 0). We deliberately keep the
        // default rollback journal rather than WAL: WAL would route writes to a side
        // -wal file, so the main .db file no longer reflects the queue's true size and
        // the MaxQueueSizeMb cap (which measures the .db file) would stop enforcing.
        // Off the hot path: Track() is non-blocking (in-memory queue); only the
        // background flush thread touches the disk queue.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS queued_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                queued_at TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Enqueues one or more event payloads (serialized JSON strings) to the disk queue.
    /// </summary>
    public void Enqueue(IEnumerable<string> payloads)
    {
        lock (_lock)
        {
            if (_disposed) return;

            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO queued_events (event_id, payload_json, queued_at, retry_count)
                VALUES ($eventId, $payload, $queuedAt, 0);
                """;

            var eventIdParam = cmd.Parameters.Add("$eventId", SqliteType.Text);
            var payloadParam = cmd.Parameters.Add("$payload", SqliteType.Text);
            var queuedAtParam = cmd.Parameters.Add("$queuedAt", SqliteType.Text);

            var queuedAt = DateTimeOffset.UtcNow.ToString("O");

            foreach (var payload in payloads)
            {
                // Extract event_id from the JSON payload
                var eventId = ExtractEventId(payload) ?? Guid.NewGuid().ToString();
                eventIdParam.Value = eventId;
                payloadParam.Value = payload;
                queuedAtParam.Value = queuedAt;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Dequeues up to <paramref name="count"/> events from the disk queue, ordered by id ascending.
    /// Returns a list of (id, payload_json) tuples.
    /// </summary>
    public IReadOnlyList<(long Id, string Payload)> DequeueUpTo(int count)
    {
        lock (_lock)
        {
            if (_disposed) return [];

            var results = new List<(long, string)>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, payload_json FROM queued_events ORDER BY id ASC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", count);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            return results;
        }
    }

    /// <summary>
    /// Deletes the specified rows by their ids from the disk queue.
    /// </summary>
    public void Delete(IEnumerable<long> ids)
    {
        lock (_lock)
        {
            if (_disposed) return;

            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM queued_events WHERE id = $id;";
            var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);

            foreach (var id in ids)
            {
                idParam.Value = id;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Returns the current file size of the SQLite database in bytes.
    /// </summary>
    public long GetFileSizeBytes()
    {
        try
        {
            var fileInfo = new FileInfo(_dbPath);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes the oldest <paramref name="count"/> events from the queue.
    /// </summary>
    public void DeleteOldest(int count)
    {
        lock (_lock)
        {
            if (_disposed) return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM queued_events WHERE id IN (SELECT id FROM queued_events ORDER BY id ASC LIMIT $limit);";
            cmd.Parameters.AddWithValue("$limit", count);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Enforces the disk queue size cap by deleting oldest events until the file is below the limit.
    /// Returns the total number of events dropped.
    /// </summary>
    public int EnforceMaxSize(long maxSizeBytes)
    {
        var totalDropped = 0;
        const int batchSize = 100;

        while (GetFileSizeBytes() > maxSizeBytes)
        {
            lock (_lock)
            {
                if (_disposed) return totalDropped;

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM queued_events WHERE id IN (SELECT id FROM queued_events ORDER BY id ASC LIMIT $limit);";
                cmd.Parameters.AddWithValue("$limit", batchSize);
                var deleted = cmd.ExecuteNonQuery();

                if (deleted == 0)
                    break;

                totalDropped += deleted;
            }
        }

        // Vacuum once after all deletions to reclaim space
        if (totalDropped > 0)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed) return totalDropped;
                    using var vacuumCmd = _connection.CreateCommand();
                    vacuumCmd.CommandText = "VACUUM;";
                    vacuumCmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // Vacuum failure is non-critical
            }
        }

        return totalDropped;
    }

    /// <summary>
    /// Returns the count of events currently in the disk queue.
    /// </summary>
    public long GetCount()
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queued_events;";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _connection.Close();
                _connection.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    private static string? ExtractEventId(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("event_id", out var prop))
                return prop.GetString();
        }
        catch
        {
            // Ignore parse errors
        }
        return null;
    }
}
