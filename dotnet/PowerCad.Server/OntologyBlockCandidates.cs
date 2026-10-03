using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

public sealed partial class OntologyRestTools
{
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool Bool(JsonObject row, string name) =>
        row[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static JsonObject CandidateInfo(JsonObject block)
    {
        var info = new JsonObject();
        foreach (var key in new[] { "name", "category", "instance_count", "attribute_tags", "layers", "example_files", "effective_names" })
        {
            if (block[key] is { } value)
                info[key == "name" ? "ontology_name" : key] = value.DeepClone();
        }
        return info;
    }

    private static bool NameMatches(string name, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal)
            .Replace("%", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> BlockNames(JsonObject block)
    {
        if (Text(block["name"]) is { } name)
            yield return name;
        if (block["effective_names"] is JsonArray aliases)
            foreach (var alias in aliases)
                if (Text(alias) is { } value)
                    yield return value;
    }

    [McpServerTool(Name = "ontology_block_candidates", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Cross-check Ontology block candidates against the live drawing's native block definitions. "
        + "Only complete native inventory matches are marked insertable; XREF/layout/anonymous definitions are blocked, "
        + "and missing candidates stay unverified when the native block inventory is truncated or unavailable. "
        + "This is discovery only and never authorizes or performs insertion.")]
    public async Task<string> BlockCandidates(
        [Description("Block name/wildcard (DOOR*) or a task such as '문 블록 배치'")] string name_or_task,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("Maximum candidate rows per result bucket, 1-500")] int limit = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name_or_task))
            throw new McpException("[INVALID_ARGUMENT] name_or_task must not be empty.");
        Range(nameof(limit), limit, 1, 500);
        ontology.EnsureConfigured();

        var query = name_or_task.Trim();
        var hints = OntologyRest.InferTask(query);
        var warnings = new List<string>();
        var found = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        async Task Add(string label, string? category, string? nameLike)
        {
            try
            {
                var page = await ontology.BlocksAsync(category, nameLike, project_id, limit, ct: ct).ConfigureAwait(false);
                foreach (var block in page.Items)
                    if (Text(block["name"]) is { } name)
                        found.TryAdd(name, block);
            }
            catch (OntologyRestException ex) when (!ex.Unavailable)
            {
                warnings.Add($"blocks[{label}]: {ex.Detail}");
            }
        }

        var queried = false;
        if (!query.Any(char.IsWhiteSpace))
        {
            queried = true;
            await Add($"name_like={query}", null, query).ConfigureAwait(false);
        }
        if (!query.Any(ch => ch is '*' or '?' or '%'))
        {
            foreach (var cls in hints.Classes)
            {
                queried = true;
                await Add($"category={cls}", cls, null).ConfigureAwait(false);
            }
        }
        if (!queried)
        {
            await Add("all", null, null).ConfigureAwait(false);
            warnings.Add("no block name or element class recognised; queried the bounded Ontology block catalog.");
        }

        OntologyLiveDrawing? live = null;
        JsonObject? inventory = null;
        if (gateway is not null)
        {
            try
            {
                live = await OntologyCad.OpenDrawingAsync(gateway, ct).ConfigureAwait(false);
                if (live is not null)
                {
                    var parameters = new JsonObject
                    {
                        ["max_blocks"] = 5000,
                        ["max_references"] = 1,
                        ["max_depth"] = 0,
                    };
                    if (live.DocumentId is { } id)
                        parameters["expected_document_id"] = id;
                    inventory = await gateway.SendAsync("drawing_inventory", parameters, ct).ConfigureAwait(false) as JsonObject;
                }
            }
            catch (CadException ex) when (ex.Code == ErrorCodes.DocumentChanged)
            {
                return Json(new JsonObject
                {
                    ["query"] = query,
                    ["error"] = ex.Message,
                    ["code"] = ex.Code,
                    ["read_only"] = true,
                    ["may_execute_mutation"] = false,
                    ["inventory_complete"] = false,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warnings.Add($"native block inventory unavailable: {ex.Message}");
            }
        }

        var inventoryComplete = inventory is not null
            && inventory["blocks_truncated"] is JsonValue truncated
            && truncated.TryGetValue<bool>(out var isTruncated)
            && !isTruncated;

        var liveByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (inventory?["block_definitions"] is JsonArray definitions)
        {
            foreach (var node in definitions.OfType<JsonObject>())
            {
                foreach (var name in new[] { Text(node["name"]), Text(node["effective_name"]) }.Where(x => x is not null))
                    liveByName.TryAdd(name!, node);
            }
        }

        var insertable = new JsonArray();
        var otherFiles = new JsonArray();
        var notInsertable = new JsonArray();
        var unverified = new JsonArray();
        var usedLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in found.Values)
        {
            var info = CandidateInfo(block);
            var aliases = BlockNames(block).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hit = aliases.Select(a => liveByName.GetValueOrDefault(a)).FirstOrDefault(x => x is not null);

            if (Bool(block, "is_xref"))
            {
                info["reason"] = "Ontology marks this as an external reference (XREF), not an insertable block.";
                notInsertable.Add(info);
                continue;
            }

            if (hit is not null)
            {
                if (Text(hit["name"]) is { } liveName)
                    usedLive.Add(liveName);

                if (Bool(hit, "is_xref") || Bool(hit, "is_from_external_reference"))
                {
                    info["reason"] = "The live definition is an external reference.";
                    notInsertable.Add(info);
                }
                else if (Bool(hit, "is_layout"))
                {
                    info["reason"] = "The live definition is a layout block, not an insertable user block.";
                    notInsertable.Add(info);
                }
                else if (Bool(hit, "is_anonymous"))
                {
                    info["reason"] = "The matched live definition is anonymous; use its named source definition instead.";
                    notInsertable.Add(info);
                }
                else
                {
                    info["insert_name"] = Text(hit["name"]);
                    if (hit["entity_count"] is { } count)
                        info["entity_count"] = count.DeepClone();
                    info["candidate_only"] = true;
                    insertable.Add(info);
                }
                continue;
            }

            if (aliases.Count == 1 && aliases[0].StartsWith("*", StringComparison.Ordinal))
            {
                info["reason"] = "Anonymous Ontology block has no effective name.";
                notInsertable.Add(info);
            }
            else if (!inventoryComplete)
            {
                info["reason"] = inventory is null
                    ? "Live native block inventory is unavailable."
                    : "Live native block inventory is truncated; absence cannot be proven.";
                unverified.Add(info);
            }
            else
            {
                info["note"] = "Not defined in the current live drawing; example_files are evidence only and are never auto-imported.";
                otherFiles.Add(info);
            }
        }

        if (!query.Any(char.IsWhiteSpace) && inventoryComplete)
        {
            foreach (var entry in liveByName.Values.Distinct())
            {
                var name = Text(entry["name"]);
                if (name is null
                    || usedLive.Contains(name)
                    || name.StartsWith("*", StringComparison.Ordinal)
                    || Bool(entry, "is_xref")
                    || Bool(entry, "is_from_external_reference")
                    || Bool(entry, "is_layout")
                    || Bool(entry, "is_anonymous")
                    || !NameMatches(name, query))
                    continue;

                insertable.Add(new JsonObject
                {
                    ["insert_name"] = name,
                    ["entity_count"] = entry["entity_count"]?.DeepClone(),
                    ["note"] = "Defined in the live drawing but not returned by the Ontology query.",
                    ["candidate_only"] = true,
                });
            }
        }

        JsonArray Take(JsonArray rows) => new(rows.Take(limit).Select(x => x?.DeepClone()).ToArray());
        return Json(new JsonObject
        {
            ["query"] = query,
            ["project_id"] = project_id,
            ["inferred_classes"] = new JsonArray(hints.Classes.Select(x => (JsonNode)x).ToArray()),
            ["open_drawing"] = live?.Drawing.DeepClone(),
            ["inventory_complete"] = inventoryComplete,
            ["counts"] = new JsonObject
            {
                ["ontology_blocks"] = found.Count,
                ["insertable"] = insertable.Count,
                ["other_files"] = otherFiles.Count,
                ["not_insertable"] = notInsertable.Count,
                ["unverified"] = unverified.Count,
            },
            ["insertable"] = Take(insertable),
            ["other_files"] = Take(otherFiles),
            ["not_insertable"] = Take(notInsertable),
            ["unverified"] = Take(unverified),
            ["read_only"] = true,
            ["candidate_only"] = true,
            ["source_revision_verified"] = false,
            ["may_execute_mutation"] = false,
            ["requires_live_insert_revalidation"] = true,
            ["warnings"] = new JsonArray(warnings.Select(x => (JsonNode)x).ToArray()),
        });
    }
}
