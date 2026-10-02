using System.Globalization;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>
/// Backend-neutral entity/layer appearance values. Colors are normalised to "bylayer", "byblock",
/// an ACI index 1-255, or "#rrggbb" (true color). Lineweights are hundredths of a millimetre, with
/// -1 = ByLayer, -2 = ByBlock, -3 = Default (AutoCAD's own encoding).
/// </summary>
public static class Styling
{
    public const int LineweightByLayer = -1;
    public const int LineweightByBlock = -2;
    public const int LineweightDefault = -3;

    private static readonly Dictionary<string, int> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 1, ["yellow"] = 2, ["green"] = 3, ["cyan"] = 4, ["blue"] = 5, ["magenta"] = 6, ["white"] = 7, ["black"] = 7,
        ["gray"] = 8, ["grey"] = 8, ["lightgray"] = 9, ["lightgrey"] = 9,
    };

    /// <summary>AutoCAD's standard lineweights (hundredths of a mm).</summary>
    public static readonly int[] StandardLineweights = [0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211];

    public static JsonNode ParseColor(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonValue v when CadJson.TryNumber(v, out var d):
                return AciOrSpecial(d, name);
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Trim();
                if (s.Equals("bylayer", StringComparison.OrdinalIgnoreCase) || s.Equals("byblock", StringComparison.OrdinalIgnoreCase))
                {
                    return s.ToLowerInvariant();
                }

                if (ColorNames.TryGetValue(s, out var aci))
                {
                    return aci;
                }

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    return AciOrSpecial(n, name);
                }

                if (s.StartsWith('#') && s.Length == 7 && int.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    return s.ToLowerInvariant();
                }

                break;
            case JsonArray { Count: 3 } rgb:
                var c = rgb.Select(x => CadJson.TryNumber(x, out var cd) && cd is >= 0 and <= 255 && cd == Math.Floor(cd) ? (int)cd : -1).ToArray();
                if (c.All(x => x >= 0))
                {
                    return $"#{c[0]:x2}{c[1]:x2}{c[2]:x2}";
                }

                break;
        }

        throw CadException.Invalid(
            $"'{name}' is not a color.",
            "Use an ACI index 1-255, 'bylayer', 'byblock', a name (red, yellow, green, cyan, blue, magenta, white, gray), '#rrggbb' or [r,g,b].");
    }

    private static JsonNode AciOrSpecial(double d, string name)
    {
        if (d != Math.Floor(d) || d is < 0 or > 256)
        {
            throw CadException.Invalid($"'{name}' ACI index must be an integer 0-256.");
        }

        return d switch
        {
            0 => "byblock",
            256 => "bylayer",
            _ => (int)d,
        };
    }

    /// <summary>Accepts a standard lineweight in millimetres (0.25 or "0.25mm") or bylayer/byblock/default.</summary>
    public static int ParseLineweight(JsonNode? node, string name)
    {
        double? mm = null;
        switch (node)
        {
            case JsonValue v when CadJson.TryNumber(v, out var d):
                mm = d;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Trim().ToLowerInvariant();
                switch (s)
                {
                    case "bylayer":
                        return LineweightByLayer;
                    case "byblock":
                        return LineweightByBlock;
                    case "default":
                        return LineweightDefault;
                }

                if (double.TryParse(s.Replace("mm", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    mm = p;
                }

                break;
        }

        if (mm is { } m && m >= 0)
        {
            var hundredths = (int)Math.Round(m * 100);
            if (StandardLineweights.Contains(hundredths))
            {
                return hundredths;
            }

            throw CadException.Invalid(
                $"'{name}' {m} mm is not a standard lineweight.",
                "Use one of: " + string.Join(", ", StandardLineweights.Select(x => (x / 100.0).ToString("0.00", CultureInfo.InvariantCulture))) + " (mm), or bylayer/byblock/default.");
        }

        throw CadException.Invalid($"'{name}' must be a lineweight in mm (e.g. 0.25) or bylayer/byblock/default.");
    }

    public static JsonNode FormatLineweight(int lw) => lw switch
    {
        LineweightByLayer => "bylayer",
        LineweightByBlock => "byblock",
        LineweightDefault => "default",
        _ => CadJson.Round(lw / 100.0),
    };

    public static bool SameColor(JsonNode? a, JsonNode? b) =>
        JsonNode.DeepEquals(a ?? "bylayer", b ?? "bylayer");

    // -------------------------------------------------------------- text justification
    /// <summary>Single-line TEXT justifications (AutoCAD names). MTEXT accepts the nine TL..BR codes.</summary>
    public static readonly string[] TextJustify = ["left", "center", "right", "middle", "TL", "TC", "TR", "ML", "MC", "MR", "BL", "BC", "BR"];

    public static readonly string[] MTextJustify = ["TL", "TC", "TR", "ML", "MC", "MR", "BL", "BC", "BR"];

    public static string ParseJustify(string value, bool mtext)
    {
        var allowed = mtext ? MTextJustify : TextJustify;
        var hit = allowed.FirstOrDefault(j => string.Equals(j, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit ?? throw CadException.Invalid(
            $"Unknown justification '{value}'.",
            $"Use one of: {string.Join(", ", allowed)}{(mtext ? "" : " (middle = AutoCAD 'Middle', MC = 'Middle Center')")}.");
    }
}

/// <summary>Changes to an entity's appearance or text formatting. Null means "leave as is".</summary>
public sealed class PropertyEdit
{
    public string? Layer { get; set; }

    public JsonNode? Color { get; set; }

    public string? Linetype { get; set; }

    public double? LinetypeScale { get; set; }

    public int? Lineweight { get; set; }

    public double? Height { get; set; }

    public double? Rotation { get; set; }

    public string? Style { get; set; }

    public string? Justify { get; set; }

    public double? WidthFactor { get; set; }

    public bool IsEmpty => Layer is null && Color is null && Linetype is null && LinetypeScale is null && Lineweight is null
        && Height is null && Rotation is null && Style is null && Justify is null && WidthFactor is null;

    public bool TouchesText => Height is not null || Rotation is not null || Style is not null || Justify is not null || WidthFactor is not null;
}

/// <summary>Create or change a layer. Null means "leave as is" (or the AutoCAD default for a new layer).</summary>
public sealed class LayerEdit
{
    public required string Name { get; init; }

    public JsonNode? Color { get; set; }

    public string? Linetype { get; set; }

    public int? Lineweight { get; set; }

    public bool? On { get; set; }

    public bool? Frozen { get; set; }

    public bool? Locked { get; set; }

    public bool? Plot { get; set; }

    public string? Description { get; set; }

    public bool MakeCurrent { get; set; }

    public bool CreateIfMissing { get; set; } = true;
}

/// <summary>A 2D similarity/mirror transform: rotate about a base point, uniform scale, or mirror across a line.</summary>
public sealed record Transform2D(string Op, Vec3 Base, double AngleDegrees = 0, double Factor = 1, Vec3 AxisEnd = default)
{
    public Vec3 Apply(Vec3 p)
    {
        switch (Op)
        {
            case "rotate":
                var r = AngleDegrees * Math.PI / 180;
                var (c, s) = (Math.Cos(r), Math.Sin(r));
                var d = p - Base;
                return new Vec3(Base.X + (d.X * c) - (d.Y * s), Base.Y + (d.X * s) + (d.Y * c), p.Z);
            case "scale":
                return Base + ((p - Base) * Factor);
            case "mirror":
                var u = AxisEnd - Base;
                var len2 = (u.X * u.X) + (u.Y * u.Y);
                var w = p - Base;
                var t = ((w.X * u.X) + (w.Y * u.Y)) / len2;
                var foot = new Vec3(Base.X + (u.X * t), Base.Y + (u.Y * t), p.Z);
                return new Vec3((2 * foot.X) - p.X, (2 * foot.Y) - p.Y, p.Z);
            default:
                throw CadException.Invalid($"Unknown transform '{Op}'.");
        }
    }

    /// <summary>Angle of the mirror axis in degrees (mirror only).</summary>
    public double AxisAngleDegrees => Math.Atan2(AxisEnd.Y - Base.Y, AxisEnd.X - Base.X) * 180 / Math.PI;

    public static bool AngleClose(double? actual, double expected, double tol = 1e-6)
    {
        if (actual is not { } a)
        {
            return false;
        }

        var diff = Math.Abs(((a - expected) % 360 + 540) % 360 - 180);
        return diff <= tol;
    }
}
