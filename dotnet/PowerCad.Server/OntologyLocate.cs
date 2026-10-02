using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

// Ontology -> CAD (port of the "ontology -> CAD" section of src/power_cad_mcp/ontology.py and its ontology_locate tool).
// Ontology rows carry the DWG/DXF handle the element was parsed from, but a handle is only unique inside one drawing.
// Before an edit tool touches it, the element's source file must be the drawing that is open now, the handle must exist
// there, and the entity must look like the element. Everything here is read-only.

/// <summary>The drawing open in the CAD backend: <c>{name, path}</c> as the Python backend's drawing_info, plus its document_id.</summary>
public sealed record OntologyLiveDrawing(JsonObject Drawing, string? DocumentId);

/// <summary>Reads the open drawing and single entities through the gateway; any CAD failure just means "unknown".</summary>
public static class OntologyCad
{
    /// <summary>The open drawing, or null when none is reachable (no gateway, AutoCAD not running, no document).</summary>
    public static async Task<OntologyLiveDrawing?> OpenDrawingAsync(ICadGateway? gateway, CancellationToken ct)
    {
        if (gateway is null)
        {
            return null;
        }

        JsonNode? info;
        try
        {
            info = await gateway.SendAsync("document_identity", null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // any backend failure just means "no open drawing"
        }

        return Describe(info);
    }

    /// <summary>document_identity / status answer -> <c>{name, path?}</c>. The plugin reports the full path, the simulator a bare name.</summary>
    public static OntologyLiveDrawing? Describe(JsonNode? info)
    {
        if (info is not JsonObject o || o["document"] is not JsonValue v || !v.TryGetValue<string>(out var document))
        {
            return null;
        }

        document = document.Trim().Trim('"');
        if (document.Length == 0)
        {
            return null;
        }

        var name = document.Split('\\', '/')[^1].Trim();
        var drawing = new JsonObject();
        if (name.Length > 0)
        {
            drawing["name"] = name;
        }

        if (name != document)
        {
            drawing["path"] = document;
        }

        var id = o["document_id"] is JsonValue idValue && idValue.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        return new OntologyLiveDrawing(drawing, id);
    }

    /// <summary>
    /// Read one entity by handle; null when it does not exist (or cannot be read). The read is pinned to the document that
    /// was open when the drawing was described, so a drawing switch in between reads nothing instead of an unrelated entity.
    /// </summary>
    public static Func<string, Task<JsonObject?>> Lookup(ICadGateway? gateway, OntologyLiveDrawing? live, CancellationToken ct) => async handle =>
    {
        if (gateway is null)
        {
            return null;
        }

        var parameters = new JsonObject { ["handles"] = new JsonArray(handle) };
        if (live?.DocumentId is { } id)
        {
            parameters["expected_document_id"] = id;
        }

        try
        {
            var result = await gateway.SendAsync("get", parameters, ct).ConfigureAwait(false);
            return (result?["entities"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // a missing/invalid handle is a result, not an error
        }
    };
}

public static partial class OntologyRest
{
    private static readonly string[] CadExtensions = [".dwg", ".dxf", ".dwt", ".dws"];

    private static readonly HashSet<string> Geometry =
        ["LINE", "ARC", "CIRCLE", "ELLIPSE", "LWPOLYLINE", "POLYLINE", "SPLINE", "INSERT", "HATCH", "SOLID", "MLINE", "3DFACE", "REGION"];

    private static readonly HashSet<string> TextTypes = ["TEXT", "MTEXT", "ATTRIB", "ATTDEF"];

    // Element classes whose evidence may legitimately be a text entity (room names, labels, marks).
    private static readonly HashSet<string> TextClasses =
        ["Space", "Room", "Text", "Annotation", "Label", "Note", "Tag", "Mark", "TitleBlock", "DrawingTitle", "Grid", "GridLine"];

    // Entity types that are never a building element.
    private static readonly HashSet<string> NotElement =
        ["DIMENSION", "LEADER", "MLEADER", "MULTILEADER", "VIEWPORT", "XLINE", "RAY", "IMAGE", "OLE2FRAME", "WIPEOUT"];

    private static readonly HashSet<string> PhysicalClasses =
        ["Door", "Window", "Wall", "Column", "Beam", "SteelSection", "Stair", "Slab", "Furniture", "Opening", "CurtainWall"];

    // The live entity fields worth showing (the C# entity JSON calls an INSERT's insertion point "position"), plus the
    // fingerprint the C# edit tools take as expect_fingerprint.
    private static readonly string[] EntityKeys = ["handle", "type", "layer", "name", "text", "insert", "position", "center", "start", "end", "fingerprint"];

    /// <summary>
    /// 'C:\Proj\A-201.DWG' / 'a-201.dxf' / 'A-201' -> 'a-201': basename, case-folded, without a CAD extension, so the DWG an
    /// element was parsed from matches the DXF copy that is open.
    /// </summary>
    public static string? DrawingKey(string? name)
    {
        var text = (name ?? "").Trim().Trim('"');
        if (text.Length == 0)
        {
            return null;
        }

        var key = text.Split('\\', '/')[^1].Trim().ToLowerInvariant();
        foreach (var ext in CadExtensions)
        {
            if (key.EndsWith(ext, StringComparison.Ordinal))
            {
                key = key[..^ext.Length];
                break;
            }
        }

        return key.Length > 0 ? key : null;
    }

    private static string? DrawingKey(JsonNode? name) => DrawingKey(StrOrEmpty(name));

    private static HashSet<string> DrawingKeys(JsonObject? openDrawing)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (openDrawing is null)
        {
            return keys;
        }

        foreach (var key in new[] { DrawingKey(openDrawing["name"]), DrawingKey(openDrawing["path"]) })
        {
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static JsonObject DrawingLabel(JsonObject openDrawing) => DropEmpty(("name", openDrawing["name"]), ("path", openDrawing["path"]));

    /// <summary>True when <paramref name="sourceFile"/> is the open drawing, false when it is another one, null when unknown.</summary>
    public static bool? InDrawing(JsonNode? sourceFile, JsonObject? openDrawing)
    {
        var key = DrawingKey(sourceFile);
        var keys = DrawingKeys(openDrawing);
        return key is null || keys.Count == 0 ? null : keys.Contains(key);
    }

    /// <inheritdoc cref="InDrawing(JsonNode?, JsonObject?)"/>
    public static bool? InDrawing(string? sourceFile, JsonObject? openDrawing) => InDrawing(sourceFile is null ? null : JsonValue.Create(sourceFile), openDrawing);

    public static JsonObject EntitySummary(JsonObject entity)
    {
        var summary = new JsonObject();
        foreach (var key in EntityKeys)
        {
            var value = entity[key];
            if (value is not null && !(value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0))
            {
                summary[key] = value.DeepClone();
            }
        }

        return summary;
    }

    /// <summary>Reasons the live entity does not look like the Ontology element (empty list = plausible).</summary>
    public static List<string> Plausibility(JsonObject element, JsonObject entity)
    {
        var reasons = new List<string>();
        var etype = StrOrEmpty(entity["type"]).ToUpperInvariant();
        var cls = StrOrEmpty(element["class"]);
        if (PhysicalClasses.Contains(cls) || TextClasses.Contains(cls))
        {
            var allowed = TextClasses.Contains(cls) ? Geometry.Concat(TextTypes) : Geometry;
            if (etype.Length > 0 && !allowed.Contains(etype))
            {
                reasons.Add($"a {etype} entity is not a plausible {cls}");
            }
        }
        else if (NotElement.Contains(etype) && cls.Length > 0 && cls is not ("Dimension" or "Annotation" or "Viewport"))
        {
            reasons.Add($"a {etype} entity is not a plausible {cls}");
        }

        var block = StrOrEmpty(element["block_name"]);
        if (block.Length > 0 && etype.Length > 0 && etype != "INSERT")
        {
            reasons.Add($"element is block '{block}' but the entity is a {etype}");
        }
        else if (block.Length > 0 && etype == "INSERT")
        {
            var name = StrOrEmpty(entity["name"]);
            // Dynamic/anonymous references (*U12) carry the effective name only in the Ontology row.
            if (name.Length > 0 && !name.StartsWith('*') && !name.Equals(block, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"element is block '{block}' but the entity inserts '{name}'");
            }
        }

        var layer = StrOrEmpty(element["layer"]);
        var liveLayer = StrOrEmpty(entity["layer"]);
        if (layer.Length > 0 && liveLayer.Length > 0 && !layer.Equals(liveLayer, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"element is on layer '{layer}' but the entity is on '{liveLayer}'");
        }

        return reasons;
    }

    /// <summary>
    /// Decide whether an Ontology element can be acted on in the open drawing. Never edits anything. <paramref name="lookup"/>
    /// returns the live entity or null when the handle does not exist. Status: matched (same drawing, handle exists, entity
    /// plausible), mismatch (handle exists but the entity does not look like the element), handle_missing (same drawing, no
    /// such entity / no handle recorded), other_drawing (another file, or the open drawing is unknown) or not_found.
    /// </summary>
    public static async Task<JsonObject> MatchElementAsync(
        JsonObject? element,
        JsonObject? openDrawing,
        Func<string, Task<JsonObject?>> lookup,
        string? elementId = null)
    {
        var label = openDrawing is null ? null : DrawingLabel(openDrawing);
        if (element is null || element.Count == 0)
        {
            return DropEmpty(("element_id", elementId), ("status", "not_found"), ("open_drawing", label));
        }

        var handle = StrOrEmpty(element["handle"]).Trim();
        var fields = new List<(string Key, JsonNode? Value)>
        {
            ("element_id", Truthy(element["id"]) ? element["id"] : elementId),
            ("class", element["class"]),
            ("name", element["name"]),
            ("handle", handle),
            ("source_file", element["source_file"]),
            ("sheet", element["sheet"]),
            ("open_drawing", label),
        };
        JsonObject Result(params (string Key, JsonNode? Value)[] more) => DropEmpty([.. fields, .. more]);

        var same = InDrawing(element["source_file"], openDrawing);
        if (same != true)
        {
            return same is null
                ? Result(("status", "other_drawing"), ("note", DrawingKeys(openDrawing).Count == 0 ? "no drawing is open" : "the element has no source_file"))
                : Result(("status", "other_drawing"));
        }

        if (handle.Length == 0)
        {
            return Result(("status", "handle_missing"), ("note", "the element has no handle"));
        }

        var entity = await lookup(handle).ConfigureAwait(false);
        if (entity is null || entity.Count == 0)
        {
            var note = "no model-space entity with this handle in the open drawing";
            var sheet = StrOrEmpty(element["sheet"]);
            if (sheet.Length > 0 && !sheet.Equals("model", StringComparison.OrdinalIgnoreCase))
            {
                note += $" (the element was parsed from layout '{sheet}')";
            }

            return Result(("status", "handle_missing"), ("note", note));
        }

        fields.Add(("entity", EntitySummary(entity)));
        var reasons = Plausibility(element, entity);
        return reasons.Count > 0
            ? Result(("status", "mismatch"), ("reasons", new JsonArray(reasons.Select(r => (JsonNode)r).ToArray())))
            : Result(("status", "matched"));
    }

    /// <summary>The element row inside an /v1/elements/{id}/context answer (or a bare element row).</summary>
    public static JsonObject? ElementOf(JsonNode? payload)
    {
        if (payload is not JsonObject obj)
        {
            return null;
        }

        var element = CompactElement(obj["element"] as JsonObject ?? obj);
        return Truthy(element["id"]) ? element : null;
    }

    /// <summary>Fetch each element and <see cref="MatchElementAsync"/> it against the open drawing (read-only).</summary>
    public static async Task<JsonObject> LocateAsync(
        OntologyRestClient client,
        IEnumerable<string> elementIds,
        JsonObject? openDrawing,
        Func<string, Task<JsonObject?>> lookup,
        CancellationToken ct = default)
    {
        client.EnsureConfigured();
        var results = new List<JsonObject>();
        foreach (var id in Unique(elementIds.Select(i => (i ?? "").Trim()).Where(i => i.Length > 0)))
        {
            JsonObject? element;
            string? error = null;
            try
            {
                element = ElementOf(await client.ElementContextAsync(id, 1, ct).ConfigureAwait(false));
            }
            catch (OntologyRestException e) when (!e.Unavailable)
            {
                element = null;
                error = e.Detail;
            }

            var result = await MatchElementAsync(element, openDrawing, lookup, id).ConfigureAwait(false);
            if (error is not null && Str(result["status"]) == "not_found")
            {
                result["error"] = error;
            }

            results.Add(result);
        }

        var counts = new JsonObject();
        foreach (var result in results)
        {
            var status = Str(result["status"]);
            counts[status] = (counts[status]?.GetValue<int>() ?? 0) + 1;
        }

        var matched = results.Where(r => Str(r["status"]) == "matched").Select(r => r["handle"]!.DeepClone()).ToArray();
        return new JsonObject
        {
            ["open_drawing"] = openDrawing is null ? null : DrawingLabel(openDrawing),
            ["counts"] = counts,
            ["matched_handles"] = new JsonArray(matched),
            ["results"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()),
            ["read_only"] = true,
        };
    }

    /// <summary>
    /// Locate the elements an auto_context bundle already carries (no extra Ontology calls). Only elements of the open
    /// drawing are looked up; the rest are just counted as other_drawing. Returns the matched handles (with class/name/
    /// entity type) the agent can pass to the edit tools.
    /// </summary>
    public static async Task<JsonObject> TargetsSummaryAsync(
        JsonObject bundle,
        JsonObject? openDrawing,
        Func<string, Task<JsonObject?>> lookup,
        int limit = 50)
    {
        var elements = (bundle["search"] as JsonArray ?? []).OfType<JsonObject>()
            .Concat((bundle["elements"] as JsonObject ?? []).Select(kv => kv.Value).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>()))
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counts = new JsonObject();
        var targets = new JsonArray();
        var other = new List<string>();
        foreach (var element in elements)
        {
            var key = Truthy(element["id"]) ? "id:" + Str(element["id"]) : "at:" + Dump(new JsonArray(element["source_file"]?.DeepClone(), element["handle"]?.DeepClone()));
            if (!seen.Add(key))
            {
                continue;
            }

            var result = await MatchElementAsync(element, openDrawing, lookup).ConfigureAwait(false);
            var status = Str(result["status"]);
            counts[status] = (counts[status]?.GetValue<int>() ?? 0) + 1;
            if (status == "matched" && targets.Count < limit)
            {
                var entity = result["entity"] as JsonObject ?? [];
                targets.Add(DropEmpty(
                    ("element_id", result["element_id"]),
                    ("class", result["class"]),
                    ("name", result["name"]),
                    ("handle", result["handle"]),
                    ("type", entity["type"]),
                    ("layer", entity["layer"])));
            }
            else if (status is "mismatch" or "handle_missing" && other.Count < limit)
            {
                other.Add($"{(result["element_id"] is { } id ? Str(id) : "None")}: {status}");
            }
        }

        var output = new JsonObject
        {
            ["open_drawing"] = openDrawing is null ? null : DrawingLabel(openDrawing),
            ["counts"] = counts,
            ["targets"] = targets,
        };
        if (other.Count > 0)
        {
            output["not_actionable"] = new JsonArray(other.Select(o => (JsonNode)o).ToArray());
        }

        return output;
    }
}

public sealed partial class OntologyRestTools
{
    [McpServerTool(Name = "ontology_locate", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Map Ontology elements to live CAD handles in the open drawing, safely and read-only. Handles are only unique per "
        + "drawing, so for each id this checks that the element's source file is the open drawing (basename, case-insensitive, "
        + ".dwg = .dxf), that the handle exists there and that the entity is plausible for the element (type, layer, block). "
        + "Status per id: matched, mismatch, handle_missing, other_drawing or not_found. Only `matched` handles are safe to pass to "
        + "cad_get / cad_move / cad_delete / cad_set_properties (with the entity's fingerprint). Never modifies the drawing.")]
    public async Task<string> Locate(
        [Description("Ontology element ids (id field), 1-200")] string[] element_ids,
        CancellationToken ct = default)
    {
        if (element_ids is null || element_ids.Length is < 1 or > 200)
        {
            throw new McpException("[INVALID_ARGUMENT] element_ids must hold between 1 and 200 ids.");
        }

        ontology.EnsureConfigured();
        var live = await OntologyCad.OpenDrawingAsync(gateway, ct).ConfigureAwait(false);
        var report = await OntologyRest.LocateAsync(ontology, element_ids, live?.Drawing, OntologyCad.Lookup(gateway, live, ct), ct).ConfigureAwait(false);
        return Json(report);
    }
}
