using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Color = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;

namespace PowerCad.Plugin;

/// <summary>Styles, dimensions, hatches, appearance, delete/copy/transform, layers and drawing resources.</summary>
internal sealed partial class AcadTransaction
{
    // ------------------------------------------------------------ text styles
    private static readonly (string Name, AttachmentPoint Value)[] TextJustifyMap =
    [
        ("left", AttachmentPoint.BaseLeft), ("center", AttachmentPoint.BaseCenter), ("right", AttachmentPoint.BaseRight),
        ("middle", AttachmentPoint.BaseMid), ("aligned", AttachmentPoint.BaseAlign), ("fit", AttachmentPoint.BaseFit),
        ("TL", AttachmentPoint.TopLeft), ("TC", AttachmentPoint.TopCenter), ("TR", AttachmentPoint.TopRight),
        ("ML", AttachmentPoint.MiddleLeft), ("MC", AttachmentPoint.MiddleCenter), ("MR", AttachmentPoint.MiddleRight),
        ("BL", AttachmentPoint.BottomLeft), ("BC", AttachmentPoint.BottomCenter), ("BR", AttachmentPoint.BottomRight),
    ];

    private static string TextJustifyName(AttachmentPoint ap) =>
        TextJustifyMap.FirstOrDefault(x => x.Value == ap).Name ?? ap.ToString();

    private static AttachmentPoint TextJustifyValue(string name) =>
        TextJustifyMap.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string MTextJustifyName(AttachmentPoint ap) => ap switch
    {
        AttachmentPoint.TopLeft => "TL",
        AttachmentPoint.TopCenter => "TC",
        AttachmentPoint.TopRight => "TR",
        AttachmentPoint.MiddleLeft => "ML",
        AttachmentPoint.MiddleCenter => "MC",
        AttachmentPoint.MiddleRight => "MR",
        AttachmentPoint.BottomLeft => "BL",
        AttachmentPoint.BottomCenter => "BC",
        AttachmentPoint.BottomRight => "BR",
        _ => ap.ToString(),
    };

    private static AttachmentPoint MTextJustifyValue(string name) => TextJustifyValue(name);

    private string SymbolName(ObjectId id) =>
        id.IsNull || id.IsErased ? "" : ((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;

    private ObjectId StyleId(string name)
    {
        var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        return st.Has(name) ? st[name] : throw new CadException(ErrorCodes.NotFound, $"Text style '{name}' does not exist.");
    }

    private void AdjustAlignment(DBText t)
    {
        if (t.Justify == AttachmentPoint.BaseLeft)
        {
            return;
        }

        var previous = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = db;
            t.AdjustAlignment(db);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previous;
        }
    }

    // ------------------------------------------------------------- describe
    private static Extents3d? SafeBounds(Entity ent)
    {
        try
        {
            return ent.Bounds;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    private void DescribeAppearance(Entity ent, JsonObject props)
    {
        var c = ent.Color;
        if (!c.IsByLayer)
        {
            props["color"] = ColorToJson(c);
        }

        if (!string.Equals(ent.Linetype, "BYLAYER", StringComparison.OrdinalIgnoreCase))
        {
            props["linetype"] = ent.Linetype;
        }

        if (ent.LineWeight != LineWeight.ByLayer)
        {
            props["lineweight"] = Styling.FormatLineweight((int)ent.LineWeight);
        }

        if (Math.Abs(ent.LinetypeScale - 1) > 1e-9)
        {
            props["linetype_scale"] = CadJson.Round(ent.LinetypeScale);
        }
    }

    private static JsonNode ColorToJson(Color c)
    {
        if (c.IsByLayer)
        {
            return "bylayer";
        }

        if (c.IsByBlock)
        {
            return "byblock";
        }

        if (c.ColorMethod == ColorMethod.ByColor)
        {
            return $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
        }

        return (int)c.ColorIndex;
    }

    private static Color ColorFromJson(JsonNode node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s switch
            {
                "bylayer" => Color.FromColorIndex(ColorMethod.ByLayer, 256),
                "byblock" => Color.FromColorIndex(ColorMethod.ByBlock, 0),
                _ when s.StartsWith('#') => Color.FromRgb(
                    byte.Parse(s[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(s[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                _ => throw CadException.Invalid($"Bad color '{s}'."),
            };
        }

        return Color.FromColorIndex(ColorMethod.ByAci, (short)node.GetValue<int>());
    }

    private void DescribeDimension(Dimension dim, JsonObject props)
    {
        switch (dim)
        {
            case RotatedDimension rd:
                props["kind"] = "rotated";
                props["xline1"] = P(rd.XLine1Point);
                props["xline2"] = P(rd.XLine2Point);
                props["dimline"] = P(rd.DimLinePoint);
                props["rotation"] = CadJson.Round(rd.Rotation * Deg);
                break;
            case AlignedDimension ad:
                props["kind"] = "aligned";
                props["xline1"] = P(ad.XLine1Point);
                props["xline2"] = P(ad.XLine2Point);
                props["dimline"] = P(ad.DimLinePoint);
                break;
            default:
                props["kind"] = dim.GetType().Name.Replace("Dimension", "", StringComparison.Ordinal).ToLowerInvariant();
                break;
        }

        props["style"] = SymbolName(dim.DimensionStyle);
        try
        {
            props["measurement"] = CadJson.Round(dim.Measurement);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // measurement is unavailable for some dimension kinds until recomputed
        }

        if (!string.IsNullOrEmpty(dim.DimensionText))
        {
            props["text_override"] = dim.DimensionText;
        }

        props["text_position"] = P(dim.TextPosition);
    }

    private static void DescribeHatch(Hatch h, JsonObject props)
    {
        props["pattern"] = h.PatternName;
        props["scale"] = CadJson.Round(h.PatternScale);
        props["angle"] = CadJson.Round(h.PatternAngle * Deg);
        props["loops"] = h.NumberOfLoops;
        if (h.NumberOfLoops > 0)
        {
            var loop = h.GetLoopAt(0);
            List<Vec3>? pts = null;
            if (loop.IsPolyline)
            {
                pts = loop.Polyline.Cast<BulgeVertex>().Select(bv => new Vec3(bv.Vertex.X, bv.Vertex.Y, h.Elevation)).ToList();
            }
            else if (loop.Curves is { Count: > 0 } curves)
            {
                // loops built from a boundary entity (CreateHatch) come back as edge curves, not a polyline
                pts = curves.Cast<Autodesk.AutoCAD.Geometry.Curve2d>()
                    .Select(c => new Vec3(c.StartPoint.X, c.StartPoint.Y, h.Elevation)).ToList();
            }

            if (pts is not null)
            {
                if (pts.Count > 1 && pts[0].IsClose(pts[^1]))
                {
                    pts.RemoveAt(pts.Count - 1);
                }

                props["points"] = new JsonArray(pts.Select(p => (JsonNode)p.ToJson()).ToArray());
            }
        }

        try
        {
            props["area"] = CadJson.Round(h.Area);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // area is undefined for some self-intersecting boundaries
        }
    }

    // --------------------------------------------------------------- create
    private Dimension MakeDimension(CreateSpec spec)
    {
        ObjectId styleId;
        if (spec.Style is { } name)
        {
            var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            styleId = dst.Has(name) ? dst[name] : throw new CadException(ErrorCodes.NotFound, $"Dimension style '{name}' does not exist.");
        }
        else
        {
            styleId = db.Dimstyle;
        }

        var text = spec.TextOverride ?? "";
        Dimension dim = spec.DimKind == "aligned"
            ? new AlignedDimension(Pt(spec.A), Pt(spec.B), Pt(spec.C), text, styleId)
            : new RotatedDimension(spec.Rotation / Deg, Pt(spec.A), Pt(spec.B), Pt(spec.C), text, styleId);
        return dim;
    }

    private string CreateHatch(BlockTableRecord ms, CreateSpec spec)
    {
        // A temporary closed polyline is the boundary AutoCAD accepts most reliably; the hatch is made
        // non-associative and the helper polyline is erased again inside the same transaction.
        var boundary = new Polyline(spec.Points.Count);
        for (var i = 0; i < spec.Points.Count; i++)
        {
            boundary.AddVertexAt(i, new Point2d(spec.Points[i].X, spec.Points[i].Y), 0, 0, 0);
        }

        boundary.Closed = true;
        boundary.Elevation = spec.Points[0].Z;
        boundary.SetDatabaseDefaults(db);
        ms.AppendEntity(boundary);
        tr.AddNewlyCreatedDBObject(boundary, true);

        var h = new Hatch();
        h.SetDatabaseDefaults(db);
        if (spec.Layer is { } layer)
        {
            EnsureLayer(layer);
            h.Layer = layer;
        }

        ApplyAppearance(h, spec.Appearance);
        ms.AppendEntity(h);
        tr.AddNewlyCreatedDBObject(h, true);
        Step("hatch pattern", () =>
        {
            // acad.pat/acadiso.pat patterns first, then a custom <name>.pat on the support path (e.g. KHAT47)
            foreach (var kind in new[] { HatchPatternType.PreDefined, HatchPatternType.CustomDefined })
            {
                try
                {
                    // scale/angle are rejected (eInvalidInput) while the hatch has no pattern yet; set the
                    // pattern first, then scale/angle, then set it again so the definition picks them up
                    h.SetHatchPattern(kind, spec.Pattern);
                    h.PatternAngle = spec.Rotation / Deg;
                    h.PatternScale = spec.Scale;
                    h.SetHatchPattern(kind, spec.Pattern);
                    return;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // try the next pattern source
                }
            }

            throw CadException.Invalid(
                $"Unknown hatch pattern '{spec.Pattern}'.",
                "Use SOLID, a pattern from acad.pat/acadiso.pat (ANSI31, ANSI37, AR-CONC, AR-SAND, NET, ...), or put <name>.pat on the AutoCAD support path.");
        });
        Step("hatch boundary", () =>
        {
            h.Associative = false;
            h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            h.EvaluateHatch(true);
        });
        boundary.Erase();
        return h.Handle.ToString();
    }

    private void DescribeLeader(Leader ld, JsonObject props)
    {
        var pts = new JsonArray();
        for (var i = 0; i < ld.NumVertices; i++)
        {
            pts.Add(P(ld.VertexAt(i)));
        }

        props["points"] = pts;
        props["style"] = SymbolName(ld.DimensionStyle);
        props["arrow"] = !ld.HasArrowHead ? "none" : ld.Dimldrblk.IsNull ? "" : SymbolName(ld.Dimldrblk).ToUpperInvariant();
    }

    private string CreateLeader(BlockTableRecord ms, CreateSpec spec)
    {
        var ld = new Leader();
        ld.SetDatabaseDefaults(db);
        for (var i = 0; i < spec.Points.Count; i++)
        {
            ld.AppendVertex(Pt(spec.Points[i]));
        }

        if (spec.Style is { } name)
        {
            var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            ld.DimensionStyle = dst.Has(name) ? dst[name] : throw new CadException(ErrorCodes.NotFound, $"Dimension style '{name}' does not exist.", "List styles with cad_inspect sections:[\"dim_styles\"].");
        }

        if (spec.Layer is { } layer)
        {
            EnsureLayer(layer);
            ld.Layer = layer;
        }

        ApplyAppearance(ld, spec.Appearance);
        ms.AppendEntity(ld);
        tr.AddNewlyCreatedDBObject(ld, true);
        Step("leader arrow", () =>
        {
            if (spec.BlockName == "none")
            {
                ld.HasArrowHead = false;
            }
            else if (spec.BlockName.Length > 0)
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(spec.BlockName))
                {
                    throw new CadException(ErrorCodes.NotFound, $"Arrow block '{spec.BlockName}' is not in this drawing.", "Use an arrow block the drawing already has (see cad_inspect blocks, e.g. _DOT), 'none', or omit arrow.");
                }

                ld.Dimldrblk = bt[spec.BlockName];
            }

            // no EvaluateLeader(): it throws eNotApplicable for a leader without an annotation object
        });
        return ld.Handle.ToString();
    }

    /// <summary>Runs one AutoCAD API step and names it in the error, so failures are diagnosable.</summary>
    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception e)
        {
            throw new CadException(ErrorCodes.Internal, $"AutoCAD rejected the {what} ({e.ErrorStatus}).", "Check the geometry (closed, non-self-intersecting boundary) and pattern name.");
        }
    }

    private void ApplyAppearance(Entity ent, PropertyEdit look)
    {
        if (look.Color is { } c)
        {
            ent.Color = ColorFromJson(c);
        }

        if (look.Linetype is { } lt)
        {
            ent.Linetype = EnsureLinetype(lt);
        }

        if (look.Lineweight is { } lw)
        {
            ent.LineWeight = (LineWeight)lw;
        }

        if (look.LinetypeScale is { } lts)
        {
            ent.LinetypeScale = lts;
        }
    }

    /// <summary>Returns the stored linetype name, loading it from acadiso.lin / acad.lin when missing.</summary>
    private string EnsureLinetype(string name)
    {
        if (name.Equals("bylayer", StringComparison.OrdinalIgnoreCase))
        {
            return "ByLayer";
        }

        if (name.Equals("byblock", StringComparison.OrdinalIgnoreCase))
        {
            return "ByBlock";
        }

        var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (!ltt.Has(name))
        {
            foreach (var file in new[] { "acadiso.lin", "acad.lin" })
            {
                try
                {
                    db.LoadLineTypeFile(name, file);
                    break;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // try the next file
                }
            }

            ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!ltt.Has(name))
            {
                throw new CadException(ErrorCodes.NotFound, $"Linetype '{name}' is not defined and could not be loaded from acadiso.lin/acad.lin.", "Call cad_inspect to list linetypes.");
            }
        }

        return SymbolName(ltt[name]);
    }

    // ------------------------------------------------------------ mutations
    public void Delete(string handle) => Open(handle, OpenMode.ForWrite).Erase();

    public void SetProperties(string handle, PropertyEdit edit)
    {
        var ent = Open(handle, OpenMode.ForWrite);
        if (edit.Layer is { } layer)
        {
            EnsureLayer(layer);
            ent.Layer = layer;
        }

        ApplyAppearance(ent, edit);
        switch (ent)
        {
            case DBText t:
                if (edit.Style is { } st)
                {
                    t.TextStyleId = StyleId(st);
                }

                if (edit.Height is { } h)
                {
                    t.Height = h;
                }

                if (edit.Rotation is { } r)
                {
                    t.Rotation = r / Deg;
                }

                if (edit.WidthFactor is { } wf)
                {
                    t.WidthFactor = wf;
                }

                if (edit.Justify is { } j)
                {
                    var anchor = t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint;
                    var ap = TextJustifyValue(j);
                    t.Justify = ap;
                    if (ap == AttachmentPoint.BaseLeft)
                    {
                        t.Position = anchor;
                    }
                    else
                    {
                        t.AlignmentPoint = anchor;
                    }
                }

                AdjustAlignment(t);
                break;
            case MText m:
                if (edit.Style is { } ms)
                {
                    m.TextStyleId = StyleId(ms);
                }

                if (edit.Height is { } mh)
                {
                    m.TextHeight = mh;
                }

                if (edit.Rotation is { } mr)
                {
                    m.Rotation = mr / Deg;
                }

                if (edit.Justify is { } mj)
                {
                    m.Attachment = MTextJustifyValue(mj);
                }

                break;
        }
    }

    public string Copy(string handle, Vec3 d)
    {
        var src = Open(handle, OpenMode.ForRead);
        var ids = new ObjectIdCollection { src.ObjectId };
        var map = new IdMapping();
        db.DeepCloneObjects(ids, ModelSpaceId, map, false);
        var copy = (Entity)tr.GetObject(map[src.ObjectId].Value, OpenMode.ForWrite);
        if (d.Length > 0)
        {
            copy.TransformBy(Matrix3d.Displacement(new Vector3d(d.X, d.Y, d.Z)));
        }

        return copy.Handle.ToString();
    }

    public void Transform(string handle, Transform2D t)
    {
        var ent = Open(handle, OpenMode.ForWrite);
        if (t.Op == "mirror" && ent is DBText or MText)
        {
            // MIRRTEXT=0 behaviour: keep text readable, reflect only its anchor.
            var anchor = Describe(ent).Anchor!.Value;
            var d = t.Apply(anchor) - anchor;
            ent.TransformBy(Matrix3d.Displacement(new Vector3d(d.X, d.Y, d.Z)));
            return;
        }

        var b = Pt(t.Base);
        var m = t.Op switch
        {
            "rotate" => Matrix3d.Rotation(t.AngleDegrees / Deg, Vector3d.ZAxis, b),
            "scale" => Matrix3d.Scaling(t.Factor, b),
            "mirror" => Matrix3d.Mirroring(new Line3d(b, Pt(t.AxisEnd))),
            _ => throw CadException.Invalid($"Unknown transform '{t.Op}'."),
        };
        ent.TransformBy(m);
    }

    // --------------------------------------------------------------- layers
    public IReadOnlyList<JsonObject> Layers()
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var list = new List<JsonObject>();
        foreach (ObjectId id in lt)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not LayerTableRecord rec)
            {
                continue;
            }

            var o = new JsonObject
            {
                ["name"] = rec.Name,
                ["color"] = ColorToJson(rec.Color),
                ["linetype"] = SymbolName(rec.LinetypeObjectId),
                ["lineweight"] = Styling.FormatLineweight((int)rec.LineWeight),
                ["on"] = !rec.IsOff,
                ["frozen"] = rec.IsFrozen,
                ["locked"] = rec.IsLocked,
                ["plot"] = rec.IsPlottable,
                ["current"] = id == db.Clayer,
            };
            if (!string.IsNullOrEmpty(rec.Description))
            {
                o["description"] = rec.Description;
            }

            if (rec.IsDependent)
            {
                o["xref_dependent"] = true;
            }

            list.Add(o);
        }

        return list.OrderBy(l => l["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SetLayer(LayerEdit edit)
    {
        EnsureLayer(edit.Name);
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var rec = (LayerTableRecord)tr.GetObject(lt[edit.Name], OpenMode.ForWrite);
        if (edit.Color is { } c)
        {
            rec.Color = ColorFromJson(c);
        }

        if (edit.Linetype is { } ltName)
        {
            var stored = EnsureLinetype(ltName);
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            rec.LinetypeObjectId = ltt[stored];
        }

        if (edit.Lineweight is { } lw)
        {
            rec.LineWeight = (LineWeight)lw;
        }

        if (edit.On is { } on)
        {
            rec.IsOff = !on;
        }

        if (edit.Frozen is { } frozen)
        {
            if (frozen && rec.ObjectId == db.Clayer)
            {
                throw CadException.Invalid($"Layer '{edit.Name}' is current and cannot be frozen.", "Make another layer current first.");
            }

            rec.IsFrozen = frozen;
        }

        if (edit.Locked is { } locked)
        {
            rec.IsLocked = locked;
        }

        if (edit.Plot is { } plot)
        {
            rec.IsPlottable = plot;
        }

        if (edit.Description is { } d)
        {
            rec.Description = d;
        }

        if (edit.MakeCurrent)
        {
            db.Clayer = rec.ObjectId;
        }
    }

    // -------------------------------------------------------- hatch patterns
    public JsonObject HatchPattern(string handle)
    {
        if (Open(handle, OpenMode.ForRead) is not Hatch h)
        {
            throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} is not a hatch.");
        }

        var lines = new JsonArray();
        for (var i = 0; i < h.NumberOfPatternDefinitions; i++)
        {
            var d = h.GetPatternDefinitionAt(i);
            var line = new JsonObject
            {
                ["angle"] = d.Angle * Deg,
                ["base"] = new JsonArray(d.BaseX, d.BaseY),
                ["offset"] = new JsonArray(d.OffsetX, d.OffsetY),
            };
            if (d.GetDashes() is { Count: > 0 } dashes)
            {
                line["dashes"] = new JsonArray(dashes.Cast<double>().Select(x => (JsonNode)x).ToArray());
            }

            lines.Add(line);
        }

        string? support = null;
        try
        {
            if (Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("ROAMABLEROOTPREFIX") is string root && root.Length > 0)
            {
                support = Path.Combine(root, "Support");
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // leave it to the caller's folder
        }

        return new JsonObject
        {
            ["name"] = h.PatternName,
            ["type"] = h.PatternType switch
            {
                HatchPatternType.PreDefined => h.PatternName.Equals("SOLID", StringComparison.OrdinalIgnoreCase) ? "solid" : "predefined",
                HatchPatternType.CustomDefined => "custom",
                _ => "user",
            },
            ["scale"] = h.PatternScale,
            ["angle"] = h.PatternAngle * Deg,
            ["double"] = h.PatternDouble,
            ["lines"] = lines,
            ["support_dir"] = support,
        };
    }

    // ---------------------------------------------------------- block assets
    public JsonObject ExportBlock(string name, string path)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
        var ids = new ObjectIdCollection();
        var texts = new JsonArray();
        Extents3d? ext = null;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
            {
                continue;
            }

            ids.Add(id);
            switch (e)
            {
                case DBText t when !string.IsNullOrWhiteSpace(t.TextString):
                    texts.Add(new JsonObject { ["text"] = t.TextString, ["position"] = P(t.Position), ["height"] = CadJson.Round(t.Height), ["layer"] = t.Layer });
                    break;
                case MText m when !string.IsNullOrWhiteSpace(m.Text):
                    texts.Add(new JsonObject { ["text"] = m.Text, ["position"] = P(m.Location), ["height"] = CadJson.Round(m.TextHeight), ["layer"] = m.Layer });
                    break;
            }

            if (e is not AttributeDefinition && SafeBounds(e) is { } b)
            {
                if (ext is { } x)
                {
                    x.AddExtents(b);
                    ext = x;
                }
                else
                {
                    ext = b;
                }
            }
        }

        if (ids.Count == 0)
        {
            throw new CadException(ErrorCodes.Unsupported, $"Block '{name}' is empty.");
        }

        // Clone the definition's entities into a new drawing's model space; the block base point becomes
        // the origin, so inserting the file as a block reproduces the original definition.
        using (var outDb = new Database(true, true))
        {
            var mapping = new IdMapping();
            db.WblockCloneObjects(ids, outDb.CurrentSpaceId, mapping, DuplicateRecordCloning.Ignore, false);
            outDb.Insunits = db.Insunits;
            outDb.Insbase = btr.Origin;
            outDb.SaveAs(path, DwgVersion.Current);
        }

        var card = new JsonObject
        {
            ["units"] = db.Insunits.ToString(),
            ["base_point"] = P(btr.Origin),
            ["entity_count"] = ids.Count,
            ["attributes"] = new JsonArray(ids.Cast<ObjectId>().Select(i => tr.GetObject(i, OpenMode.ForRead)).OfType<AttributeDefinition>().Select(a => (JsonNode)a.Tag).ToArray()),
            ["texts"] = texts,
        };
        if (ext is { } r)
        {
            card["extents"] = new JsonArray(P(r.MinPoint), P(r.MaxPoint));
            card["size"] = new JsonArray(CadJson.Round(r.MaxPoint.X - r.MinPoint.X), CadJson.Round(r.MaxPoint.Y - r.MinPoint.Y));
        }

        return card;
    }

    public void ImportBlock(string path, string name, bool replace)
    {
        using var src = new Database(false, true);
        try
        {
            src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
            src.CloseInput(true);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception e)
        {
            throw new CadException(ErrorCodes.InvalidParams, $"Cannot read '{path}' as a DWG ({e.ErrorStatus}).");
        }

        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (bt.Has(name) && !replace)
        {
            return;
        }

        // Database.Insert makes (or redefines) a block from the whole source drawing, base point = INSBASE.
        Step("block import", () => db.Insert(name, src, true));
    }

    // ------------------------------------------------------------- resources
    public bool ResourceExists(string kind, string name) => kind switch
    {
        "text_style" => ((TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead)).Has(name),
        "dim_style" => ((DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead)).Has(name),
        "linetype" => ((LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead)).Has(name),
        "layer" => ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Has(name),
        "block" => BlockExists(name),
        _ => false,
    };

    public JsonObject Inspect()
    {
        var info = new JsonObject
        {
            ["document"] = Path.GetFileName(db.Filename),
            ["units"] = db.Insunits.ToString(),
            ["current"] = new JsonObject
            {
                ["layer"] = SymbolName(db.Clayer),
                ["text_style"] = SymbolName(db.Textstyle),
                ["dim_style"] = SymbolName(db.Dimstyle),
                ["linetype"] = SymbolName(db.Celtype),
                ["ltscale"] = CadJson.Round(db.Ltscale),
            },
        };

        var textStyles = new JsonArray();
        foreach (ObjectId id in (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord ts && !ts.IsShapeFile && !id.IsErased)
            {
                var o = new JsonObject { ["name"] = ts.Name, ["font_file"] = ts.FileName, ["height"] = CadJson.Round(ts.TextSize), ["width_factor"] = CadJson.Round(ts.XScale) };
                if (!string.IsNullOrEmpty(ts.BigFontFileName))
                {
                    o["bigfont_file"] = ts.BigFontFileName;
                }

                if (!string.IsNullOrEmpty(ts.Font.TypeFace))
                {
                    o["typeface"] = ts.Font.TypeFace;
                }

                textStyles.Add(o);
            }
        }

        info["text_styles"] = textStyles;

        var dimStyles = new JsonArray();
        foreach (ObjectId id in (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is DimStyleTableRecord ds && !id.IsErased)
            {
                dimStyles.Add(new JsonObject
                {
                    ["name"] = ds.Name,
                    ["scale"] = CadJson.Round(ds.Dimscale),
                    ["text_height"] = CadJson.Round(ds.Dimtxt),
                    ["arrow"] = ds.Dimblk.IsNull ? "closed filled" : SymbolName(ds.Dimblk),
                    ["text_style"] = SymbolName(ds.Dimtxsty),
                    ["ext_offset"] = CadJson.Round(ds.Dimexo),
                    ["ext_extend"] = CadJson.Round(ds.Dimexe),
                    ["text_gap"] = CadJson.Round(ds.Dimgap),
                    ["decimals"] = ds.Dimdec,
                    ["text_above"] = ds.Dimtad,
                });
            }
        }

        info["dim_styles"] = dimStyles;

        var linetypes = new JsonArray();
        foreach (ObjectId id in (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead))
        {
            if (!id.IsErased)
            {
                linetypes.Add(SymbolName(id));
            }
        }

        info["linetypes"] = linetypes;

        var blocks = new JsonArray();
        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr || btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsDependent)
            {
                continue;
            }

            Extents3d? ext = null;
            var tags = new JsonArray();
            foreach (ObjectId eid in btr)
            {
                var e = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                if (e is AttributeDefinition ad)
                {
                    tags.Add(ad.Tag);
                    continue;
                }

                if (e is not null && SafeBounds(e) is { } b)
                {
                    if (ext is { } x)
                    {
                        x.AddExtents(b);
                        ext = x;
                    }
                    else
                    {
                        ext = b;
                    }
                }
            }

            var o = new JsonObject
            {
                ["name"] = btr.Name,
                ["dynamic"] = btr.IsDynamicBlock,
                ["references"] = btr.GetBlockReferenceIds(true, false).Count,
                ["attributes"] = tags,
            };
            if (ext is { } r)
            {
                o["extents"] = new JsonArray(P(r.MinPoint), P(r.MaxPoint));
            }

            blocks.Add(o);
        }

        info["blocks"] = blocks;

        var layerCount = 0;
        foreach (ObjectId _ in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
        {
            layerCount++;
        }

        info["layer_count"] = layerCount;
        Extents3d? all = null;
        var count = 0;
        var ms = (BlockTableRecord)tr.GetObject(ModelSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in ms)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity e)
            {
                continue;
            }

            count++;
            if (SafeBounds(e) is { } b)
            {
                if (all is { } x)
                {
                    x.AddExtents(b);
                    all = x;
                }
                else
                {
                    all = b;
                }
            }
        }

        info["entity_count"] = count;
        if (all is { } a)
        {
            info["extents"] = new JsonArray(P(a.MinPoint), P(a.MaxPoint));
        }

        return info;
    }
}
