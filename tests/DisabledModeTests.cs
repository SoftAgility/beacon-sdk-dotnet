// Covers:
//   AC-506: Track() with Enabled=false -> no-op (FR-442)
//   AC-507: FlushAsync() with Enabled=false -> returns immediately (FR-442)

using FluentAssertions;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for SDK behavior when Enabled=false. All methods should be silent no-ops.
/// </summary>
public sealed class DisabledModeTests : IDisposable
{
    private readonly BeaconTracker _tracker;

    public DisabledModeTests()
    {
        _tracker = new BeaconTracker(new BeaconOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = "https://beacon.example.com",
            Product = "TestApp",
            AppVersion = "1.0.0",
            Enabled = false
        });
    }

    public void Dispose()
    {
        _tracker.Dispose();
    }

    // AC-506: Track() with Enabled=false returns immediately, no exception
    [Fact]
    public void Track_WhenDisabled_DoesNotThrowAndIsNoOp()
    {
        // Arrange — tracker is already disabled

        // Act
        var act = () => _tracker.Track("category", "name", "actor-1");

        // Assert
        act.Should().NotThrow();
    }

    // AC-507: FlushAsync() with Enabled=false returns completed task immediately
    [Fact]
    public async Task FlushAsync_WhenDisabled_ReturnsImmediately()
    {
        // Arrange — tracker is already disabled

        // Act
        var task = _tracker.FlushAsync();

        // Assert
        task.IsCompleted.Should().BeTrue("FlushAsync should return a completed task when disabled");
        await task; // Should not throw
    }

    // AC-506 supplemental: LastFlushStatus is Disabled when Enabled=false
    [Fact]
    public void LastFlushStatus_WhenDisabled_IsDisabled()
    {
        // Assert
        _tracker.LastFlushStatus.Should().Be(FlushStatus.Disabled);
    }

    // AC-506 supplemental: StartSession when disabled is no-op
    [Fact]
    public void StartSession_WhenDisabled_DoesNotThrow()
    {
        // Act
        var act = () => _tracker.StartSession("actor-1");

        // Assert
        act.Should().NotThrow();
    }

    // AC-506 supplemental: EndSession when disabled is no-op
    [Fact]
    public void EndSession_WhenDisabled_DoesNotThrow()
    {
        // Act
        var act = () => _tracker.EndSession();

        // Assert
        act.Should().NotThrow();
    }

    // Parameterless Track() when disabled is no-op even without Identify()
    [Fact]
    public void Track_Parameterless_WhenDisabled_DoesNotThrow()
    {
        // No Identify() call — _actorId is null

        // Act
        var act = () => _tracker.Track("category", "name");

        // Assert
        act.Should().NotThrow();
    }

    // Parameterless StartSession() when disabled is no-op even without Identify()
    [Fact]
    public void StartSession_Parameterless_WhenDisabled_DoesNotThrow()
    {
        // No Identify() call — _actorId is null

        // Act
        var act = () => _tracker.StartSession();

        // Assert
        act.Should().NotThrow();
    }

    // Identify() with invalid input when disabled is silent no-op
    [Fact]
    public void Identify_WithNull_WhenDisabled_DoesNotThrow()
    {
        // Act — null actorId would throw on enabled tracker, but disabled = no-op
        var act = () => _tracker.Identify(null!);

        // Assert
        act.Should().NotThrow();
    }

    // Identify() with valid input when disabled is silent no-op
    [Fact]
    public void Identify_WhenDisabled_DoesNotThrow()
    {
        var act = () => _tracker.Identify("actor-1");
        act.Should().NotThrow();
    }

    // Track(actorId) with invalid actorId when disabled is silent no-op
    [Fact]
    public void Track_WithNullActorId_WhenDisabled_DoesNotThrow()
    {
        var act = () => _tracker.Track("cat", "name", (string)null!);
        act.Should().NotThrow();
    }

    // StartSession(actorId) with invalid actorId when disabled is silent no-op
    [Fact]
    public void StartSession_WithNullActorId_WhenDisabled_DoesNotThrow()
    {
        var act = () => _tracker.StartSession((string)null!);
        act.Should().NotThrow();
    }
}
