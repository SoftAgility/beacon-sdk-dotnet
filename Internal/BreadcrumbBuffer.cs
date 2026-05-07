namespace SoftAgility.Beacon.Internal;

/// <summary>
/// A single breadcrumb entry captured from a <see cref="BeaconTracker.Track"/> call.
/// In-memory only; never persisted to disk.
/// </summary>
internal sealed record BreadcrumbEntry(
    string Timestamp,
    string Category,
    string Name,
    Dictionary<string, object>? Properties);

/// <summary>
/// Thread-safe, fixed-capacity ring buffer for breadcrumb entries.
/// When full, the oldest entry is discarded on <see cref="Add"/>.
/// </summary>
internal sealed class BreadcrumbBuffer
{
    private readonly object _lock = new();
    private readonly List<BreadcrumbEntry> _entries;
    private readonly int _maxCapacity;

    /// <summary>
    /// Creates a new breadcrumb buffer with the given maximum capacity.
    /// </summary>
    /// <param name="maxCapacity">Maximum number of entries to retain. Must be greater than 0.</param>
    public BreadcrumbBuffer(int maxCapacity)
    {
        _maxCapacity = maxCapacity;
        _entries = new List<BreadcrumbEntry>(Math.Min(maxCapacity, 64));
    }

    /// <summary>
    /// Adds a breadcrumb entry. If the buffer is at capacity, the oldest entry is discarded.
    /// Thread-safe.
    /// </summary>
    public void Add(BreadcrumbEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= _maxCapacity)
            {
                _entries.RemoveAt(0);
            }

            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Returns a snapshot of all entries in insertion order (oldest first).
    /// The buffer is NOT cleared by this operation. Thread-safe.
    /// </summary>
    public List<BreadcrumbEntry> Snapshot()
    {
        lock (_lock)
        {
            return new List<BreadcrumbEntry>(_entries);
        }
    }

    /// <summary>
    /// Removes all entries from the buffer. Thread-safe.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
