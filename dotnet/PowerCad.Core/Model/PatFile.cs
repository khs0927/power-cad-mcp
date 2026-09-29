using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>
/// Rebuilds an AutoCAD .pat definition from a hatch's evaluated pattern lines. The hatch stores each line
/// in drawing units, already rotated by the hatch angle and multiplied by its scale; a .pat line is
/// "angle, x-origin, y-origin, delta-x (along the line), delta-y (across it) [, dash...]" at scale 1, angle 0.
/// </summary>
public static class PatFile
{
    /// <param name="lines">Each: angle (degrees, world), base [x,y], offset [x,y] (world vector), dashes [..].</param>
    public static string Build(string name, string? description, double scale, double angleDeg, JsonArray lines)
    {
        if (scale <= 0)
        {
            throw CadException.Invalid("Hatch scale must be positive.");
        }

        var inv = CultureInfo.InvariantCulture;
        var phi = angleDeg * Math.PI / 180;
        var sb = new StringBuilder();
        sb.Append('*').Append(name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(", ").Append(description.Replace('\n', ' '));
        }

        sb.Append("\r\n");
        foreach (var node in lines)
        {
            var line = node!.AsObject();
            var thetaWorld = line["angle"]!.GetValue<double>() * Math.PI / 180;
            var bx = line["base"]![0]!.GetValue<double>();
            var by = line["base"]![1]!.GetValue<double>();
            var ox = line["offset"]![0]!.GetValue<double>();
            var oy = line["offset"]![1]!.GetValue<double>();

            // undo the hatch rotation for the origin, then the scale
            var x = ((bx * Math.Cos(-phi)) - (by * Math.Sin(-phi))) / scale;
            var y = ((bx * Math.Sin(-phi)) + (by * Math.Cos(-phi))) / scale;

            // the offset is expressed in the line's own frame (along, across)
            var dx = ((ox * Math.Cos(thetaWorld)) + (oy * Math.Sin(thetaWorld))) / scale;
            var dy = ((-ox * Math.Sin(thetaWorld)) + (oy * Math.Cos(thetaWorld))) / scale;
            var theta = Normalize((thetaWorld - phi) * 180 / Math.PI);

            var parts = new List<double> { theta, x, y, dx, dy };
            if (line["dashes"] is JsonArray dashes)
            {
                parts.AddRange(dashes.Select(d => d!.GetValue<double>() / scale));
            }

            sb.AppendJoin(", ", parts.Select(v => Fmt(v, inv))).Append("\r\n");
        }

        return sb.ToString();
    }

    private static double Normalize(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    private static string Fmt(double v, IFormatProvider inv)
    {
        var r = Math.Round(v, 6);
        return (Math.Abs(r) < 1e-9 ? 0 : r).ToString("0.######", inv);
    }
}
