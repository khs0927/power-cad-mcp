using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

[McpServerToolType]
public sealed class ReviewTools(SnapshotStore snapshots)
{
    [McpServerTool(Name = "cad_review_snapshot", ReadOnly = true, Idempotent = true)]
    [Description("Review frozen captured data against the bundled ZIUM layer standard. Reports exact duplicate-state groups, unmapped/legacy layers and confirmed ACI mismatches. Findings require review, never trigger edits, and do not prove wall topology or architectural correctness.")]
    public string Review(string snapshot_id)
    {
        var snapshot = snapshots.Get(snapshot_id);
        using var stream = typeof(ReviewTools).Assembly.GetManifestResourceStream("PowerCad.DraftingStandard")
            ?? throw new InvalidOperationException("Bundled drafting standard is missing.");
        var standard = JsonNode.Parse(stream)!.AsObject();
        var rules = standard["layers"]!.AsObject();
        var mappings = standard["layer_merge_map"]?.AsObject();
        var legacy = standard["legacy_layers_drawing2"]?.AsObject();
        var findings = new JsonArray();
        var total = 0;
        void Add(JsonObject finding)
        {
            total++;
            if (findings.Count < 100) findings.Add(finding);
        }
        foreach (var node in snapshot["layers"]!.AsArray())
        {
            var layer = node!.AsObject();
            var name = layer["name"]!.GetValue<string>();
            if (name == "0") continue;
            var mapped = mappings?[name] is JsonValue value && value.TryGetValue<string>(out var destination) ? destination : name;
            if (rules[mapped] is not JsonObject rule)
            {
                Add(new JsonObject { ["code"] = legacy?[name] is null ? "UNMAPPED_LAYER" : "LEGACY_LAYER", ["layer"] = name, ["requires_review"] = true });
                continue;
            }
            if (rule["color"] is JsonValue color && color.TryGetValue<int>(out var expected)
                && layer["color"] is JsonValue actual && actual.TryGetValue<int>(out var observed) && expected != observed)
                Add(new JsonObject { ["code"] = "LAYER_COLOR_MISMATCH", ["layer"] = name, ["mapped_layer"] = mapped, ["expected_aci"] = expected, ["observed_aci"] = observed, ["requires_review"] = true });
        }
        foreach (var group in snapshot["entities"]!.AsArray().GroupBy(n => n!["fingerprint"]!.GetValue<string>()).Where(g => g.Count() > 1))
            Add(new JsonObject
            {
                ["code"] = "EXACT_DUPLICATE_STATE",
                ["handles"] = new JsonArray(group.Select(n => n!["handle"]!.DeepClone()).ToArray()),
                ["requires_review"] = true,
                ["note"] = "Identical described states can be intentional; hidden or unsupported properties are not compared.",
            });
        return new JsonObject
        {
            ["snapshot_id"] = snapshot_id,
            ["document_id"] = snapshot["document_id"]!.DeepClone(),
            ["snapshot_hash"] = snapshot["content_hash"]!.DeepClone(),
            ["standard_version"] = standard["version"]!.DeepClone(),
            ["standard_hash"] = CadJson.Hash(standard),
            ["scope"] = snapshot["scope"]!.DeepClone(),
            ["scan_complete"] = snapshot["scan_complete"]?.DeepClone(),
            ["counts_scope"] = snapshot["counts_scope"]?.DeepClone(),
            ["content_hash_scope"] = snapshot["content_hash_scope"]?.DeepClone(),
            ["input_truncated"] = snapshot["truncated"]!.GetValue<bool>() || snapshot["resources_truncated"]!.GetValue<bool>(),
            ["excluded_scopes"] = snapshot["excluded_scopes"]!.DeepClone(),
            ["finding_count"] = total,
            ["findings_truncated"] = total > findings.Count,
            ["findings"] = findings,
            ["may_execute_mutation"] = false,
        }.ToJsonString(CadJson.Options);
    }
}
