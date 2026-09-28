using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;

namespace PowerCad.Plugin;

/// <summary>Maps the backend-neutral transaction contract onto one AutoCAD <see cref="Transaction"/>.</summary>
internal sealed partial class AcadTransaction(Database db, Transaction tr) : ICadTransaction
{
    private const double Deg = 180.0 / Math.PI;

    private ObjectId ModelSpaceId => SymbolUtilityServices.GetBlockModelSpaceId(db);

    // ------------------------------------------------------------------ reads
    public IEnumerable<EntityState> ScanModelSpace()
    {
        var ms = (BlockTableRecord)tr.GetObject(ModelSpaceId, OpenMode.ForRead);
        foreach (ObjectId id in ms)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity ent)
            {
                continue;
            }

            yield return Describe(ent);
        }
    }

    public EntityState Read(string handle) => Describe(Open(handle, OpenMode.ForRead));

    private Entity Open(string handle, OpenMode mode)
    {
        long value;
        try
        {
            value = Convert.ToInt64(handle.Trim(), 16);
        }
        catch (FormatException)
        {
            throw CadException.Invalid($"'{handle}' is not a hexadecimal handle.");
        }

        if (!db.TryGetObjectId(new Handle(value), out var id) || id.IsErased || id.IsNull)
        {
            throw new CadException(ErrorCodes.NotFound, $"No entity with handle '{handle}'.", "Query the drawing to get current handles.");
        }

        if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent || ent.OwnerId != ModelSpaceId)
        {
            throw new CadException(ErrorCodes.NotFound, $"'{handle}' is not a model-space entity.");
        }

        if (mode == OpenMode.ForWrite)
        {
            if (IsLayerLocked(ent.Layer))
            {
                throw new CadException(ErrorCodes.LockedLayer, $"Entity {handle} is on locked layer '{ent.Layer}'.");
            }

            ent.UpgradeOpen();
        }

        return ent;
    }

    public bool IsLayerLocked(string layer)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        return lt.Has(layer) && ((LayerTableRecord)tr.GetObject(lt[layer], OpenMode.ForRead)).IsLocked;
    }

    private EntityState Describe(Entity ent)
    {
        var props = new JsonObject();
        string type;
        switch (ent)
        {
            case Line l:
                type = EntityTypes.Line;
                props["start"] = P(l.StartPoint);
                props["end"] = P(l.EndPoint);
                props["length"] = CadJson.Round(l.Length);
                break;
            case Polyline pl:
                type = EntityTypes.Polyline;
                props["points"] = new JsonArray(Enumerable.Range(0, pl.NumberOfVertices)
                    .Select(i => (JsonNode)P(pl.GetPoint3dAt(i))).ToArray());
                props["closed"] = pl.Closed;
                break;
            case Arc a:
                type = EntityTypes.Arc;
                props["center"] = P(a.Center);
                props["radius"] = CadJson.Round(a.Radius);
                props["start_angle"] = CadJson.Round(a.StartAngle * Deg);
                props["end_angle"] = CadJson.Round(a.EndAngle * Deg);
                break;
            case Circle c:
                type = EntityTypes.Circle;
                props["center"] = P(c.Center);
                props["radius"] = CadJson.Round(c.Radius);
                break;
            case DBText t:
                type = EntityTypes.Text;
                props["text"] = t.TextString;
                props["position"] = P(t.Position);
                props["height"] = CadJson.Round(t.Height);
                props["rotation"] = CadJson.Round(t.Rotation * Deg);
                props["style"] = SymbolName(t.TextStyleId);
                if (TextJustifyName(t.Justify) is var j && j != "left")
                {
                    props["justify"] = j;
                    props["alignment_point"] = P(t.AlignmentPoint);
                }

                if (Math.Abs(t.WidthFactor - 1) > 1e-9)
                {
                    props["width_factor"] = CadJson.Round(t.WidthFactor);
                }

                break;
            case MText m:
                type = EntityTypes.MText;
                props["text"] = m.Contents;
                props["position"] = P(m.Location);
                props["height"] = CadJson.Round(m.TextHeight);
                props["width"] = CadJson.Round(m.Width);
                props["rotation"] = CadJson.Round(m.Rotation * Deg);
                props["style"] = SymbolName(m.TextStyleId);
                if (MTextJustifyName(m.Attachment) is var mj && mj != "TL")
                {
                    props["justify"] = mj;
                }

                break;
            case Dimension dim:
                type = EntityTypes.Dimension;
                DescribeDimension(dim, props);
                break;
            case Hatch h:
                type = EntityTypes.Hatch;
                DescribeHatch(h, props);
                break;
            case BlockReference br:
                type = EntityTypes.Insert;
                DescribeInsert(br, props);
                break;
            case DBPoint pt:
                type = EntityTypes.Point;
                props["position"] = P(pt.Position);
                break;
            default:
                type = ent.GetRXClass().DxfName is { Length: > 0 } dxf ? dxf : ent.GetType().Name.ToUpperInvariant();
                break;
        }

        DescribeAppearance(ent, props);
        if (SafeBounds(ent) is { } b)
        {
            props["bbox"] = new JsonArray(P(b.MinPoint), P(b.MaxPoint));
        }

        return new EntityState(ent.Handle.ToString(), type, ent.Layer, props);
    }

    private void DescribeInsert(BlockReference br, JsonObject props)
    {
        var name = EffectiveName(br);
        props["name"] = name;
        props["position"] = P(br.Position);
        props["rotation"] = CadJson.Round(br.Rotation * Deg);
        var s = br.ScaleFactors;
        props["scale"] = new JsonArray(CadJson.Round(s.X), CadJson.Round(s.Y), CadJson.Round(s.Z));

        var dynamic = new JsonObject();
        if (br.IsDynamicBlock)
        {
            foreach (DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)
            {
                if (prop.PropertyName is "Origin" || prop.Value is null)
                {
                    continue;
                }

                dynamic[prop.PropertyName] = prop.Value switch
                {
                    double d => CadJson.Round(d),
                    short or int or long => Convert.ToDouble(prop.Value, CultureInfo.InvariantCulture),
                    string str => str,
                    _ => prop.Value.ToString(),
                };
            }
        }

        props["dynamic"] = dynamic;

        var attributes = new JsonObject();
        foreach (ObjectId attId in br.AttributeCollection)
        {
            if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att && !attributes.ContainsKey(att.Tag))
            {
                attributes[att.Tag] = att.TextString;
            }
        }

        props["attributes"] = attributes;
        var width = Openings.EffectiveWidth(dynamic, BlockDefinitionWidth(name), s.X);
        props["width"] = width is { } w ? CadJson.Round(w) : null;
    }

    private string EffectiveName(BlockReference br)
    {
        var btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).Name;
    }

    public double? BlockDefinitionWidth(string blockName)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(blockName))
        {
            return null;
        }

        var btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
        Extents3d? ext = null;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Entity e && e is not AttributeDefinition && e.Bounds is { } b)
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

        return ext is { } r ? r.MaxPoint.X - r.MinPoint.X : null;
    }

    public bool BlockExists(string blockName) =>
        ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Has(blockName);

    // ----------------------------------------------------------------- writes
    public void SetText(string handle, string text)
    {
        switch (Open(handle, OpenMode.ForWrite))
        {
            case DBText t:
                t.TextString = text;
                break;
            case MText m:
                m.Contents = text;
                break;
            default:
                throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} has no editable text.");
        }
    }

    public void Move(string handle, Vec3 d) =>
        Open(handle, OpenMode.ForWrite).TransformBy(Matrix3d.Displacement(new Vector3d(d.X, d.Y, d.Z)));

    public void EditInsert(string handle, InsertEdit edit)
    {
        if (Open(handle, OpenMode.ForWrite) is not BlockReference br)
        {
            throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} is not a block reference.");
        }

        // Transform (not property assignment) so attribute references follow the block.
        if (edit.Position is { } p)
        {
            br.TransformBy(Matrix3d.Displacement(new Point3d(p.X, p.Y, p.Z) - br.Position));
        }

        if (edit.RotationDegrees is { } deg)
        {
            br.TransformBy(Matrix3d.Rotation(deg / Deg - br.Rotation, br.Normal, br.Position));
        }

        if (edit.ScaleX is not null || edit.ScaleY is not null)
        {
            var s = br.ScaleFactors;
            br.ScaleFactors = new Scale3d(edit.ScaleX ?? s.X, edit.ScaleY ?? s.Y, s.Z);
        }

        if (edit.Dynamic.Count > 0)
        {
            if (!br.IsDynamicBlock)
            {
                throw new CadException(ErrorCodes.Unsupported, $"Block {handle} is not a dynamic block.");
            }

            foreach (var (name, value) in edit.Dynamic)
            {
                var prop = br.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
                    .FirstOrDefault(x => string.Equals(x.PropertyName, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CadException(ErrorCodes.Unsupported, $"Block {handle} has no dynamic property '{name}'.");
                if (prop.ReadOnly)
                {
                    throw new CadException(ErrorCodes.Unsupported, $"Dynamic property '{name}' is read-only.");
                }

                prop.Value = value; // AutoCAD snaps to the parameter's value set; the harness verifies the result
            }
        }

        foreach (var (tag, value) in edit.Attributes)
        {
            var found = false;
            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (tr.GetObject(attId, OpenMode.ForWrite) is AttributeReference att
                    && string.Equals(att.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    att.TextString = value;
                    found = true;
                }
            }

            if (!found)
            {
                throw new CadException(ErrorCodes.Unsupported, $"Block {handle} has no attribute '{tag}'.");
            }
        }
    }

    public string Create(CreateSpec spec)
    {
        var ms = (BlockTableRecord)tr.GetObject(ModelSpaceId, OpenMode.ForWrite);
        if (spec.Type == EntityTypes.Hatch)
        {
            return CreateHatch(ms, spec);
        }

        Entity ent = spec.Type switch
        {
            EntityTypes.Line => new Line(Pt(spec.A), Pt(spec.B)),
            EntityTypes.Polyline => MakePolyline(spec),
            EntityTypes.Circle => new Circle(Pt(spec.A), Vector3d.ZAxis, spec.Radius),
            EntityTypes.Arc => new Arc(Pt(spec.A), spec.Radius, spec.StartAngle / Deg, spec.EndAngle / Deg),
            EntityTypes.Text => new DBText { TextString = spec.Text, Position = Pt(spec.A), Height = spec.Height, Rotation = spec.Rotation / Deg },
            EntityTypes.MText => new MText { Contents = spec.Text, Location = Pt(spec.A), TextHeight = spec.Height, Width = spec.Width, Rotation = spec.Rotation / Deg },
            EntityTypes.Insert => MakeInsert(spec),
            EntityTypes.Point => new DBPoint(Pt(spec.A)),
            EntityTypes.Dimension => MakeDimension(spec),
            _ => throw new CadException(ErrorCodes.Unsupported, $"Cannot create {spec.Type}."),
        };

        if (spec.Layer is { } layer)
        {
            EnsureLayer(layer);
            ent.Layer = layer;
        }

        ent.SetDatabaseDefaults(db);
        if (spec.Layer is { } l2)
        {
            ent.Layer = l2; // SetDatabaseDefaults resets the layer to CLAYER
        }

        ApplyAppearance(ent, spec.Appearance);
        ms.AppendEntity(ent);
        tr.AddNewlyCreatedDBObject(ent, true);
        if (ent is BlockReference br)
        {
            AddAttributes(br);
        }

        switch (ent)
        {
            case DBText t:
                if (spec.Style is { } ts)
                {
                    t.TextStyleId = StyleId(ts);
                }

                if (spec.WidthFactor is { } wf)
                {
                    t.WidthFactor = wf;
                }

                if (spec.Justify is { } j && j != "left")
                {
                    t.Justify = TextJustifyValue(j);
                    t.AlignmentPoint = Pt(spec.A);
                }

                AdjustAlignment(t);
                break;
            case MText m:
                if (spec.Style is { } ms2)
                {
                    m.TextStyleId = StyleId(ms2);
                }

                if (spec.Justify is { } mj)
                {
                    m.Attachment = MTextJustifyValue(mj);
                }

                break;
        }

        return ent.Handle.ToString();
    }

    private static Polyline MakePolyline(CreateSpec spec)
    {
        var pl = new Polyline(spec.Points.Count);
        for (var i = 0; i < spec.Points.Count; i++)
        {
            pl.AddVertexAt(i, new Point2d(spec.Points[i].X, spec.Points[i].Y), 0, 0, 0);
        }

        pl.Elevation = spec.Points[0].Z;
        pl.Closed = spec.Closed;
        return pl;
    }

    private BlockReference MakeInsert(CreateSpec spec)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(spec.BlockName))
        {
            throw new CadException(ErrorCodes.NotFound, $"Block '{spec.BlockName}' is not defined in this drawing.");
        }

        return new BlockReference(Pt(spec.A), bt[spec.BlockName])
        {
            Rotation = spec.Rotation / Deg,
            ScaleFactors = new Scale3d(spec.Scale),
        };
    }

    private void AddAttributes(BlockReference br)
    {
        var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
        if (!def.HasAttributeDefinitions)
        {
            return;
        }

        foreach (ObjectId id in def)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
            {
                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                ar.TextString = ad.TextString;
                br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
            }
        }
    }

    private void EnsureLayer(string name)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
        {
            return;
        }

        SymbolUtilityServices.ValidateSymbolName(name, false);
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = name };
        lt.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
    }

    private static Point3d Pt(Vec3 v) => new(v.X, v.Y, v.Z);

    private static JsonArray P(Point3d p) => new Vec3(p.X, p.Y, p.Z).ToJson();
}
