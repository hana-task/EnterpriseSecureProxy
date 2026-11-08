using System.Text.Json;

namespace SecureProxy.Api.Validation;

internal static class JsonObjectHelper
{
    /// <summary>
    /// True if the element is a JSON object that later checks can read safely:
    ///
    /// - No field appears twice. JSON allows it; parsers keep one of the values, and different
    ///   systems pick different ones. We reject it, so everyone sees the same request.
    /// - All text is valid. "\uD800" is legal JSON syntax but not a valid character, and reading it
    ///   as a string throws. Checking everything once here means no later check can throw on it.
    /// </summary>
    public static bool IsValidObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    return false;
                ReadAllText(property.Value);
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Reads every string and field name inside the value; throws if one is invalid.</summary>
    private static void ReadAllText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                _ = value.GetString();
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    _ = property.Name;
                    ReadAllText(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ReadAllText(item);
                break;
        }
    }
}
