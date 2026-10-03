using System.ComponentModel;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// The open drawing, or null when none is reachable (no gateway, AutoCAD not running, no document). When the gateway is
    /// bound to a document (cad_bind_document), the bound document is described; if the active drawing is another one this
    /// throws a <see cref="CadException"/> with <see cref="ErrorCodes.DocumentChanged"/> ("bound to A, active drawing is B")
    /// instead of describing the active drawing, whose handles the bound edit tools would refuse anyway.
    /// </summary>
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

        var live = Describe(info);
        if (gateway is DocumentBoundGateway { BoundDocumentId: { } bound } boundGateway && live?.DocumentId != bound)
        {
            var boundLive = Describe(boundGateway.BoundDocument);
            throw new CadException(
                ErrorCodes.DocumentChanged,
                $"bound to {Label(boundLive, bound)}, active drawing is {Label(live, null)}",
                "Activate the bound drawing, or call cad_bind_document for the active one, then locate again.");
        }

        return live;
    }

    private static string Label(OntologyLiveDrawing? live, string? fallback)
    {
        var text = live is null ? null : (live.Drawing["path"] ?? live.Drawing["name"])?.GetValue<string>();
        return text ?? fallback ?? "(no drawing)";
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
    /// was open when the drawing was described, so a drawing switch in between never reads an unrelated entity: it throws
    /// the backend's DOCUMENT_CHANGED <see cref="CadException"/> instead of reporting the handle as missing.
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
        catch (CadException e) when (e.Code == ErrorCodes.DocumentChanged)
        {
            throw; // the drawing switched (or differs from the binding): an error, not a missing handle
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

    /// <summary>
    /// 'C:\Proj\A-201.DWG' -> 'c:/proj/a-201': folder + stem, case-folded, separators normalised, without a CAD extension;
    /// null when the name has no folder (then only the basename <see cref="DrawingKey(string?)"/> can be compared).
    /// </summary>
    public static string? DrawingPathKey(string? name)
    {
        var text = (name ?? "").Trim().Trim('"').Replace('\\', '/');
        var cut = text.LastIndexOf('/');
        var stem = DrawingKey(text);
        if (cut < 0 || stem is null)
        {
            return null;
        }

        var folder = string.Join('/', text[..cut].Split('/').Select(part => part.Trim())
            .Where((part, i) => part.Length > 0 || i == 0)).TrimEnd('/').ToLowerInvariant();
        return folder.Length == 0 && !text.StartsWith('/') ? null : folder + "/" + stem;
    }

    /// <summary>AutoCAD's names for a drawing that was never saved (Drawing1.dwg ...): such a name proves nothing.</summary>
    [GeneratedRegex(@"^drawing\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefaultDrawingName();

    public static bool IsDefaultDrawingName(string? key) => key is not null && DefaultDrawingName().IsMatch(key);

    internal const string UnprovableNote = "open drawing is unsaved/default-named; cannot prove it is the source file";

    /// <summary>
    /// Whether <paramref name="sourceFile"/> is the open drawing: true/false, or null when unknown (no drawing open, no
    /// source_file), plus a note when the answer is false only because it cannot be proven. When both sides carry a folder
    /// the whole normalised path must agree; the basename is compared only when one side has no folder. A default name
    /// (Drawing1) or an open drawing without a saved path never counts as the same drawing.
    /// </summary>
    public static (bool? Same, string? Note) DrawingMatch(JsonNode? sourceFile, JsonObject? openDrawing)
    {
        var keys = DrawingKeys(openDrawing);
        if (keys.Count == 0)
        {
            return (null, "no drawing is open");
        }

        var source = StrOrEmpty(sourceFile);
        var key = DrawingKey(source);
        if (key is null)
        {
            return (null, "the element has no source_file");
        }

        var openPath = DrawingPathKey(StrOrEmpty(openDrawing!["path"]));
        var sourcePath = DrawingPathKey(source);
        var same = sourcePath is not null && openPath is not null ? sourcePath == openPath : keys.Contains(key);
        if (!same)
        {
            return (false, null);
        }

        return openPath is null || IsDefaultDrawingName(key) ? (false, UnprovableNote) : (true, null);
    }

    /// <summary>True when <paramref name="sourceFile"/> is provably the open drawing, false when it is another one (or cannot be proven), null when unknown.</summary>
    public static bool? InDrawing(JsonNode? sourceFile, JsonObject? openDrawing) => DrawingMatch(sourceFile, openDrawing).Same;

    /// <inheritdoc cref="InDrawing(JsonNode?, JsonObject?)"/>
    public static bool? InDrawing(string? sourceFile, JsonObject? openDrawing) => InDrawing(sourceFile is null ? null : JsonValue.Create(sourceFile), openDrawing);

    /// <summary>
    /// True when something positively ties the live entity to the element: a class whose plausible entity types are known,
    /// the same layer, or the element's block inserted by the entity. Without one, "no reason against it" proves nothing.
    /// </summary>
    public static bool HasPositiveSignal(JsonObject element, JsonObject entity)
    {
        var cls = StrOrEmpty(element["class"]);
        if (PhysicalClasses.Contains(cls) || TextClasses.Contains(cls))
        {
            return true;
        }

        var layer = StrOrEmpty(element["layer"]);
        if (layer.Length > 0 && layer.Equals(StrOrEmpty(entity["layer"]), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var block = StrOrEmpty(element["block_name"]);
        var name = StrOrEmpty(entity["name"]);
        return block.Length > 0 && StrOrEmpty(entity["type"]).Equals("INSERT", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith('*') || name.Equals(block, StringComparison.OrdinalIgnoreCase));
    }

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
    /// plausible and positively tied to it by class, layer or block), unverified (handle exists, nothing against the entity
    /// but nothing ties it to the element either), mismatch (handle exists but the entity does not look like the element),
    /// handle_missing (same drawing, no such entity / no handle recorded), other_drawing (another file, the open drawing is
    /// unknown, or it is unsaved/default-named so it cannot be proven to be the source file) or not_found.
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

        var (same, drawingNote) = DrawingMatch(element["source_file"], openDrawing);
        if (same != true)
        {
            return Result(("status", "other_drawing"), ("note", drawingNote));
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
        if (reasons.Count > 0)
        {
            return Result(("status", "mismatch"), ("reasons", new JsonArray(reasons.Select(r => (JsonNode)r).ToArray())));
        }

        return HasPositiveSignal(element, entity)
            ? Result(("status", "matched"))
            : Result(("status", "unverified"), ("note", "nothing ties the entity to the element (no known class, matching layer or block name)"));
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
            ["match_scope"] = "drawing_identity_and_live_entity_plausibility",
            ["source_revision_verified"] = false,
            ["may_execute_mutation"] = false,
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
            else if (status is "mismatch" or "handle_missing" or "unverified" && other.Count < limit)
            {
                other.Add($"{(result["element_id"] is { } id ? Str(id) : "None")}: {status}");
            }
        }

        var output = new JsonObject
        {
            ["open_drawing"] = openDrawing is null ? null : DrawingLabel(openDrawing),
            ["counts"] = counts,
            ["targets"] = targets,
            ["match_scope"] = "drawing_identity_and_live_entity_plausibility",
            ["source_revision_verified"] = false,
            ["may_execute_mutation"] = false,
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
        + "drawing, so for each id this checks that the element's source file is the open drawing (full path when both sides have a "
        + "folder, otherwise the basename; case-insensitive, .dwg = .dxf; an unsaved or Drawing1-style drawing never counts), that the "
        + "handle exists there and that the entity is plausible for the element and tied to it by class, layer or block. When this "
        + "client is bound to a drawing (cad_bind_document) the bound drawing is checked, and a different active drawing is an error. "
        + "Status per id: matched, unverified, mismatch, handle_missing, other_drawing or not_found. A `matched` handle is only a "
        + "review candidate: source revision and edit-time identity are not authorized here. Inspect with cad_get; mutation still requires "
        + "the SOURCE_BOUND executor handoff plus transaction-time document/target revalidation. Never modifies the drawing.")]
    public async Task<string> Locate(
        [Description("Ontology element ids (id field), 1-200")] string[] element_ids,
        CancellationToken ct = default)
    {
        if (element_ids is null || element_ids.Length is < 1 or > 200)
        {
            throw new McpException("[INVALID_ARGUMENT] element_ids must hold between 1 and 200 ids.");
        }

        foreach (var id in element_ids)
        {
            RejectDotId(id, "element_ids");
        }

        ontology.EnsureConfigured();
        try
        {
            var live = await OntologyCad.OpenDrawingAsync(gateway, ct).ConfigureAwait(false);
            var report = await OntologyRest.LocateAsync(ontology, element_ids, live?.Drawing, OntologyCad.Lookup(gateway, live, ct), ct).ConfigureAwait(false);
            return Json(report);
        }
        catch (CadException e) when (e.Code == ErrorCodes.DocumentChanged)
        {
            // The drawing switched (or is not the bound one): no handle can be trusted, so nothing is reported per id.
            return Json(new JsonObject { ["error"] = e.Message, ["code"] = e.Code, ["hint"] = e.Hint, ["read_only"] = true });
        }
    }

    /// <summary>'.' / '..' would be collapsed by URI normalisation into another REST route, so they are never element ids.</summary>
    private static void RejectDotId(string? id, string name)
    {
        var text = (id ?? "").Trim();
        if (text.Length > 0 && text.All(c => c == '.'))
        {
            throw new McpException($"[INVALID_ARGUMENT] {name} must not be '{text}' (dot-only ids are not element ids).");
        }
    }
}
