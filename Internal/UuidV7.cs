using SoftAgility.Beacon.Internal.Compat;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Generates UUID v7 (time-ordered, RFC 9562 Section 5.7) compatible with .NET 8.
/// Thread-safe. Maintains monotonicity within the process by detecting clock regression
/// and incrementing a sequence counter.
/// </summary>
internal static class UuidV7
{
    private static readonly object Lock = new();
    private static long _lastTimestamp;
    private static ushort _sequence;

    /// <summary>
    /// Generates a new UUID v7 with millisecond-precision Unix timestamp in the high bits.
    /// </summary>
    public static Guid NewId()
    {
        Span<byte> bytes = stackalloc byte[16];

        long timestamp;
        ushort seq;

        lock (Lock)
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (timestamp <= _lastTimestamp)
            {
                // Clock regression or same millisecond — increment sequence
                _sequence++;

                if (_sequence > 0x0FFF)
                {
                    // Sequence overflow — wait for the next millisecond
                    while (timestamp <= _lastTimestamp)
                    {
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                    _sequence = 0;
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = timestamp;
            seq = _sequence;
        }

        // Bytes 0-5: 48-bit Unix timestamp in milliseconds (big-endian)
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;

        // Bytes 6-7: version (0111) + 12-bit sequence/rand
        // High nibble of byte 6 = 0x7 (version 7)
        bytes[6] = (byte)(0x70 | ((seq >> 8) & 0x0F));
        bytes[7] = (byte)(seq & 0xFF);

        // Bytes 8-15: variant (10xx) + random
        Rng.Fill(bytes.Slice(8, 8));
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // Set variant to 10xx

        // Convert to Guid — .NET Guid constructor expects a specific byte layout
        // Guid(int, short, short, byte[8]) uses mixed-endian format
        // We need to convert from big-endian UUID to .NET Guid format
        var a = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        var b = (short)((bytes[4] << 8) | bytes[5]);
        var c = (short)((bytes[6] << 8) | bytes[7]);

        return new Guid(a, b, c,
            bytes[8], bytes[9], bytes[10], bytes[11],
            bytes[12], bytes[13], bytes[14], bytes[15]);
    }
}
