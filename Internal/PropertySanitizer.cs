using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SoftAgility.Beacon.Internal;

/// <summary>
/// Validates and sanitizes event properties. Enforces: max 20 keys, key max 64 chars,
/// value max 256 chars, no nested objects or arrays. Invalid entries are silently dropped
/// with warnings logged.
/// </summary>
internal static class PropertySanitizer
{
    private const int MaxKeys = 20;
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 256;

    /// <summary>
    /// Sanitizes an object (anonymous type or Dictionary) into a clean dictionary.
    /// Returns null if the input is null or produces an empty result.
    /// </summary>
    public static Dictionary<string, object>? Sanitize(object? properties, ILogger? logger)
    {
        if (properties is null)
            return null;

        var raw = ConvertToRawDictionary(properties);
        if (raw is null || raw.Count == 0)
            return null;

        var result = new Dictionary<string, object>();
        var count = 0;

        foreach (var kvp in raw)
        {
            if (count >= MaxKeys)
            {
                logger?.LogWarning("Beacon: property '{Key}' dropped - exceeded maximum of {Max} keys.", kvp.Key, MaxKeys);
                continue;
            }

            if (kvp.Key.Length > MaxKeyLength)
            {
                logger?.LogWarning("Beacon: property '{Key}' dropped - key exceeds {Max} characters.", kvp.Key, MaxKeyLength);
                continue;
            }

            if (IsNestedValue(kvp.Value))
            {
                logger?.LogWarning("Beacon: property '{Key}' dropped - nested objects and arrays are not allowed.", kvp.Key);
                continue;
            }

            var stringValue = ConvertValueToString(kvp.Value);
            if (stringValue is not null && stringValue.Length > MaxValueLength)
            {
                logger?.LogWarning("Beacon: property '{Key}' dropped - value exceeds {Max} characters.", kvp.Key, MaxValueLength);
                continue;
            }

            result[kvp.Key] = kvp.Value ?? "";
            count++;
        }

        return result.Count > 0 ? result : null;
    }

    private static Dictionary<string, object?>? ConvertToRawDictionary(object properties)
    {
        if (properties is Dictionary<string, object> dict)
            return dict!;

        if (properties is Dictionary<string, object?> nullableDict)
            return nullableDict;

        // Handle anonymous types and other objects via JSON round-trip
        try
        {
            var json = JsonSerializer.Serialize(properties, JsonOptions);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = ConvertJsonElement(prop.Value);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            // Return the JsonElement itself for nested objects/arrays so IsNestedValue can detect them
            JsonValueKind.Object => element.Clone(),
            JsonValueKind.Array => element.Clone(),
            _ => element.ToString()
        };
    }

    private static bool IsNestedValue(object? value)
    {
        if (value is null)
            return false;

        if (value is JsonElement je)
            return je.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

        // Accept primitives, common scalar types, and enums as flat values.
        // These all produce sensible output via ToString() and are consistent
        // with the anonymous-object path (which JSON-serializes first).
        if (value is string or decimal or DateTime or DateTimeOffset or DateOnly
            or TimeOnly or TimeSpan or Guid or Uri)
            return false;

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum)
            return false;

        // Anything else (collections, nested objects) is rejected
        return true;
    }

    private static string? ConvertValueToString(object? value)
    {
        if (value is null)
            return null;

        if (value is string s)
            return s;

        return value.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
