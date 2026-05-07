namespace SoftAgility.Beacon;

/// <summary>
/// Indicates the result of the most recent flush attempt.
/// </summary>
public enum FlushStatus
{
    /// <summary>
    /// No flush has been attempted yet. Initial state for enabled trackers.
    /// </summary>
    NotConnected,

    /// <summary>
    /// The last flush successfully delivered events to the backend.
    /// </summary>
    Connected,

    /// <summary>
    /// The last flush failed (network error, server error, etc.) and events were queued.
    /// </summary>
    Offline,

    /// <summary>
    /// The SDK is disabled via BeaconOptions.Enabled = false.
    /// </summary>
    Disabled,

    /// <summary>
    /// The SDK is opted out via <see cref="IBeaconTracker.OptOut"/>.
    /// No events are tracked or flushed until <see cref="IBeaconTracker.OptIn"/> is called.
    /// </summary>
    OptedOut
}
