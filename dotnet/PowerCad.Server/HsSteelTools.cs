using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;
using PowerCad.Core.Commands;

namespace PowerCad.Server;

/// <summary>Read-only bridge from hs-steel-cad draw plans into reviewable Power CAD plan steps.</summary>
[McpServerToolType]
public sealed class HsSteelTools
{
    private static string CanonicalJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{CanonicalJson(pair.Value)}")) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(CanonicalJson)) + "]",
        _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
    };

    private static string Digest(JsonNode node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(node))))
            .ToLowerInvariant();

    private static McpException Invalid(string message) =>
        new($"[INVALID_HS_STEEL_HANDOFF] {message}");

    private static string RequiredString(JsonObject root, string name) =>
        root[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw Invalid($"{name} is required.");

    [McpServerTool(Name = "cad_hs_steel_prepare", ReadOnly = true, Idempotent = true)]
    [Description("Validate hs-steel-draw-plan/1 and convert its strict create specs into <=200-entity cad_plan_create steps. HS tags are returned separately for later XData persistence. This tool never edits CAD and never grants execution authorization.")]
    public string Prepare(JsonElement handoff)
    {
        if (JsonNode.Parse(handoff.GetRawText()) is not JsonObject root)
        {
            throw Invalid("handoff must be a JSON object.");
        }

        if (RequiredString(root, "schema") != "hs-steel-draw-plan/1")
        {
            throw Invalid("schema must be hs-steel-draw-plan/1.");
        }

        if (RequiredString(root, "producer") != "khs0927/hs-steel-cad")
        {
            throw Invalid("producer must be khs0927/hs-steel-cad.");
        }

        if (RequiredString(root, "units") != "mm")
        {
            throw Invalid("units must be mm.");
        }

        if (root["execution_authorized"]?.GetValue<bool>() is not false
            || root["may_execute_mutation"]?.GetValue<bool>() is not false
            || root["requires_live_document_binding"]?.GetValue<bool>() is not true
            || root["tags_require_xdata_persistence"]?.GetValue<bool>() is not true)
        {
            throw Invalid("handoff safety flags are invalid.");
        }

        var contractDigest = RequiredString(root, "contract_digest");
        if (contractDigest.Length != 64 || contractDigest.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw Invalid("contract_digest must be a SHA-256 hex digest.");
        }

        var unsigned = root.DeepClone().AsObject();
        unsigned.Remove("contract_digest");
        var expectedDigest = Digest(unsigned);
        if (!string.Equals(contractDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("contract_digest does not match the handoff payload.");
        }

        if (root["entities"] is not JsonArray entities || entities.Count is < 1 or > 4000)
        {
            throw Invalid("entities must contain 1-4000 rows.");
        }

        var steps = new JsonArray();
        var tagManifest = new JsonArray();
        var current = new JsonArray();
        for (var index = 0; index < entities.Count; index++)
        {
            if (entities[index] is not JsonObject row
                || row.Any(pair => pair.Key is not ("spec" or "tag"))
                || row["spec"] is not JsonObject spec)
            {
                throw Invalid($"entities[{index}] must contain only spec and optional tag.");
            }

            try
            {
                _ = CreateSpec.Parse(spec.DeepClone().AsObject());
            }
            catch (CadException e)
            {
                throw Invalid($"entities[{index}].spec is invalid: {e.Message}");
            }

            current.Add(spec.DeepClone());
            if (row["tag"] is JsonObject tag)
            {
                tagManifest.Add(new JsonObject
                {
                    ["index"] = index,
                    ["tag"] = tag.DeepClone(),
                });
            }
            else if (row["tag"] is not null)
            {
                throw Invalid($"entities[{index}].tag must be an object or null.");
            }

            if (current.Count == 200 || index == entities.Count - 1)
            {
                steps.Add(new JsonObject
                {
                    ["command"] = "create",
                    ["params"] = new JsonObject { ["entities"] = current },
                });
                current = new JsonArray();
            }
        }

        if (steps.Count > CommandDispatcher.MaxBatch)
        {
            throw Invalid($"handoff needs {steps.Count} create steps; maximum is {CommandDispatcher.MaxBatch}.");
        }

        if (steps.ToJsonString().Length > 150_000)
        {
            throw Invalid("prepared plan steps exceed the bounded plan payload; split the HS-STEEL drawing.");
        }

        var result = new JsonObject
        {
            ["schema"] = "power-cad-hs-steel-prepared/1",
            ["source_schema"] = "hs-steel-draw-plan/1",
            ["source_contract_digest"] = contractDigest.ToLowerInvariant(),
            ["source_digest_verified"] = true,
            ["title"] = root["title"]?.DeepClone(),
            ["scale"] = root["scale"]?.DeepClone(),
            ["entity_count"] = entities.Count,
            ["chunk_count"] = steps.Count,
            ["steps"] = steps,
            ["tag_manifest"] = tagManifest,
            ["xdata_write_supported"] = false,
            ["tag_persistence_required"] = tagManifest.Count > 0,
            ["execution_authorized"] = false,
            ["may_execute_mutation"] = false,
            ["requires_snapshot_and_plan"] = true,
            ["note"] = "Pass steps to cad_plan_create against a fresh bound snapshot, preview with cad_plan_execute(dry_run=true), then explicitly approve commit. HS tags remain pending until XData write support is implemented.",
        };
        return result.ToJsonString(CadJson.Options);
    }
}
