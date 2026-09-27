using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>A validated request to create one entity. Angles are degrees.</summary>
public sealed record CreateSpec(string Type, string? Layer)
{
    public Vec3 A { get; init; }

    public Vec3 B { get; init; }

    public IReadOnlyList<Vec3> Points { get; init; } = [];

    public bool Closed { get; init; }

    public double Radius { get; init; }

    public double StartAngle { get; init; }

    public double EndAngle { get; init; }

    public string Text { get; init; } = "";

    public double Height { get; init; }

    public double Width { get; init; }

    public double Rotation { get; init; }

    public double Scale { get; init; } = 1;

    public string BlockName { get; init; } = "";

    public static CreateSpec Parse(JsonObject node)
    {
        var p = new Params(node);
        var type = p.String("type").ToLowerInvariant();
        var layer = p.OptString("layer");
        switch (type)
        {
            case "line":
                p.AllowOnly("type", "layer", "start", "end");
                var s = p.Point("start");
                var e = p.Point("end");
                return s.IsClose(e) ? throw CadException.Invalid("Line start and end must differ.") : new CreateSpec(EntityTypes.Line, layer) { A = s, B = e };
            case "polyline":
                p.AllowOnly("type", "layer", "points", "closed");
                if (node["points"] is not JsonArray pts || pts.Count < 2)
                {
                    throw CadException.Invalid("'points' needs at least 2 points.");
                }

                var points = pts.Select((x, i) => Vec3.FromJson(x, $"points[{i}]")).ToList();
                if (points.Select(v => v.Z).Distinct().Count() > 1)
                {
                    throw CadException.Invalid("All polyline points must share one Z (elevation).");
                }

                return new CreateSpec(EntityTypes.Polyline, layer) { Points = points, Closed = p.Bool("closed") };
            case "circle":
                p.AllowOnly("type", "layer", "center", "radius");
                return new CreateSpec(EntityTypes.Circle, layer) { A = p.Point("center"), Radius = p.Positive("radius") };
            case "arc":
                p.AllowOnly("type", "layer", "center", "radius", "start_angle", "end_angle");
                return new CreateSpec(EntityTypes.Arc, layer)
                {
                    A = p.Point("center"),
                    Radius = p.Positive("radius"),
                    StartAngle = p.Number("start_angle"),
                    EndAngle = p.Number("end_angle"),
                };
            case "text":
                p.AllowOnly("type", "layer", "text", "position", "height", "rotation");
                return new CreateSpec(EntityTypes.Text, layer)
                {
                    Text = p.String("text"),
                    A = p.Point("position"),
                    Height = p.Has("height") ? p.Positive("height") : 2.5,
                    Rotation = p.OptNumber("rotation") ?? 0,
                };
            case "mtext":
                p.AllowOnly("type", "layer", "text", "position", "height", "width");
                return new CreateSpec(EntityTypes.MText, layer)
                {
                    Text = p.String("text"),
                    A = p.Point("position"),
                    Height = p.Has("height") ? p.Positive("height") : 2.5,
                    Width = Math.Max(0, p.OptNumber("width") ?? 0),
                };
            case "insert":
                p.AllowOnly("type", "layer", "name", "position", "rotation", "scale");
                return new CreateSpec(EntityTypes.Insert, layer)
                {
                    BlockName = p.String("name"),
                    A = p.Point("position"),
                    Rotation = p.OptNumber("rotation") ?? 0,
                    Scale = p.Has("scale") ? p.Positive("scale") : 1,
                };
            default:
                throw CadException.Invalid(
                    $"Unknown entity type '{type}'.",
                    "Use line, polyline, circle, arc, text, mtext or insert.");
        }
    }

    /// <summary>Postcondition: does the created entity match what was requested?</summary>
    public bool Matches(EntityState s)
    {
        if (s.Type != Type || (Layer is not null && !string.Equals(s.Layer, Layer, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Type switch
        {
            EntityTypes.Line => Close(s.Point("start"), A) && Close(s.Point("end"), B),
            EntityTypes.Polyline => s.Props["points"] is JsonArray arr && arr.Count == Points.Count
                && arr.Select((x, i) => Vec3.FromJson(x, "p").IsClose(Points[i])).All(ok => ok),
            EntityTypes.Circle => Close(s.Point("center"), A) && Near(s.Number("radius"), Radius),
            EntityTypes.Arc => Close(s.Point("center"), A) && Near(s.Number("radius"), Radius),
            EntityTypes.Text or EntityTypes.MText => s.Text == Text && Close(s.Point("position"), A),
            EntityTypes.Insert => string.Equals(s.Props["name"]?.GetValue<string>(), BlockName, StringComparison.OrdinalIgnoreCase)
                && Close(s.Point("position"), A),
            _ => false,
        };
    }

    private static bool Close(Vec3? a, Vec3 b) => a is { } v && v.IsClose(b);

    internal static bool Near(double? a, double b) => a is { } v && Math.Abs(v - b) <= 1e-6 * Math.Max(1, Math.Abs(b));
}
