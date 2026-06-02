// Regression guard RG-2 — UuidV7 down-level shim parity.
//
// PRD ref: Test plan "New RG-2". Maps to R6: UuidV7 uses Span<byte> + stackalloc and
// RandomNumberGenerator.Fill(Span<byte>) (netstandard2.1+). Down-level (net48/
// netstandard2.0) these route through Internal/Compat Rng.Fill (instance RNG into a
// byte[] then copied into the span) and System.Memory's Span support. A botched shim
// would corrupt the version nibble, variant bits, or monotonicity. Running this on
// net48 exercises the #else Rng.Fill branch; on net8/net6 it exercises the fast path.

using FluentAssertions;
using SoftAgility.Beacon.Internal;

namespace SoftAgility.Beacon.Tests.RegressionGuards;

public sealed class RG2_UuidV7DownLevelTests
{
    // RG-2: version nibble is 7 and variant bits are 10xx for a large sample. This is
    // the bit-layout the Span-fill writes; a broken down-level Rng.Fill/stackalloc shim
    // would scramble these positions.
    [Fact]
    public void NewId_BitLayout_IsVersion7AndVariant10xx_AcrossManySamples()
    {
        for (var i = 0; i < 1_000; i++)
        {
            // Act
            var id = UuidV7.NewId();
            var parts = id.ToString().Split('-');

            // Assert — version nibble (1st char of 3rd group) == '7'
            parts[2][0].Should().Be('7', "every UUID v7 must carry version 7");

            // variant nibble (1st char of 4th group) ∈ {8,9,a,b} (binary 10xx)
            var variantNibble = Convert.ToInt32(parts[3][0].ToString(), 16);
            variantNibble.Should().BeInRange(0x8, 0xB, "variant bits must be 10xx");
        }
    }

    // RG-2: rapid generation stays unique (monotonic sequence counter on same-ms).
    // The random tail is filled via Rng.Fill — uniqueness regresses if the down-level
    // shim returns zeroed/constant bytes.
    [Fact]
    public void NewId_RapidGeneration_RemainsUnique()
    {
        // Act
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 20_000; i++)
            ids.Add(UuidV7.NewId());

        // Assert
        ids.Should().HaveCount(20_000, "the random tail (Rng.Fill) must vary every call");
    }

    // RG-2: time ordering preserved — high bits carry the millisecond timestamp written
    // through the span. Verifies the byte ordering survived the down-level fill/copy.
    [Fact]
    public void NewId_IsTimeOrdered()
    {
        // Act
        var first = UuidV7.NewId().ToString();
        Thread.Sleep(3);
        var second = UuidV7.NewId().ToString();

        // Assert
        string.Compare(first, second, StringComparison.Ordinal)
            .Should().BeLessThan(0, "earlier UUID v7 sorts before later one");
    }
}
