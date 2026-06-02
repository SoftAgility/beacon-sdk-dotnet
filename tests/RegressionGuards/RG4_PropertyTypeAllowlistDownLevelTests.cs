// Regression guard RG-4 — property type allowlist down-level (R10).
//
// PRD ref: Test plan "New RG-4". PropertySanitizer's flat-value allowlist includes
// `or DateOnly or TimeOnly` only under #if NET6_0_OR_GREATER (those types were added in
// .NET 6 and do not exist on net48/netstandard2.0). The guard asserts that:
//   - the common scalar allowlist (string/int/decimal/Guid/DateTimeOffset/...) is accepted
//     on every TFM, and
//   - nested objects / arrays are still rejected,
// so the #if gating around the two .NET 6 types does not change behavior for any type a
// down-level consumer can actually construct.

using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG4_PropertyTypeAllowlistDownLevelTests
{
    // RG-4: cross-TFM scalar allowlist (excluding the .NET 6-only DateOnly/TimeOnly,
    // which a net48 consumer cannot construct) is accepted intact.
    [Fact]
    public void Sanitize_CommonScalarTypes_AreAccepted_OnEveryTfm()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["string_val"] = "hello",
            ["int_val"] = 42,
            ["long_val"] = 9_000_000_000L,
            ["decimal_val"] = 3.14m,
            ["double_val"] = 2.5d,
            ["bool_val"] = true,
            ["guid_val"] = Guid.NewGuid(),
            ["datetime_val"] = DateTime.UtcNow,
            ["offset_val"] = DateTimeOffset.UtcNow,
            ["timespan_val"] = TimeSpan.FromMinutes(5),
            ["uri_val"] = new Uri("https://example.com"),
            ["enum_val"] = DayOfWeek.Monday
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().HaveCount(properties.Count,
            "every common scalar type must remain in the allowlist on all TFMs");
    }

    // RG-4: nested object and array are still rejected (allowlist boundary unchanged by
    // the DateOnly/TimeOnly #if).
    [Fact]
    public void Sanitize_NestedObjectsAndArrays_AreRejected_OnEveryTfm()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["valid"] = "ok",
            ["nested_obj"] = new Dictionary<string, object> { ["inner"] = "x" },
            ["nested_list"] = new List<string> { "a", "b" },
            ["nested_array"] = new[] { 1, 2, 3 }
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("valid");
        result.Should().NotContainKey("nested_obj");
        result.Should().NotContainKey("nested_list");
        result.Should().NotContainKey("nested_array");
        result.Should().HaveCount(1, "only the flat scalar survives");
    }

#if NET6_0_OR_GREATER
    // RG-4: on TFMs where DateOnly/TimeOnly exist, they are part of the allowlist (the
    // #if true branch). This asserts the modern branch keeps them flat (not rejected as
    // nested). Compiled out on net48 where the types do not exist.
    [Fact]
    public void Sanitize_DateOnlyAndTimeOnly_AreAccepted_OnNet6Plus()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["date_only"] = new DateOnly(2026, 6, 2),
            ["time_only"] = new TimeOnly(13, 30)
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("date_only");
        result.Should().ContainKey("time_only");
    }
#endif
}
