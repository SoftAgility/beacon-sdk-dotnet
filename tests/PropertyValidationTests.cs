// Covers:
//   AC-511: Properties with 21 keys -> 21st key dropped with warning (FR-444, EC-424)
//   AC-512: Property key > 64 chars -> dropped with warning (FR-444, EC-424)
//   AC-513: Property value > 256 chars -> dropped with warning (FR-444, EC-424)
//   AC-514: Nested object property -> dropped with warning (FR-444, EC-424)
//   EC-424: Properties contain invalid values

using FluentAssertions;
using Microsoft.Extensions.Logging;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for PropertySanitizer — validates property key/value constraints.
/// </summary>
public sealed class PropertyValidationTests
{
    // AC-511: More than 20 keys -> excess keys dropped
    [Fact]
    public void Sanitize_WithTwentyOneKeys_DropsTwentyFirstKey()
    {
        // Arrange
        var properties = new Dictionary<string, object>();
        for (var i = 1; i <= 21; i++)
        {
            properties[$"key{i:D2}"] = $"value{i}";
        }

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Count.Should().Be(20, "only 20 keys should be accepted");
    }

    // AC-511 supplemental: Exactly 20 keys are all accepted
    [Fact]
    public void Sanitize_WithExactlyTwentyKeys_AcceptsAll()
    {
        // Arrange
        var properties = new Dictionary<string, object>();
        for (var i = 1; i <= 20; i++)
        {
            properties[$"key{i:D2}"] = $"value{i}";
        }

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Count.Should().Be(20);
    }

    // AC-512: Key exceeding 64 characters is dropped
    [Fact]
    public void Sanitize_WithKeyExceeding64Chars_DropsKey()
    {
        // Arrange
        var longKey = new string('k', 65);
        var properties = new Dictionary<string, object>
        {
            [longKey] = "value",
            ["valid_key"] = "valid_value"
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("valid_key");
        result.Should().NotContainKey(longKey);
    }

    // AC-512 supplemental: Key of exactly 64 characters is accepted
    [Fact]
    public void Sanitize_WithKeyOfExactly64Chars_AcceptsKey()
    {
        // Arrange
        var exactKey = new string('k', 64);
        var properties = new Dictionary<string, object>
        {
            [exactKey] = "value"
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey(exactKey);
    }

    // AC-513: Value exceeding 256 characters is dropped
    [Fact]
    public void Sanitize_WithValueExceeding256Chars_DropsValue()
    {
        // Arrange
        var longValue = new string('v', 257);
        var properties = new Dictionary<string, object>
        {
            ["long_value_key"] = longValue,
            ["valid_key"] = "valid_value"
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("valid_key");
        result.Should().NotContainKey("long_value_key");
    }

    // AC-513 supplemental: Value of exactly 256 characters is accepted
    [Fact]
    public void Sanitize_WithValueOfExactly256Chars_AcceptsValue()
    {
        // Arrange
        var exactValue = new string('v', 256);
        var properties = new Dictionary<string, object>
        {
            ["key"] = exactValue
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("key");
        result["key"].Should().Be(exactValue);
    }

    // AC-514: Nested object is dropped
    [Fact]
    public void Sanitize_WithNestedObjectProperty_DropsProperty()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["valid_key"] = "valid_value",
            ["nested_key"] = new Dictionary<string, object> { ["inner"] = "value" }
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("valid_key");
        result.Should().NotContainKey("nested_key",
            "nested objects should be dropped from properties");
    }

    // AC-514 supplemental: Array property is dropped
    [Fact]
    public void Sanitize_WithArrayProperty_DropsProperty()
    {
        // Arrange — use an anonymous type with an array via JSON round-trip
        var properties = new { valid_key = "value", tags = new[] { "a", "b" } };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("valid_key");
        result.Should().NotContainKey("tags",
            "array properties should be dropped");
    }

    // EC-424 supplemental: Null properties returns null
    [Fact]
    public void Sanitize_WithNullProperties_ReturnsNull()
    {
        // Act
        var result = PropertySanitizer.Sanitize(null, null);

        // Assert
        result.Should().BeNull();
    }

    // EC-424 supplemental: Empty dictionary returns null
    [Fact]
    public void Sanitize_WithEmptyDictionary_ReturnsNull()
    {
        // Act
        var result = PropertySanitizer.Sanitize(new Dictionary<string, object>(), null);

        // Assert
        result.Should().BeNull();
    }

    // AC-514 supplemental: Anonymous type with flat properties works
    [Fact]
    public void Sanitize_WithAnonymousType_ExtractsProperties()
    {
        // Arrange
        var properties = new { color = "red", count = 42 };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().ContainKey("color");
        result.Should().ContainKey("count");
    }

    // Dictionary with scalar value types (Guid, DateTime, etc.) should be accepted,
    // consistent with the anonymous-object path that JSON-serializes first.
    [Fact]
    public void Sanitize_Dictionary_WithScalarValueTypes_AcceptsAll()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["guid_val"] = Guid.NewGuid(),
            ["datetime_val"] = DateTime.UtcNow,
            ["offset_val"] = DateTimeOffset.UtcNow,
            ["timespan_val"] = TimeSpan.FromMinutes(5),
            ["uri_val"] = new Uri("https://example.com"),
            ["enum_val"] = DayOfWeek.Monday,
            ["decimal_val"] = 3.14m,
            ["string_val"] = "hello"
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().HaveCount(8, "all scalar types should be accepted");
    }

    // Same scalar types via anonymous object should also work (consistency check)
    [Fact]
    public void Sanitize_AnonymousObject_WithScalarValueTypes_AcceptsAll()
    {
        // Arrange
        var properties = new
        {
            guid_val = Guid.NewGuid(),
            datetime_val = DateTime.UtcNow,
            offset_val = DateTimeOffset.UtcNow,
            enum_val = DayOfWeek.Monday,
            decimal_val = 3.14m
        };

        // Act
        var result = PropertySanitizer.Sanitize(properties, null);

        // Assert
        result.Should().NotBeNull();
        result!.Should().HaveCount(5);
    }
}
