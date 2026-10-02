using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

public sealed partial class CommandDispatcher
{
    private JsonObject ExtractSnapshot(ICadTransaction tx, Params p)
    {
        p.AllowOnly("max_entities", "max_scanned_entities");
        var max = p.Int("max_entities", 1000, 1, 1000);
        var scanLimit = p.Int("max_scanned_entities", 10000, 1, 100000);
        var entities = new JsonArray();
        var counts = new JsonObject();
        var unsupported = new JsonObject();
        var known = new HashSet<string>(["LINE", "LWPOLYLINE", "CIRCLE", "ARC", "TEXT", "MTEXT", "INSERT", "POINT", "DIMENSION", "HATCH", "LEADER"]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var raw = 0;
        var budget = new PayloadBudget();
        var truncated = false;
        var scanComplete = true;
        foreach (var entity in tx.ScanModelSpace())
        {
            raw++;
            counts[entity.Type] = (counts[entity.Type]?.GetValue<int>() ?? 0) + 1;
            if (!known.Contains(entity.Type))
                unsupported[entity.Type] = (unsupported[entity.Type]?.GetValue<int>() ?? 0) + 1;
            hash.AppendData(Encoding.UTF8.GetBytes($"{entity.Handle}:{entity.Fingerprint}\n"));
            var json = entity.ToJson();
            // Leave room for metadata and the pipe envelope (the transport is capped at 1 MiB).
            if (entities.Count < max && budget.TryAdd(json, InventoryOptions.DefaultMaxBytes))
            {
                entities.Add(json);
            }
            else truncated = true;
            if (raw >= scanLimit)
            {
                // Do not advance the iterator again: native MoveNext describes another entity.
                // At exactly the limit completion is conservatively unknown.
                scanComplete = false;
                truncated = true;
                break;
            }
        }

        var identity = document.Describe();
        var layerRows = tx.Layers();
        foreach (var layer in layerRows) hash.AppendData(Encoding.UTF8.GetBytes(layer.ToJsonString(CadJson.Options)));
        var layers = new JsonArray();
        foreach (var layer in layerRows.Take(1000))
        {
            if (!budget.TryAdd(layer, 650_000)) break;
            layers.Add(layer.DeepClone());
        }
        return new JsonObject
        {
            ["document_id"] = identity["document_id"]?.DeepClone(),
            ["session_id"] = identity["session_id"]?.DeepClone(),
            ["units"] = identity["units"]?.DeepClone(),
            ["content_hash"] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            ["content_hash_scope"] = scanComplete ? "all_top_level_entities_and_layers" : "scanned_top_level_prefix_and_layers",
            ["scan_complete"] = scanComplete,
            ["scan_limit"] = scanLimit,
            ["scan_stop_reason"] = scanComplete ? "end_of_model_space" : "entity_limit",
            ["counts_scope"] = scanComplete ? "all_top_level_entities" : "scanned_top_level_prefix",
            ["total_count_known"] = scanComplete,
            ["total_count"] = scanComplete ? raw : (int?)null,
            ["scope"] = "model_space_top_level",
            ["excluded_scopes"] = new JsonArray("paper_space", "block_definitions", "nested_instances", "xref_contents"),
            ["raw_count"] = raw,
            ["processed_count"] = raw,
            ["returned_count"] = entities.Count,
            ["unsupported_count"] = unsupported.Sum(row => row.Value!.GetValue<int>()),
            ["unsupported_types"] = unsupported,
            ["counts_by_type"] = counts,
            ["truncated"] = truncated,
            ["entities"] = entities,
            ["layers"] = layers,
            ["layer_count"] = layerRows.Count,
            ["resources_truncated"] = layers.Count != layerRows.Count,
        };
    }

    /// <summary>Read-only layouts, block definitions, block references (nested) and XREFs, bounded like snapshots.</summary>
    private JsonObject Inventory(Params p)
    {
        p.AllowOnly("max_blocks", "max_references", "max_depth");
        var options = new InventoryOptions(
            p.Int("max_blocks", 500, 1, DrawingInventory.MaxBlocksLimit),
            p.Int("max_references", 2000, 1, DrawingInventory.MaxReferencesLimit),
            p.Int("max_depth", 2, 0, DrawingInventory.MaxDepthLimit));
        var inventory = document.GetDrawingInventory(options);
        var identity = document.Describe();
        inventory["document_id"] = identity["document_id"]?.DeepClone();
        inventory["session_id"] = identity["session_id"]?.DeepClone();
        inventory["units"] = identity["units"]?.DeepClone();
        return inventory;
    }
}
