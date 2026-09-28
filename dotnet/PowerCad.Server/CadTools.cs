using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

public sealed record TextTarget(
    [property: JsonPropertyName("handle"), Description("Handle of the TEXT/MTEXT entity")] string Handle,
    [property: JsonPropertyName("expect_text"), Description("The text you saw; the edit is refused if it differs now")] string? ExpectText = null,
    [property: JsonPropertyName("expect_fingerprint"), Description("Fingerprint from cad_query/cad_get; refused if the entity changed")] string? ExpectFingerprint = null);

public sealed record EntityTarget(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("expect_fingerprint"), Description("Fingerprint from cad_query/cad_get")] string? ExpectFingerprint = null);

/// <summary>
/// MCP tools. Every mutating tool follows the same contract: preconditions (fingerprint, locked layer)
/// → change inside one AutoCAD transaction → postcondition checks on the touched entities only →
/// commit (or roll back on any failure) → before/after diff. Use dry_run=true to preview.
/// </summary>
[McpServerToolType]
public sealed class CadTools(ICadGateway gateway)
{
    private async Task<string> Call(string command, JsonObject parameters, CancellationToken ct)
    {
        foreach (var key in parameters.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
        {
            parameters.Remove(key);
        }

        try
        {
            var result = await gateway.SendAsync(command, parameters, ct).ConfigureAwait(false);
            return result?.ToJsonString(CadJson.Options) ?? "null";
        }
        catch (CadException e)
        {
            throw new McpException($"[{e.Code}] {e.Message}" + (e.Hint is null ? "" : $" Hint: {e.Hint}"), e);
        }
    }

    private static JsonNode? Node<T>(T? value) => value is null ? null : JsonSerializer.SerializeToNode(value, CadJson.Options);

    private static JsonNode? Point(double[]? p) => p is null ? null : new JsonArray(p.Select(v => (JsonNode)v).ToArray());

    [McpServerTool(Name = "cad_status", ReadOnly = true, Idempotent = true)]
    [Description("Connection and drawing status: backend (autocad/simulator), document name, entity count, available commands. Call first.")]
    public Task<string> Status(CancellationToken ct) => Call("status", [], ct);

    [McpServerTool(Name = "cad_list_targets", ReadOnly = true, Idempotent = true)]
    [Description("List running AutoCAD sessions that have the Power CAD plugin loaded.")]
    public string ListTargets() => gateway.ListTargets().ToJsonString(CadJson.Options);

    [McpServerTool(Name = "cad_select_target", Idempotent = true)]
    [Description("Pin one AutoCAD session (target name or PID from cad_list_targets) when several are running.")]
    public string SelectTarget([Description("Target from cad_list_targets, e.g. autocad-2027-12345")] string target)
    {
        try
        {
            return gateway.SelectTarget(target).ToJsonString(CadJson.Options);
        }
        catch (CadException e)
        {
            throw new McpException($"[{e.Code}] {e.Message}", e);
        }
    }

    [McpServerTool(Name = "cad_query", ReadOnly = true, Idempotent = true)]
    [Description("Find model-space entities. Returns handle, type, layer, fingerprint and geometry (text, position, points, block name, width, dynamic properties, attributes). Filter narrowly; results are capped.")]
    public Task<string> Query(
        [Description("Entity types: LINE, LWPOLYLINE, CIRCLE, ARC, TEXT, MTEXT, INSERT (blocks/doors/windows), POINT")] string[]? types = null,
        [Description("Layer names")] string[]? layers = null,
        [Description("Exact handles")] string[]? handles = null,
        [Description("Case-insensitive substring of TEXT/MTEXT/attribute text")] string? text_contains = null,
        [Description("Regular expression over text")] string? text_regex = null,
        [Description("Block name, wildcards * and ? allowed (e.g. DOOR*)")] string? block_name = null,
        [Description("Window [[xmin,ymin],[xmax,ymax]] tested against each entity's reference point")] double[][]? within = null,
        [Description("1-1000, default 100")] int? max_results = null,
        CancellationToken ct = default) =>
        Call("query", new JsonObject
        {
            ["types"] = Node(types),
            ["layers"] = Node(layers),
            ["handles"] = Node(handles),
            ["text_contains"] = text_contains,
            ["text_regex"] = text_regex,
            ["block_name"] = block_name,
            ["within"] = Node(within),
            ["max_results"] = max_results,
        }, ct);

    [McpServerTool(Name = "cad_get", ReadOnly = true, Idempotent = true)]
    [Description("Read specific entities by handle (current state + fingerprint). Missing handles are listed separately.")]
    public Task<string> Get([Description("Entity handles")] string[] handles, CancellationToken ct = default) =>
        Call("get", new JsonObject { ["handles"] = Node(handles) }, ct);

    [McpServerTool(Name = "cad_replace_text", Destructive = true)]
    [Description("Change TEXT/MTEXT content. Mode A: 'targets' + 'new_text' (pin each with expect_text/expect_fingerprint). Mode B: 'find' + 'replace' across the drawing, capped by max_changes. Verified after the change; rolled back on mismatch.")]
    public Task<string> ReplaceText(
        [Description("Mode A: entities to set to new_text")] TextTarget[]? targets = null,
        [Description("Mode A: the new full text")] string? new_text = null,
        [Description("Mode B: text (or regex) to find")] string? find = null,
        [Description("Mode B: replacement")] string? replace = null,
        [Description("Mode B: treat find as a regular expression")] bool? regex = null,
        [Description("Mode B: default true")] bool? case_sensitive = null,
        [Description("Mode B: only these layers")] string[]? layers = null,
        [Description("Mode B: refuse if more entities would change (default 50)")] int? max_changes = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("replace_text", new JsonObject
        {
            ["targets"] = Node(targets),
            ["new_text"] = new_text,
            ["find"] = find,
            ["replace"] = replace,
            ["regex"] = regex,
            ["case_sensitive"] = case_sensitive,
            ["layers"] = Node(layers),
            ["max_changes"] = max_changes,
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_move", Destructive = true)]
    [Description("Move entities by a displacement (or from→to). Each entity's reference point is verified after the move.")]
    public Task<string> Move(
        [Description("Entities to move, optionally pinned by fingerprint")] EntityTarget[] targets,
        [Description("[dx, dy] or [dx, dy, dz]")] double[]? displacement = null,
        [Description("Base point (alternative to displacement)")] double[]? from = null,
        [Description("Destination point (with from)")] double[]? to = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("move", new JsonObject
        {
            ["targets"] = Node(targets),
            ["displacement"] = Point(displacement),
            ["from"] = Point(from),
            ["to"] = Point(to),
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_modify_opening", Destructive = true)]
    [Description("Modify a door/window/opening block reference: width (dynamic Width/Distance parameter, else X scale from the block definition), position or slide along its wall axis, rotation, hand/facing flip, attribute values. All requested values are verified afterwards.")]
    public Task<string> ModifyOpening(
        [Description("Handle of the INSERT")] string handle,
        [Description("Fingerprint from cad_query/cad_get")] string? expect_fingerprint = null,
        [Description("New opening width in drawing units")] double? width = null,
        [Description("New insertion point")] double[]? position = null,
        [Description("Distance to slide along the block's X axis (the host wall)")] double? slide = null,
        [Description("New rotation in degrees")] double? rotation = null,
        [Description("Mirror hinge side (X)")] bool? flip_hand = null,
        [Description("Mirror swing side (Y)")] bool? flip_facing = null,
        [Description("Attribute tag → new value, e.g. {\"DOOR_NO\":\"D2\"}")] Dictionary<string, string>? attributes = null,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("modify_opening", new JsonObject
        {
            ["handle"] = handle,
            ["expect_fingerprint"] = expect_fingerprint,
            ["width"] = width,
            ["position"] = Point(position),
            ["slide"] = slide,
            ["rotation"] = rotation,
            ["flip_hand"] = flip_hand,
            ["flip_facing"] = flip_facing,
            ["attributes"] = Node(attributes),
            ["dry_run"] = dry_run,
        }, ct);

    [McpServerTool(Name = "cad_create", Destructive = false)]
    [Description("Create entities. Each item: {type: line|polyline|circle|arc|text|mtext|insert, layer?, ...}. line{start,end}; polyline{points,closed}; circle{center,radius}; arc{center,radius,start_angle,end_angle}; text{text,position,height?,rotation?}; mtext{text,position,height?,width?}; insert{name,position,rotation?,scale?}.")]
    public Task<string> Create(
        [Description("Entities to create (max 200)")] JsonElement entities,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("create", new JsonObject { ["entities"] = JsonNode.Parse(entities.GetRawText()), ["dry_run"] = dry_run }, ct);

    [McpServerTool(Name = "cad_batch", Destructive = true)]
    [Description("Run up to 20 edit steps atomically in ONE transaction: [{command: replace_text|move|modify_opening|create, params: {...same as the tools...}}]. Any failed step or check rolls everything back.")]
    public Task<string> Batch(
        [Description("Steps, max 20")] JsonElement steps,
        [Description("Preview: run, verify and report, then roll back")] bool dry_run = false,
        CancellationToken ct = default) =>
        Call("batch", new JsonObject { ["steps"] = JsonNode.Parse(steps.GetRawText()), ["dry_run"] = dry_run }, ct);
}
