using System.Text.Json;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Reading the dictionary answers, whose parts are all optional: a word with no synonyms has no
/// <c>synsets</c> at all, rather than an empty one.
/// </summary>
internal static class LookupJson
{
    /// <summary>A string property, or null when it is missing, null or blank.</summary>
    public static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>The items of an array property, or none when it is missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    /// <summary>A number property, or null when it is missing or not a number.</summary>
    public static double? OptionalDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
