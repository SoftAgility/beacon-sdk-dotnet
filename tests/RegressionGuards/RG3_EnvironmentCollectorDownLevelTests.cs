// Regression guard RG-3 — EnvironmentCollector down-level shim parity.
//
// PRD ref: Test plan "New RG-3". Maps to:
//   R7 Convert.ToHexString  -> Internal/Compat Hex.Lower (manual lowercase hex down-level)
//   R8 SHA256.HashData      -> Internal/Compat Hashing.Sha256 (instance SHA256 down-level)
//   R9 GC.GetGCMemoryInfo   -> #if NET6_0_OR_GREATER bucket; #else "unknown"
//   R1 SnakeCaseLower keys
//   WINDOWS define gates display dims
// On net48 this exercises the #else hash/hex shims and the "unknown" RAM fallback; the
// 64-char lowercase-hex assertion is the load-bearing check that Hex.Lower matches
// Convert.ToHexString byte-for-byte.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using SoftAgility.Beacon.Internal;
using SoftAgility.Beacon.Tests.Helpers;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG3_EnvironmentCollectorDownLevelTests
{
    // RG-3: machine_name_hash is a 64-char lowercase hex SHA-256 of the machine name.
    // Down-level this is produced by Hashing.Sha256 + Hex.Lower; the value must match a
    // TFM-independent reference computed the same way (instance SHA256 + manual hex).
    [Fact]
    public void CollectJson_MachineNameHash_Is64CharLowercaseHexSha256()
    {
        // Arrange
        var expected = CompatTestHelpers.Sha256LowerHex(
            Encoding.UTF8.GetBytes(Environment.MachineName));

        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var hash = doc.RootElement.GetProperty("machine_name_hash").GetString();

        // Assert
        hash.Should().NotBeNull();
        hash!.Length.Should().Be(64, "SHA-256 hex is 64 chars");
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "Hex.Lower must emit lowercase hex only");
        hash.Should().Be(expected, "down-level Hashing/Hex must match the static-API output");
    }

    // RG-3: total_ram_mb_bucket is always present and is one of the documented buckets.
    // On net48 (R9 #else) this is "unknown"; on net6/net8 it is a real bucket. Either way
    // the key must exist and be a valid bucket string (no null / no exception leak).
    [Fact]
    public void CollectJson_TotalRamMbBucket_PresentAndValid()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var bucket = doc.RootElement.GetProperty("total_ram_mb_bucket").GetString();

        // Assert
        var valid = new[] { "< 2 GB", "2-4 GB", "4-8 GB", "8-16 GB", "16-32 GB", "> 32 GB", "unknown" };
        valid.Should().Contain(bucket);
    }

    // RG-3: snake_case keys present (R1 cross-check on the real EnvironmentData wire path)
    // and display dims present iff the SDK build defines WINDOWS (net48 test run resolves
    // the WINDOWS SDK build).
    [Fact]
    public void CollectJson_KeysAreSnakeCase_AndDisplayDimsTrackWindows()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert — snake_case keys
        root.TryGetProperty("os_name", out _).Should().BeTrue();
        root.TryGetProperty("runtime_version", out _).Should().BeTrue();
        root.TryGetProperty("cpu_core_count", out _).Should().BeTrue();

#if WINDOWS
        root.TryGetProperty("display_width", out _).Should().BeTrue(
            "WINDOWS SDK build (net48) enriches display dims");
#else
        root.TryGetProperty("display_width", out _).Should().BeFalse(
            "non-WINDOWS TFM omits display dims (WhenWritingNull)");
#endif
    }
}
