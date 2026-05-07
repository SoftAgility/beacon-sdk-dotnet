namespace SoftAgility.Beacon;

/// <summary>
/// Severity level for an exception report sent via <see cref="IBeaconTracker.TrackException(Exception, ExceptionSeverity)"/>.
/// </summary>
public enum ExceptionSeverity
{
    /// <summary>
    /// A fatal exception that caused application termination or an unrecoverable state.
    /// Serializes to <c>"fatal"</c> in the JSON payload.
    /// </summary>
    Fatal,

    /// <summary>
    /// A non-fatal exception that was handled but should still be reported for diagnostics.
    /// Serializes to <c>"non_fatal"</c> in the JSON payload.
    /// </summary>
    NonFatal
}
