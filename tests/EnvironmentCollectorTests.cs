// Covers:
//   AC-541: On non-Windows/Linux, display_width and display_height are omitted (FR-463, ED-426)
//   AC-542: Environment data contains all expected fields (FR-463)
//   AC-543: machine_name_hash is SHA256 hex of Environment.MachineName (FR-463)
//   ED-426: Non-Windows platform — display dimensions omitted

using System.Text;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Internal;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the EnvironmentCollector — environment data collection and serialization.
/// Note: These tests run on the actual platform (Windows in CI). Display dimension
/// tests verify behavior for the platform they are running on.
/// </summary>
public sealed class EnvironmentCollectorTests
{
    // AC-542: CollectJson contains all expected fields
    [Fact]
    public void CollectJson_ContainsExpectedFields()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert — all required fields are present
        root.TryGetProperty("os_name", out var osName).Should().BeTrue();
        osName.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("os_version", out var osVersion).Should().BeTrue();
        osVersion.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("os_architecture", out var osArch).Should().BeTrue();
        osArch.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("runtime_name", out var rtName).Should().BeTrue();
        rtName.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("runtime_version", out var rtVersion).Should().BeTrue();
        rtVersion.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("machine_name_hash", out var hash).Should().BeTrue();
        hash.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("total_ram_mb_bucket", out var ram).Should().BeTrue();
        ram.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("cpu_core_count", out var cores).Should().BeTrue();
        cores.GetInt32().Should().BeGreaterThan(0);

        root.TryGetProperty("locale", out var locale).Should().BeTrue();
        locale.GetString().Should().NotBeNull();
    }

    // AC-543: machine_name_hash is SHA256 hex of Environment.MachineName
    [Fact]
    public void CollectJson_MachineNameHash_IsSha256OfMachineName()
    {
        // Arrange
        var expectedHash = ComputeExpectedHash(Environment.MachineName);

        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var actualHash = doc.RootElement.GetProperty("machine_name_hash").GetString();

        // Assert
        actualHash.Should().Be(expectedHash,
            "machine_name_hash should be SHA256 hex of Environment.MachineName");
    }

    // AC-542 supplemental: CollectBase64 returns valid Base64 that decodes to the same JSON
    [Fact]
    public void CollectBase64_ReturnsValidBase64EncodedJson()
    {
        // Act
        var base64 = EnvironmentCollector.CollectBase64();

        // Assert
        base64.Should().NotBeNullOrEmpty();
        var bytes = Convert.FromBase64String(base64);
        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("os_name", out _).Should().BeTrue();
    }

    // AC-542: total_ram_mb_bucket is one of the expected bucket values
    [Fact]
    public void CollectJson_TotalRamMbBucket_IsValidBucketString()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var bucket = doc.RootElement.GetProperty("total_ram_mb_bucket").GetString();

        // Assert
        var validBuckets = new[]
        {
            "< 2 GB", "2-4 GB", "4-8 GB", "8-16 GB", "16-32 GB", "> 32 GB", "unknown"
        };
        validBuckets.Should().Contain(bucket);
    }

    // AC-541 / ED-426: display dimensions are present iff the SDK build defines WINDOWS.
    // The test references the SDK via ProjectReference, so each test TFM resolves the
    // matching SDK TFM: net8.0 (no WINDOWS) → dims omitted; net48 (WINDOWS defined in
    // the SDK csproj) → dims present. The assertion is therefore TFM-gated on the same
    // WINDOWS symbol the SDK uses, keeping the original net8 intent while staying green
    // on the net48 down-level run (RG-3 cross-check).
    [Fact]
    public void CollectJson_DisplayDimensions_TrackTheWindowsBuild()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

#if WINDOWS
        // On a WINDOWS-defined SDK build (net48 test run resolves lib/net48), the
        // #if WINDOWS branch in EnvironmentCollector runs and enriches display dims.
        root.TryGetProperty("display_width", out _).Should().BeTrue(
            "display_width should be present on a WINDOWS SDK build (net48)");
        root.TryGetProperty("display_height", out _).Should().BeTrue(
            "display_height should be present on a WINDOWS SDK build (net48)");
#else
        // The test project targets net8.0/net6.0 (not -windows), so WINDOWS is not
        // defined in the resolved SDK build and the dims are null → omitted via
        // JsonIgnoreCondition.WhenWritingNull.
        root.TryGetProperty("display_width", out _).Should().BeFalse(
            "display_width should be omitted on a non-WINDOWS TFM (net8.0/net6.0)");
        root.TryGetProperty("display_height", out _).Should().BeFalse(
            "display_height should be omitted on a non-WINDOWS TFM (net8.0/net6.0)");
#endif
    }

    private static string ComputeExpectedHash(string machineName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(machineName);
        // Down-level-safe: instance SHA256 + manual lowercase hex (the static
        // SHA256.HashData / Convert.ToHexString are .NET 5+ and absent on net48).
        return CompatTestHelpers.Sha256LowerHex(nameBytes);
    }
}
