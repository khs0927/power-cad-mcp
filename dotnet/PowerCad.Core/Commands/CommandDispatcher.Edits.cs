using System.Text.Json.Nodes;
using PowerCad.Core.Harness;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>Delete / properties / copy / transform / layers / drawing resources / view.</summary>
public sealed partial class CommandDispatcher
{
    public const int MaxSnapshotSize = 4000;

    // ------------------------------------------------------------------ delete
    private static void Delete(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles");
        foreach (var (handle, fingerprint) in Targets(p))
        {
            var before = s.Capture(handle, fingerprint);
            s.Tx.Delete(before.Handle);
            s.RecordDeleted(before.Handle);
        }
    }

    // ---------------------------------------------------------- set_properties
    private static void SetProperties(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles", "layer", "color", "linetype", "linetype_scale", "lineweight", "height", "rotation", "style", "justify", "width_factor");
        var edit = new PropertyEdit
        {
            Layer = p.OptString("layer"),
            Color = p.Has("color") ? Styling.ParseColor(p.Node["color"], "color") : null,
            Linetype = p.OptString("linetype"),
            LinetypeScale = p.Has("linetype_scale") ? p.Positive("linetype_scale") : null,
            Lineweight = p.Has("lineweight") ? Styling.ParseLineweight(p.Node["lineweight"], "lineweight") : null,
            Height = p.Has("height") ? p.Positive("height") : null,
            Rotation = p.OptNumber("rotation"),
            Style = p.OptString("style"),
            Justify = p.OptString("justify"),
            WidthFactor = p.Has("width_factor") ? p.Positive("width_factor") : null,
        };
        if (edit.IsEmpty)
        {
            throw CadException.Invalid("Nothing to change.", "Pass layer, color, linetype, linetype_scale, lineweight, height, rotation, style, justify or width_factor.");
        }

        if (edit.Layer is { } layer && s.Tx.IsLayerLocked(layer))
        {
            throw new CadException(ErrorCodes.LockedLayer, $"Target layer '{layer}' is locked.", "Ask the user before unlocking it.");
        }

        if (edit.Style is { } style && !s.Tx.ResourceExists("text_style", style))
        {
            throw new CadException(ErrorCodes.NotFound, $"Text style '{style}' does not exist.", "Call cad_inspect to list text styles.");
        }

        foreach (var (handle, fingerprint) in Targets(p))
        {
            var before = s.Capture(handle, fingerprint);
            var isText = EntityTypes.TextLike.Contains(before.Type);
            if (edit.TouchesText && !isText)
            {
                throw new CadException(
                    ErrorCodes.Unsupported,
                    $"Entity {before.Handle} is {before.Type}; height/rotation/style/justify/width_factor apply to TEXT/MTEXT only.",
                    "Use cad_transform to rotate or scale other entities.");
            }

            var mine = edit;
            if (edit.Justify is { } j)
            {
                mine = Clone(edit);
                mine.Justify = Styling.ParseJustify(j, before.Type == EntityTypes.MText);
            }

            if (mine.WidthFactor is not null && before.Type == EntityTypes.MText)
            {
                throw new CadException(ErrorCodes.Unsupported, "width_factor applies to TEXT only.");
            }

            // Justification keeps the text where it is visually: the insertion point becomes the anchor.
            s.Tx.SetProperties(before.Handle, mine);
            var h = before.Handle;
            if (mine.Layer is { } l)
            {
                s.Expect(h, $"layer == {l}", a => string.Equals(a.Layer, l, StringComparison.OrdinalIgnoreCase));
            }

            if (mine.Color is not null || mine.Linetype is not null || mine.Lineweight is not null)
            {
                s.Expect(h, "appearance applied", a => CreateSpec.AppearanceMatches(a, mine));
            }

            if (mine.LinetypeScale is { } lts)
            {
                s.Expect(h, $"linetype_scale == {lts}", a => CreateSpec.Near(a.Number("linetype_scale") ?? 1, lts));
            }

            if (mine.Height is { } ht)
            {
                s.Expect(h, $"height == {ht}", a => CreateSpec.Near(a.Number("height"), ht));
            }

            if (mine.Rotation is { } rot)
            {
                s.Expect(h, $"rotation == {rot}", a => AngleClose(a.Number("rotation"), rot));
            }

            if (mine.Style is { } st)
            {
                s.Expect(h, $"style == {st}", a => string.Equals(a.Props["style"]?.GetValue<string>(), st, StringComparison.OrdinalIgnoreCase));
            }

            if (mine.Justify is { } jj)
            {
                var anchor = before.Anchor;
                var dflt = before.Type == EntityTypes.MText ? "TL" : "left";
                s.Expect(h, $"justify == {jj}", a => string.Equals(a.Props["justify"]?.GetValue<string>() ?? dflt, jj, StringComparison.OrdinalIgnoreCase));
                if (anchor is { } an)
                {
                    s.Expect(h, $"text anchor stays at {Fmt(an)}", a => a.Anchor is { } x && x.IsClose(an, 1e-4));
                }
            }

            if (mine.WidthFactor is { } wf)
            {
                s.Expect(h, $"width_factor == {wf}", a => CreateSpec.Near(a.Number("width_factor") ?? 1, wf));
            }
        }
    }

    private static PropertyEdit Clone(PropertyEdit e) => new()
    {
        Layer = e.Layer, Color = e.Color?.DeepClone(), Linetype = e.Linetype, LinetypeScale = e.LinetypeScale, Lineweight = e.Lineweight,
        Height = e.Height, Rotation = e.Rotation, Style = e.Style, Justify = e.Justify, WidthFactor = e.WidthFactor,
    };

    // -------------------------------------------------------------------- copy
    private static void Copy(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles", "displacement", "from", "to", "count");
        var d = Displacement(p);
        var count = p.Int("count", 1, 1, 200);
        foreach (var (handle, fingerprint) in Targets(p))
        {
            var src = s.Tx.Read(handle);
            if (!string.IsNullOrEmpty(fingerprint) && !string.Equals(fingerprint, src.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new CadException(ErrorCodes.StaleTarget, $"Entity {src.Handle} changed since it was analysed (expected fingerprint {fingerprint}, now {src.Fingerprint}).", "Re-query the entity and retry.");
            }

            var anchor = src.Anchor ?? throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} ({src.Type}) has no verifiable reference point.");
            for (var i = 1; i <= count; i++)
            {
                var step = d * i;
                var copy = s.Tx.Copy(src.Handle, step);
                s.RecordCreated(copy);
                var expected = anchor + step;
                var type = src.Type;
                var layer = src.Layer;
                s.Expect(copy, $"copy of {src.Handle} at {Fmt(expected)}", a => a.Type == type && a.Layer == layer && a.Anchor is { } x && x.IsClose(expected, 1e-4));
            }
        }
    }

    private static Vec3 Displacement(Params p)
    {
        Vec3 d;
        if (p.Has("displacement"))
        {
            d = p.Point("displacement");
        }
        else if (p.Has("from") && p.Has("to"))
        {
            d = p.Point("to") - p.Point("from");
        }
        else
        {
            throw CadException.Invalid("Give 'displacement' or both 'from' and 'to'.");
        }

        return d.Length < 1e-12 ? throw CadException.Invalid("The displacement is zero.") : d;
    }

    // --------------------------------------------------------------- transform
    private static void Transform(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles", "op", "base", "angle", "factor", "axis", "copy");
        var op = p.String("op").ToLowerInvariant();
        Transform2D t = op switch
        {
            "rotate" => new Transform2D(op, p.Point("base"), AngleDegrees: p.Number("angle")),
            "scale" => new Transform2D(op, p.Point("base"), Factor: p.Positive("factor")),
            "mirror" => MirrorOf(p),
            _ => throw CadException.Invalid($"Unknown op '{op}'.", "Use rotate (base, angle), scale (base, factor) or mirror (axis [[x1,y1],[x2,y2]])."),
        };
        if (op != "mirror" && p.Has("axis"))
        {
            throw CadException.Invalid("'axis' applies to mirror only.");
        }

        var keepOriginal = p.Bool("copy");
        foreach (var (handle, fingerprint) in Targets(p))
        {
            EntityState before;
            string target;
            if (keepOriginal)
            {
                before = s.Tx.Read(handle);
                if (!string.IsNullOrEmpty(fingerprint) && !string.Equals(fingerprint, before.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CadException(ErrorCodes.StaleTarget, $"Entity {before.Handle} changed since it was analysed.", "Re-query the entity and retry.");
                }

                target = s.Tx.Copy(before.Handle, new Vec3(0, 0));
                s.RecordCreated(target);
            }
            else
            {
                before = s.Capture(handle, fingerprint);
                target = before.Handle;
            }

            var anchor = before.Anchor ?? throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} ({before.Type}) has no verifiable reference point.");
            s.Tx.Transform(target, t);
            var expected = t.Apply(anchor);
            s.Expect(target, $"{op}: reference point at {Fmt(expected)}", a => a.Anchor is { } x && x.IsClose(expected, 1e-4));
            if (op == "scale")
            {
                foreach (var key in new[] { "radius", "height" })
                {
                    if (before.Number(key) is { } v)
                    {
                        var want = v * t.Factor;
                        s.Expect(target, $"{key} == {CadJson.Format(want)}", a => CreateSpec.Near(a.Number(key), want));
                    }
                }
            }

            if (op == "rotate" && before.Type is EntityTypes.Text or EntityTypes.MText or EntityTypes.Insert && before.Number("rotation") is { } r0)
            {
                var want = r0 + t.AngleDegrees;
                s.Expect(target, $"rotation == {CadJson.Format(want)}", a => AngleClose(a.Number("rotation"), want));
            }
        }
    }

    private static Transform2D MirrorOf(Params p)
    {
        if (p.Node["axis"] is not JsonArray { Count: 2 } axis)
        {
            throw CadException.Invalid("mirror needs 'axis': [[x1, y1], [x2, y2]].");
        }

        if (p.Has("base"))
        {
            throw CadException.Invalid("mirror takes 'axis', not 'base'.");
        }

        var a = Vec3.FromJson(axis[0], "axis[0]");
        var b = Vec3.FromJson(axis[1], "axis[1]");
        return a.IsClose(b) ? throw CadException.Invalid("The mirror axis points must differ.") : new Transform2D("mirror", a, AxisEnd: b);
    }

    // ------------------------------------------------------------------ layers
    private static JsonObject ListLayers(ICadTransaction tx, Params p)
    {
        p.AllowOnly("names");
        var names = p.Strings("names").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layers = tx.Layers().Where(l => names.Count == 0 || names.Contains(l["name"]!.GetValue<string>())).ToList();
        return new JsonObject { ["count"] = layers.Count, ["layers"] = new JsonArray(layers.Select(l => (JsonNode)l.DeepClone()).ToArray()) };
    }

    private static void SetLayer(ChangeSession s, Params p)
    {
        p.AllowOnly("name", "color", "linetype", "lineweight", "on", "frozen", "locked", "plot", "description", "make_current", "create", "user_confirmed_unlock");
        var name = p.String("name");
        var edit = new LayerEdit
        {
            Name = name,
            Color = p.Has("color") ? Styling.ParseColor(p.Node["color"], "color") : null,
            Linetype = p.OptString("linetype"),
            Lineweight = p.Has("lineweight") ? Styling.ParseLineweight(p.Node["lineweight"], "lineweight") : null,
            On = p.Has("on") ? p.Bool("on") : null,
            Frozen = p.Has("frozen") ? p.Bool("frozen") : null,
            Locked = p.Has("locked") ? p.Bool("locked") : null,
            Plot = p.Has("plot") ? p.Bool("plot") : null,
            Description = p.OptString("description"),
            MakeCurrent = p.Bool("make_current"),
            CreateIfMissing = p.Bool("create", true),
        };
        if (edit.Color is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs is "bylayer" or "byblock")
        {
            throw CadException.Invalid("A layer color must be an ACI index, a name or '#rrggbb'.");
        }

        if (edit.Lineweight is Styling.LineweightByLayer or Styling.LineweightByBlock)
        {
            throw CadException.Invalid("A layer lineweight must be a value in mm or 'default'.");
        }

        var before = s.Tx.Layers().FirstOrDefault(l => string.Equals(l["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        if (before is null && !edit.CreateIfMissing)
        {
            throw new CadException(ErrorCodes.NotFound, $"Layer '{name}' does not exist.", "Pass create=true to create it.");
        }

        if (before is not null && edit.Locked == false && before["locked"]!.GetValue<bool>() && !p.Bool("user_confirmed_unlock"))
        {
            throw new CadException(
                ErrorCodes.LockedLayer,
                $"Layer '{name}' is locked; unlocking needs the user's explicit consent.",
                "Ask the user, then retry with user_confirmed_unlock=true.");
        }

        if (edit.MakeCurrent && edit.Frozen == true)
        {
            throw CadException.Invalid("The current layer cannot be frozen.");
        }

        s.Tx.SetLayer(edit);
        JsonObject After() => s.Tx.Layers().FirstOrDefault(l => string.Equals(l["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new CadException(ErrorCodes.NotFound, $"Layer '{name}' was not found after the change.");
        s.ExpectGlobal($"layer {name} exists", () => After() is not null);
        if (edit.Color is { } c)
        {
            s.ExpectGlobal($"layer {name} color == {c.ToJsonString()}", () => Styling.SameColor(After()["color"], c));
        }

        if (edit.Linetype is { } lt)
        {
            s.ExpectGlobal($"layer {name} linetype == {lt}", () => string.Equals(After()["linetype"]?.GetValue<string>(), lt, StringComparison.OrdinalIgnoreCase));
        }

        if (edit.Lineweight is { } lw)
        {
            s.ExpectGlobal($"layer {name} lineweight", () => JsonNode.DeepEquals(After()["lineweight"], Styling.FormatLineweight(lw)));
        }

        foreach (var (key, want) in new[] { ("on", edit.On), ("frozen", edit.Frozen), ("locked", edit.Locked), ("plot", edit.Plot), ("current", edit.MakeCurrent ? true : null) })
        {
            if (want is { } w)
            {
                s.ExpectGlobal($"layer {name} {key} == {w}", () => After()[key]?.GetValue<bool>() == w);
            }
        }

        s.Note(new JsonObject
        {
            ["layer"] = name,
            ["action"] = before is null ? "created" : "updated",
            ["before"] = before?.DeepClone(),
        });
    }

    // ------------------------------------------------------------ inspect/view
    private static JsonObject Inspect(ICadTransaction tx, Params p)
    {
        p.AllowOnly("sections");
        var info = tx.Inspect();
        var sections = p.Strings("sections").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sections.Count == 0)
        {
            return info;
        }

        var filtered = new JsonObject();
        foreach (var (k, v) in info)
        {
            if (sections.Contains(k) || k is "document" or "units")
            {
                filtered[k] = v?.DeepClone();
            }
        }

        return filtered;
    }

    private JsonObject View(Params p, bool snapshot)
    {
        p.AllowOnly(snapshot ? new[] { "window", "handles", "extents", "margin", "width", "height" } : new[] { "window", "handles", "extents", "margin" });
        var margin = p.OptNumber("margin") ?? 0.05;
        if (margin is < 0 or > 5)
        {
            throw CadException.Invalid("'margin' must be between 0 and 5 (fraction of the window size).");
        }

        var modes = new[] { p.Has("window"), p.Has("handles"), p.Bool("extents") }.Count(x => x);
        if (modes != 1)
        {
            throw CadException.Invalid("Give exactly one of 'window', 'handles' or extents=true.");
        }

        (Vec3 Min, Vec3 Max) box;
        if (p.Has("window"))
        {
            box = ParseWindow(p.Node["window"])!.Value;
        }
        else if (p.Has("handles"))
        {
            var handles = p.Strings("handles", required: true);
            box = document.Execute(tx => Union(handles.Select(tx.Read)), commit: false);
        }
        else
        {
            box = document.Execute(tx => Union(tx.ScanModelSpace()), commit: false);
        }

        var size = box.Max - box.Min;
        var pad = new Vec3(Math.Max(size.X, size.Y) * margin, Math.Max(size.X, size.Y) * margin);
        if (size.X < 1e-9 && size.Y < 1e-9)
        {
            pad = new Vec3(1, 1);
        }

        var min = box.Min - pad;
        var max = box.Max + pad;
        int? w = null, h = null;
        if (snapshot)
        {
            w = p.Int("width", 1600, 64, MaxSnapshotSize);
            h = p.Int("height", 0, 0, MaxSnapshotSize);
            if (h == 0)
            {
                var aspect = (max.Y - min.Y) / Math.Max(1e-9, max.X - min.X);
                h = (int)Math.Clamp(Math.Round(w.Value * aspect), 64, MaxSnapshotSize);
            }
        }

        var result = document.View(min, max, w, h);
        result["window"] = new JsonArray(min.ToJson(), max.ToJson());
        return result;
    }

    private static (Vec3 Min, Vec3 Max) Union(IEnumerable<EntityState> entities)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var e in entities)
        {
            if (e.Props["bbox"] is JsonArray { Count: 2 } bb)
            {
                var a = Vec3.FromJson(bb[0], "bbox");
                var b = Vec3.FromJson(bb[1], "bbox");
                (x0, y0, x1, y1) = (Math.Min(x0, a.X), Math.Min(y0, a.Y), Math.Max(x1, b.X), Math.Max(y1, b.Y));
            }
            else if (e.Anchor is { } an)
            {
                (x0, y0, x1, y1) = (Math.Min(x0, an.X), Math.Min(y0, an.Y), Math.Max(x1, an.X), Math.Max(y1, an.Y));
            }
        }

        return x0 > x1
            ? throw new CadException(ErrorCodes.NotFound, "Nothing to show: no entities with known extents.")
            : (new Vec3(x0, y0), new Vec3(x1, y1));
    }
}
