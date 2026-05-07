// Covers:
//   AC-501: AddBeacon registers singleton IBeaconTracker in DI container (FR-440)

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the DI extension method AddBeacon.
/// </summary>
public sealed class DiExtensionTests
{
    // AC-501: AddBeacon registers IBeaconTracker as a singleton
    [Fact]
    public void AddBeacon_RegistersSingletonIBeaconTracker()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBeacon(options =>
        {
            options.ApiKey = "test-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        var provider = services.BuildServiceProvider();

        // Act
        var tracker1 = provider.GetRequiredService<IBeaconTracker>();
        var tracker2 = provider.GetRequiredService<IBeaconTracker>();

        // Assert
        tracker1.Should().NotBeNull();
        tracker1.Should().BeOfType<BeaconTracker>();
        tracker1.Should().BeSameAs(tracker2, "IBeaconTracker should be registered as a singleton");

        // Cleanup
        (tracker1 as IDisposable)?.Dispose();
    }

    // AC-501 supplemental: BeaconTracker is also resolvable directly
    [Fact]
    public void AddBeacon_BeaconTrackerResolvableDirectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBeacon(options =>
        {
            options.ApiKey = "test-key";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
            options.AppVersion = "1.0.0";
        });

        var provider = services.BuildServiceProvider();

        // Act
        var tracker = provider.GetRequiredService<BeaconTracker>();
        var iTracker = provider.GetRequiredService<IBeaconTracker>();

        // Assert
        tracker.Should().NotBeNull();
        tracker.Should().BeSameAs(iTracker,
            "BeaconTracker and IBeaconTracker should resolve to the same instance");

        // Cleanup
        tracker.Dispose();
    }

    // AC-501 supplemental: AddBeacon with invalid options resolves a disabled instance
    [Fact]
    public void AddBeacon_WithMissingApiKey_ResolvesDisabledInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBeacon(options =>
        {
            options.ApiKey = "";
            options.ApiBaseUrl = "https://beacon.example.com";
            options.AppName = "TestApp";
        });

        var provider = services.BuildServiceProvider();

        // Act
        var tracker = provider.GetRequiredService<IBeaconTracker>();

        // Assert — instance exists but is disabled (all calls are no-ops)
        tracker.Should().NotBeNull();
        tracker.LastFlushStatus.Should().Be(FlushStatus.Disabled);

        // Cleanup
        (tracker as IDisposable)?.Dispose();
    }
}
