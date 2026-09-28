using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>
/// Backend-neutral snapshot of one model-space entity. <see cref="Props"/> holds type-specific geometry
/// (position/start/center/points, text, block name, scale, dynamic properties, attributes ...).
/// The fingerprint changes whenever anything an agent could have reasoned about changes, so a stale
/// analysis is detected before an edit is applied (harness-style precondition).
/// </summary>
public sealed record EntityState(string Handle, string Type, string Layer, JsonObject Props)
{
    public string Fingerprint => CadJson.Hash(new JsonObject
    {
        ["type"] = Type,
        ["layer"] = Layer,
        ["props"] = Props.DeepClone(),
    });

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["handle"] = Handle,
            ["type"] = Type,
            ["layer"] = Layer,
            ["fingerprint"] = Fingerprint,
        };
        foreach (var (k, v) in Props)
        {
            o[k] = v?.DeepClone();
        }

        return o;
    }

    public string? Text => Props["text"]?.GetValue<string>();

    public Vec3? Point(string key) => Props[key] is JsonArray ? Vec3.FromJson(Props[key], key) : null;

    public double? Number(string key) => CadJson.TryNumber(Props[key], out var d) ? d : null;

    /// <summary>The point that moves one-to-one with a translation (used to verify moves).</summary>
    public Vec3? Anchor => Type switch
    {
        EntityTypes.Line => Point("start"),
        EntityTypes.Circle or EntityTypes.Arc => Point("center"),
        EntityTypes.Polyline or EntityTypes.Hatch => Props["points"] is JsonArray { Count: > 0 } pts ? Vec3.FromJson(pts[0], "points[0]") : null,
        EntityTypes.Dimension => Point("xline1"),
        EntityTypes.Text or EntityTypes.MText => Point("alignment_point") ?? Point("position"),
        _ => Point("position"),
    };
}

public static class EntityTypes
{
    public const string Line = "LINE";
    public const string Polyline = "LWPOLYLINE";
    public const string Circle = "CIRCLE";
    public const string Arc = "ARC";
    public const string Text = "TEXT";
    public const string MText = "MTEXT";
    public const string Insert = "INSERT";
    public const string Point = "POINT";
    public const string Dimension = "DIMENSION";
    public const string Hatch = "HATCH";

    public static readonly string[] TextLike = [Text, MText];
}
