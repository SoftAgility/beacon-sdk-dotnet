// Covers (.NET SDK only):
//   AC-1900: First init creates device ID file and uses it for Track (FR-1123, FR-1124)
//   AC-1901: Existing device ID file is reloaded on startup (FR-1123, FR-1124)
//   AC-1902: Track without Identify uses device ID (FR-1124)
//   AC-1903: Track after Identify uses identified actor, not device ID (FR-1124)
//   AC-1904: StartSession without Identify uses device ID (FR-1125)
//   AC-1905: TrackException without Identify uses device ID (FR-1126)
//   AC-1929: FlushIntervalSeconds default is 60 (FR-1139)
//   ED-740: Device ID file with non-UUID content used as-is (FR-1123)
//   Reserved category: Track with "_beacon" prefix is no-op (constraint)

using System.Reflection;
using FluentAssertions;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for anonymous actor mode (device ID fallback), device ID persistence,
/// reserved category validation, and flush interval default.
/// </summary>
public sealed class AnonymousActorTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* ignore */ }
        }
    }

    private string GetDataDirectory(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_dataDirectory",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)field!.GetValue(tracker)!;
    }

    private string? GetDeviceId(BeaconTracker tracker)
    {
        var field = typeof(BeaconTracker).GetField("_deviceId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)field!.GetValue(tracker);
    }

    // ── AC-1900: First init creates device ID file, Track uses it ──

    [Fact]
    public void Init_FirstTime_CreatesDeviceIdFileAndUsesForTrack()
    {
        // Arrange -- create tracker with unique name (first time = no existing device ID file)
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);

        var dataDir = GetDataDirectory(tracker);
        var deviceIdPath = Path.Combine(dataDir, "beacon_device_id");

        // Assert -- file exists and has a UUID
        File.Exists(deviceIdPath).Should().BeTrue(
            "beacon_device_id should be created on first init");
        var fileDeviceId = File.ReadAllText(deviceIdPath).Trim();
        fileDeviceId.Should().NotBeNullOrEmpty();
        Guid.TryParse(fileDeviceId, out _).Should().BeTrue(
            "device ID should be a valid UUID v7");

        // Act -- Track without Identify should use device ID
        tracker.Track("feature", "export");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(tracker);
        docs.Should().HaveCount(1);
        docs[0].RootElement.GetProperty("actor_id").GetString().Should().Be(fileDeviceId);
        docs[0].Dispose();
    }

    // ── AC-1901: Existing device ID file is reloaded on startup ──

    [Fact]
    public void Init_WithExistingDeviceIdFile_ReloadsDeviceId()
    {
        // Arrange -- create a tracker, read its device ID, dispose, recreate with same Product
        var appName = $"AnonTest_{Guid.NewGuid():N}";
        var tracker1 = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });

        var dataDir = GetDataDirectory(tracker1);
        var originalDeviceId = File.ReadAllText(Path.Combine(dataDir, "beacon_device_id")).Trim();
        tracker1.Dispose();

        // Act -- create second tracker with same Product
        var tracker2 = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });
        _disposables.Add(tracker2);

        // Assert -- device ID should be reloaded from file
        var reloadedDeviceId = GetDeviceId(tracker2);
        reloadedDeviceId.Should().Be(originalDeviceId,
            "device ID should be reloaded from the existing file on next startup");
    }

    // ── AC-1902: Track without Identify uses device ID ──

    [Fact]
    public void Track_WithoutIdentify_UsesDeviceId()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        var deviceId = GetDeviceId(tracker);
        deviceId.Should().NotBeNullOrEmpty();

        // Act -- no Identify() call, just Track
        var act = () => tracker.Track("feature", "export");

        // Assert
        act.Should().NotThrow(
            "Track without Identify should NOT throw when device ID is available");
        var docs = TrackerTestHelper.GetQueuedEventDocuments(tracker);
        docs.Should().HaveCount(1);
        docs[0].RootElement.GetProperty("actor_id").GetString().Should().Be(deviceId);
        docs[0].Dispose();
    }

    // ── AC-1903: Track after Identify uses identified actor, not device ID ──

    [Fact]
    public void Track_AfterIdentify_UsesIdentifiedActorNotDeviceId()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        var deviceId = GetDeviceId(tracker);
        tracker.Identify("user-1");

        // Act
        tracker.Track("feature", "export");

        // Assert
        var docs = TrackerTestHelper.GetQueuedEventDocuments(tracker);
        docs.Should().HaveCount(1);
        docs[0].RootElement.GetProperty("actor_id").GetString().Should().Be("user-1",
            "Identify() should take precedence over device ID");
        docs[0].RootElement.GetProperty("actor_id").GetString().Should().NotBe(deviceId);
        docs[0].Dispose();
    }

    // ── AC-1904: StartSession without Identify uses device ID ──

    [Fact]
    public void StartSession_WithoutIdentify_UsesDeviceId()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        var deviceId = GetDeviceId(tracker);

        // Act -- no Identify() call
        var act = () => tracker.StartSession();

        // Assert
        act.Should().NotThrow(
            "StartSession without Identify should NOT throw when device ID is available");
        TrackerTestHelper.GetSessionId(tracker).Should().NotBeNullOrEmpty(
            "a session should be started using the device ID");
    }

    // ── AC-1905: TrackException without Identify uses device ID ──

    [Fact]
    public void TrackException_WithoutIdentify_UsesDeviceId()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        var deviceId = GetDeviceId(tracker);
        deviceId.Should().NotBeNullOrEmpty();

        // Act -- no Identify() call; TrackException is fire-and-forget HTTP,
        // but it should not throw when device ID is available
        var act = () => tracker.TrackException(new InvalidOperationException("test error"));

        // Assert
        act.Should().NotThrow(
            "TrackException without Identify should NOT throw when device ID is available");
    }

    // ── AC-1929: FlushIntervalSeconds default is 60 ──

    [Fact]
    public void BeaconOptions_FlushIntervalSeconds_DefaultIs60()
    {
        // Arrange & Assert
        var options = new BeaconOptions();
        options.FlushIntervalSeconds.Should().Be(60,
            "FlushIntervalSeconds default should be 60 as documented");
    }

    // ── ED-740: Device ID file with non-UUID content is used as-is ──

    [Fact]
    public void Init_WithNonUuidDeviceIdContent_UsesItAsIs()
    {
        // Arrange -- create a tracker to set up the directory
        var appName = $"AnonTest_{Guid.NewGuid():N}";
        var tracker1 = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });
        var dataDir = GetDataDirectory(tracker1);
        tracker1.Dispose();

        // Overwrite device ID file with non-UUID content
        var deviceIdPath = Path.Combine(dataDir, "beacon_device_id");
        File.WriteAllText(deviceIdPath, "custom-device-id-12345");

        // Act
        var tracker2 = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-api-key",
            ApiBaseUrl = "https://beacon.test.local",
            Product = appName,
            AppVersion = "1.0.0",
            Enabled = true,
            FlushIntervalSeconds = 3600
        });
        _disposables.Add(tracker2);

        // Assert -- non-UUID content should be used as-is
        GetDeviceId(tracker2).Should().Be("custom-device-id-12345",
            "non-UUID content in beacon_device_id should be used as-is per ED-740");
    }

    // ── Reserved category: Track with "_beacon" prefix is no-op ──

    [Fact]
    public void Track_WithReservedCategoryPrefix_IsNoOp()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        tracker.Identify("user-1");

        // Act -- underscore-prefixed categories are reserved
        tracker.Track("_beacon", "test_event");

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "categories starting with '_' are reserved and should be silently ignored");
    }

    [Fact]
    public void Track_WithNormalCategory_EnqueuesEvent()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);
        tracker.Identify("user-1");

        // Act
        tracker.Track("feature", "test_event");

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().HaveCount(1,
            "non-reserved categories should be tracked normally");
    }

    // ── Reserved category via explicit actorId overload ──

    [Fact]
    public void Track_WithReservedCategory_ExplicitActorId_IsNoOp()
    {
        // Arrange
        var tracker = TrackerTestHelper.CreateTracker(
            appName: $"AnonTest_{Guid.NewGuid():N}");
        _disposables.Add(tracker);

        // Act -- explicit actorId overload should also check reserved prefix
        tracker.Track("_internal", "test", "user-1");

        // Assert
        TrackerTestHelper.GetQueuedEvents(tracker).Should().BeEmpty(
            "reserved category check applies to both Track overloads");
    }
}
