using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SoftAgility.Beacon.Tests.Helpers;

/// <summary>
/// Down-level-safe replacements for test-only APIs that are unavailable on
/// net48 / netstandard2.0. These keep the existing test behavior identical
/// while letting the suite compile and run on every target framework.
///
/// They intentionally do NOT reuse the SDK's <c>Internal/Compat</c> shims:
/// those are production helpers and these (a 5s timeout waiter, a hash+hex
/// pair, a stream-write convenience) are test-only fixtures with no production
/// analogue, so duplicating them here is correct rather than forking a
/// production helper.
/// </summary>
internal static class CompatTestHelpers
{
    /// <summary>
    /// Test-only replacement for <c>Task.WaitAsync(TimeSpan)</c> (added .NET 6).
    /// Throws <see cref="TimeoutException"/> if the task does not complete within
    /// <paramref name="timeout"/> — matching the .NET 6 overload's contract closely
    /// enough for these tests (which only ever treat a timeout as a failure).
    ///
    /// On net6.0/net8.0 the built-in instance method wins (extension methods lose to
    /// instance methods), so this overload is only used down-level on net48.
    /// </summary>
    public static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
            throw new TimeoutException($"Task did not complete within {timeout}.");

        // Surface any exception / cancellation from the awaited task.
        await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Test-only replacement for <c>Task&lt;T&gt;.WaitAsync(TimeSpan)</c> (added .NET 6).
    /// </summary>
    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
            throw new TimeoutException($"Task did not complete within {timeout}.");

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Test-only replacement for <c>Task&lt;T&gt;.WaitAsync(CancellationToken)</c> (added .NET 6).
    /// Throws <see cref="OperationCanceledException"/> when the token fires before the
    /// task completes — matching the overload the tests rely on.
    /// </summary>
    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs))
        {
            var completed = await Task.WhenAny(task, cancelTcs.Task).ConfigureAwait(false);
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Test-only convenience for <c>Stream.WriteAsync(byte[])</c> single-arg (the
    /// <c>WriteAsync(ReadOnlyMemory&lt;byte&gt;)</c> overload, netstandard2.1+, absent
    /// on net48). On net6.0/net8.0 the built-in instance overload binds first; on net48
    /// this extension provides the equivalent <c>WriteAsync(buffer, 0, length)</c>.
    /// </summary>
    public static Task WriteAsync(this Stream stream, byte[] buffer)
        => stream.WriteAsync(buffer, 0, buffer.Length);

    /// <summary>
    /// Test-only replacement for <c>SHA256.HashData(byte[])</c> (static, added .NET 5)
    /// followed by <c>Convert.ToHexString(...).ToLowerInvariant()</c> (added .NET 5).
    /// Produces the same lowercase hex string on every TFM, so test expectations are
    /// byte-identical to the production output of <c>Internal/Compat</c>'s Hashing/Hex.
    /// </summary>
    public static string Sha256LowerHex(byte[] input)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(input);
        }

        const string hexChars = "0123456789abcdef";
        var chars = new char[hash.Length * 2];
        for (var i = 0; i < hash.Length; i++)
        {
            chars[i * 2] = hexChars[hash[i] >> 4];
            chars[i * 2 + 1] = hexChars[hash[i] & 0x0F];
        }

        return new string(chars);
    }
}
