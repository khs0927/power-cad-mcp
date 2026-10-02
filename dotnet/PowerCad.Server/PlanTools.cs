using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

/// <summary>Durable receipts; an interrupted Executing plan is never automatically replayed.</summary>
public sealed class PlanStore
{
    public string Root { get; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    public PlanStore(string? root = null) => Root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerCad", "plans");

    private string Resolve(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new McpException("[INVALID_PLAN] Invalid plan_id.");
        return Path.Combine(Root, id + ".json");
    }

    public JsonObject Read(string id)
    {
        var path = Resolve(id);
        if (!File.Exists(path)) throw new McpException("[PLAN_NOT_FOUND] No such plan.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonNode.Parse(stream)!.AsObject();
    }

    public IDisposable AcquireExecution(string id)
    {
        Directory.CreateDirectory(Root);
        try { return new FileStream(Resolve(id) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new McpException("[PLAN_BUSY] Another process is executing this plan.", e); }
    }

    public void Write(JsonObject plan)
    {
        Directory.CreateDirectory(Root);
        var path = Resolve(plan["plan_id"]!.GetValue<string>());
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
            {
                writer.Write(plan.ToJsonString(CadJson.Options));
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

[McpServerToolType]
public sealed class PlanTools(ICadGateway gateway, SnapshotStore snapshots, PlanStore plans)
{
    private static readonly HashSet<string> Targeted = ["move", "delete", "copy", "transform", "offset", "set_properties", "replace_text"];

    [McpServerTool(Name = "cad_plan_create", Destructive = false)]
    [Description("Persist a reviewable <=20-step plan from a captured snapshot. Target fingerprints are injected; each existing handle may appear in only one step. Supports create and targeted edits, not broad find/replace, save, import or layer changes. Does not edit CAD.")]
    public async Task<string> Create(string snapshot_id, JsonElement steps, string reason, string? standard_version = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4000 || standard_version?.Length > 128)
            throw new McpException("[INVALID_PLAN] A bounded reason and standard_version are required.");
        var snapshot = snapshots.Get(snapshot_id);
        if (JsonNode.Parse(steps.GetRawText()) is not JsonArray rows || rows.Count is < 1 or > 20)
            throw new McpException("[INVALID_PLAN] steps must contain 1–20 objects.");
        var entities = snapshot["entities"]!.AsArray().ToDictionary(n => n!["handle"]!.GetValue<string>(), n => n!.AsObject(), StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Pin(JsonObject target)
        {
            var handle = target["handle"]?.GetValue<string>();
            if (handle is null || !entities.TryGetValue(handle, out var state) || !touched.Add(handle))
                throw new McpException("[INVALID_PLAN] Every target must occur once and exist in the captured snapshot.");
            var fingerprint = state["fingerprint"]!.GetValue<string>();
            if (target["expect_fingerprint"] is { } expected && expected.GetValue<string>() != fingerprint)
                throw new McpException("[INVALID_PLAN] Target fingerprint differs from the snapshot.");
            target["expect_fingerprint"] = fingerprint;
        }
        foreach (var node in rows)
        {
            if (node is not JsonObject step || step["command"] is not JsonValue commandNode || step["params"] is not JsonObject p
                || !commandNode.TryGetValue<string>(out var command) || step.Any(k => k.Key is not ("command" or "params")))
                throw new McpException("[INVALID_PLAN] Each step requires command and params only.");
            if (p.ContainsKey("expected_document_id") || p.ContainsKey("dry_run"))
                throw new McpException("[INVALID_PLAN] Binding and dry_run are controlled by plan execution.");
            if (command == "create") continue;
            if (command == "modify_opening") { Pin(p); continue; }
            if (!Targeted.Contains(command)) throw new McpException($"[INVALID_PLAN] Unsupported planned command: {command}.");
            if (p["targets"] is not JsonArray targets || targets.Count == 0 || p.ContainsKey("handles") || p.ContainsKey("find"))
                throw new McpException("[INVALID_PLAN] Explicit nonempty targets are required.");
            foreach (var target in targets)
                Pin(target as JsonObject ?? throw new McpException("[INVALID_PLAN] Targets must be objects."));
        }

        var plan = new JsonObject
        {
            ["schema_version"] = 1,
            ["plan_id"] = Guid.NewGuid().ToString("N"),
            ["document_id"] = snapshot["document_id"]!.DeepClone(),
            ["snapshot_hash"] = snapshot["content_hash"]!.DeepClone(),
            ["snapshot_truncated"] = snapshot["truncated"]!.DeepClone(),
            ["snapshot_scope"] = snapshot["scope"]!.DeepClone(),
            ["reason"] = reason,
            ["standard_version"] = standard_version,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["state"] = "Prepared",
            ["steps"] = rows,
        };
        if (plan.ToJsonString().Length > 200_000) throw new McpException("[INVALID_PLAN] Plan is too large.");
        await plans.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { plans.Write(plan); }
        finally { plans.Gate.Release(); }
        return plan.ToJsonString(CadJson.Options);
    }

    [McpServerTool(Name = "cad_plan_get", ReadOnly = true, Idempotent = true)]
    [Description("Read a persisted plan and execution receipt. Prepared/Previewed plans expire after 10 minutes; interrupted plans require reconciliation.")]
    public string Get(string plan_id) => plans.Read(plan_id).ToJsonString(CadJson.Options);

    [McpServerTool(Name = "cad_plan_execute", Destructive = true)]
    [Description("Preview a persisted plan (dry_run default true), then apply only a successfully Previewed plan. Revalidates document and all injected fingerprints inside one CAD batch transaction. Repeated committed calls return the receipt; interrupted or uncertain executions are never replayed.")]
    public async Task<string> Execute(string plan_id, bool dry_run = true, CancellationToken ct = default)
    {
        await plans.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var executionLock = plans.AcquireExecution(plan_id);
            var plan = plans.Read(plan_id);
            var state = plan["state"]!.GetValue<string>();
            if (state == "Committed") return plan.ToJsonString(CadJson.Options);
            if (state is not ("Prepared" or "Previewed") || (!dry_run && state != "Previewed"))
                throw new McpException("[PLAN_STATE] Preview first. Failed, executing or indeterminate plans cannot be replayed.");
            if (DateTimeOffset.UtcNow - DateTimeOffset.Parse(plan["created_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) > TimeSpan.FromMinutes(10))
                throw new McpException("[PLAN_EXPIRED] Capture and plan again.");

            plan["state"] = "Executing";
            plans.Write(plan);
            try
            {
                var result = await gateway.SendAsync("batch", new JsonObject
                {
                    ["expected_document_id"] = plan["document_id"]!.DeepClone(),
                    ["steps"] = plan["steps"]!.DeepClone(),
                    ["dry_run"] = dry_run,
                }, ct).ConfigureAwait(false);
                if (result is not JsonObject receipt || receipt["committed"]?.GetValue<bool>() != !dry_run
                    || receipt["dry_run"]?.GetValue<bool>() != dry_run)
                    throw new CadException(ErrorCodes.Internal, "The execution result did not contain a verifiable commit receipt.");
                plan["state"] = dry_run ? "Previewed" : "Committed";
                plan[dry_run ? "preview" : "result"] = result?.DeepClone();
                plan["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
                plans.Write(plan);
                return plan.ToJsonString(CadJson.Options);
            }
            catch (Exception error)
            {
                var definiteFailure = error is CadException cad && cad.Code is not (ErrorCodes.Timeout or ErrorCodes.NotConnected or ErrorCodes.Internal);
                plan["state"] = definiteFailure ? "Failed" : "Indeterminate";
                plan["error_code"] = error is CadException known ? known.Code : "EXECUTION_UNCERTAIN";
                plans.Write(plan);
                if (error is CadException failure) throw new McpException($"[{failure.Code}] {failure.Message}", failure);
                throw;
            }
        }
        finally { plans.Gate.Release(); }
    }
}
