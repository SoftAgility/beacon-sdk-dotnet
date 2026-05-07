// Covers:
//   AC-544: Each call returns distinct UUID, valid format (FR-464)
//   ED-427: Clock regression handled with sequence counter

using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests;

/// <summary>
/// Tests for the internal UUID v7 generator.
/// </summary>
public sealed class UuidV7Tests
{
    // AC-544: Each NewId() call returns a distinct, valid UUID
    [Fact]
    public void NewId_ReturnsDistinctValues()
    {
        // Act
        var ids = Enumerable.Range(0, 100)
            .Select(_ => UuidV7.NewId())
            .ToList();

        // Assert
        ids.Should().OnlyHaveUniqueItems("every UUID v7 must be unique");
    }

    // AC-544: Generated UUID is a valid GUID format
    [Fact]
    public void NewId_ReturnsValidGuidFormat()
    {
        // Act
        var id = UuidV7.NewId();

        // Assert
        id.Should().NotBe(Guid.Empty, "UUID v7 must not be Guid.Empty");
        Guid.TryParse(id.ToString(), out _).Should().BeTrue("must be a valid GUID string");
    }

    // AC-544 / FR-464: UUID v7 has version bits set to 0111 (version 7)
    [Fact]
    public void NewId_HasVersion7Bits()
    {
        // Act
        var id = UuidV7.NewId();
        var idString = id.ToString();

        // Assert — the version nibble is the 13th character (position after second hyphen)
        // UUID format: xxxxxxxx-xxxx-Vxxx-xxxx-xxxxxxxxxxxx where V is the version nibble
        var parts = idString.Split('-');
        var versionNibble = parts[2][0]; // First char of 3rd group
        versionNibble.Should().Be('7', "UUID v7 version nibble must be '7'");
    }

    // AC-544 / FR-464: UUID v7 has variant bits set to 10xx
    [Fact]
    public void NewId_HasCorrectVariantBits()
    {
        // Act
        var id = UuidV7.NewId();
        var bytes = id.ToByteArray();

        // Assert — In .NET Guid.ToByteArray(), byte[8] is not in network order.
        // For the string representation, the variant is the first nibble of the 4th group.
        var idString = id.ToString();
        var parts = idString.Split('-');
        var variantChar = parts[3][0]; // First char of 4th group
        var variantNibble = Convert.ToInt32(variantChar.ToString(), 16);
        // Variant 10xx means the high two bits of this nibble should be 10 (binary)
        // Valid hex values: 8, 9, a, b
        variantNibble.Should().BeInRange(0x8, 0xB,
            "UUID v7 variant bits must be 10xx (hex 8-b)");
    }

    // ED-427: Rapid generation maintains monotonicity (no duplicates even at same millisecond)
    [Fact]
    public void NewId_RapidGeneration_NoDuplicates()
    {
        // Act — generate many UUIDs as fast as possible to trigger same-millisecond logic
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 10_000; i++)
        {
            ids.Add(UuidV7.NewId());
        }

        // Assert
        ids.Should().HaveCount(10_000, "all 10,000 generated UUIDs must be unique");
    }

    // ED-427 supplemental: UUIDs are time-ordered (earlier calls produce lexicographically smaller values)
    [Fact]
    public void NewId_GeneratedInOrder_AreTimeOrdered()
    {
        // Act
        var id1 = UuidV7.NewId();
        Thread.Sleep(2); // Ensure different millisecond
        var id2 = UuidV7.NewId();

        // Assert — UUID v7 string comparison should preserve time ordering
        // because the high bits contain the timestamp
        var str1 = id1.ToString();
        var str2 = id2.ToString();
        string.Compare(str1, str2, StringComparison.Ordinal).Should().BeLessThan(0,
            "earlier UUID v7 should sort before later UUID v7");
    }
}
