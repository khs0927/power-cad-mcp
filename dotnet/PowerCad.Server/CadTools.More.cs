using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

/// <summary>Drawing resources, layers, entity properties, delete/copy/transform and view tools.</summary>
public sealed partial class CadTools
{
    private static JsonNode? Any(JsonElement? e) => e is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } v ? JsonNode.Parse(v.GetRawText()) : null;

    private static JsonNode? Window(double[][]? w) => w is null ? null : JsonSerializer.SerializeToNode(w, CadJson.Options);

    [McpServerTool(Name = "cad_inspect", ReadOnly = true, Idempotent = true)]
    [Description("Drawing resources: units, current layer/text style/dim style/linetype, text styles (font), dimension styles (scale, text height, arrow), linetypes, blocks (extents, attribute tags, reference count), model-space extents. Use before creating styled text, dimensions or inserts.")]
    public Task<string> Inspect(
        [Description("Only these sections: current, text_styles, dim_styles, linetypes, blocks, extents")] string[]? sections = null,
        CancellationToken ct = default) =>
        Call("inspect", new JsonObject { ["sections"] = Node(sections) }, ct);

    [McpServerTool(Name = "cad_layers", ReadOnly = true, Idempotent = true)]
    [Description("List layers with color, linetype, lineweight, on/frozen/locked/plot state and which one is current.")]
    public Task<string> Layers([Description("Only these layer names")] string[]? names = null, CancellationToken ct = default) =>
        Call("layers", new JsonObject { ["names"] = Node(names) }, ct);

    [McpServerTool(Name = "cad_set_layer", Destructive = true)]
    [Description("Create a layer or change its color/linetype/lineweight/on/frozen/plot/description, optionally make it current. Unlocking a locked layer requires user_confirmed_unlock=true after asking the user. Verified afterwards.")]
    public Task<string> SetLayer(
        [Description("Layer name")] string name,
        [Description("ACI 1-255, color name or #rrggbb")] JsonElement? color = null,
        [Description("Linetype name; loaded from acadiso.lin/acad.lin if missing (e.g. CENTER, DASHED, HIDDEN)")] string? linetype = null,
        [Description("Lineweight in mm (0.25) or 'default'")] JsonElement? lineweight = null,
        [Description("Layer on (visible)")] bool? on = null,
        [Description("Frozen")] bool? frozen = null,
        [Description("Locked")] bool? locked = null,
        [Description("Plottable")] bool? plot = null,
        [Description("Layer description")] string? description = null,
        [Description("Make it the current layer")] bool? make_current = null,
        [Description("Create the layer when missing (default true)")] bool? create = null,
        [Description("Only after the user explicitly agreed to unlock this layer")] bool? user_confirmed_unlock = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("set_layer", new JsonObject
        {
            ["name"] = name,
            ["color"] = Any(color),
            ["linetype"] = linetype,
            ["lineweight"] = Any(lineweight),
            ["on"] = on,
            ["frozen"] = frozen,
            ["locked"] = locked,
            ["plot"] = plot,
            ["description"] = description,
            ["make_current"] = make_current,
            ["create"] = create,
            ["user_confirmed_unlock"] = user_confirmed_unlock,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_delete", Destructive = true)]
    [Description("Erase entities. Pin each target with expect_fingerprint from cad_query/cad_get; refused on locked layers; verified that the entities are gone. Preview with dry_run.")]
    public Task<string> Delete(
        [Description("Entities to erase")] EntityTarget[] targets,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("delete", new JsonObject { ["targets"] = Node(targets), ["dry_run"] = dry_run }, ct);

    [McpServerTool(Name = "cad_set_properties", Destructive = true)]
    [Description("Change entity properties: layer, color, linetype, linetype_scale, lineweight; for TEXT/MTEXT also height, rotation, style, justify (the text stays where it is: its current insertion/alignment point becomes the new anchor) and width_factor (TEXT). Each value is verified.")]
    public Task<string> SetProperties(
        [Description("Entities to change")] EntityTarget[] targets,
        [Description("Layer (created if missing)")] string? layer = null,
        [Description("ACI 1-255 | bylayer | byblock | name | #rrggbb")] JsonElement? color = null,
        [Description("Linetype name or ByLayer/ByBlock")] string? linetype = null,
        [Description("Linetype scale")] double? linetype_scale = null,
        [Description("Lineweight in mm (0.25) | bylayer | byblock | default")] JsonElement? lineweight = null,
        [Description("Text height")] double? height = null,
        [Description("Text rotation in degrees")] double? rotation = null,
        [Description("Text style name")] string? style = null,
        [Description("TEXT: left|center|right|middle|TL..BR; MTEXT: TL..BR")] string? justify = null,
        [Description("TEXT width factor")] double? width_factor = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("set_properties", new JsonObject
        {
            ["targets"] = Node(targets),
            ["layer"] = layer,
            ["color"] = Any(color),
            ["linetype"] = linetype,
            ["linetype_scale"] = linetype_scale,
            ["lineweight"] = Any(lineweight),
            ["height"] = height,
            ["rotation"] = rotation,
            ["style"] = style,
            ["justify"] = justify,
            ["width_factor"] = width_factor,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_copy", Destructive = false)]
    [Description("Copy entities by a displacement (or from→to); count>1 makes a linear array (copy i at i×displacement). Block attributes are copied too. Each copy's reference point is verified.")]
    public Task<string> Copy(
        [Description("Entities to copy")] EntityTarget[] targets,
        [Description("[dx, dy] or [dx, dy, dz]")] double[]? displacement = null,
        [Description("Base point (alternative to displacement)")] double[]? from = null,
        [Description("Destination point (with from)")] double[]? to = null,
        [Description("Number of copies (1-200, default 1)")] int? count = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("copy", new JsonObject
        {
            ["targets"] = Node(targets),
            ["displacement"] = Point(displacement),
            ["from"] = Point(from),
            ["to"] = Point(to),
            ["count"] = count,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_transform", Destructive = true)]
    [Description("Rotate (base, angle°), scale (base, factor) or mirror (axis [[x1,y1],[x2,y2]]) entities; copy=true keeps the originals. Text stays readable when mirrored. Reference points (and rotation/radius/height) are verified.")]
    public Task<string> Transform(
        [Description("Entities to transform")] EntityTarget[] targets,
        [Description("rotate | scale | mirror")] string op,
        [Description("Base point for rotate/scale")] double[]? @base = null,
        [Description("Rotation angle in degrees (CCW)")] double? angle = null,
        [Description("Scale factor")] double? factor = null,
        [Description("Mirror axis [[x1,y1],[x2,y2]]")] double[][]? axis = null,
        [Description("Keep the originals and transform copies")] bool? copy = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("transform", new JsonObject
        {
            ["targets"] = Node(targets),
            ["op"] = op,
            ["base"] = Point(@base),
            ["angle"] = angle,
            ["factor"] = factor,
            ["axis"] = Window(axis),
            ["copy"] = copy,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_zoom", Idempotent = true)]
    [Description("Zoom AutoCAD's model view to a window, to entities (handles) or to the drawing extents. Does not change the drawing.")]
    public Task<string> Zoom(
        [Description("Window [[xmin,ymin],[xmax,ymax]]")] double[][]? window = null,
        [Description("Zoom to these entities")] string[]? handles = null,
        [Description("Zoom to everything in model space")] bool? extents = null,
        [Description("Extra margin as a fraction of the window size (default 0.05)")] double? margin = null,
        CancellationToken ct = default) =>
        Call("zoom", new JsonObject { ["window"] = Window(window), ["handles"] = Node(handles), ["extents"] = extents, ["margin"] = margin }, ct);

    [McpServerTool(Name = "cad_snapshot", ReadOnly = false, Idempotent = true)]
    [Description("Zoom to a window / entities / extents and return a PNG picture of AutoCAD's model view, to check a drawing visually after edits. Changes only the view, never the drawing.")]
    public async Task<CallToolResult> Snapshot(
        [Description("Window [[xmin,ymin],[xmax,ymax]]")] double[][]? window = null,
        [Description("Show these entities")] string[]? handles = null,
        [Description("Show everything in model space")] bool? extents = null,
        [Description("Extra margin as a fraction of the window size (default 0.05)")] double? margin = null,
        [Description("Image width in pixels (64-4000, default 1600)")] int? width = null,
        [Description("Image height in pixels (default: from the window's aspect ratio)")] int? height = null,
        CancellationToken ct = default)
    {
        var json = await Call("snapshot", new JsonObject
        {
            ["window"] = Window(window),
            ["handles"] = Node(handles),
            ["extents"] = extents,
            ["margin"] = margin,
            ["width"] = width,
            ["height"] = height,
        }, ct).ConfigureAwait(false);
        var o = JsonNode.Parse(json)!.AsObject();
        var data = o["image_base64"]?.GetValue<string>() ?? throw new McpException("The CAD backend returned no image.");
        o.Remove("image_base64");
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(Convert.FromBase64String(data), o["mime_type"]?.GetValue<string>() ?? "image/png"),
                new TextContentBlock { Text = o.ToJsonString(CadJson.Options) },
            ],
        };
    }
}
