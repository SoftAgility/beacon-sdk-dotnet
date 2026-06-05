namespace SoftAgility.Beacon.Internal;

/// <summary>
/// A durable, self-contained pending session-end record.
/// <para>
/// Persisted locally (in the <c>pending_session_ends</c> SQLite table, a sibling of the
/// homogeneous event <see cref="DiskQueue"/>) when a session ends on <c>Dispose()</c> /
/// <c>EndSession()</c>, so a clean close is recorded even if the immediate network send is
/// skipped, fails, or the process is force-killed. The record carries the full session-start
/// context so the server's create-on-recovery path can materialize an absent session if the
/// fire-and-forget session-start never landed (the record is therefore <b>self-contained</b>).
/// </para>
/// <para>
/// <see cref="EndedAt"/> is stamped at <b>write time</b> (the last-activity / dispose instant),
/// never at delivery time — a deferred next-launch delivery still records the true end instant.
/// </para>
/// </summary>
internal sealed class PendingSessionEnd
{
    /// <summary>Autoincrement row id (delivery ordering). 0 for a not-yet-persisted record.</summary>
    public long RowId { get; init; }

    /// <summary>The session being ended. The store is a queue keyed by this value (N pending ends).</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Actor that owned the session (start context).</summary>
    public string ActorId { get; init; } = string.Empty;

    /// <summary>The product / source app (start context).</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>The product version (start context).</summary>
    public string ProductVersion { get; init; } = string.Empty;

    /// <summary>When the session started, round-trip ("O") format (start context).</summary>
    public string StartedAt { get; init; } = string.Empty;

    /// <summary>Optional account context (start context). Null when no account was set.</summary>
    public string? AccountId { get; init; }

    /// <summary>Optional license context (start context). Null when no license was set.</summary>
    public string? LicenseId { get; init; }

    /// <summary>
    /// When the session ended, round-trip ("O") format. Stamped at write time (last activity /
    /// dispose instant), NOT delivery time.
    /// </summary>
    public string EndedAt { get; init; } = string.Empty;

    /// <summary>
    /// The end reason as persisted. Records are written with the original reason (e.g. "normal");
    /// next-launch delivery overrides the wire reason to "sdk_recovery".
    /// </summary>
    public string EndReason { get; init; } = string.Empty;
}
