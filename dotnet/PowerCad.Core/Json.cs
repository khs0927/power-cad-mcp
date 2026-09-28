using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerCad.Core;

public static class CadJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Rounds numbers to 1e-6 and sorts object keys so equal geometry hashes equally.</summary>
    public static JsonNode? Canonical(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Canonical).ToArray()),
        JsonValue v when v.TryGetValue<double>(out var d) => JsonValue.Create(Round(d)),
        _ => node.DeepClone(),
    };

    public static double Round(double d)
    {
        var r = Math.Round(d, 6, MidpointRounding.AwayFromZero);
        return r == 0 ? 0 : r; // normalise -0
    }

    public static string Hash(JsonNode? node)
    {
        var text = Canonical(node)?.ToJsonString(Compact) ?? "null";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>Reads any JSON number (int/long/double, parsed or constructed) as a double.</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (v.TryGetValue(out double d))
        {
            value = d;
            return true;
        }

        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static string Format(double d) => Round(d).ToString("R", CultureInfo.InvariantCulture);
}
