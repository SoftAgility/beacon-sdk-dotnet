using Microsoft.Extensions.Logging;

namespace SoftAgility.Beacon;

/// <summary>
/// Configuration options for the Beacon SDK.
/// </summary>
public class BeaconOptions
{
    /// <summary>
    /// API key used in the Authorization: Bearer header. Required.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the Beacon backend (e.g., "https://your-beacon-instance.com"). Required.
    /// Trailing slash is normalized away during initialization.
    /// </summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The registered product in Beacon; sent as the <c>product</c> field on every event. Required.
    /// Max 128 characters.
    /// </summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>
    /// The application version; sent as the <c>product_version</c> field on every event. Required.
    /// Max 256 characters.
    /// </summary>
    public string ProductVersion { get; set; } = string.Empty;

    /// <summary>
    /// When false, all SDK methods are silent no-ops. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval in seconds between automatic flush cycles. Default: 60. Range: 1-3600.
    /// </summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of events per HTTP batch. Default: 25. Range: 1-1000.
    /// Values above 1000 are silently clamped to 1000.
    /// </summary>
    public int MaxBatchSize { get; set; } = 25;

    /// <summary>
    /// Maximum size of the SQLite disk queue in megabytes. Default: 10. Range: 1-1000.
    /// </summary>
    public int MaxQueueSizeMb { get; set; } = 10;

    /// <summary>
    /// Maximum number of breadcrumb entries to retain in the ring buffer.
    /// Breadcrumbs are automatically captured from <see cref="IBeaconTracker.Track(string, string, string, object?)"/>
    /// calls and attached to exception reports sent via <see cref="IBeaconTracker.TrackException(Exception, ExceptionSeverity)"/>.
    /// Default: 25. Set to 0 to disable breadcrumbs. Values above 200 are clamped to 200.
    /// </summary>
    public int MaxBreadcrumbs { get; set; } = 25;

    /// <summary>
    /// Builder for declaring the complete set of events this application tracks.
    /// Use <see cref="EventDefinitionBuilder.Define(string, string)"/> to register
    /// each (category, name) pair. The registered events can then be exported as a
    /// JSON manifest via <see cref="IBeaconTracker.ExportEventManifest(string)"/>
    /// for upload to the portal's Allowlists Import page.
    /// </summary>
    public EventDefinitionBuilder Events { get; } = new();

    /// <summary>
    /// Optional logger for SDK diagnostics. Default: null (silent).
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Maximum time the SDK will spend on the best-effort, blocking session-end send during
    /// <c>Dispose()</c> / <c>EndSession()</c> when a session is active.
    /// <para>
    /// The durable pending-end record is always persisted locally first (it survives offline and
    /// force-kill), so this timeout only bounds the optimization that delivers the end <i>now</i>
    /// while online. Default: 2 seconds. Set to <see cref="TimeSpan.Zero"/> to SKIP the blocking
    /// send entirely (disk-only) — the end is then delivered on the next SDK launch as
    /// <c>sdk_recovery</c>. Negative values are treated as <see cref="TimeSpan.Zero"/>.
    /// </para>
    /// </summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
