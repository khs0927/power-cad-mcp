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

    [McpServerTool(Name = "cad_export_block", Idempotent = true)]
    [Description("Save a block definition from the current drawing as a reusable asset: a DWG in the block library "
        + "(default Documents\\PowerCad\\blocks, override with POWER_CAD_BLOCK_LIBRARY) plus a JSON card with its texts, "
        + "size, attributes and source drawing. The drawing itself is not changed.")]
    public Task<string> ExportBlock(
        [Description("Block name in the current drawing (see cad_inspect blocks)")] string name,
        [Description("Optional file path or name inside the library; default <library>/<name>.dwg")] string? path = null,
        [Description("What the asset is / when to use it")] string? description = null,
        [Description("Search tags, e.g. [\"단열\", \"표\"]")] string[]? tags = null,
        CancellationToken ct = default) =>
        Call("export_block", new JsonObject { ["name"] = name, ["path"] = path, ["description"] = description, ["tags"] = Node(tags) }, ct);

    [McpServerTool(Name = "cad_import_block", Destructive = false)]
    [Description("Define a library block (or any DWG file) as a block in the current drawing, so cad_create insert can place it. "
        + "Does nothing if the drawing already has the block unless replace:true (redefines it and updates every existing reference).")]
    public Task<string> ImportBlock(
        [Description("Asset name in the library (also the block name to create)")] string? name = null,
        [Description("DWG file path instead of a library name")] string? path = null,
        [Description("Redefine an existing block of the same name")] bool replace = false,
        [Description("Only report what would happen")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("import_block", new JsonObject { ["name"] = name, ["path"] = path, ["replace"] = replace, ["dry_run"] = dry_run }, ct);

    [McpServerTool(Name = "cad_block_library", ReadOnly = true, Idempotent = true)]
    [Description("List reusable block assets in the block library (name, path, description, tags, size, texts, source drawing).")]
    public string BlockLibraryList() =>
        new JsonObject { ["library"] = PowerCad.Core.Model.BlockLibrary.Directory, ["assets"] = PowerCad.Core.Model.BlockLibrary.List() }
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [McpServerTool(Name = "cad_export_hatch_pattern", Idempotent = true)]
    [Description("Register a hatch's pattern with AutoCAD: rebuilds the .pat definition from a hatch in the drawing "
        + "(e.g. a custom pattern like KHAT47 whose .pat file is missing) and writes <name>.pat into AutoCAD's user Support "
        + "folder, so cad_create hatch and the HATCH command can use it by name in any drawing. The drawing is not changed.")]
    public Task<string> ExportHatchPattern(
        [Description("Handle of a hatch that uses the pattern")] string handle,
        [Description("Pattern name to register (default: the hatch's pattern name)")] string? name = null,
        [Description("Description after the name in the .pat header")] string? description = null,
        [Description("Folder to write to (default: AutoCAD user Support folder)")] string? folder = null,
        [Description("Replace an existing .pat of the same name")] bool overwrite = false,
        CancellationToken ct = default) =>
        Call("export_hatch_pattern", new JsonObject { ["handle"] = handle, ["name"] = name, ["description"] = description, ["folder"] = folder, ["overwrite"] = overwrite }, ct);

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

    [McpServerTool(Name = "cad_measure", ReadOnly = true, Idempotent = true)]
    [Description("Measure without changing anything: length of LINE/ARC/polyline, perimeter and area of closed polylines (arc segments included) and circles, hatch area, plus totals; 'points' measures a path. In mm drawings areas are also given in m² (room areas).")]
    public Task<string> Measure(
        [Description("Entities to measure")] string[]? handles = null,
        [Description("Path [[x,y],[x,y],...] to measure leg by leg")] double[][]? points = null,
        CancellationToken ct = default) =>
        Call("measure", new JsonObject { ["handles"] = Node(handles), ["points"] = Window(points) }, ct);

    [McpServerTool(Name = "cad_offset", Destructive = false)]
    [Description("Offset LINE, straight-edged LWPOLYLINE (corners mitred), CIRCLE or ARC by a distance, like AutoCAD OFFSET; count>1 makes parallel copies at distance×1..count (e.g. wall faces). The new entities keep the source's layer and look (or 'layer'); each is verified.")]
    public Task<string> Offset(
        [Description("Entities to offset")] EntityTarget[] targets,
        [Description("Offset distance (> 0)")] double distance,
        [Description("left | right (relative to the line/polyline direction) | inside | outside (circles, arcs, closed polylines)")] string? side = null,
        [Description("A point on the wanted side (alternative to side)")] double[]? through = null,
        [Description("Number of parallel copies (1-50, default 1)")] int? count = null,
        [Description("Layer for the new entities (default: the source's layer)")] string? layer = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("offset", new JsonObject
        {
            ["targets"] = Node(targets),
            ["distance"] = distance,
            ["side"] = side,
            ["through"] = Point(through),
            ["count"] = count,
            ["layer"] = layer,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_save", Destructive = true)]
    [Description("Write the drawing to disk. mode=copy (default) writes a DWG/DXF copy to 'path' and leaves the open drawing untouched; an existing file is refused unless overwrite=true. mode=save saves the open drawing itself (DWG) and needs user_confirmed=true after asking the user; 'path' is required for a never-saved drawing.")]
    public Task<string> Save(
        [Description("copy | save")] string? mode = null,
        [Description("Absolute path ending in .dwg or .dxf")] string? path = null,
        [Description("dwg | dxf (default: from the path's extension)")] string? format = null,
        [Description("Replace an existing file (only after the user agreed)")] bool? overwrite = null,
        [Description("mode=save only: the user explicitly agreed to save the open drawing")] bool? user_confirmed = null,
        CancellationToken ct = default) =>
        Call("save", new JsonObject
        {
            ["mode"] = mode,
            ["path"] = path,
            ["format"] = format,
            ["overwrite"] = overwrite,
            ["user_confirmed"] = user_confirmed,
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
