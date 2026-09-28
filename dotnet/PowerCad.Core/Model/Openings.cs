using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>Shared rules for doors, windows and other opening blocks.</summary>
public static class Openings
{
    /// <summary>Dynamic-block parameter names treated as the opening width, in priority order.</summary>
    public static readonly IReadOnlyList<string> DefaultWidthPropertyNames =
        ["Width", "Door Width", "DoorWidth", "Opening Width", "Distance", "Distance1", "W", "폭", "너비", "문폭"];

    /// <summary>
    /// Effective opening width of a block reference: the dynamic width parameter when present,
    /// otherwise the definition width times |X scale|. Null when neither is known.
    /// </summary>
    public static double? EffectiveWidth(JsonObject? dynamic, double? definitionWidth, double scaleX)
    {
        if (dynamic is not null)
        {
            foreach (var name in DefaultWidthPropertyNames)
            {
                var hit = dynamic.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
                if (hit.Value is JsonValue v && v.TryGetValue<double>(out var d))
                {
                    return d;
                }
            }
        }

        return definitionWidth is > 0 ? definitionWidth.Value * Math.Abs(scaleX) : null;
    }
}
