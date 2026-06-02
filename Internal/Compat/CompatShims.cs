using System.Net;
using System.Security.Cryptography;

namespace SoftAgility.Beacon.Internal.Compat;

/// <summary>
/// HTTP status-code constants that are not present on every target framework's
/// in-box <see cref="HttpStatusCode"/> enum. <see cref="HttpStatusCode.MultiStatus"/>
/// (207) exists on net48 and modern .NET but is absent from the netstandard2.0
/// in-box enum, so the SDK references this constant instead. The numeric value
/// (207) is identical across all TFMs, preserving response-handling parity.
/// </summary>
internal static class HttpStatus
{
    public const HttpStatusCode MultiStatus = (HttpStatusCode)207;
}

/// <summary>
/// Argument-guard helpers used across all target frameworks. Replaces
/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> (added .NET 8), which is
/// unavailable down-level. (R2)
/// </summary>
internal static class Guard
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="value"/> is
    /// null, empty, or whitespace-only. Behaviorally identical to
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> on .NET 8+.
    /// </summary>
    public static void NotNullOrWhiteSpace(string? value, string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (value is null)
                throw new ArgumentNullException(paramName);

            throw new ArgumentException(
                "The value cannot be an empty string or composed entirely of whitespace.",
                paramName);
        }
    }
}

/// <summary>
/// SHA-256 hashing helper that uses the modern static API on .NET 5+ and the
/// instance API down-level. Produces byte-identical output across TFMs. (R8)
/// </summary>
internal static class Hashing
{
    public static byte[] Sha256(byte[] input)
    {
#if NET6_0_OR_GREATER
        return SHA256.HashData(input);
#else
        using var sha = SHA256.Create();
        return sha.ComputeHash(input);
#endif
    }
}

/// <summary>
/// Lowercase hex-encoding helper. Uses <c>Convert.ToHexString</c> (added .NET 5)
/// on modern TFMs and a manual encoder down-level. Both emit lowercase hex with
/// no separators, so the wire/output contract is identical. (R7)
/// </summary>
internal static class Hex
{
    public static string Lower(byte[] bytes)
    {
#if NET6_0_OR_GREATER
        return Convert.ToHexString(bytes).ToLowerInvariant();
#else
        const string hexChars = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = hexChars[bytes[i] >> 4];
            chars[i * 2 + 1] = hexChars[bytes[i] & 0x0F];
        }
        return new string(chars);
#endif
    }
}

/// <summary>
/// Cryptographic RNG fill helper. Uses <c>RandomNumberGenerator.Fill(Span)</c>
/// (netstandard2.1+/.NET Core) on modern TFMs; down-level fills a byte[] via an
/// instance generator and copies into the destination span. (R6)
/// </summary>
internal static class Rng
{
    public static void Fill(Span<byte> destination)
    {
#if NET6_0_OR_GREATER
        RandomNumberGenerator.Fill(destination);
#else
        using var rng = RandomNumberGenerator.Create();
        var buffer = new byte[destination.Length];
        rng.GetBytes(buffer);
        buffer.AsSpan().CopyTo(destination);
#endif
    }
}

/// <summary>
/// <see cref="System.Net.Http.HttpContent"/> read helpers. The
/// <c>ReadAsStringAsync(CancellationToken)</c> overload was added in .NET 5 and
/// is absent down-level; this shim uses the token overload on modern TFMs and
/// honors cancellation manually down-level. (R12)
/// </summary>
internal static class HttpContentCompat
{
    public static
#if NET6_0_OR_GREATER
        Task<string>
#else
        async Task<string>
#endif
        ReadAsStringCompatAsync(
            System.Net.Http.HttpContent content,
            CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        return content.ReadAsStringAsync(cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        var result = await content.ReadAsStringAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
#endif
    }
}
