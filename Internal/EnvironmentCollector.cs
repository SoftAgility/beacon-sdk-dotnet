using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftAgility.Beacon.Internal.Compat;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Collects system environment information and serializes it as a JSON string
/// for the X-Environment-Data header. Display dimensions are only available
/// on Windows when the net8.0-windows TFM is active.
/// </summary>
internal static class EnvironmentCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Collects environment data and returns it as a UTF-8 JSON string.
    /// </summary>
    public static string CollectJson()
    {
        var data = new EnvironmentData
        {
            OsName = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeName = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(),
            MachineNameHash = ComputeMachineNameHash(),
            TotalRamMbBucket = ComputeRamBucket(),
            CpuCoreCount = Environment.ProcessorCount,
            Locale = CultureInfo.CurrentCulture.Name
        };

        CollectDisplayDimensions(data);

        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>
    /// Returns the environment JSON as a Base64-encoded string for the HTTP header.
    /// </summary>
    public static string CollectBase64()
    {
        var json = CollectJson();
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }

    private static string ComputeMachineNameHash()
    {
        var nameBytes = Encoding.UTF8.GetBytes(Environment.MachineName);
        var hashBytes = Hashing.Sha256(nameBytes);
        return Hex.Lower(hashBytes);
    }

    private static string ComputeRamBucket()
    {
        try
        {
#if NET6_0_OR_GREATER
            var memoryInfo = GC.GetGCMemoryInfo();
            var totalBytes = memoryInfo.TotalAvailableMemoryBytes;
            var totalMb = totalBytes / (1024.0 * 1024.0);

            return totalMb switch
            {
                < 2048 => "< 2 GB",
                < 4096 => "2-4 GB",
                < 8192 => "4-8 GB",
                < 16384 => "8-16 GB",
                < 32768 => "16-32 GB",
                _ => "> 32 GB"
            };
#else
            // GC.GetGCMemoryInfo() (added .NET Core 3.0) is unavailable on
            // netstandard2.0 / .NET Framework. Per PRD Q-2, return "unknown"
            // on the down-level path (no P/Invoke for v1).
            return "unknown";
#endif
        }
        catch
        {
            return "unknown";
        }
    }

    private static void CollectDisplayDimensions(EnvironmentData data)
    {
#if WINDOWS
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                data.DisplayWidth = screen.Bounds.Width;
                data.DisplayHeight = screen.Bounds.Height;
            }
        }
        catch
        {
            // Display info unavailable — leave as null (omitted from JSON)
        }
#endif
    }

    private sealed class EnvironmentData
    {
        public string OsName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string OsArchitecture { get; set; } = string.Empty;
        public string RuntimeName { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string MachineNameHash { get; set; } = string.Empty;
        public string TotalRamMbBucket { get; set; } = string.Empty;
        public int CpuCoreCount { get; set; }
        public int? DisplayWidth { get; set; }
        public int? DisplayHeight { get; set; }
        public string Locale { get; set; } = string.Empty;
    }
}
