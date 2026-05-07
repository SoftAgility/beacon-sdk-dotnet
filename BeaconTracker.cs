using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon;

/// <summary>
/// Main Beacon tracker implementation. Manages event queuing, session lifecycle,
/// offline persistence, and background flush to the Beacon backend.
/// Thread-safe. Implements IBeaconTracker, IDisposable, and IAsyncDisposable.
/// </summary>
public sealed class BeaconTracker : IBeaconTracker
{
    // ── Static singleton ────────────────────────────────────────────────
    private static readonly object ConfigureLock = new();
    private static BeaconTracker? _instance;

    /// <summary>
    /// The singleton instance created by <see cref="Configure"/>. Null until configured.
    /// </summary>
    public static BeaconTracker? Instance => _instance;

    /// <summary>
    /// Configures and creates the singleton BeaconTracker instance.
    /// Throws <see cref="InvalidOperationException"/> if called a second time.
    /// If required options (ApiKey, ApiBaseUrl, AppName) are missing, the SDK
    /// disables itself silently and logs a warning — it does not throw.
    /// </summary>
    public static void Configure(Action<BeaconOptions> configure)
    {
        lock (ConfigureLock)
        {
            if (_instance is not null)
                throw new InvalidOperationException("BeaconTracker is already configured.");

            var options = new BeaconOptions();
            configure(options);

            _instance = new BeaconTracker(options);
        }
    }

    /// <summary>
    /// Resets the static instance. Intended for testing only.
    /// </summary>
    internal static void ResetInstance()
    {
        lock (ConfigureLock)
        {
            _instance = null;
        }
    }

    // ── Instance fields ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Characters that are invalid in Windows directory names.
    /// Used by <see cref="SanitizeAppNameForPath"/> to ensure safe data directory paths.
    /// </summary>
    private static readonly char[] InvalidPathChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly BeaconOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<string> _memoryQueue = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly object _sessionLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private BeaconHttpClient? _httpClient;
    private DiskQueue? _diskQueue;

    private readonly IReadOnlyList<(string Category, string Name)> _definedEvents;
    private readonly string? _environmentDataBase64;
    private readonly BreadcrumbBuffer? _breadcrumbs;

    private string? _sessionId;
    private string? _actorId;
    private bool _environmentSent;
    private bool _disposed;
    private bool _halted; // Set on 401 — no further flush attempts
    private int _preflightDone; // 0 = not started, 1 = done (Interlocked)
    private int _memoryQueueCount; // Approximate count for size-triggered flush

    // Consent and identity fields (FR-1119 through FR-1126)
    private string? _dataDirectory;
    private string? _deviceId;
    private int _isOptedOut; // 0 = not opted out, 1 = opted out (Interlocked for thread safety)
    private System.Threading.Timer? _flushTimer;

    /// <inheritdoc />
    public FlushStatus LastFlushStatus { get; private set; }

    // ── Constructor (internal for DI, private validation shared) ────────

    /// <summary>
    /// Creates a new BeaconTracker. Used by <see cref="Configure"/> and by the DI extension.
    /// </summary>
    public BeaconTracker(BeaconOptions options)
    {
        _options = options;
        _logger = options.Logger;

        if (!ValidateOptions(options))
        {
            _options.Enabled = false;
        }

        // Clamp MaxBatchSize to 1000
        if (_options.MaxBatchSize > 1000)
            _options.MaxBatchSize = 1000;

        // Clamp FlushIntervalSeconds
        if (_options.FlushIntervalSeconds < 1)
            _options.FlushIntervalSeconds = 1;
        if (_options.FlushIntervalSeconds > 3600)
            _options.FlushIntervalSeconds = 3600;

        // Clamp MaxQueueSizeMb
        if (_options.MaxQueueSizeMb < 1)
            _options.MaxQueueSizeMb = 1;
        if (_options.MaxQueueSizeMb > 1000)
            _options.MaxQueueSizeMb = 1000;

        // Clamp MaxBreadcrumbs to 0-200
        if (_options.MaxBreadcrumbs < 0)
            _options.MaxBreadcrumbs = 0;
        if (_options.MaxBreadcrumbs > 200)
            _options.MaxBreadcrumbs = 200;

        // Truncate AppName/AppVersion to documented limits
        if (_options.AppName.Length > 128)
            _options.AppName = _options.AppName[..128];
        if (_options.AppVersion.Length > 256)
            _options.AppVersion = _options.AppVersion[..256];

        // Build the event definitions list before checking Enabled — ExportEventManifest
        // must work even when the SDK is disabled.
        _definedEvents = options.Events.Build();

        // Initialize data directory, device ID, and opt-out state BEFORE checking Enabled
        // (FR-1123, FR-1119): these are needed by Reset(), OptOut(), OptIn() which all
        // operate regardless of Enabled state.
        InitializeDataDirectory();

        if (!_options.Enabled)
        {
            // If opted out at init, set OptedOut; otherwise Disabled
            if (Interlocked.CompareExchange(ref _isOptedOut, 0, 0) == 1)
                LastFlushStatus = FlushStatus.OptedOut;
            else
                LastFlushStatus = FlushStatus.Disabled;
            return;
        }

        // Initialize HTTP client
        _httpClient = new BeaconHttpClient(
            _options.ApiBaseUrl,
            _options.ApiKey,
            _logger);

        // Initialize disk queue
        try
        {
            var queuePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftAgility",
                "Beacon",
                SanitizeAppNameForPath(_options.AppName),
                "queue.db");
            _diskQueue = new DiskQueue(queuePath, _logger);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to initialize disk queue. Offline persistence unavailable.");
        }

        // Eagerly collect environment data once at construction
        try
        {
            _environmentDataBase64 = EnvironmentCollector.CollectBase64();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to collect environment data at startup.");
            _environmentDataBase64 = null;
        }

        // Allocate breadcrumb buffer if MaxBreadcrumbs > 0
        if (_options.MaxBreadcrumbs > 0)
        {
            _breadcrumbs = new BreadcrumbBuffer(_options.MaxBreadcrumbs);
        }

        // Check if opted out at init (FR-1119) — if so, do NOT start the flush timer
        if (Interlocked.CompareExchange(ref _isOptedOut, 0, 0) == 1)
        {
            LastFlushStatus = FlushStatus.OptedOut;
            // Timer is not started; will be started on OptIn()
        }
        else
        {
            // Start background flush timer — first tick at 5s to drain any disk-queued
            // events from a previous run, then at the configured interval.
            var intervalMs = _options.FlushIntervalSeconds * 1000;
            var initialDelayMs = Math.Min(5000, intervalMs);
            _flushTimer = new System.Threading.Timer(OnFlushTimerElapsed, null, initialDelayMs, intervalMs);
        }
    }

    /// <summary>
    /// DI constructor that reads from IOptions.
    /// </summary>
    public BeaconTracker(IOptions<BeaconOptions> options) : this(options.Value)
    {
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Identify(string actorId)
    {
        if (_disposed)
            return;

        if (!_options.Enabled || IsOptedOut)
            return;

        ValidateActorId(actorId);

        string? previousActorId;
        lock (_sessionLock)
        {
            previousActorId = _actorId;
            _actorId = actorId;
        }

        // Fire best-effort identify POST if this is a new identification (not re-identify)
        if (previousActorId != actorId && _deviceId is not null)
        {
            var httpClient = _httpClient;
            if (httpClient is not null)
            {
                var deviceId = _deviceId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await httpClient.PostIdentifyAsync(
                            deviceId,
                            actorId,
                            _options.AppName,
                            _options.AppVersion);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Beacon: identify POST failed for device {DeviceId}.", deviceId);
                    }
                });
            }
        }
    }

    /// <inheritdoc />
    public void Track(string category, string name, object? properties = null)
    {
        if (_disposed)
            return;

        if (!_options.Enabled || IsOptedOut)
            return;

        // Reserved category check: categories starting with '_' are reserved for SDK-internal use
        if (category.Length > 0 && category[0] == '_')
        {
            _logger?.LogWarning("Beacon: Track() called with reserved category '{Category}' — ignored. Categories starting with '_' are reserved for SDK-internal use.", category);
            return;
        }

        string actorId;
        lock (_sessionLock)
        {
            if (_actorId is not null)
            {
                actorId = _actorId;
            }
            else if (!string.IsNullOrEmpty(_deviceId))
            {
                actorId = _deviceId;
            }
            else
            {
                _logger?.LogWarning("Beacon: no actor ID available — call Identify() or check device ID initialization.");
                return;
            }
        }

        Track(category, name, actorId, properties);
    }

    /// <inheritdoc />
    public void Track(string category, string name, string actorId, object? properties = null)
    {
        try
        {
            if (!_options.Enabled)
                return;

            if (_disposed)
            {
                _logger?.LogWarning("Beacon: method called on disposed tracker - ignored.");
                return;
            }

            if (IsOptedOut)
                return;

            // Reserved category check: categories starting with '_' are reserved for SDK-internal use
            if (category.Length > 0 && category[0] == '_')
            {
                _logger?.LogWarning("Beacon: Track() called with reserved category '{Category}' — ignored. Categories starting with '_' are reserved for SDK-internal use.", category);
                return;
            }

            TriggerPreflight();

            ValidateActorId(actorId);

            // Truncate category/name to documented limits
            if (category.Length > 128)
                category = category[..128];
            if (name.Length > 256)
                name = name[..256];

            // Sanitize properties
            var sanitized = PropertySanitizer.Sanitize(properties, _logger);

            // Build event payload
            string? sessionId;
            lock (_sessionLock)
            {
                sessionId = _sessionId;
            }

            var payload = new Dictionary<string, object?>
            {
                ["event_id"] = UuidV7.NewId().ToString(),
                ["category"] = category,
                ["name"] = name,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["actor_id"] = actorId,
                ["source_app"] = _options.AppName,
                ["source_version"] = _options.AppVersion
            };

            if (sessionId is not null)
                payload["session_id"] = sessionId;

            if (sanitized is not null)
                payload["properties"] = sanitized;

            // Add breadcrumb entry before enqueueing (captures the Track call context)
            _breadcrumbs?.Add(new BreadcrumbEntry(
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Category: category,
                Name: name,
                Properties: sanitized));

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            _memoryQueue.Enqueue(json);

            var currentCount = Interlocked.Increment(ref _memoryQueueCount);

            // Size-triggered flush (skip if another flush is in progress)
            if (currentCount >= _options.MaxBatchSize)
            {
                _ = Task.Run(() => FlushCoreAsync(_shutdownCts.Token, waitForSemaphore: false));
            }
        }
        catch (ArgumentException)
        {
            throw; // Programmer error — let it surface
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in Track().");
        }
    }

    /// <inheritdoc />
    public void StartSession()
    {
        if (_disposed)
            return;

        if (!_options.Enabled || IsOptedOut)
            return;

        string actorId;
        lock (_sessionLock)
        {
            if (_actorId is not null)
            {
                actorId = _actorId;
            }
            else if (!string.IsNullOrEmpty(_deviceId))
            {
                actorId = _deviceId;
            }
            else
            {
                _logger?.LogWarning("Beacon: no actor ID available — call Identify() or check device ID initialization.");
                return;
            }
        }

        StartSession(actorId);
    }

    /// <inheritdoc />
    public void StartSession(string actorId)
    {
        try
        {
            if (!_options.Enabled)
                return;

            if (_disposed)
            {
                _logger?.LogWarning("Beacon: method called on disposed tracker - ignored.");
                return;
            }

            if (IsOptedOut)
                return;

            TriggerPreflight();
            ValidateActorId(actorId);

            lock (_sessionLock)
            {
                // Store the actor ID so subsequent Track()/StartSession() calls can use it
                _actorId = actorId;

                // If a session is already active, end it first
                if (_sessionId is not null)
                {
                    _ = EndSessionInternal("normal");
                }

                var newSessionId = UuidV7.NewId().ToString();
                _sessionId = newSessionId;
                _environmentSent = false; // Reset so env data is sent on next flush

                // Send session start to the backend (fire and forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_httpClient is not null)
                        {
                            await _httpClient.SendSessionStartAsync(
                                newSessionId,
                                actorId,
                                _options.AppName,
                                _options.AppVersion,
                                DateTimeOffset.UtcNow);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Beacon: failed to send session start.");
                    }
                });
            }
        }
        catch (ArgumentException)
        {
            throw; // Programmer error — let it surface
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in StartSession().");
        }
    }

    /// <inheritdoc />
    public void EndSession()
    {
        try
        {
            if (!_options.Enabled)
                return;

            if (_disposed)
            {
                _logger?.LogWarning("Beacon: method called on disposed tracker - ignored.");
                return;
            }

            if (IsOptedOut)
                return;

            lock (_sessionLock)
            {
                _ = EndSessionInternal("normal");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in EndSession().");
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_options.Enabled)
                return;

            if (_disposed)
            {
                _logger?.LogWarning("Beacon: method called on disposed tracker - ignored.");
                return;
            }

            if (IsOptedOut)
                return;

            await FlushCoreAsync(cancellationToken, waitForSemaphore: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in FlushAsync().");
        }
    }

    // ── Consent API (FR-1120, FR-1121) ───────────────────────────────

    /// <inheritdoc />
    public void OptOut()
    {
        try
        {
            // Operates regardless of Enabled state (FR-1120)
            if (Interlocked.CompareExchange(ref _isOptedOut, 1, 0) == 1)
                return; // Already opted out — idempotent (ED-735)

            // Drain in-memory queue without sending (FR-1120)
            DrainMemoryQueueSilently();

            // Stop the flush timer (FR-1120)
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // Set status
            LastFlushStatus = FlushStatus.OptedOut;

            // Persist opt-out to disk (EC-656)
            if (_dataDirectory is not null)
            {
                try
                {
                    var optOutPath = Path.Combine(_dataDirectory, "beacon_opted_out");
                    File.Create(optOutPath).Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Beacon: failed to persist opt-out flag to disk — opted out in-memory only. State will not survive restart.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in OptOut().");
        }
    }

    /// <inheritdoc />
    public void OptIn()
    {
        try
        {
            // Operates regardless of Enabled state (FR-1121)
            if (Interlocked.CompareExchange(ref _isOptedOut, 0, 1) == 0)
                return; // Not opted out — idempotent (ED-732)

            // Delete opt-out file (EC-657)
            if (_dataDirectory is not null)
            {
                try
                {
                    var optOutPath = Path.Combine(_dataDirectory, "beacon_opted_out");
                    if (File.Exists(optOutPath))
                    {
                        File.Delete(optOutPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Beacon: failed to remove opt-out flag file — opted in in-memory only. Opt-out may re-apply on next restart.");
                }
            }

            // Reset flush status
            LastFlushStatus = FlushStatus.NotConnected;

            // Restart flush timer if SDK is enabled (FR-1121)
            if (_options.Enabled && !_disposed)
            {
                var intervalMs = _options.FlushIntervalSeconds * 1000;
                if (_flushTimer is not null)
                {
                    _flushTimer.Change(intervalMs, intervalMs);
                }
                else
                {
                    var initialDelayMs = Math.Min(5000, intervalMs);
                    _flushTimer = new System.Threading.Timer(OnFlushTimerElapsed, null, initialDelayMs, intervalMs);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in OptIn().");
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        try
        {
            // Operates regardless of Enabled or opt-out state (FR-1122)

            // Step 1: End active session (fire-and-forget)
            lock (_sessionLock)
            {
                if (_sessionId is not null)
                {
                    _ = EndSessionInternal("normal");
                    // _sessionId is cleared by EndSessionInternal
                }

                // Step 2: Clear actor ID
                _actorId = null;
            }

            // Step 3: Drain in-memory queue
            DrainMemoryQueueSilently();

            // Step 4: Clear breadcrumbs
            _breadcrumbs?.Clear();

            // Step 5 & 6: Generate new device ID and persist
            var newDeviceId = UuidV7.NewId().ToString();

            if (_dataDirectory is not null)
            {
                try
                {
                    var deviceIdPath = Path.Combine(_dataDirectory, "beacon_device_id");
                    File.WriteAllText(deviceIdPath, newDeviceId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Beacon: failed to write new device ID to disk — anonymous ID is ephemeral for this session.");
                }
            }

            _deviceId = newDeviceId;

            // Step 7: Opt-out state is NOT changed
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error in Reset().");
        }
    }

    // ── Exception Tracking ────────────────────────────────────────────

    /// <inheritdoc />
    public void TrackException(Exception ex, ExceptionSeverity severity = ExceptionSeverity.NonFatal)
    {
        // Disabled or disposed — silent no-op (FR-486, ED-434)
        if (!_options.Enabled || _disposed)
            return;

        // Opted out — silent no-op (FR-1127)
        if (IsOptedOut)
            return;

        // Null exception — warn and return (EC-429)
        if (ex is null)
        {
            _logger?.LogWarning("Beacon: TrackException() called with null exception — ignored.");
            return;
        }

        // Use identified actor, or fall back to device ID (FR-1126)
        string? actorId;
        lock (_sessionLock)
        {
            actorId = _actorId;
        }

        if (actorId is null)
        {
            if (!string.IsNullOrEmpty(_deviceId))
            {
                actorId = _deviceId;
            }
            else
            {
                _logger?.LogWarning("Beacon: no actor ID available — call Identify() or check device ID initialization.");
                return;
            }
        }

        SendExceptionFireAndForget(ex, actorId, severity);
    }

    /// <inheritdoc />
    public void TrackException(Exception ex, string actorId, ExceptionSeverity severity = ExceptionSeverity.NonFatal)
    {
        // Disabled or disposed — silent no-op (FR-486, ED-434)
        if (!_options.Enabled || _disposed)
            return;

        // Opted out — silent no-op (FR-1127)
        if (IsOptedOut)
            return;

        // Null exception — warn and return (EC-429)
        if (ex is null)
        {
            _logger?.LogWarning("Beacon: TrackException() called with null exception — ignored.");
            return;
        }

        // Validate actorId — throws ArgumentException on invalid input (EC-430)
        ValidateActorId(actorId);

        SendExceptionFireAndForget(ex, actorId, severity);
    }

    private void SendExceptionFireAndForget(Exception ex, string actorId, ExceptionSeverity severity)
    {
        // Capture all state needed for the payload on the calling thread
        var exceptionId = UuidV7.NewId().ToString();
        var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        var occurredAt = DateTimeOffset.UtcNow.ToString("O");
        var severityString = severity == ExceptionSeverity.Fatal ? "fatal" : "non_fatal";

        // Truncate message to 1000 chars; set to null if empty (ED-438)
        var message = ex.Message;
        if (message is not null && message.Length > 1000)
            message = message[..1000];
        if (string.IsNullOrEmpty(message))
            message = null;

        // Truncate stack trace to 32768 chars; set to null if empty (ED-437, ED-439)
        var stackTrace = ex.ToString();
        if (stackTrace is not null && stackTrace.Length > 32768)
            stackTrace = stackTrace[..32768];
        if (string.IsNullOrEmpty(stackTrace))
            stackTrace = null;

        // Capture session ID
        string? sessionId;
        lock (_sessionLock)
        {
            sessionId = _sessionId;
        }

        // Snapshot breadcrumbs (FR-489)
        List<BreadcrumbEntry>? breadcrumbSnapshot = null;
        if (_breadcrumbs is not null)
        {
            var snapshot = _breadcrumbs.Snapshot();
            if (snapshot.Count > 0)
                breadcrumbSnapshot = snapshot;
        }

        // Build payload
        var payload = new Dictionary<string, object?>
        {
            ["exception_id"] = exceptionId,
            ["exception_type"] = exceptionType,
            ["severity"] = severityString,
            ["occurred_at"] = occurredAt,
            ["actor_id"] = actorId,
            ["source_app"] = _options.AppName,
            ["source_version"] = _options.AppVersion,
            ["message"] = message,
            ["stack_trace"] = stackTrace,
            ["session_id"] = sessionId
        };

        if (breadcrumbSnapshot is not null)
            payload["breadcrumbs"] = breadcrumbSnapshot;

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        // Fire-and-forget HTTP POST (FR-485)
        var httpClient = _httpClient;
        if (httpClient is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await httpClient.SendExceptionAsync(json, CancellationToken.None);
            }
            catch (Exception sendEx)
            {
                _logger?.LogWarning(sendEx, "Beacon: failed to send exception report.");
            }
        });
    }

    // ── Event Manifest ────────────────────────────────────────────────

    /// <inheritdoc />
    public void ExportEventManifest(string filePath)
    {
        var manifest = new
        {
            schema_version = "1",
            generated_at = DateTimeOffset.UtcNow.ToString("O"),
            source_app = _options.AppName,
            source_version = _options.AppVersion,
            entries = _definedEvents.Select(e => new
            {
                category = e.Category,
                name = e.Name
            }).ToList()
        };

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(manifest, jsonOptions);
        File.WriteAllText(filePath, json);
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed || !_options.Enabled)
        {
            _disposed = true;
            _shutdownCts.Dispose();
            return;
        }

        // Run on a thread-pool thread to avoid deadlocking with UI SynchronizationContexts
        // (WinForms/WPF marshal continuations back to the UI thread, which we're blocking)
        Task.Run(async () => await DisposeAsync()).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (!_options.Enabled)
        {
            _disposed = true;
            _shutdownCts.Dispose();
            return;
        }

        try
        {
            // Mark disposed first so concurrent Track()/StartSession() calls
            // see the flag and become no-ops before we drain the memory queue.
            _disposed = true;

            // Cancel any in-flight timer-driven flushes so they release the semaphore
            await _shutdownCts.CancelAsync();

            // Stop the timer
            if (_flushTimer is not null)
            {
                await _flushTimer.DisposeAsync();
            }

            if (_options.Enabled)
            {
                // Persist any in-memory events to the disk queue for delivery on
                // next launch. This is instant (local SQLite write) and avoids
                // blocking app shutdown with network I/O that may hang or fail.
                WriteRemainingToDisk();

                // Clear session state locally — the server infers session end
                // from inactivity or the next session-start event.
                lock (_sessionLock)
                {
                    _sessionId = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error during dispose.");
        }
        finally
        {
            _diskQueue?.Dispose();
            _httpClient?.Dispose();
            _flushSemaphore.Dispose();
            _shutdownCts.Dispose();
        }
    }

    // ── Private ─────────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe read of the opt-out flag.
    /// </summary>
    private bool IsOptedOut => Interlocked.CompareExchange(ref _isOptedOut, 0, 0) == 1;

    /// <summary>
    /// Initializes the data directory, device ID file, and checks for the opt-out sentinel.
    /// Runs regardless of Enabled state (FR-1123, FR-1119).
    /// </summary>
    private void InitializeDataDirectory()
    {
        try
        {
            var sanitizedAppName = SanitizeAppNameForPath(_options.AppName);
            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftAgility",
                "Beacon",
                sanitizedAppName);

            // Create directory if it doesn't exist (ED-736)
            Directory.CreateDirectory(_dataDirectory);

            // Read or create device ID (FR-1123)
            var deviceIdPath = Path.Combine(_dataDirectory, "beacon_device_id");
            try
            {
                if (File.Exists(deviceIdPath))
                {
                    var content = File.ReadAllText(deviceIdPath).Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        _deviceId = content;
                    }
                    else
                    {
                        // Empty file — generate and write new ID (ED-740)
                        _deviceId = UuidV7.NewId().ToString();
                        File.WriteAllText(deviceIdPath, _deviceId);
                    }
                }
                else
                {
                    // File doesn't exist — generate and write new ID
                    _deviceId = UuidV7.NewId().ToString();
                    File.WriteAllText(deviceIdPath, _deviceId);
                }
            }
            catch (Exception ex)
            {
                // Transient fallback (FR-1123, EC-658 path)
                _deviceId = UuidV7.NewId().ToString();
                _logger?.LogWarning(ex, "Beacon: failed to read/write beacon_device_id — anonymous ID is ephemeral for this session.");
            }

            // Check opt-out sentinel (FR-1119) — AFTER device ID so _deviceId is always available
            var optOutPath = Path.Combine(_dataDirectory, "beacon_opted_out");
            if (File.Exists(optOutPath))
            {
                Interlocked.Exchange(ref _isOptedOut, 1);
            }
        }
        catch (Exception ex)
        {
            // Data directory creation failed entirely — generate transient device ID
            if (_deviceId is null)
            {
                _deviceId = UuidV7.NewId().ToString();
            }
            _logger?.LogWarning(ex, "Beacon: failed to initialize data directory — device ID and opt-out state are ephemeral for this session.");
        }
    }

    /// <summary>
    /// Sanitizes an app name for use as a directory name component (ED-737).
    /// Replaces path-unsafe characters with underscore.
    /// </summary>
    private static string SanitizeAppNameForPath(string appName)
    {
        var chars = appName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidPathChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Drains the in-memory ConcurrentQueue without sending any events.
    /// Used by OptOut() and Reset().
    /// </summary>
    private void DrainMemoryQueueSilently()
    {
        while (_memoryQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _memoryQueueCount);
        }
    }

    private void OnFlushTimerElapsed(object? state)
    {
        // Skip flush if opted out (ED-731, FR-1127)
        if (IsOptedOut)
            return;

        _ = Task.Run(() => FlushCoreAsync(_shutdownCts.Token, waitForSemaphore: false));
    }

    /// <summary>
    /// Fires a single background empty-batch POST to validate the API key early.
    /// Called once on first Track()/StartSession(). If the server returns 401,
    /// the SDK halts immediately instead of waiting for the first flush timer.
    /// Network/server errors are ignored (offline-first design).
    /// </summary>
    private void TriggerPreflight()
    {
        if (Interlocked.CompareExchange(ref _preflightDone, 1, 0) != 0)
            return; // Already triggered

        _ = Task.Run(async () =>
        {
            try
            {
                if (_httpClient is null || _halted)
                    return;

                var result = await _httpClient.SendEventsAsync([], null, _shutdownCts.Token);

                if (result.IsUnauthorized)
                {
                    _halted = true;
                    _logger?.LogError("{Message}", result.ErrorMessage);
                    LastFlushStatus = FlushStatus.Offline;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown in progress — ignore
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: preflight check failed (non-fatal).");
            }
        });
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken, bool waitForSemaphore = false)
    {
        if (_halted || IsOptedOut)
            return;

        // Only one flush at a time. Timer-driven and size-triggered flushes skip
        // if another flush is in progress. Manual FlushAsync() waits.
        if (waitForSemaphore)
        {
            await _flushSemaphore.WaitAsync(cancellationToken);
        }
        else
        {
            if (!await _flushSemaphore.WaitAsync(0, cancellationToken))
                return;
        }

        try
        {
            if (_httpClient is null)
                return;

            // Re-check opt-out after acquiring semaphore (ED-731)
            if (IsOptedOut)
                return;

            // 1. Flush disk-queued events first
            await FlushDiskQueueAsync(cancellationToken);

            // Re-check halted — a 401 during disk flush means the API key is
            // invalid, so skip the memory flush to avoid an extra unauthorized request.
            if (_halted)
                return;

            // 2. Flush memory queue
            await FlushMemoryQueueAsync(cancellationToken);
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    private async Task FlushDiskQueueAsync(CancellationToken cancellationToken)
    {
        if (_diskQueue is null || _httpClient is null)
            return;

        try
        {
            while (true)
            {
                var batch = _diskQueue.DequeueUpTo(_options.MaxBatchSize);
                if (batch.Count == 0)
                    break;

                var payloads = batch.Select(b => b.Payload).ToList();
                var result = await SendBatchWithRetryAsync(payloads, cancellationToken);

                if (result.IsSuccess)
                {
                    _diskQueue.Delete(batch.Select(b => b.Id));
                    if (!IsOptedOut)
                        LastFlushStatus = FlushStatus.Connected;

                    LogRejections(result);
                }
                else if (result.IsUnauthorized)
                {
                    _halted = true;
                    _logger?.LogError("{Message}", result.ErrorMessage);
                    return;
                }
                else if (result.IsClientError)
                {
                    // Permanent client error — delete from disk, events are unrecoverable
                    _diskQueue.Delete(batch.Select(b => b.Id));
                    _logger?.LogWarning("{Message}", result.ErrorMessage);
                }
                else
                {
                    // Leave in disk queue for next cycle
                    LastFlushStatus = FlushStatus.Offline;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: error flushing disk queue.");
        }
    }

    private async Task FlushMemoryQueueAsync(CancellationToken cancellationToken)
    {
        if (_httpClient is null)
            return;

        while (true)
        {
            var batch = DequeueUpToBatch();
            if (batch.Count == 0)
                break;

            var result = await SendBatchWithRetryAsync(batch, cancellationToken);

            if (result.IsSuccess)
            {
                if (!IsOptedOut)
                    LastFlushStatus = FlushStatus.Connected;
                LogRejections(result);
            }
            else if (result.IsUnauthorized)
            {
                _halted = true;
                _logger?.LogError("{Message}", result.ErrorMessage);
                // Write current batch + remaining memory events to disk so they
                // survive app restart with a new (rotated) API key
                WriteToDiskQueue(batch);
                WriteRemainingToDisk();
                LastFlushStatus = FlushStatus.Offline;
                return;
            }
            else if (result.IsClientError)
            {
                // Permanent client error (400/403/404 etc.) — drop the batch
                _logger?.LogWarning("{Message}", result.ErrorMessage);
            }
            else if (result.IsHardCapped)
            {
                // Write to disk queue
                _logger?.LogWarning("{Message}", result.ErrorMessage);
                WriteToDiskQueue(batch);
                LastFlushStatus = FlushStatus.Offline;
            }
            else
            {
                // Network/server error — already retried, write to disk
                WriteToDiskQueue(batch);
                LastFlushStatus = FlushStatus.Offline;
            }
        }
    }

    private async Task<FlushResult> SendBatchWithRetryAsync(
        IReadOnlyList<string> payloads,
        CancellationToken cancellationToken)
    {
        // Use the cached environment data collected at construction
        string? envData = null;
        lock (_sessionLock)
        {
            if (!_environmentSent && _environmentDataBase64 is not null)
            {
                envData = _environmentDataBase64;
            }
        }

        var result = await _httpClient!.SendEventsAsync(payloads, envData, cancellationToken);

        // Handle rate limiting (429)
        if (result.IsRateLimited)
        {
            _logger?.LogWarning("{Message}", result.ErrorMessage);
            await Task.Delay(TimeSpan.FromSeconds(result.RetryAfterSeconds), cancellationToken);
            // Retry once after wait — include envData so it isn't lost on retry success
            result = await _httpClient.SendEventsAsync(payloads, envData, cancellationToken);
        }

        // Handle 5xx with exponential backoff (3 attempts)
        if (result.IsServerError)
        {
            var delays = new[] { 1000, 2000, 4000 };
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _logger?.LogWarning("Beacon: server error ({StatusCode}). Attempt {Attempt}/3.",
                    (int)result.StatusCode, attempt);

                if (attempt <= delays.Length)
                {
                    await Task.Delay(delays[attempt - 1], cancellationToken);
                }

                result = await _httpClient.SendEventsAsync(payloads, envData, cancellationToken);

                if (!result.IsServerError)
                    break;
            }
        }

        // Handle network errors with exponential backoff (3 attempts)
        if (result.IsNetworkError && !result.IsUnauthorized)
        {
            var delays = new[] { 1000, 2000, 4000 };
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _logger?.LogWarning("Beacon: network error. Attempt {Attempt}/3.", attempt);

                if (attempt <= delays.Length)
                {
                    await Task.Delay(delays[attempt - 1], cancellationToken);
                }

                result = await _httpClient.SendEventsAsync(payloads, envData, cancellationToken);

                if (!result.IsNetworkError)
                    break;
            }
        }

        // Only mark environment as sent after all retries complete successfully
        if (envData is not null && result.IsSuccess)
        {
            lock (_sessionLock) { _environmentSent = true; }
        }

        return result;
    }

    private void LogRejections(FlushResult result)
    {
        if (result.IsPartialSuccess)
        {
            foreach (var rejected in result.RejectedEvents)
            {
                _logger?.LogWarning("Beacon: event {EventId} rejected by server - {Reason}.",
                    rejected.EventId, rejected.Reason);
            }
        }
    }

    private List<string> DequeueUpToBatch()
    {
        var batch = new List<string>(_options.MaxBatchSize);
        while (batch.Count < _options.MaxBatchSize && _memoryQueue.TryDequeue(out var payload))
        {
            batch.Add(payload);
            Interlocked.Decrement(ref _memoryQueueCount);
        }
        return batch;
    }

    private void WriteToDiskQueue(IReadOnlyList<string> payloads)
    {
        try
        {
            if (_diskQueue is null)
                return;

            var maxBytes = (long)_options.MaxQueueSizeMb * 1024 * 1024;

            // Enforce size cap before writing
            var dropped = _diskQueue.EnforceMaxSize(maxBytes);
            if (dropped > 0)
            {
                _logger?.LogWarning("Beacon: disk queue full - dropped {Count} events.", dropped);
            }

            _diskQueue.Enqueue(payloads);

            // Re-enforce after writing in case the new batch pushed over the cap
            var droppedAfter = _diskQueue.EnforceMaxSize(maxBytes);
            if (droppedAfter > 0)
            {
                _logger?.LogWarning("Beacon: disk queue over cap after write - dropped {Count} oldest events.", droppedAfter);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to write to disk queue.");
            // Events stay in memory for next cycle
        }
    }

    private void WriteRemainingToDisk()
    {
        try
        {
            var remaining = new List<string>();
            while (_memoryQueue.TryDequeue(out var payload))
            {
                remaining.Add(payload);
            }

            if (remaining.Count > 0)
            {
                WriteToDiskQueue(remaining);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Beacon: failed to write remaining events to disk during dispose.");
        }
    }

    private Task EndSessionInternal(string endReason, CancellationToken cancellationToken = default)
    {
        // Must be called under _sessionLock
        var currentSessionId = _sessionId;
        if (currentSessionId is null)
            return Task.CompletedTask;

        _sessionId = null;

        // Fire and forget session end
        var httpClient = _httpClient;
        if (httpClient is null)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await httpClient.SendSessionEndAsync(
                    currentSessionId,
                    DateTimeOffset.UtcNow,
                    endReason,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Grace period expired — session end is best-effort
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Beacon: failed to send session end.");
            }
        });
    }

    private static void ValidateActorId(string actorId)
    {
        if (string.IsNullOrEmpty(actorId))
            throw new ArgumentException("actorId must not be null or empty.", nameof(actorId));

        if (actorId.Length > 512)
            throw new ArgumentException("actorId must not exceed 512 characters.", nameof(actorId));
    }

    private bool ValidateOptions(BeaconOptions options)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            _logger?.LogWarning("Beacon: ApiKey is missing or empty. SDK disabled.");
            return false;
        }

        if (string.IsNullOrEmpty(options.ApiBaseUrl))
        {
            _logger?.LogWarning("Beacon: ApiBaseUrl is missing or empty. SDK disabled.");
            return false;
        }

        if (string.IsNullOrEmpty(options.AppName))
        {
            _logger?.LogWarning("Beacon: AppName is missing or empty. SDK disabled.");
            return false;
        }

        if (string.IsNullOrEmpty(options.AppVersion))
        {
            _logger?.LogWarning("Beacon: AppVersion is missing or empty. SDK disabled.");
            return false;
        }

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _))
        {
            _logger?.LogWarning("Beacon: ApiBaseUrl is not a valid absolute URI. SDK disabled.");
            return false;
        }

        return true;
    }
}
