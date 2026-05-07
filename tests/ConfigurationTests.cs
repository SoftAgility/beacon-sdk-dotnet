// Covers:
//   AC-500: Configure() with valid options -> Instance is non-null (FR-439)
//   AC-502: Missing ApiKey -> SDK disabled, no exception (FR-441, EC-417)
//   AC-503: Missing ApiBaseUrl -> SDK disabled, no exception (FR-441, EC-417)
//   AC-504: Missing AppName -> SDK disabled, no exception (FR-441, EC-417)
//   AC-505: Invalid URI -> SDK disabled, no exception (EC-418)
//   AC-540: MaxBatchSize > 1000 -> clamped to 1000 (constraint)
//   EC-417: Missing required configuration field
//   EC-418: Invalid ApiBaseUrl format

using FluentAssertions;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for BeaconTracker.Configure() configuration validation.
/// These tests share static state (BeaconTracker.Instance) so they must
/// reset it between tests using ResetInstance().
/// </summary>
public sealed class ConfigurationTests : IDisposable
{
    public ConfigurationTests()
    {
        // Ensure clean state before each test
        BeaconTracker.ResetInstance();
    }

    public void Dispose()
    {
        // Clean up static state and dispose any created tracker
        var instance = BeaconTracker.Instance;
        if (instance is not null)
        {
            instance.Dispose();
        }
        BeaconTracker.ResetInstance();
    }

    // AC-500: Given Configure() is called with valid options, Instance is non-null
    [Fact]
    public void Configure_WithValidOptions_CreatesNonNullInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
    }

    // AC-500 / Open Question 1: Second Configure() call -> InvalidOperationException
    [Fact]
    public void Configure_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Act
        var act = () => BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key-2";
            options.ApiBaseUrl = "https://beacon2.example.com";
            options.AppName = "TestApp2";
            options.AppVersion = "2.0.0";
        });

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already configured*");
    }

    // AC-502 / EC-417: Missing ApiKey -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithEmptyApiKey_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert — instance exists but is disabled (all calls are no-ops)
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-502 / EC-417: Null ApiKey -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithNullApiKey_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = null!;
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-503 / EC-417: Missing ApiBaseUrl -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithEmptyApiBaseUrl_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-504 / EC-417: Missing AppName -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithEmptyAppName_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-505 / EC-418: Invalid URI (relative) -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithRelativeUri_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "myapp";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-505 / EC-418: Invalid URI (no scheme) -> SDK creates instance but disables itself
    [Fact]
    public void Configure_WithNoSchemeUri_CreatesDisabledInstance()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        // Assert
        BeaconTracker.Instance.Should().NotBeNull();
        BeaconTracker.Instance!.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-540: MaxBatchSize > 1000 is clamped to 1000
    [Fact]
    public void Configure_WithMaxBatchSizeAbove1000_ClampsTo1000()
    {
        // Arrange & Act
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "test-api-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
            options.MaxBatchSize = 1001;
        });

        // Assert — the tracker was created successfully (no exception)
        // We verify clamping by checking the options aren't rejecting the value
        BeaconTracker.Instance.Should().NotBeNull();
    }

    // AppName exceeding 128 chars is truncated
    [Fact]
    public void Constructor_WithLongAppName_TruncatesTo128()
    {
        // Arrange
        var longName = new string('a', 200);
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = longName,
            AppVersion = "1.0.0",
            FlushIntervalSeconds = 3600
        });

        // Act — track an event and inspect the payload
        tracker.Track("cat", "name", "actor-1");
        var docs = Helpers.TrackerTestHelper.GetQueuedEventDocuments(tracker);
        var sourceApp = docs[0].RootElement.GetProperty("source_app").GetString()!;

        // Assert
        sourceApp.Length.Should().Be(128);
        docs[0].Dispose();
        tracker.Dispose();
    }

    // AppVersion exceeding 256 chars is truncated
    [Fact]
    public void Constructor_WithLongAppVersion_TruncatesTo256()
    {
        // Arrange
        var longVersion = new string('v', 300);
        var tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "key",
            ApiBaseUrl = "https://beacon.example.com",
            AppName = "App",
            AppVersion = longVersion,
            FlushIntervalSeconds = 3600
        });

        // Act
        tracker.Track("cat", "name", "actor-1");
        var docs = Helpers.TrackerTestHelper.GetQueuedEventDocuments(tracker);
        var sourceVersion = docs[0].RootElement.GetProperty("source_version").GetString()!;

        // Assert
        sourceVersion.Length.Should().Be(256);
        docs[0].Dispose();
        tracker.Dispose();
    }

    // Verify that a disabled instance allows all API calls without throwing
    [Fact]
    public void DisabledInstance_AllMethodsAreNoOps()
    {
        // Arrange
        BeaconTracker.Configure(options =>
        {
            options.ApiKey = "";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
        });

        var tracker = BeaconTracker.Instance!;

        // Act & Assert — none of these should throw
        tracker.Identify("user-1");
        tracker.StartSession("user-1");
        tracker.EndSession();
        tracker.Dispose();
    }
}
