using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace PowerCad.Server;

/// <summary>Read-only native inventory of layouts, blocks, block references and XREFs.</summary>
public sealed partial class CadTools
{
    [McpServerTool(Name = "cad_inventory", ReadOnly = true, Idempotent = true)]
    [Description("List every layout, block definition, block reference and XREF in the live drawing without naming them, in one read-only transaction. "
        + "Layouts: name, tab order, model/paper, plot device and media, viewport/entity counts. Block definitions: effective (dynamic) name, anonymous/layout/xref flags, "
        + "attribute definitions (tag, prompt, default, constant), entity counts by type, nested blocks, insert counts per layout. "
        + "Block references per layout: handle, block and effective name, position, rotation, scale, layer, attribute values, recursing into nested references "
        + "to max_depth (path lists the handles from the layout down; nested positions are in the parent block's coordinates). "
        + "XREFs: path, found/unresolved status, attach/overlay and the nested XREF graph; XREF files are never opened. "
        + "Counts cover the whole drawing; returned rows are bounded and *_truncated flags report omissions.")]
    public Task<string> Inventory(
        [Description("Maximum block definitions returned (1-5000)")] int max_blocks = 500,
        [Description("Maximum block reference rows returned, nested rows included (1-10000)")] int max_references = 2000,
        [Description("Nested reference depth below the layout (0 = top-level only, max 8)")] int max_depth = 2,
        CancellationToken ct = default) =>
        Call("drawing_inventory", new JsonObject { ["max_blocks"] = max_blocks, ["max_references"] = max_references, ["max_depth"] = max_depth }, ct);
}
