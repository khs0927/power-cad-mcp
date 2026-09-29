using System.Globalization;
using System.Text.Json.Nodes;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;

namespace PowerCad.Core.Simulation;

/// <summary>
/// A transactional in-memory drawing with the same contract as the AutoCAD adapter. It powers the
/// server's <c>--simulate</c> mode (try the tools without AutoCAD) and the automated tests.
/// Each transaction works on a copy; only a committed transaction replaces the drawing.
/// </summary>
public sealed class InMemoryCadDocument : ICadDocument
{
    private readonly Lock _lock = new();
    private Store _store = new();

    public string Name { get; set; } = "Simulated.dwg";

    /// <summary>Test hook imitating CAD-side behaviour (e.g. a font that cannot store some characters).</summary>
    public Func<string, string>? TextFilter { get; set; }

    public int CommitCount { get; private set; }

    public JsonObject Describe()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["backend"] = "simulator",
                ["application"] = "Power CAD in-memory simulator",
                ["document"] = Name,
                ["entity_count"] = _store.Entities.Count,
                ["current_layer"] = _store.CurrentLayer,
                ["layers"] = new JsonArray(_store.Layers.Keys.Order().Select(l => (JsonNode)l).ToArray()),
                ["blocks"] = new JsonArray(_store.Blocks.Keys.Order().Select(b => (JsonNode)b).ToArray()),
            };
        }
    }

    public T Execute<T>(Func<ICadTransaction, T> work, bool commit)
    {
        lock (_lock)
        {
            var working = _store.Clone();
            var result = work(new Tx(working, this));
            if (commit)
            {
                _store = working;
                CommitCount++;
            }

            return result;
        }
    }

    /// <summary>A 1x1 white PNG: the simulator has no renderer, but the snapshot contract stays testable.</summary>
    private static readonly byte[] BlankPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");

    public (Vec3 Min, Vec3 Max)? LastView { get; private set; }

    public JsonObject View(Vec3 min, Vec3 max, int? snapshotWidth, int? snapshotHeight)
    {
        LastView = (min, max);
        var o = new JsonObject { ["zoomed"] = true };
        if (snapshotWidth is not null)
        {
            o["mime_type"] = "image/png";
            o["width"] = 1;
            o["height"] = 1;
            o["image_base64"] = Convert.ToBase64String(BlankPng);
            o["note"] = "simulator: blank image";
        }

        return o;
    }

    // ----------------------------------------------------------------- setup
    public void AddLayer(string name, bool locked = false)
    {
        lock (_lock)
        {
            _store.Layers[name] = NewLayer(name);
            _store.Layers[name]["locked"] = locked;
        }
    }

    private static JsonObject NewLayer(string name) => new()
    {
        ["name"] = name,
        ["color"] = 7,
        ["linetype"] = "Continuous",
        ["lineweight"] = "default",
        ["on"] = true,
        ["frozen"] = false,
        ["locked"] = false,
        ["plot"] = true,
    };

    public void DefineBlock(string name, double width, IReadOnlyDictionary<string, double>? dynamic = null, IReadOnlyDictionary<string, string>? attributes = null)
    {
        lock (_lock)
        {
            _store.Blocks[name] = new BlockDef(width, dynamic?.ToDictionary() ?? [], attributes?.ToDictionary() ?? []);
        }
    }

    public string Add(CreateSpec spec) => Execute(tx => tx.Create(spec), commit: true);

    /// <summary>A small apartment plan: walls, room names, a dynamic door, a plain window, a locked layer.</summary>
    public static InMemoryCadDocument CreateSample()
    {
        var doc = new InMemoryCadDocument { Name = "Sample-Plan.dwg" };
        doc.AddLayer("A-WALL");
        doc.AddLayer("A-DOOR");
        doc.AddLayer("A-GLAZ");
        doc.AddLayer("A-ANNO");
        doc.AddLayer("A-ANNO-LOCKED", locked: true);
        doc.DefineBlock("DOOR_SINGLE", 900, new Dictionary<string, double> { ["Width"] = 900 }, new Dictionary<string, string> { ["DOOR_NO"] = "D1" });
        doc.DefineBlock("WINDOW_1200", 1200);

        Vec3[] walls = [new(0, 0), new(12000, 0), new(12000, 8000), new(0, 8000)];
        for (var i = 0; i < walls.Length; i++)
        {
            doc.Add(new CreateSpec(EntityTypes.Line, "A-WALL") { A = walls[i], B = walls[(i + 1) % walls.Length] });
        }

        doc.Add(new CreateSpec(EntityTypes.Line, "A-WALL") { A = new(6000, 0), B = new(6000, 8000) });
        doc.Add(new CreateSpec(EntityTypes.Text, "A-ANNO") { Text = "거실", A = new(2500, 4000), Height = 300 });
        doc.Add(new CreateSpec(EntityTypes.Text, "A-ANNO") { Text = "침실 1", A = new(8500, 4000), Height = 300 });
        doc.Add(new CreateSpec(EntityTypes.MText, "A-ANNO") { Text = "주방 KITCHEN", A = new(2500, 6500), Height = 250, Width = 2000 });
        doc.Add(new CreateSpec(EntityTypes.Insert, "A-DOOR") { BlockName = "DOOR_SINGLE", A = new(6000, 1000), Rotation = 90 });
        doc.Add(new CreateSpec(EntityTypes.Insert, "A-GLAZ") { BlockName = "WINDOW_1200", A = new(2000, 0) });
        doc.Add(new CreateSpec(EntityTypes.Text, "A-ANNO-LOCKED") { Text = "도면번호 A-101", A = new(10000, -800), Height = 200 });
        return doc;
    }

    // --------------------------------------------------------------- storage
    private sealed record BlockDef(double Width, Dictionary<string, double> Dynamic, Dictionary<string, string> Attributes);

    private sealed class Store
    {
        public Dictionary<string, EntityState> Entities { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Order { get; init; } = [];

        public Dictionary<string, JsonObject> Layers { get; init; } = new(StringComparer.OrdinalIgnoreCase) { ["0"] = NewLayer("0") };

        public string CurrentLayer { get; set; } = "0";

        public HashSet<string> TextStyles { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "Standard" };

        public HashSet<string> DimStyles { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "Standard", "ISO-25" };

        public HashSet<string> Linetypes { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "ByLayer", "ByBlock", "Continuous" };

        public Dictionary<string, BlockDef> Blocks { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public long NextHandle { get; set; } = 0x2A0;

        public Store Clone() => new()
        {
            Entities = Entities.ToDictionary(kv => kv.Key, kv => kv.Value with { Props = (JsonObject)kv.Value.Props.DeepClone() }, StringComparer.OrdinalIgnoreCase),
            Order = [.. Order],
            Layers = Layers.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone(), StringComparer.OrdinalIgnoreCase),
            CurrentLayer = CurrentLayer,
            TextStyles = new HashSet<string>(TextStyles, StringComparer.OrdinalIgnoreCase),
            DimStyles = new HashSet<string>(DimStyles, StringComparer.OrdinalIgnoreCase),
            Linetypes = new HashSet<string>(Linetypes, StringComparer.OrdinalIgnoreCase),
            Blocks = new Dictionary<string, BlockDef>(Blocks, StringComparer.OrdinalIgnoreCase),
            NextHandle = NextHandle,
        };
    }

    private sealed class Tx(Store store, InMemoryCadDocument owner) : ICadTransaction
    {
        public IEnumerable<EntityState> ScanModelSpace() => store.Order.Select(h => Snapshot(store.Entities[h])).ToList();

        public EntityState Read(string handle) =>
            store.Entities.TryGetValue(handle.Trim(), out var e)
                ? Snapshot(e)
                : throw new CadException(ErrorCodes.NotFound, $"No model-space entity with handle '{handle}'.", "Query the drawing to get current handles.");

        private static EntityState Snapshot(EntityState e) => e with { Props = (JsonObject)e.Props.DeepClone() };

        private EntityState Mutable(string handle)
        {
            var e = store.Entities.TryGetValue(handle.Trim(), out var found)
                ? found
                : throw new CadException(ErrorCodes.NotFound, $"No model-space entity with handle '{handle}'.");
            if (IsLayerLocked(e.Layer))
            {
                throw new CadException(ErrorCodes.LockedLayer, $"Entity {e.Handle} is on locked layer '{e.Layer}'.");
            }

            return e;
        }

        public bool IsLayerLocked(string layer) => store.Layers.TryGetValue(layer, out var rec) && rec["locked"]!.GetValue<bool>();

        public void SetText(string handle, string text)
        {
            var e = Mutable(handle);
            if (!EntityTypes.TextLike.Contains(e.Type))
            {
                throw new CadException(ErrorCodes.Unsupported, $"{e.Type} has no text.");
            }

            e.Props["text"] = owner.TextFilter is { } f ? f(text) : text;
        }

        public void Move(string handle, Vec3 d)
        {
            var e = Mutable(handle);
            foreach (var key in new[] { "position", "start", "end", "center", "xline1", "xline2", "dimline", "alignment_point" })
            {
                if (e.Props[key] is JsonArray)
                {
                    e.Props[key] = (Vec3.FromJson(e.Props[key], key) + d).ToJson();
                }
            }

            if (e.Props["points"] is JsonArray pts)
            {
                e.Props["points"] = new JsonArray(pts.Select((p, i) => (JsonNode)(Vec3.FromJson(p, $"points[{i}]") + d).ToJson()).ToArray());
            }
        }

        public void EditInsert(string handle, InsertEdit edit)
        {
            var e = Mutable(handle);
            if (e.Type != EntityTypes.Insert)
            {
                throw new CadException(ErrorCodes.Unsupported, $"{e.Type} is not a block reference.");
            }

            if (edit.Position is { } pos)
            {
                e.Props["position"] = pos.ToJson();
            }

            if (edit.RotationDegrees is { } rot)
            {
                e.Props["rotation"] = CadJson.Round(((rot % 360) + 360) % 360);
            }

            var scale = Vec3.FromJson(e.Props["scale"], "scale");
            scale = new Vec3(edit.ScaleX ?? scale.X, edit.ScaleY ?? scale.Y, scale.Z);
            e.Props["scale"] = scale.ToJson();

            var dyn = e.Props["dynamic"] as JsonObject;
            foreach (var (name, value) in edit.Dynamic)
            {
                var key = dyn?.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CadException(ErrorCodes.Unsupported, $"Block has no dynamic property '{name}'.");
                dyn![key] = CadJson.Round(value);
            }

            var attrs = e.Props["attributes"] as JsonObject;
            foreach (var (tag, value) in edit.Attributes)
            {
                var key = attrs?.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, tag, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CadException(ErrorCodes.Unsupported, $"Block has no attribute '{tag}'.");
                attrs![key] = value;
            }

            UpdateWidth(e);
        }

        private void UpdateWidth(EntityState e)
        {
            var name = e.Props["name"]!.GetValue<string>();
            var width = Openings.EffectiveWidth(
                e.Props["dynamic"] as JsonObject,
                store.Blocks.TryGetValue(name, out var def) ? def.Width : null,
                Vec3.FromJson(e.Props["scale"], "scale").X);
            e.Props["width"] = width is { } w ? CadJson.Round(w) : null;
        }

        public double? BlockDefinitionWidth(string blockName) => store.Blocks.TryGetValue(blockName, out var def) ? def.Width : null;

        public bool BlockExists(string blockName) => store.Blocks.ContainsKey(blockName);

        public string Create(CreateSpec spec)
        {
            var layer = spec.Layer ?? store.CurrentLayer;
            if (!store.Layers.ContainsKey(layer))
            {
                store.Layers[layer] = NewLayer(layer);
            }

            var handle = (store.NextHandle++).ToString("X", CultureInfo.InvariantCulture);
            var props = new JsonObject();
            switch (spec.Type)
            {
                case EntityTypes.Line:
                    props["start"] = spec.A.ToJson();
                    props["end"] = spec.B.ToJson();
                    props["length"] = CadJson.Round((spec.B - spec.A).Length);
                    break;
                case EntityTypes.Polyline:
                    props["points"] = new JsonArray(spec.Points.Select(p => (JsonNode)p.ToJson()).ToArray());
                    props["closed"] = spec.Closed;
                    break;
                case EntityTypes.Circle:
                    props["center"] = spec.A.ToJson();
                    props["radius"] = CadJson.Round(spec.Radius);
                    break;
                case EntityTypes.Arc:
                    props["center"] = spec.A.ToJson();
                    props["radius"] = CadJson.Round(spec.Radius);
                    props["start_angle"] = CadJson.Round(spec.StartAngle);
                    props["end_angle"] = CadJson.Round(spec.EndAngle);
                    break;
                case EntityTypes.Text:
                    props["text"] = owner.TextFilter is { } f ? f(spec.Text) : spec.Text;
                    props["position"] = spec.A.ToJson();
                    props["height"] = CadJson.Round(spec.Height);
                    props["rotation"] = CadJson.Round(spec.Rotation);
                    props["style"] = spec.Style ?? "Standard";
                    if (spec.Justify is { } j && j != "left")
                    {
                        props["justify"] = j;
                        props["alignment_point"] = spec.A.ToJson();
                    }

                    if (spec.WidthFactor is { } wf && wf != 1)
                    {
                        props["width_factor"] = CadJson.Round(wf);
                    }

                    break;
                case EntityTypes.MText:
                    props["text"] = spec.Text;
                    props["position"] = spec.A.ToJson();
                    props["height"] = CadJson.Round(spec.Height);
                    props["width"] = CadJson.Round(spec.Width);
                    props["rotation"] = CadJson.Round(spec.Rotation);
                    props["style"] = spec.Style ?? "Standard";
                    if (spec.Justify is { } mj && mj != "TL")
                    {
                        props["justify"] = mj;
                    }

                    break;
                case EntityTypes.Point:
                    props["position"] = spec.A.ToJson();
                    break;
                case EntityTypes.Dimension:
                    props["kind"] = spec.DimKind;
                    props["style"] = spec.Style ?? "ISO-25";
                    props["xline1"] = spec.A.ToJson();
                    props["xline2"] = spec.B.ToJson();
                    props["dimline"] = spec.C.ToJson();
                    if (spec.DimKind == "rotated")
                    {
                        props["rotation"] = CadJson.Round(spec.Rotation);
                    }

                    props["measurement"] = CadJson.Round(Measure(spec));
                    if (spec.TextOverride is { Length: > 0 } ov)
                    {
                        props["text_override"] = ov;
                    }

                    break;
                case EntityTypes.Hatch:
                    props["pattern"] = spec.Pattern;
                    props["scale"] = CadJson.Round(spec.Scale);
                    props["angle"] = CadJson.Round(spec.Rotation);
                    props["points"] = new JsonArray(spec.Points.Select(p => (JsonNode)p.ToJson()).ToArray());
                    props["area"] = CadJson.Round(Area(spec.Points));
                    break;
                case EntityTypes.Leader:
                    props["points"] = new JsonArray(spec.Points.Select(p => (JsonNode)p.ToJson()).ToArray());
                    props["style"] = spec.Style ?? "ISO-25";
                    props["arrow"] = spec.BlockName;
                    break;
                case EntityTypes.Insert:
                    var def = store.Blocks.TryGetValue(spec.BlockName, out var d)
                        ? d
                        : throw new CadException(ErrorCodes.NotFound, $"Block '{spec.BlockName}' is not defined.");
                    props["name"] = spec.BlockName;
                    props["position"] = spec.A.ToJson();
                    props["rotation"] = CadJson.Round(spec.Rotation);
                    props["scale"] = new Vec3(spec.Scale, spec.Scale, spec.Scale).ToJson();
                    props["dynamic"] = new JsonObject(def.Dynamic.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
                    props["attributes"] = new JsonObject(def.Attributes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
                    break;
                default:
                    throw new CadException(ErrorCodes.Unsupported, $"Cannot create {spec.Type}.");
            }

            ApplyAppearance(props, spec.Appearance);
            var entity = new EntityState(handle, spec.Type, layer, props);
            store.Entities[handle] = entity;
            store.Order.Add(handle);
            if (spec.Type == EntityTypes.Insert)
            {
                UpdateWidth(entity);
            }

            return handle;
        }

        private static readonly HashSet<string> LoadableLinetypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CENTER", "CENTER2", "CENTERX2", "DASHED", "DASHED2", "DASHEDX2", "HIDDEN", "HIDDEN2", "HIDDENX2",
            "PHANTOM", "PHANTOM2", "DOT", "DOT2", "DASHDOT", "DASHDOT2", "BORDER", "DIVIDE",
        };

        private void EnsureLinetype(string name)
        {
            if (store.Linetypes.Contains(name))
            {
                return;
            }

            if (!LoadableLinetypes.Contains(name))
            {
                throw new CadException(ErrorCodes.NotFound, $"Linetype '{name}' is not defined and not in acad.lin.", "Call cad_inspect to list linetypes.");
            }

            store.Linetypes.Add(name.ToUpperInvariant());
        }

        private void ApplyAppearance(JsonObject props, PropertyEdit look)
        {
            if (look.Color is { } c)
            {
                if (c is JsonValue v && v.TryGetValue<string>(out var cs) && cs == "bylayer")
                {
                    props.Remove("color");
                }
                else
                {
                    props["color"] = c.DeepClone();
                }
            }

            if (look.Linetype is { } lt)
            {
                if (lt.Equals("bylayer", StringComparison.OrdinalIgnoreCase))
                {
                    props.Remove("linetype");
                }
                else
                {
                    EnsureLinetype(lt);
                    props["linetype"] = store.Linetypes.First(x => x.Equals(lt, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (look.Lineweight is { } lw)
            {
                if (lw == Styling.LineweightByLayer)
                {
                    props.Remove("lineweight");
                }
                else
                {
                    props["lineweight"] = Styling.FormatLineweight(lw);
                }
            }

            if (look.LinetypeScale is { } lts)
            {
                props["linetype_scale"] = CadJson.Round(lts);
            }
        }

        private static double Measure(CreateSpec spec)
        {
            var d = spec.B - spec.A;
            if (spec.DimKind == "aligned")
            {
                return Math.Sqrt((d.X * d.X) + (d.Y * d.Y));
            }

            var r = spec.Rotation * Math.PI / 180;
            return Math.Abs((d.X * Math.Cos(r)) + (d.Y * Math.Sin(r)));
        }

        private static double Area(IReadOnlyList<Vec3> pts)
        {
            double a = 0;
            for (var i = 0; i < pts.Count; i++)
            {
                var (p, q) = (pts[i], pts[(i + 1) % pts.Count]);
                a += (p.X * q.Y) - (q.X * p.Y);
            }

            return Math.Abs(a) / 2;
        }

        public void Delete(string handle)
        {
            var e = Mutable(handle);
            store.Entities.Remove(e.Handle);
            store.Order.Remove(e.Handle);
        }

        public void SetProperties(string handle, PropertyEdit edit)
        {
            var e = Mutable(handle);
            if (edit.Layer is { } layer)
            {
                if (!store.Layers.ContainsKey(layer))
                {
                    store.Layers[layer] = NewLayer(layer);
                }

                store.Entities[e.Handle] = e = e with { Layer = store.Layers[layer]["name"]!.GetValue<string>() };
            }

            ApplyAppearance(e.Props, edit);
            if (edit.Height is { } h)
            {
                e.Props["height"] = CadJson.Round(h);
            }

            if (edit.Rotation is { } r)
            {
                e.Props["rotation"] = CadJson.Round(r);
            }

            if (edit.Style is { } st)
            {
                e.Props["style"] = st;
            }

            if (edit.WidthFactor is { } wf)
            {
                if (wf == 1)
                {
                    e.Props.Remove("width_factor");
                }
                else
                {
                    e.Props["width_factor"] = CadJson.Round(wf);
                }
            }

            if (edit.Justify is { } j)
            {
                var anchor = e.Anchor ?? new Vec3(0, 0);
                var dflt = e.Type == EntityTypes.MText ? "TL" : "left";
                if (j == dflt)
                {
                    e.Props.Remove("justify");
                    e.Props.Remove("alignment_point");
                    e.Props["position"] = anchor.ToJson();
                }
                else
                {
                    e.Props["justify"] = j;
                    if (e.Type == EntityTypes.Text)
                    {
                        e.Props["alignment_point"] = anchor.ToJson();
                    }
                }
            }
        }

        public string Copy(string handle, Vec3 d)
        {
            var src = Read(handle);
            var copy = src with { Handle = (store.NextHandle++).ToString("X", CultureInfo.InvariantCulture), Props = (JsonObject)src.Props.DeepClone() };
            store.Entities[copy.Handle] = copy;
            store.Order.Add(copy.Handle);
            if (d.Length > 0)
            {
                Move(copy.Handle, d);
            }

            return copy.Handle;
        }

        public void Transform(string handle, Transform2D t)
        {
            var e = Mutable(handle);
            var text = EntityTypes.TextLike.Contains(e.Type);
            if (t.Op == "mirror" && text)
            {
                // MIRRTEXT=0: text is not reversed, only its anchor is reflected.
                var a = e.Anchor!.Value;
                Move(handle, t.Apply(a) - a);
                return;
            }

            foreach (var key in new[] { "position", "start", "end", "center", "xline1", "xline2", "dimline", "alignment_point" })
            {
                if (e.Props[key] is JsonArray)
                {
                    e.Props[key] = t.Apply(Vec3.FromJson(e.Props[key], key)).ToJson();
                }
            }

            if (e.Props["points"] is JsonArray pts)
            {
                e.Props["points"] = new JsonArray(pts.Select((p, i) => (JsonNode)t.Apply(Vec3.FromJson(p, $"points[{i}]")).ToJson()).ToArray());
            }

            switch (t.Op)
            {
                case "rotate":
                    foreach (var key in new[] { "rotation", "start_angle", "end_angle" })
                    {
                        if (e.Number(key) is { } v && !(e.Type == EntityTypes.Dimension && key == "rotation"))
                        {
                            e.Props[key] = CadJson.Round((((v + t.AngleDegrees) % 360) + 360) % 360);
                        }
                    }

                    break;
                case "scale":
                    foreach (var key in new[] { "radius", "height", "length", "width" })
                    {
                        if (e.Number(key) is { } v && !(e.Type == EntityTypes.Insert && key == "width"))
                        {
                            e.Props[key] = CadJson.Round(v * t.Factor);
                        }
                    }

                    if (e.Props["scale"] is JsonArray sc)
                    {
                        e.Props["scale"] = (Vec3.FromJson(sc, "scale") * t.Factor).ToJson();
                        UpdateWidth(e);
                    }

                    break;
                case "mirror":
                    var ax = t.AxisAngleDegrees;
                    if (e.Number("start_angle") is { } s0 && e.Number("end_angle") is { } e0)
                    {
                        e.Props["start_angle"] = CadJson.Round((((2 * ax) - e0) % 360 + 360) % 360);
                        e.Props["end_angle"] = CadJson.Round((((2 * ax) - s0) % 360 + 360) % 360);
                    }

                    break;
            }
        }

        public IReadOnlyList<JsonObject> Layers() => store.Layers.Values
            .OrderBy(l => l["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .Select(l =>
            {
                var o = (JsonObject)l.DeepClone();
                o["current"] = string.Equals(l["name"]!.GetValue<string>(), store.CurrentLayer, StringComparison.OrdinalIgnoreCase);
                return o;
            })
            .ToList();

        public void SetLayer(LayerEdit edit)
        {
            if (!store.Layers.TryGetValue(edit.Name, out var rec))
            {
                rec = store.Layers[edit.Name] = NewLayer(edit.Name);
            }

            if (edit.Color is { } c)
            {
                rec["color"] = c.DeepClone();
            }

            if (edit.Linetype is { } lt)
            {
                EnsureLinetype(lt);
                rec["linetype"] = store.Linetypes.First(x => x.Equals(lt, StringComparison.OrdinalIgnoreCase));
            }

            if (edit.Lineweight is { } lw)
            {
                rec["lineweight"] = Styling.FormatLineweight(lw);
            }

            foreach (var (key, v) in new[] { ("on", edit.On), ("frozen", edit.Frozen), ("locked", edit.Locked), ("plot", edit.Plot) })
            {
                if (v is { } b)
                {
                    rec[key] = b;
                }
            }

            if (edit.Description is { } d)
            {
                rec["description"] = d;
            }

            if (edit.MakeCurrent)
            {
                store.CurrentLayer = rec["name"]!.GetValue<string>();
            }
        }

        public JsonObject Inspect() => new()
        {
            ["document"] = owner.Name,
            ["units"] = "Millimeters",
            ["current"] = new JsonObject { ["layer"] = store.CurrentLayer, ["text_style"] = "Standard", ["dim_style"] = "ISO-25" },
            ["text_styles"] = new JsonArray(store.TextStyles.Order().Select(x => (JsonNode)new JsonObject { ["name"] = x }).ToArray()),
            ["dim_styles"] = new JsonArray(store.DimStyles.Order().Select(x => (JsonNode)new JsonObject { ["name"] = x }).ToArray()),
            ["linetypes"] = new JsonArray(store.Linetypes.Order().Select(x => (JsonNode)x).ToArray()),
            ["blocks"] = new JsonArray(store.Blocks.OrderBy(b => b.Key).Select(b => (JsonNode)new JsonObject
            {
                ["name"] = b.Key,
                ["width"] = b.Value.Width,
                ["attributes"] = new JsonArray(b.Value.Attributes.Keys.Select(k => (JsonNode)k).ToArray()),
                ["dynamic"] = b.Value.Dynamic.Count > 0,
            }).ToArray()),
            ["layer_count"] = store.Layers.Count,
            ["entity_count"] = store.Entities.Count,
        };

        public JsonObject ExportBlock(string name, string path)
        {
            var def = store.Blocks[name];
            var file = new JsonObject
            {
                ["simulated_block"] = name,
                ["width"] = def.Width,
                ["dynamic"] = new JsonObject(def.Dynamic.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                ["attributes"] = new JsonObject(def.Attributes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            };
            File.WriteAllText(path, file.ToJsonString());
            return new JsonObject { ["width"] = def.Width, ["entity_count"] = 1, ["texts"] = new JsonArray() };
        }

        public void ImportBlock(string path, string name, bool replace)
        {
            var file = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new CadException(ErrorCodes.InvalidParams, $"'{path}' is not a simulated block file.");
            store.Blocks[name] = new BlockDef(
                file["width"]!.GetValue<double>(),
                file["dynamic"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>()),
                file["attributes"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
        }

        public JsonObject HatchPattern(string handle)
        {
            var e = Read(handle);
            if (e.Type != EntityTypes.Hatch)
            {
                throw new CadException(ErrorCodes.Unsupported, $"Entity {handle} is not a hatch.");
            }

            // the simulator only knows ANSI31: one 45 degree line family, 3.175 apart
            var scale = e.Number("scale") ?? 1;
            var angle = (e.Number("angle") ?? 0) + 45;
            var r = angle * Math.PI / 180;
            return new JsonObject
            {
                ["name"] = e.Props["pattern"]!.GetValue<string>(),
                ["type"] = "predefined",
                ["scale"] = scale,
                ["angle"] = e.Number("angle") ?? 0,
                ["lines"] = new JsonArray(new JsonObject
                {
                    ["angle"] = angle,
                    ["base"] = new JsonArray(0.0, 0.0),
                    ["offset"] = new JsonArray(-Math.Sin(r) * 3.175 * scale, Math.Cos(r) * 3.175 * scale),
                }),
            };
        }

        public bool ResourceExists(string kind, string name) => kind switch
        {
            "text_style" => store.TextStyles.Contains(name),
            "dim_style" => store.DimStyles.Contains(name),
            "linetype" => store.Linetypes.Contains(name),
            "layer" => store.Layers.ContainsKey(name),
            "block" => store.Blocks.ContainsKey(name),
            _ => false,
        };
    }
}
