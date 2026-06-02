// Regression guard RG-1 — snake_case wire parity (down-level).
//
// PRD ref: sdk-dotnet-broaden-target-frameworks.prd.md, Test plan "New RG-1".
// Maps to remediation R1: JsonNamingPolicy.SnakeCaseLower (added .NET 8) is supplied
// down-level (net6.0/netstandard2.0/net48) by an explicit System.Text.Json 8.0.5
// PackageReference. A missing OR duplicate STJ reference would silently regress the
// wire contract — every event/manifest/environment payload would emit PascalCase or
// the wrong casing. This guard runs on every TFM; it is the net48 run that proves the
// down-level package wiring resolves SnakeCaseLower at all.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG1_SnakeCaseWireParityTests
{
    private sealed class WireSample
    {
        public string ProductVersion { get; set; } = "";
        public string MachineNameHash { get; set; } = "";
        public int CpuCoreCount { get; set; }
    }

    // RG-1: JsonNamingPolicy.SnakeCaseLower resolves and transforms multi-word CLR
    // property names to snake_case on the current TFM. This is the exact API R1 wires
    // down-level via System.Text.Json 8.0.5 — the assertion fails to even compile/run
    // if SnakeCaseLower were unavailable.
    [Fact]
    public void SnakeCaseLowerPolicy_TransformsPascalCaseToSnakeCase()
    {
        // Arrange
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var sample = new WireSample
        {
            ProductVersion = "3.2.0",
            MachineNameHash = "abc123",
            CpuCoreCount = 8
        };

        // Act
        var json = JsonSerializer.Serialize(sample, options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert — snake_case keys present, PascalCase keys absent
        root.TryGetProperty("product_version", out var pv).Should().BeTrue(
            "SnakeCaseLower must emit product_version (R1 wire contract)");
        pv.GetString().Should().Be("3.2.0");
        root.TryGetProperty("machine_name_hash", out _).Should().BeTrue();
        root.TryGetProperty("cpu_core_count", out var cores).Should().BeTrue();
        cores.GetInt32().Should().Be(8);
        root.TryGetProperty("ProductVersion", out _).Should().BeFalse(
            "PascalCase keys must not leak onto the wire");
    }

    // RG-1 cross-check: the SDK's OWN serializer (EnvironmentCollector.JsonOptions) emits
    // snake_case for genuine PascalCase CLR properties on EnvironmentData. cpu_core_count
    // can only appear if SnakeCaseLower actually ran against the CpuCoreCount property —
    // this validates the real production wire path, not just a standalone policy.
    [Fact]
    public void EnvironmentCollector_RealWirePath_EmitsSnakeCaseKeys()
    {
        // Act
        var json = EnvironmentCollector.CollectJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert — keys derived from PascalCase CLR props via the policy
        root.TryGetProperty("os_name", out _).Should().BeTrue();
        root.TryGetProperty("machine_name_hash", out _).Should().BeTrue();
        root.TryGetProperty("cpu_core_count", out _).Should().BeTrue(
            "cpu_core_count proves SnakeCaseLower ran against CpuCoreCount on this TFM");
        root.TryGetProperty("total_ram_mb_bucket", out _).Should().BeTrue();

        // No PascalCase leakage
        root.TryGetProperty("OsName", out _).Should().BeFalse();
        root.TryGetProperty("CpuCoreCount", out _).Should().BeFalse();
    }
}
