using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>A validated request to create one entity. Angles are degrees.</summary>
public sealed record CreateSpec(string Type, string? Layer)
{
    private static readonly string[] Common = ["type", "layer", "color", "linetype", "lineweight"];

    public Vec3 A { get; init; }

    public Vec3 B { get; init; }

    /// <summary>Dimension line location (dimension only).</summary>
    public Vec3 C { get; init; }

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

    /// <summary>Text/MText justification (see <see cref="Styling.TextJustify"/>); null = left / top-left.</summary>
    public string? Justify { get; init; }

    /// <summary>Text style (TEXT/MTEXT) or dimension style (DIMENSION); null = current.</summary>
    public string? Style { get; init; }

    public double? WidthFactor { get; init; }

    /// <summary>"rotated" or "aligned" (dimension only).</summary>
    public string DimKind { get; init; } = "rotated";

    /// <summary>Dimension text override; null keeps the measured value.</summary>
    public string? TextOverride { get; init; }

    /// <summary>Hatch pattern name (hatch only).</summary>
    public string Pattern { get; init; } = "SOLID";

    /// <summary>Color / linetype / lineweight for the new entity (Layer is ignored here).</summary>
    public PropertyEdit Appearance { get; init; } = new();

    public static CreateSpec Parse(JsonObject node)
    {
        var p = new Params(node);
        var type = p.String("type").ToLowerInvariant();
        var layer = p.OptString("layer");
        var look = ParseAppearance(p);
        CreateSpec spec;
        switch (type)
        {
            case "line":
                Allow(p, "start", "end");
                var s = p.Point("start");
                var e = p.Point("end");
                spec = s.IsClose(e) ? throw CadException.Invalid("Line start and end must differ.") : new CreateSpec(EntityTypes.Line, layer) { A = s, B = e };
                break;
            case "polyline":
                Allow(p, "points", "closed");
                var points = ParsePoints(node, "points", 2);
                spec = new CreateSpec(EntityTypes.Polyline, layer) { Points = points, Closed = p.Bool("closed") };
                break;
            case "circle":
                Allow(p, "center", "radius");
                spec = new CreateSpec(EntityTypes.Circle, layer) { A = p.Point("center"), Radius = p.Positive("radius") };
                break;
            case "arc":
                Allow(p, "center", "radius", "start_angle", "end_angle");
                spec = new CreateSpec(EntityTypes.Arc, layer)
                {
                    A = p.Point("center"),
                    Radius = p.Positive("radius"),
                    StartAngle = p.Number("start_angle"),
                    EndAngle = p.Number("end_angle"),
                };
                break;
            case "text":
                Allow(p, "text", "position", "height", "rotation", "justify", "style", "width_factor");
                spec = new CreateSpec(EntityTypes.Text, layer)
                {
                    Text = p.String("text"),
                    A = p.Point("position"),
                    Height = p.Has("height") ? p.Positive("height") : 2.5,
                    Rotation = p.OptNumber("rotation") ?? 0,
                    Justify = p.OptString("justify") is { } j ? Styling.ParseJustify(j, mtext: false) : null,
                    Style = p.OptString("style"),
                    WidthFactor = p.Has("width_factor") ? p.Positive("width_factor") : null,
                };
                break;
            case "mtext":
                Allow(p, "text", "position", "height", "width", "justify", "style", "rotation");
                spec = new CreateSpec(EntityTypes.MText, layer)
                {
                    Text = p.String("text"),
                    A = p.Point("position"),
                    Height = p.Has("height") ? p.Positive("height") : 2.5,
                    Width = Math.Max(0, p.OptNumber("width") ?? 0),
                    Rotation = p.OptNumber("rotation") ?? 0,
                    Justify = p.OptString("justify") is { } mj ? Styling.ParseJustify(mj, mtext: true) : null,
                    Style = p.OptString("style"),
                };
                break;
            case "insert":
                Allow(p, "name", "position", "rotation", "scale");
                spec = new CreateSpec(EntityTypes.Insert, layer)
                {
                    BlockName = p.String("name"),
                    A = p.Point("position"),
                    Rotation = p.OptNumber("rotation") ?? 0,
                    Scale = p.Has("scale") ? p.Positive("scale") : 1,
                };
                break;
            case "point":
                Allow(p, "position");
                spec = new CreateSpec(EntityTypes.Point, layer) { A = p.Point("position") };
                break;
            case "dimension":
                spec = ParseDimension(p, layer);
                break;
            case "hatch":
                Allow(p, "points", "pattern", "scale", "angle");
                var loop = ParsePoints(node, "points", 3);
                if (loop.Count > 3 && loop[0].IsClose(loop[^1]))
                {
                    loop = loop.Take(loop.Count - 1).ToList(); // the loop is closed implicitly
                }

                spec = new CreateSpec(EntityTypes.Hatch, layer)
                {
                    Points = loop,
                    Pattern = (p.OptString("pattern") ?? "SOLID").Trim().ToUpperInvariant(),
                    Scale = p.Has("scale") ? p.Positive("scale") : 1,
                    Rotation = p.OptNumber("angle") ?? 0,
                };
                break;
            default:
                throw CadException.Invalid(
                    $"Unknown entity type '{type}'.",
                    "Use line, polyline, circle, arc, text, mtext, insert, point, dimension or hatch.");
        }

        return spec with { Appearance = look };
    }

    private static void Allow(Params p, params string[] names) => p.AllowOnly([.. Common, .. names]);

    private static PropertyEdit ParseAppearance(Params p) => new()
    {
        Color = p.Has("color") ? Styling.ParseColor(p.Node["color"], "color") : null,
        Linetype = p.OptString("linetype"),
        Lineweight = p.Has("lineweight") ? Styling.ParseLineweight(p.Node["lineweight"], "lineweight") : null,
    };

    private static List<Vec3> ParsePoints(JsonObject node, string name, int min)
    {
        if (node[name] is not JsonArray pts || pts.Count < min)
        {
            throw CadException.Invalid($"'{name}' needs at least {min} points.");
        }

        var points = pts.Select((x, i) => Vec3.FromJson(x, $"{name}[{i}]")).ToList();
        if (points.Select(v => v.Z).Distinct().Count() > 1)
        {
            throw CadException.Invalid($"All '{name}' must share one Z (elevation).");
        }

        return points;
    }

    /// <summary>
    /// dimension{kind: rotated|aligned, p1, p2, line_point | offset, rotation?, style?, text?}.
    /// rotation defaults to 0 (horizontal) or 90 (vertical) from the p1→p2 direction; offset places the
    /// dimension line that far from p1–p2 (positive = left of p1→p2 for aligned, +Y/−X side for rotated).
    /// </summary>
    private static CreateSpec ParseDimension(Params p, string? layer)
    {
        Allow(p, "kind", "p1", "p2", "line_point", "offset", "rotation", "style", "text");
        var kind = (p.OptString("kind") ?? "rotated").Trim().ToLowerInvariant();
        if (kind is not ("rotated" or "aligned"))
        {
            throw CadException.Invalid($"Unknown dimension kind '{kind}'.", "Use rotated (linear) or aligned.");
        }

        var a = p.Point("p1");
        var b = p.Point("p2");
        if (a.IsClose(b))
        {
            throw CadException.Invalid("Dimension p1 and p2 must differ.");
        }

        var d = b - a;
        var rotation = kind == "rotated"
            ? p.OptNumber("rotation") ?? (Math.Abs(d.Y) > Math.Abs(d.X) ? 90 : 0)
            : Math.Atan2(d.Y, d.X) * 180 / Math.PI;
        if (kind == "aligned" && p.Has("rotation"))
        {
            throw CadException.Invalid("'rotation' applies to rotated dimensions only.");
        }

        Vec3 linePoint;
        if (p.Has("line_point") == p.Has("offset"))
        {
            throw CadException.Invalid("Give exactly one of 'line_point' or 'offset'.");
        }

        if (p.OptPoint("line_point") is { } lp)
        {
            linePoint = lp;
        }
        else
        {
            var off = p.Number("offset");
            var r = rotation * Math.PI / 180;
            var normal = new Vec3(-Math.Sin(r), Math.Cos(r));
            var mid = (a + b) * 0.5;
            linePoint = mid + (normal * off);
        }

        return new CreateSpec(EntityTypes.Dimension, layer)
        {
            DimKind = kind,
            A = a,
            B = b,
            C = linePoint,
            Rotation = rotation,
            Style = p.OptString("style"),
            TextOverride = p.OptString("text"),
        };
    }

    /// <summary>Postcondition: does the created entity match what was requested?</summary>
    public bool Matches(EntityState s)
    {
        if (s.Type != Type || (Layer is not null && !string.Equals(s.Layer, Layer, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!AppearanceMatches(s, Appearance))
        {
            return false;
        }

        return Type switch
        {
            EntityTypes.Line => Close(s.Point("start"), A) && Close(s.Point("end"), B),
            EntityTypes.Polyline => PointsMatch(s, Points),
            EntityTypes.Circle => Close(s.Point("center"), A) && Near(s.Number("radius"), Radius),
            EntityTypes.Arc => Close(s.Point("center"), A) && Near(s.Number("radius"), Radius)
                && Transform2D.AngleClose(s.Number("start_angle"), StartAngle) && Transform2D.AngleClose(s.Number("end_angle"), EndAngle),
            EntityTypes.Text or EntityTypes.MText => s.Text == Text
                && Close(Justified ? s.Point("alignment_point") ?? s.Point("position") : s.Point("position"), A)
                && Near(s.Number("height"), Height)
                && (Style is null || string.Equals(s.Props["style"]?.GetValue<string>(), Style, StringComparison.OrdinalIgnoreCase))
                && (Justify is null || string.Equals(s.Props["justify"]?.GetValue<string>() ?? DefaultJustify, Justify, StringComparison.OrdinalIgnoreCase)),
            EntityTypes.Insert => string.Equals(s.Props["name"]?.GetValue<string>(), BlockName, StringComparison.OrdinalIgnoreCase)
                && Close(s.Point("position"), A),
            EntityTypes.Point => Close(s.Point("position"), A),
            EntityTypes.Dimension => string.Equals(s.Props["kind"]?.GetValue<string>(), DimKind, StringComparison.Ordinal)
                && Close(s.Point("xline1"), A) && Close(s.Point("xline2"), B)
                && (DimKind != "rotated" || Transform2D.AngleClose(s.Number("rotation"), Rotation))
                && (Style is null || string.Equals(s.Props["style"]?.GetValue<string>(), Style, StringComparison.OrdinalIgnoreCase))
                && (TextOverride is null || s.Props["text_override"]?.GetValue<string>() == TextOverride),
            EntityTypes.Hatch => string.Equals(s.Props["pattern"]?.GetValue<string>(), Pattern, StringComparison.OrdinalIgnoreCase)
                && PointsMatch(s, Points),
            _ => false,
        };
    }

    private bool Justified => Justify is not null && Justify != DefaultJustify;

    private string DefaultJustify => Type == EntityTypes.MText ? "TL" : "left";

    /// <summary>Checks the appearance fields that were requested (null fields are not checked).</summary>
    public static bool AppearanceMatches(EntityState s, PropertyEdit want) =>
        (want.Color is null || Styling.SameColor(s.Props["color"], want.Color))
        && (want.Linetype is null || string.Equals(s.Props["linetype"]?.GetValue<string>() ?? "ByLayer", want.Linetype, StringComparison.OrdinalIgnoreCase))
        && (want.Lineweight is null || JsonNode.DeepEquals(s.Props["lineweight"] ?? "bylayer", Styling.FormatLineweight(want.Lineweight.Value)));

    private static bool PointsMatch(EntityState s, IReadOnlyList<Vec3> points) =>
        s.Props["points"] is JsonArray arr && arr.Count == points.Count
        && arr.Select((x, i) => Vec3.FromJson(x, "p").IsClose(points[i], 1e-4)).All(ok => ok);

    private static bool Close(Vec3? a, Vec3 b) => a is { } v && v.IsClose(b, 1e-4);

    internal static bool Near(double? a, double b) => a is { } v && Math.Abs(v - b) <= 1e-6 * Math.Max(1, Math.Abs(b));
}
