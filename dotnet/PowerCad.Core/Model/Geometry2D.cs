using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>
/// Plane geometry on entity snapshots: lengths, areas and offsets. Shared by the AutoCAD plugin and the
/// simulator so measurements and offset postconditions are computed the same way on both backends.
/// </summary>
public static class Geometry2D
{
    private const double Eps = 1e-9;

    /// <summary>Polyline vertices, closed flag and per-vertex bulges (0 when the entity reports none).</summary>
    public static (List<Vec3> Points, bool Closed, double[] Bulges) Polyline(EntityState e)
    {
        var pts = e.Props["points"] is JsonArray arr ? arr.Select((x, i) => Vec3.FromJson(x, $"points[{i}]")).ToList() : [];
        var closed = e.Props["closed"] is JsonValue c && c.TryGetValue<bool>(out var b) && b;
        var bulges = new double[pts.Count];
        if (e.Props["bulges"] is JsonArray bl)
        {
            for (var i = 0; i < Math.Min(bl.Count, pts.Count); i++)
            {
                bulges[i] = CadJson.TryNumber(bl[i], out var d) ? d : 0;
            }
        }

        return (pts, closed, bulges);
    }

    public static bool HasArcs(double[] bulges) => bulges.Any(b => Math.Abs(b) > Eps);

    /// <summary>Length of one polyline segment; a non-zero bulge makes it a circular arc.</summary>
    public static double SegmentLength(Vec3 a, Vec3 b, double bulge)
    {
        var chord = (b - a).Length;
        if (Math.Abs(bulge) <= Eps || chord <= Eps)
        {
            return chord;
        }

        var theta = 4 * Math.Atan(Math.Abs(bulge));
        var r = chord / (2 * Math.Sin(theta / 2));
        return theta * r;
    }

    /// <summary>Signed area between a bulged segment's arc and its chord (positive bulge = CCW arc adds area to a CCW loop).</summary>
    private static double BulgeSegmentArea(Vec3 a, Vec3 b, double bulge)
    {
        var chord = (b - a).Length;
        if (Math.Abs(bulge) <= Eps || chord <= Eps)
        {
            return 0;
        }

        var theta = 4 * Math.Atan(Math.Abs(bulge));
        var r = chord / (2 * Math.Sin(theta / 2));
        return Math.Sign(bulge) * r * r / 2 * (theta - Math.Sin(theta));
    }

    public static double PolylineLength(IReadOnlyList<Vec3> pts, bool closed, double[] bulges)
    {
        var n = pts.Count;
        var total = 0.0;
        for (var i = 0; i < (closed ? n : n - 1); i++)
        {
            total += SegmentLength(pts[i], pts[(i + 1) % n], bulges[i]);
        }

        return total;
    }

    /// <summary>Signed area of a closed loop (counter-clockwise positive), arcs included.</summary>
    public static double SignedArea(IReadOnlyList<Vec3> pts, double[] bulges)
    {
        var n = pts.Count;
        var area = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            area += ((a.X * b.Y) - (b.X * a.Y)) / 2;
            area += BulgeSegmentArea(a, b, bulges[i]);
        }

        return area;
    }

    /// <summary>Area centroid of a straight-edged closed loop, or null when degenerate.</summary>
    public static Vec3? Centroid(IReadOnlyList<Vec3> pts)
    {
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            var cross = (p.X * q.Y) - (q.X * p.Y);
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }

        return Math.Abs(a) <= Eps ? null : new Vec3(cx / (3 * a), cy / (3 * a), pts[0].Z);
    }

    public static bool PointInPolygon(IReadOnlyList<Vec3> poly, Vec3 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>+1 when p is left of a→b, -1 when right, 0 when on the line.</summary>
    public static int Side(Vec3 a, Vec3 b, Vec3 p)
    {
        var cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
        return Math.Abs(cross) <= Eps ? 0 : Math.Sign(cross);
    }

    private static Vec3 LeftNormal(Vec3 a, Vec3 b)
    {
        var d = b - a;
        var len = d.Length;
        return new Vec3(-d.Y / len, d.X / len);
    }

    /// <summary>
    /// Offsets a straight-edged polyline by <paramref name="distance"/> to the left (positive) or right
    /// (negative) of its vertex order. Adjacent offset segments are joined at their intersection (mitre).
    /// </summary>
    public static List<Vec3> OffsetPolyline(IReadOnlyList<Vec3> raw, bool closed, double distance)
    {
        var pts = new List<Vec3>();
        foreach (var p in raw)
        {
            if (pts.Count == 0 || !pts[^1].IsClose(p))
            {
                pts.Add(p);
            }
        }

        if (closed && pts.Count > 1 && pts[0].IsClose(pts[^1]))
        {
            pts.RemoveAt(pts.Count - 1);
        }

        var n = pts.Count;
        if (n < 2)
        {
            throw CadException.Invalid("The polyline has fewer than two distinct vertices.");
        }

        var segCount = closed ? n : n - 1;
        var lines = new (Vec3 A, Vec3 B)[segCount];
        for (var i = 0; i < segCount; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            var shift = LeftNormal(a, b) * distance;
            lines[i] = (a + shift, b + shift);
        }

        var result = new List<Vec3>(n);
        for (var i = 0; i < n; i++)
        {
            if (!closed && i == 0)
            {
                result.Add(lines[0].A);
                continue;
            }

            if (!closed && i == n - 1)
            {
                result.Add(lines[segCount - 1].B);
                continue;
            }

            var prev = lines[(i - 1 + segCount) % segCount];
            var next = lines[i % segCount];
            result.Add(Intersect(prev.A, prev.B, next.A, next.B) ?? next.A);
        }

        return result;
    }

    /// <summary>
    /// True when every edge of <paramref name="offset"/> still points the same way as the matching edge of
    /// <paramref name="original"/> (both de-duplicated the same way), i.e. no edge collapsed and flipped.
    /// </summary>
    public static bool KeepsEdgeDirections(IReadOnlyList<Vec3> original, IReadOnlyList<Vec3> offset, bool closed)
    {
        var src = new List<Vec3>();
        foreach (var p in original)
        {
            if (src.Count == 0 || !src[^1].IsClose(p))
            {
                src.Add(p);
            }
        }

        if (closed && src.Count > 1 && src[0].IsClose(src[^1]))
        {
            src.RemoveAt(src.Count - 1);
        }

        if (src.Count != offset.Count)
        {
            return false;
        }

        var n = src.Count;
        for (var i = 0; i < (closed ? n : n - 1); i++)
        {
            var a = src[(i + 1) % n] - src[i];
            var b = offset[(i + 1) % n] - offset[i];
            if ((a.X * b.X) + (a.Y * b.Y) <= Eps)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Intersection of two infinite lines, or null when they are parallel.</summary>
    public static Vec3? Intersect(Vec3 a1, Vec3 a2, Vec3 b1, Vec3 b2)
    {
        var d1 = a2 - a1;
        var d2 = b2 - b1;
        var den = (d1.X * d2.Y) - (d1.Y * d2.X);
        if (Math.Abs(den) <= Eps * Math.Max(1, d1.Length * d2.Length))
        {
            return null;
        }

        var t = (((b1.X - a1.X) * d2.Y) - ((b1.Y - a1.Y) * d2.X)) / den;
        return a1 + (d1 * t);
    }
}
