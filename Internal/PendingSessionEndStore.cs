using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Durable, typed sibling store for pending session-end records — a queue keyed by
/// <c>session_id</c> holding N self-contained <see cref="PendingSessionEnd"/> rows.
/// <para>
/// This is intentionally SEPARATE from the homogeneous event <see cref="DiskQueue"/>: that
/// queue holds serialized event payloads delivered to the <c>/v1/events</c> endpoint, and
/// mixing session-lifecycle records would break its batch contract. The two stores share the
/// same DB file (so they live and die together with the per-product data directory) but use
/// independent <see cref="SqliteConnection"/>s and tables.
/// </para>
/// <para>
/// Uses a SHORT <c>busy_timeout</c> (200ms) and a dedicated connection so a dispose-time write
/// is effectively non-blocking: under contention the worst case is a small bounded wait, never
/// the 5s of <see cref="DiskQueue"/>. Thread-safe: all operations are internally synchronized.
/// </para>
/// </summary>
internal sealed class PendingSessionEndStore : IDisposable
{
    private const int BusyTimeoutMs = 200;

    private readonly SqliteConnection _connection;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private bool _disposed;

    public PendingSessionEndStore(string dbPath, ILogger? logger)
    {
        _logger = logger;

        // Ensure the directory exists (mirrors DiskQueue).
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        // SHORT busy timeout (200ms) so a dispose-time write is effectively non-blocking.
        // We deliberately keep the default rollback journal (not WAL), matching DiskQueue.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
            pragma.ExecuteNonQuery();
        }

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pending_session_ends (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                product TEXT NOT NULL,
                product_version TEXT NOT NULL,
                started_at TEXT NOT NULL,
                account_id TEXT NULL,
                license_id TEXT NULL,
                ended_at TEXT NOT NULL,
                end_reason TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Persists one pending session-end record. Effectively non-blocking (200ms busy timeout).
    /// Returns the assigned row id, or 0 if the write failed (e.g. SQLITE_BUSY after the timeout) —
    /// failure is non-fatal: the caller proceeds with the best-effort immediate send.
    /// </summary>
    public long Enqueue(PendingSessionEnd record)
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO pending_session_ends
                        (session_id, actor_id, product, product_version, started_at,
                         account_id, license_id, ended_at, end_reason)
                    VALUES
                        ($sessionId, $actorId, $product, $productVersion, $startedAt,
                         $accountId, $licenseId, $endedAt, $endReason);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$sessionId", record.SessionId);
                cmd.Parameters.AddWithValue("$actorId", record.ActorId);
                cmd.Parameters.AddWithValue("$product", record.Product);
                cmd.Parameters.AddWithValue("$productVersion", record.ProductVersion);
                cmd.Parameters.AddWithValue("$startedAt", record.StartedAt);
                cmd.Parameters.AddWithValue("$accountId", (object?)record.AccountId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$licenseId", (object?)record.LicenseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$endedAt", record.EndedAt);
                cmd.Parameters.AddWithValue("$endReason", record.EndReason);

                var rowId = (long)(cmd.ExecuteScalar() ?? 0L);
                return rowId;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: failed to persist pending session-end for session {SessionId}.", record.SessionId);
                return 0;
            }
        }
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> pending session-end records, oldest first.
    /// </summary>
    public IReadOnlyList<PendingSessionEnd> DequeueUpTo(int count)
    {
        lock (_lock)
        {
            if (_disposed) return [];

            var results = new List<PendingSessionEnd>();

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT id, session_id, actor_id, product, product_version, started_at,
                           account_id, license_id, ended_at, end_reason
                    FROM pending_session_ends
                    ORDER BY id ASC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$limit", count);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new PendingSessionEnd
                    {
                        RowId = reader.GetInt64(0),
                        SessionId = reader.GetString(1),
                        ActorId = reader.GetString(2),
                        Product = reader.GetString(3),
                        ProductVersion = reader.GetString(4),
                        StartedAt = reader.GetString(5),
                        AccountId = reader.IsDBNull(6) ? null : reader.GetString(6),
                        LicenseId = reader.IsDBNull(7) ? null : reader.GetString(7),
                        EndedAt = reader.GetString(8),
                        EndReason = reader.GetString(9)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: failed to read pending session-ends.");
            }

            return results;
        }
    }

    /// <summary>
    /// Deletes a single pending session-end record by its row id (after successful delivery
    /// or a successful immediate send).
    /// </summary>
    public void Delete(long rowId)
    {
        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM pending_session_ends WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", rowId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: failed to delete pending session-end {RowId}.", rowId);
            }
        }
    }

    /// <summary>
    /// Purges all pending session-end records without delivering them. Used by
    /// <c>OptOut()</c> and <c>Reset()</c> (consent-respecting: do not deliver).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM pending_session_ends;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: failed to clear pending session-ends.");
            }
        }
    }

    /// <summary>Returns the count of pending session-end records (test/diagnostic helper).</summary>
    public long GetCount()
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM pending_session_ends;";
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
            catch
            {
                return 0;
            }
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
                // Ignore disposal errors (mirrors DiskQueue).
            }
        }
    }
}
