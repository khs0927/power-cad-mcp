using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

public sealed class SnapshotStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Created, JsonObject Data)> _items = [];
    public string Put(JsonObject data)
    {
        lock (_lock)
        {
            foreach (var key in _items.Where(row => DateTimeOffset.UtcNow - row.Value.Created > TimeSpan.FromMinutes(10)).Select(row => row.Key).ToArray())
                _items.Remove(key);
            if (_items.Count >= 16) _items.Remove(_items.MinBy(row => row.Value.Created).Key);
            var id = Guid.NewGuid().ToString("N");
            _items[id] = (DateTimeOffset.UtcNow, (JsonObject)data.DeepClone());
            return id;
        }
    }
    public JsonObject Get(string id)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var item) || DateTimeOffset.UtcNow - item.Created > TimeSpan.FromMinutes(10))
                throw new McpException("[SNAPSHOT_EXPIRED] Capture a new snapshot. Snapshots expire after 10 minutes or eviction.");
            return (JsonObject)item.Data.DeepClone();
        }
    }
}

[McpServerToolType]
public sealed class SnapshotTools(ICadGateway gateway, SnapshotStore store)
{
    [McpServerTool(Name = "cad_extract_snapshot", ReadOnly = true)]
    [Description("Capture top-level model-space DTOs in one transaction. max_entities bounds returned rows; max_scanned_entities bounds inspected rows (default 10000, maximum 100000). Check scan_complete/counts_scope/content_hash_scope: a limited scan is a prefix, not a whole drawing count or hash. Layouts, block definitions and XREF contents are excluded. Returns frozen snapshot_id for paging/planning.")]
    public async Task<string> Extract(int max_entities = 1000, int max_scanned_entities = 10000, CancellationToken ct = default)
    {
        try
        {
            var identity = await gateway.SendAsync("document_identity", null, ct).ConfigureAwait(false);
            var id = identity?["document_id"]?.GetValue<string>() ?? throw new McpException("[PLUGIN_OUTDATED] Document identity is required.");
            var data = (await gateway.SendAsync("extract_snapshot", new JsonObject { ["max_entities"] = max_entities, ["max_scanned_entities"] = max_scanned_entities, ["expected_document_id"] = id }, ct).ConfigureAwait(false))!.AsObject();
            var snapshotId = store.Put(data);
            data.Remove("entities");
            data["snapshot_id"] = snapshotId;
            data["expires_in_seconds"] = 600;
            return data.ToJsonString(CadJson.Options);
        }
        catch (CadException e) { throw new McpException($"[{e.Code}] {e.Message}", e); }
    }

    [McpServerTool(Name = "cad_query_page", ReadOnly = true, Idempotent = true)]
    [Description("Page a frozen captured snapshot, not the current live drawing. Edits do not rewrite it; execution must revalidate target fingerprints. offset is within this snapshot only.")]
    public string Page(string snapshot_id, int offset = 0, int page_size = 100)
    {
        var data = store.Get(snapshot_id);
        var rows = data["entities"]!.AsArray();
        if (offset < 0 || offset > rows.Count || page_size is < 1 or > 1000)
            throw new McpException("[INVALID_PARAMS] Invalid offset or page_size (1–1000).");
        data["entities"] = new JsonArray(rows.Skip(offset).Take(page_size).Select(n => n?.DeepClone()).ToArray());
        data["snapshot_id"] = snapshot_id;
        data["offset"] = offset;
        data["next_offset"] = offset + page_size < rows.Count ? offset + page_size : null;
        data["live_currentness_verified"] = false;
        return data.ToJsonString(CadJson.Options);
    }
}
