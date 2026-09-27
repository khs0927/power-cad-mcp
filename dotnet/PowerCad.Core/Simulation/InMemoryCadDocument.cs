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

    // ----------------------------------------------------------------- setup
    public void AddLayer(string name, bool locked = false)
    {
        lock (_lock)
        {
            _store.Layers[name] = locked;
        }
    }

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

        public Dictionary<string, bool> Layers { get; init; } = new(StringComparer.OrdinalIgnoreCase) { ["0"] = false };

        public Dictionary<string, BlockDef> Blocks { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public long NextHandle { get; set; } = 0x2A0;

        public Store Clone() => new()
        {
            Entities = Entities.ToDictionary(kv => kv.Key, kv => kv.Value with { Props = (JsonObject)kv.Value.Props.DeepClone() }, StringComparer.OrdinalIgnoreCase),
            Order = [.. Order],
            Layers = new Dictionary<string, bool>(Layers, StringComparer.OrdinalIgnoreCase),
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

        public bool IsLayerLocked(string layer) => store.Layers.TryGetValue(layer, out var locked) && locked;

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
            foreach (var key in new[] { "position", "start", "end", "center" })
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
            var layer = spec.Layer ?? "0";
            if (!store.Layers.ContainsKey(layer))
            {
                store.Layers[layer] = false;
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
                    break;
                case EntityTypes.MText:
                    props["text"] = spec.Text;
                    props["position"] = spec.A.ToJson();
                    props["height"] = CadJson.Round(spec.Height);
                    props["width"] = CadJson.Round(spec.Width);
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

            var entity = new EntityState(handle, spec.Type, layer, props);
            store.Entities[handle] = entity;
            store.Order.Add(handle);
            if (spec.Type == EntityTypes.Insert)
            {
                UpdateWidth(entity);
            }

            return handle;
        }
    }
}
