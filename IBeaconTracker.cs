namespace SoftAgility.Beacon;

/// <summary>
/// Public interface for the Beacon tracker. Used as the DI service type.
/// </summary>
public interface IBeaconTracker : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Stores the actor ID for subsequent <see cref="Track(string, string, object?)"/>
    /// and <see cref="StartSession()"/> calls. Can be called multiple times (e.g., user
    /// logs out and a new user logs in). Thread-safe.
    /// When the actor ID differs from the current value and a device ID is available,
    /// fires a best-effort HTTP POST to /v1/actors/identify to link the device ID
    /// (anonymous) to the identified actor ID. On HTTP 409 (already linked to a
    /// different user), a warning is logged but the actor ID is not reverted.
    /// Re-identifying with the same actor ID is a no-op (no POST fired).
    /// </summary>
    /// <param name="actorId">Actor identifier (max 512 chars). Must not be null or empty.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="actorId"/> is null or empty.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="actorId"/> exceeds 512 characters.</exception>
    void Identify(string actorId);

    /// <summary>
    /// Tracks an event with the specified category, name, actor ID, and optional properties.
    /// Returns immediately without blocking. Thread-safe.
    /// </summary>
    /// <param name="category">Event category (max 128 chars).</param>
    /// <param name="name">Event name (max 256 chars).</param>
    /// <param name="actorId">Actor identifier (max 512 chars).</param>
    /// <param name="properties">Optional flat dictionary or anonymous object of event properties.</param>
    void Track(string category, string name, string actorId, object? properties = null);

    /// <summary>
    /// Tracks an event using the actor ID previously set by <see cref="Identify"/>.
    /// Returns immediately without blocking. Thread-safe.
    /// </summary>
    /// <param name="category">Event category (max 128 chars).</param>
    /// <param name="name">Event name (max 256 chars).</param>
    /// <param name="properties">Optional flat dictionary or anonymous object of event properties.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no actor has been identified. Call <see cref="Identify"/> first or use the
    /// <see cref="Track(string, string, string, object?)"/> overload that accepts actorId.
    /// </exception>
    void Track(string category, string name, object? properties = null);

    /// <summary>
    /// Starts a new session for the given actor. If a session is already active,
    /// it is ended first. Also stores the actor ID for subsequent calls (equivalent
    /// to calling <see cref="Identify"/>). Sends POST /v1/events/sessions to the backend.
    /// </summary>
    /// <param name="actorId">Actor identifier for the session.</param>
    void StartSession(string actorId);

    /// <summary>
    /// Starts a new session using the actor ID previously set by <see cref="Identify"/>.
    /// If a session is already active, it is ended first.
    /// Sends POST /v1/events/sessions to the backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no actor has been identified. Call <see cref="Identify"/> first or use the
    /// <see cref="StartSession(string)"/> overload that accepts actorId.
    /// </exception>
    void StartSession();

    /// <summary>
    /// Ends the current session. If no session is active, this is a no-op.
    /// Sends POST /v1/events/sessions/end to the backend.
    /// </summary>
    void EndSession();

    /// <summary>
    /// Flushes all in-memory and disk-queued events to the backend.
    /// Awaitable and thread-safe.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports an exception to the Beacon backend using the actor ID previously set
    /// by <see cref="Identify"/> or <see cref="StartSession(string)"/>.
    /// The report is sent as a fire-and-forget HTTP POST; this method returns immediately
    /// and never throws. If no actor has been identified, the call is a no-op with a
    /// logged warning. If the SDK is disabled or disposed, the call is a silent no-op.
    /// </summary>
    /// <param name="ex">The exception to report. If null, a warning is logged and the call is a no-op.</param>
    /// <param name="severity">Severity level. Default: <see cref="ExceptionSeverity.NonFatal"/>.</param>
    void TrackException(Exception ex, ExceptionSeverity severity = ExceptionSeverity.NonFatal);

    /// <summary>
    /// Reports an exception to the Beacon backend using the specified actor ID.
    /// The report is sent as a fire-and-forget HTTP POST; this method returns immediately
    /// and never throws (except for invalid <paramref name="actorId"/>).
    /// If the SDK is disabled or disposed, the call is a silent no-op.
    /// </summary>
    /// <param name="ex">The exception to report. If null, a warning is logged and the call is a no-op.</param>
    /// <param name="actorId">Actor identifier (max 512 chars). Must not be null or empty.</param>
    /// <param name="severity">Severity level. Default: <see cref="ExceptionSeverity.NonFatal"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="actorId"/> is null, empty, or exceeds 512 characters.
    /// </exception>
    void TrackException(Exception ex, string actorId, ExceptionSeverity severity = ExceptionSeverity.NonFatal);

    /// <summary>
    /// Stops all tracking immediately and persists the opt-out state to disk.
    /// Clears the in-memory event queue. The flush timer is stopped.
    /// Subsequent calls to <see cref="Track(string, string, object?)"/>,
    /// <see cref="StartSession()"/>, <see cref="EndSession"/>, <see cref="FlushAsync"/>,
    /// <see cref="Identify"/>, and <see cref="TrackException(Exception, ExceptionSeverity)"/>
    /// become no-ops until <see cref="OptIn"/> is called.
    /// <see cref="ExportEventManifest"/> continues to work.
    /// Idempotent: calling when already opted out is a no-op.
    /// Never throws.
    /// </summary>
    void OptOut();

    /// <summary>
    /// Resumes tracking after a prior <see cref="OptOut"/> call.
    /// Deletes the opt-out flag file from disk and restarts the flush timer.
    /// Idempotent: calling when not opted out is a no-op.
    /// Never throws.
    /// </summary>
    void OptIn();

    /// <summary>
    /// Clears actor identity, session state, account context, license context,
    /// in-memory queue, and breadcrumbs, then generates a new anonymous device ID.
    /// If a session is active, it is ended first (fire-and-forget).
    /// Operates regardless of <see cref="BeaconOptions.Enabled"/> or opt-out state.
    /// After reset, the next <see cref="Track(string, string, object?)"/> or
    /// <see cref="StartSession()"/> call uses the new anonymous device ID.
    /// Never throws.
    /// </summary>
    void Reset();

    /// <summary>
    /// Sets the account context for subsequent events, sessions, and exception reports.
    /// </summary>
    /// <param name="accountId">
    /// An opaque identifier for the vendor's customer account / organization
    /// (1-256 chars; opaque; never validated). Pseudonymous IDs only — do not
    /// pass personally identifying strings like email addresses. Cleared by
    /// <see cref="ClearAccount"/> or <see cref="Reset"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="accountId"/> is null, empty, whitespace-only,
    /// contains a control character, or exceeds 256 characters.
    /// </exception>
    void SetAccount(string accountId);

    /// <summary>
    /// Clears the account context. Subsequent events emit with account_id = null.
    /// </summary>
    void ClearAccount();

    /// <summary>
    /// Sets the license context for subsequent events, sessions, and exception reports.
    /// </summary>
    /// <param name="licenseId">
    /// An opaque identifier for the license, contract, or entitlement under which
    /// usage is occurring. <strong>Prefer per-contract IDs</strong> (a single string shared
    /// across all of a customer's users — e.g., a subscription ID, site key, or
    /// bundle SKU) for the richest Beacon analytics. Per-user license IDs work but
    /// reduce the License Detail page to a near-duplicate of the Actor Identities
    /// view and disable the multi-account-sharing warning. See the Beacon docs
    /// section "Modeling licenses" for guidance.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="licenseId"/> is null, empty, whitespace-only,
    /// contains a control character, or exceeds 256 characters.
    /// </exception>
    void SetLicense(string licenseId);

    /// <summary>
    /// Clears the license context. Subsequent events emit with license_id = null.
    /// </summary>
    void ClearLicense();

    /// <summary>
    /// The result of the most recent flush attempt.
    /// </summary>
    FlushStatus LastFlushStatus { get; }

    /// <summary>
    /// Writes a JSON manifest of all events defined via <see cref="BeaconOptions.Events"/>
    /// during configuration. The manifest matches the portal's import schema and can be
    /// uploaded directly to the Allowlists &gt; Import page.
    /// Works even when the SDK is disabled.
    /// </summary>
    void ExportEventManifest(string filePath);
}
