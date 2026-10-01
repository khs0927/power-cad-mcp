using System.Text.Json.Nodes;
using PowerCad.Core.Harness;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>measure (read-only), offset (verified change) and save (writes the drawing to disk).</summary>
public sealed partial class CommandDispatcher
{
    public const int MaxOffsetCount = 50;

    // ---------------------------------------------------------------- measure
    private JsonObject Measure(ICadTransaction tx, Params p)
    {
        p.AllowOnly("handles", "points");
        if (!p.Has("handles") && !p.Has("points"))
        {
            throw CadException.Invalid("Give 'handles' (entities to measure) and/or 'points' (a path to measure).");
        }

        var entities = new JsonArray();
        var skipped = new JsonArray();
        double totalLength = 0, totalArea = 0;
        foreach (var handle in p.Strings("handles").Take(MaxResults))
        {
            var e = tx.Read(handle);
            var m = MeasureOne(e);
            if (m is null)
            {
                skipped.Add(new JsonObject { ["handle"] = e.Handle, ["type"] = e.Type, ["reason"] = "no length or area" });
                continue;
            }

            totalLength += m["length"]?.GetValue<double>() ?? 0;
            totalArea += m["area"]?.GetValue<double>() ?? 0;
            entities.Add(m);
        }

        var result = new JsonObject
        {
            ["entities"] = entities,
            ["total_length"] = CadJson.Round(totalLength),
            ["total_area"] = CadJson.Round(totalArea),
        };

        if (p.Node["points"] is JsonArray path)
        {
            if (path.Count < 2)
            {
                throw CadException.Invalid("'points' needs at least two points.");
            }

            var pts = path.Select((x, i) => Vec3.FromJson(x, $"points[{i}]")).ToList();
            var legs = new JsonArray();
            var sum = 0.0;
            for (var i = 1; i < pts.Count; i++)
            {
                var d = (pts[i] - pts[i - 1]).Length;
                sum += d;
                legs.Add(CadJson.Round(d));
            }

            result["path"] = new JsonObject { ["legs"] = legs, ["length"] = CadJson.Round(sum) };
        }

        if (skipped.Count > 0)
        {
            result["skipped"] = skipped;
        }

        // Area in m² is what room schedules need; only offered when the drawing unit is known to be mm.
        if (string.Equals(document.Describe()["units"]?.GetValue<string>(), "Millimeters", StringComparison.OrdinalIgnoreCase))
        {
            result["units"] = "mm";
            result["total_area_m2"] = CadJson.Round(totalArea / 1e6);
            foreach (var m in entities)
            {
                if (m?["area"] is { } a)
                {
                    m["area_m2"] = CadJson.Round(a.GetValue<double>() / 1e6);
                }
            }
        }

        return result;
    }

    private static JsonObject? MeasureOne(EntityState e)
    {
        var o = new JsonObject { ["handle"] = e.Handle, ["type"] = e.Type, ["layer"] = e.Layer };
        switch (e.Type)
        {
            case EntityTypes.Line:
                o["length"] = CadJson.Round(((e.Point("end") ?? default) - (e.Point("start") ?? default)).Length);
                return o;
            case EntityTypes.Circle:
                var r = e.Number("radius") ?? 0;
                o["length"] = CadJson.Round(2 * Math.PI * r);
                o["area"] = CadJson.Round(Math.PI * r * r);
                return o;
            case EntityTypes.Arc:
                var ar = e.Number("radius") ?? 0;
                var sweep = ((e.Number("end_angle") ?? 0) - (e.Number("start_angle") ?? 0)) % 360;
                if (sweep <= 0)
                {
                    sweep += 360;
                }

                o["length"] = CadJson.Round(ar * sweep * Math.PI / 180);
                return o;
            case EntityTypes.Polyline:
                var (pts, closed, bulges) = Geometry2D.Polyline(e);
                if (pts.Count < 2)
                {
                    return null;
                }

                o["closed"] = closed;
                o["length"] = CadJson.Round(Geometry2D.PolylineLength(pts, closed, bulges));
                if (closed && pts.Count >= 3)
                {
                    o["area"] = CadJson.Round(Math.Abs(Geometry2D.SignedArea(pts, bulges)));
                    if (!Geometry2D.HasArcs(bulges) && Geometry2D.Centroid(pts) is { } c)
                    {
                        o["centroid"] = c.ToJson();
                    }
                }

                return o;
            case EntityTypes.Hatch when e.Number("area") is { } ha:
                o["area"] = CadJson.Round(ha);
                return o;
            default:
                return null;
        }
    }

    // ----------------------------------------------------------------- offset
    private static void Offset(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles", "distance", "side", "through", "count", "layer");
        var distance = p.Positive("distance");
        var side = p.OptString("side")?.Trim().ToLowerInvariant();
        var through = p.OptPoint("through");
        if ((side is null) == (through is null))
        {
            throw CadException.Invalid("Give exactly one of 'side' (left|right|inside|outside) or 'through' (a point on the wanted side).");
        }

        if (side is not (null or "left" or "right" or "inside" or "outside"))
        {
            throw CadException.Invalid($"Unknown side '{side}'.", "Use left, right (lines/polylines, relative to their direction) or inside, outside (circles, arcs, closed polylines).");
        }

        var count = p.Int("count", 1, 1, MaxOffsetCount);
        var layerOverride = p.OptString("layer");
        if (layerOverride is not null && s.Tx.IsLayerLocked(layerOverride))
        {
            throw new CadException(ErrorCodes.LockedLayer, $"Layer '{layerOverride}' is locked.");
        }

        foreach (var (handle, fingerprint) in Targets(p))
        {
            var source = s.Capture(handle, fingerprint);
            var look = SourceAppearance(source);
            for (var k = 1; k <= count; k++)
            {
                var spec = OffsetSpec(source, distance * k, side, through) with { Appearance = look };
                spec = spec with { Layer = layerOverride ?? source.Layer };
                var created = s.Tx.Create(spec);
                s.RecordCreated(created);
                s.Expect(created, $"offset of {source.Handle} by {JsonFmt(distance * k)}", spec.Matches);
            }
        }
    }

    /// <summary>Copies the source's explicit color/linetype/lineweight so the offset looks the same.</summary>
    private static PropertyEdit SourceAppearance(EntityState e) => new()
    {
        Color = e.Props["color"] is { } c ? Styling.ParseColor(c, "color") : null,
        Linetype = e.Props["linetype"]?.GetValue<string>(),
        Lineweight = e.Props["lineweight"] is { } lw ? Styling.ParseLineweight(lw, "lineweight") : null,
    };

    private static CreateSpec OffsetSpec(EntityState e, double d, string? side, Vec3? through)
    {
        switch (e.Type)
        {
            case EntityTypes.Line:
            {
                var a = e.Point("start")!.Value;
                var b = e.Point("end")!.Value;
                var sign = side switch
                {
                    "left" => 1,
                    "right" => -1,
                    null => Geometry2D.Side(a, b, through!.Value),
                    _ => throw CadException.Invalid($"A LINE has no {side}; use left/right or through."),
                };
                if (sign == 0)
                {
                    throw CadException.Invalid("'through' lies on the line; pick a point on one side.");
                }

                var shift = Geometry2D.OffsetPolyline([a, b], closed: false, sign * d);
                return new CreateSpec(EntityTypes.Line, e.Layer) { A = shift[0], B = shift[1] };
            }

            case EntityTypes.Circle or EntityTypes.Arc:
            {
                var center = e.Point("center")!.Value;
                var r = e.Number("radius") ?? 0;
                var outward = side switch
                {
                    "outside" => true,
                    "inside" => false,
                    null => (through!.Value - center).Length > r,
                    _ => throw CadException.Invalid($"A {e.Type} has no {side}; use inside/outside or through."),
                };
                var nr = outward ? r + d : r - d;
                if (nr <= 1e-9)
                {
                    throw CadException.Invalid($"Offsetting radius {JsonFmt(r)} inward by {JsonFmt(d)} leaves nothing.", "Use a smaller distance.");
                }

                return e.Type == EntityTypes.Circle
                    ? new CreateSpec(EntityTypes.Circle, e.Layer) { A = center, Radius = nr }
                    : new CreateSpec(EntityTypes.Arc, e.Layer)
                    {
                        A = center,
                        Radius = nr,
                        StartAngle = e.Number("start_angle") ?? 0,
                        EndAngle = e.Number("end_angle") ?? 0,
                    };
            }

            case EntityTypes.Polyline:
            {
                var (pts, closed, bulges) = Geometry2D.Polyline(e);
                if (Geometry2D.HasArcs(bulges))
                {
                    throw new CadException(
                        ErrorCodes.Unsupported,
                        $"Polyline {e.Handle} has arc segments; only straight-edged polylines can be offset.",
                        "Offset its straight parts separately, or redraw the arc portion.");
                }

                var ccw = closed && Geometry2D.SignedArea(pts, bulges) > 0;
                int sign;
                if (side is "left" or "right")
                {
                    sign = side == "left" ? 1 : -1;
                }
                else if (!closed)
                {
                    sign = side is null
                        ? NearestSegmentSide(pts, through!.Value)
                        : throw CadException.Invalid("An open polyline has no inside/outside; use left/right or through.");
                }
                else
                {
                    var inward = side is null ? Geometry2D.PointInPolygon(pts, through!.Value) : side == "inside";
                    sign = inward == ccw ? 1 : -1; // CCW loop: its interior is on the left
                }

                if (sign == 0)
                {
                    throw CadException.Invalid("'through' lies on the polyline; pick a point on one side.");
                }

                var result = Geometry2D.OffsetPolyline(pts, closed, sign * d);
                if (!Geometry2D.KeepsEdgeDirections(pts, result, closed))
                {
                    // an edge shrank past zero and flipped: AutoCAD's OFFSET would drop or trim it instead
                    throw CadException.Invalid($"Offsetting polyline {e.Handle} by {JsonFmt(d)} to that side collapses one of its edges.", "Use a smaller distance.");
                }

                return new CreateSpec(EntityTypes.Polyline, e.Layer) { Points = result, Closed = closed };
            }

            default:
                throw new CadException(
                    ErrorCodes.Unsupported,
                    $"Cannot offset {e.Type} {e.Handle}.",
                    "Offset works on LINE, LWPOLYLINE (straight segments), CIRCLE and ARC.");
        }
    }

    private static int NearestSegmentSide(IReadOnlyList<Vec3> pts, Vec3 p)
    {
        var best = double.MaxValue;
        var side = 0;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var ab = pts[i + 1] - a;
            var t = Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / Math.Max(1e-12, (ab.X * ab.X) + (ab.Y * ab.Y)), 0, 1);
            var dist = (p - (a + (ab * t))).Length;
            if (dist < best)
            {
                best = dist;
                side = Geometry2D.Side(a, pts[i + 1], p);
            }
        }

        return side;
    }

    // ------------------------------------------------------------------- save
    private JsonObject Save(Params p)
    {
        p.AllowOnly("mode", "path", "format", "overwrite", "user_confirmed");
        var mode = (p.OptString("mode") ?? "copy").Trim().ToLowerInvariant();
        if (mode is not ("copy" or "save"))
        {
            throw CadException.Invalid("'mode' must be copy (write a copy, the open drawing is untouched) or save (save the open drawing itself).");
        }

        var path = p.OptString("path");
        if (path is not null && !Path.IsPathFullyQualified(path))
        {
            throw CadException.Invalid("'path' must be an absolute path, e.g. C:\\Projects\\plan-copy.dwg.");
        }

        var format = (p.OptString("format") ?? (path is null ? "dwg" : Path.GetExtension(path).TrimStart('.'))).Trim().ToLowerInvariant();
        if (format is not ("dwg" or "dxf"))
        {
            throw CadException.Invalid($"Unsupported format '{format}'.", "Use dwg or dxf (the path's extension must match).");
        }

        if (path is not null && !string.Equals(Path.GetExtension(path).TrimStart('.'), format, StringComparison.OrdinalIgnoreCase))
        {
            throw CadException.Invalid($"The path's extension does not match format '{format}'.");
        }

        if (mode == "copy")
        {
            if (path is null)
            {
                throw CadException.Invalid("'path' is required for a copy.");
            }

            if (File.Exists(path) && !p.Bool("overwrite"))
            {
                throw new CadException(ErrorCodes.InvalidParams, $"'{path}' already exists.", "Choose another path, or pass overwrite=true after the user agreed to replace it.");
            }
        }
        else
        {
            if (_options.ReadOnly)
            {
                throw new CadException(ErrorCodes.ReadOnly, "The server runs in read-only mode; only mode=copy is allowed.");
            }

            if (!p.Bool("user_confirmed"))
            {
                throw new CadException(
                    ErrorCodes.InvalidParams,
                    "Saving over the open drawing needs user_confirmed=true.",
                    "Ask the user first. To keep the original, use mode=copy with a new path.");
            }

            if (format != "dwg")
            {
                throw CadException.Invalid("The open drawing is saved as DWG; export DXF with mode=copy.");
            }

            if (path is not null && File.Exists(path) && !p.Bool("overwrite"))
            {
                throw new CadException(ErrorCodes.InvalidParams, $"'{path}' already exists.", "Pass overwrite=true after the user agreed to replace it.");
            }
        }

        var result = document.Save(new SaveRequest(path, format, mode == "copy"));
        result["mode"] = mode;
        return result;
    }
}
