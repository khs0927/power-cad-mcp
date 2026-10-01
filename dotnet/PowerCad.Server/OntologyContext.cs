using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

public interface IOntologyContextClient
{
    bool Enabled { get; }

    Task<JsonObject> QueryGlobalMemoryAsync(string question, int topK, string? projectId, CancellationToken ct);
}

public sealed class OntologyMcpClient(string? command, string? root, TimeSpan timeout) : IOntologyContextClient
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public bool Enabled => !string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(root);

    public static OntologyMcpClient FromEnvironment(Func<string, string?> env)
    {
        var root = env("POWER_CAD_ONTOLOGY_ROOT");
        var command = env("POWER_CAD_ONTOLOGY_COMMAND");
        if (!string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(command))
        {
            command = "aec-mcp";
        }

        var seconds = 20;
        if (int.TryParse(env("POWER_CAD_ONTOLOGY_TIMEOUT"), out var parsed) && parsed is >= 1 and <= 120)
        {
            seconds = parsed;
        }

        return new OntologyMcpClient(command, root, TimeSpan.FromSeconds(seconds));
    }

    public async Task<JsonObject> QueryGlobalMemoryAsync(string question, int topK, string? projectId, CancellationToken ct)
    {
        var args = new JsonObject { ["question"] = question, ["top_k"] = topK };
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            args["project_id"] = projectId;
        }

        return await CallAsync("aec.query_global_memory", args, ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new McpException("[ONTOLOGY_UNAVAILABLE] Set POWER_CAD_ONTOLOGY_ROOT (and optionally POWER_CAD_ONTOLOGY_COMMAND) to enable CAIR context.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = command!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root!);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new McpException($"[ONTOLOGY_UNAVAILABLE] Could not start '{command}': {e.Message}", e);
        }

        using (process)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            linked.CancelAfter(timeout);
            var initialize = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "power-cad-server", ["version"] = "0.1.0" },
                },
            };
            var initialized = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
                ["params"] = new JsonObject(),
            };
            var call = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
            };

            await process.StandardInput.WriteLineAsync(initialize.ToJsonString(Compact)).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(initialized.ToJsonString(Compact)).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(call.ToJsonString(Compact)).ConfigureAwait(false);
            process.StandardInput.Close();

            JsonObject? response = null;
            try
            {
                while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (JsonNode.Parse(line) is JsonObject row && row["id"]?.GetValue<int>() == 2)
                    {
                        response = row;
                    }
                }

                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new McpException("[ONTOLOGY_TIMEOUT] Ontology MCP did not respond before the configured timeout.", e);
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new McpException($"[ONTOLOGY_FAILED] Ontology MCP exited with {process.ExitCode}: {stderr.Trim()}");
            }

            if (response?["error"] is JsonNode error)
            {
                throw new McpException($"[ONTOLOGY_FAILED] {error.ToJsonString(Compact)}");
            }

            var structured = response?["result"]?["structuredContent"] as JsonObject;
            return structured ?? throw new McpException("[ONTOLOGY_FAILED] Ontology MCP returned no structuredContent.");
        }
    }
}


public sealed class SionAecClient(HttpClient http, Uri baseUri, string? bearerToken = null) : IOntologyContextClient
{
    public bool Enabled => true;

    public static SionAecClient FromEnvironment(Func<string, string?> env)
    {
        var raw = env("POWER_CAD_SION_URL");
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new McpException("[SION_CONFIG] POWER_CAD_SION_URL must be an absolute http(s) URL without embedded credentials.");
        }

        var seconds = 20;
        if (int.TryParse(env("POWER_CAD_SION_TIMEOUT"), out var parsed) && parsed is >= 1 and <= 120)
        {
            seconds = parsed;
        }

        var token = env("POWER_CAD_SION_TOKEN");
        if (!baseUri.IsLoopback && string.IsNullOrWhiteSpace(token))
        {
            throw new McpException("[SION_CONFIG] POWER_CAD_SION_TOKEN is required for a remote Sion URL.");
        }

        return new SionAecClient(new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) }, baseUri, token);
    }

    public async Task<JsonObject> QueryGlobalMemoryAsync(
        string question,
        int topK,
        string? projectId,
        CancellationToken ct)
    {
        var query = $"question={Uri.EscapeDataString(question)}&top_k={topK}";
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            query += $"&project_id={Uri.EscapeDataString(projectId)}";
        }

        var endpoint = new Uri(baseUri, $"/api/v1/aec/query?{query}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new McpException($"[SION_UNAVAILABLE] Could not query Sion AEC federation: {e.Message}", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpException($"[SION_FAILED] Sion AEC federation returned {(int)response.StatusCode}: {text}");
            }

            JsonObject body;
            try
            {
                body = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("response was not an object");
            }
            catch (JsonException e)
            {
                throw new McpException("[SION_FAILED] Sion AEC federation returned invalid JSON.", e);
            }

            if (body["canonical"]?.GetValue<bool>() is not false
                || body["read_only"]?.GetValue<bool>() is not true)
            {
                throw new McpException("[SION_CONTRACT] Refusing Sion context that is not explicitly advisory/read-only.");
            }

            return body["result"] as JsonObject
                ?? throw new McpException("[SION_CONTRACT] Sion AEC federation returned no result object.");
        }
    }
}

public static class ContextClientFactory
{
    public static IOntologyContextClient FromEnvironment(Func<string, string?> env) =>
        string.IsNullOrWhiteSpace(env("POWER_CAD_SION_URL"))
            ? OntologyMcpClient.FromEnvironment(env)
            : SionAecClient.FromEnvironment(env);
}

public sealed record OntologyCandidate(
    int Choice,
    string Handle,
    string Fingerprint,
    string? ObjectId,
    string? ProjectId,
    string? Type,
    string? GeometryRef,
    double Score);

public sealed record CadContextAction(
    int Choice,
    string Operation,
    string Description,
    bool Mutating);

internal sealed record OntologyCandidateSpace(DateTimeOffset CreatedAt, IReadOnlyList<OntologyCandidate> Options);

public sealed class OntologyCandidateStore
{
    private static readonly TimeSpan CandidateTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, OntologyCandidateSpace> _spaces = new();

    internal string Put(IReadOnlyList<OntologyCandidate> options)
    {
        var contextId = Guid.NewGuid().ToString("N");
        _spaces[contextId] = new OntologyCandidateSpace(DateTimeOffset.UtcNow, options);
        Prune();
        return contextId;
    }

    internal bool TryGet(string contextId, out OntologyCandidateSpace? space)
    {
        if (!_spaces.TryGetValue(contextId, out var current) || DateTimeOffset.UtcNow - current.CreatedAt > CandidateTtl)
        {
            _spaces.TryRemove(contextId, out _);
            space = null;
            return false;
        }

        space = current;
        return true;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in _spaces)
        {
            if (now - row.Value.CreatedAt > CandidateTtl)
            {
                _spaces.TryRemove(row.Key, out _);
            }
        }
    }
}

[McpServerToolType]
public sealed partial class OntologyContextTools(
    ICadGateway gateway,
    IOntologyContextClient ontology,
    OntologyCandidateStore candidateStore)
{

    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HandlePattern();

    private static string? ExtractHandle(string? geometryRef)
    {
        if (string.IsNullOrWhiteSpace(geometryRef))
        {
            return null;
        }

        var part = geometryRef.TrimEnd('/').Split('/').LastOrDefault();
        return part is not null && HandlePattern().IsMatch(part) ? part.ToUpperInvariant() : null;
    }

    private static JsonArray StringArray(string value) => new(JsonValue.Create(value));

    private static IReadOnlyList<CadContextAction> BuildActionSpace(JsonObject entity, OntologyCandidate selected)
    {
        var actions = new List<CadContextAction>
        {
            new(1, "cad_get", "Inspect the current live entity state", false),
            new(2, "cad_move", "Move the selected entity with fingerprint protection", true),
        };

        var liveType = entity["type"]?.GetValue<string>()?.ToUpperInvariant();
        if (liveType is "TEXT" or "MTEXT")
        {
            actions.Add(new(actions.Count + 1, "cad_replace_text", "Replace text on this exact entity", true));
        }

        var semanticType = selected.Type?.ToLowerInvariant();
        if (liveType == "INSERT" && semanticType is "door" or "window" or "opening")
        {
            actions.Add(new(actions.Count + 1, "cad_modify_opening", "Modify the verified door/window/opening block", true));
        }

        return actions;
    }

    private async Task<(OntologyCandidate Candidate, JsonObject Entity)> ResolveLive(
        string contextId,
        int choice,
        CancellationToken ct)
    {
        if (!candidateStore.TryGet(contextId, out var space) || space is null)
        {
            throw new McpException("[CONTEXT_EXPIRED] Candidate context is missing or older than 10 minutes. Run cad_context_query again.");
        }

        var selected = space.Options.SingleOrDefault(option => option.Choice == choice)
            ?? throw new McpException("[INVALID_CHOICE] choice is outside the live-verified candidate space.");

        var live = await gateway.SendAsync("get", new JsonObject { ["handles"] = StringArray(selected.Handle) }, ct).ConfigureAwait(false) as JsonObject;
        var entity = (live?["entities"] as JsonArray)?.FirstOrDefault() as JsonObject
            ?? throw new McpException("[STALE_CONTEXT] The selected CAD entity no longer exists.");
        var currentFingerprint = entity["fingerprint"]?.GetValue<string>();
        if (!string.Equals(currentFingerprint, selected.Fingerprint, StringComparison.Ordinal))
        {
            throw new McpException("[STALE_CONTEXT] The selected CAD entity changed after the Ontology context was built. Run cad_context_query again.");
        }

        return (selected, entity);
    }

    [McpServerTool(Name = "cad_context_query", ReadOnly = true, Idempotent = false)]
    [Description("Query the external Ontology/CAIR memory, map only CAD-looking geometry refs to handles, then live-verify those handles in the current AutoCAD drawing. Returns a numbered candidate space; it never authorizes a mutation.")]
    public async Task<string> Query(
        [Description("Semantic question for the Ontology cross-project memory")] string question,
        [Description("Optional CAIR project id")] string? project_id = null,
        [Description("Maximum live-verified choices, 1-50")] int max_choices = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new McpException("[INVALID_ARGUMENT] question must not be empty.");
        }

        if (max_choices is < 1 or > 50)
        {
            throw new McpException("[INVALID_ARGUMENT] max_choices must be between 1 and 50.");
        }

        if (!ontology.Enabled)
        {
            throw new McpException("[ONTOLOGY_UNAVAILABLE] Ontology context is not configured.");
        }

        var memory = await ontology.QueryGlobalMemoryAsync(question, Math.Min(max_choices * 4, 100), project_id, ct).ConfigureAwait(false);
        var hits = memory["hits"] as JsonArray ?? [];
        var handles = new List<(string Handle, JsonObject Hit)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in hits)
        {
            if (node is not JsonObject hit)
            {
                continue;
            }

            var handle = ExtractHandle(hit["geometry_ref"]?.GetValue<string>());
            if (handle is not null && seen.Add(handle))
            {
                handles.Add((handle, hit));
            }

            if (handles.Count >= max_choices * 2)
            {
                break;
            }
        }

        var options = new List<OntologyCandidate>();
        foreach (var (handle, hit) in handles)
        {
            var live = await gateway.SendAsync("get", new JsonObject { ["handles"] = StringArray(handle) }, ct).ConfigureAwait(false) as JsonObject;
            var entity = (live?["entities"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (entity is null)
            {
                continue;
            }

            var fingerprint = entity["fingerprint"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                continue;
            }

            var score = hit["score"]?.GetValue<double>() ?? 0.0;
            options.Add(new OntologyCandidate(
                options.Count + 1,
                handle,
                fingerprint,
                hit["object_id"]?.GetValue<string>(),
                hit["project_id"]?.GetValue<string>(),
                hit["type"]?.GetValue<string>(),
                hit["geometry_ref"]?.GetValue<string>(),
                score));
            if (options.Count >= max_choices)
            {
                break;
            }
        }

        var contextId = candidateStore.Put(options);

        return new JsonObject
        {
            ["status"] = "SUCCESS",
            ["context_id"] = contextId,
            ["ontology_route"] = memory["route"]?.DeepClone(),
            ["query"] = question,
            ["options"] = JsonSerializer.SerializeToNode(options, CadJson.Options),
            ["may_execute_mutation"] = false,
            ["requires_live_verification"] = true,
            ["note"] = "Choose only by choice number with cad_context_select. Then use the returned handle + fingerprint in an ordinary CAD edit tool.",
        }.ToJsonString(CadJson.Options);
    }

    [McpServerTool(Name = "cad_context_actions", ReadOnly = true, Idempotent = true)]
    [Description("Return the numbered operation space allowed for one live-verified semantic candidate. The result is advisory and cannot mutate CAD.")]
    public async Task<string> Actions(
        [Description("context_id returned by cad_context_query")] string context_id,
        [Description("1-based candidate choice")] int candidate_choice,
        CancellationToken ct = default)
    {
        var (selected, entity) = await ResolveLive(context_id, candidate_choice, ct).ConfigureAwait(false);
        var actions = BuildActionSpace(entity, selected);
        return new JsonObject
        {
            ["status"] = "SUCCESS",
            ["context_id"] = context_id,
            ["candidate_choice"] = candidate_choice,
            ["selected"] = JsonSerializer.SerializeToNode(selected, CadJson.Options),
            ["actions"] = JsonSerializer.SerializeToNode(actions, CadJson.Options),
            ["may_execute_mutation"] = false,
            ["note"] = "Choose only an action number with cad_context_action_select.",
        }.ToJsonString(CadJson.Options);
    }

    [McpServerTool(Name = "cad_context_action_select", ReadOnly = true, Idempotent = true)]
    [Description("Resolve one numbered action from the live candidate action space. Revalidates the CAD fingerprint and returns the exact edit tool name without executing it.")]
    public async Task<string> SelectAction(
        [Description("context_id returned by cad_context_query")] string context_id,
        [Description("1-based candidate choice")] int candidate_choice,
        [Description("1-based action choice returned by cad_context_actions")] int action_choice,
        CancellationToken ct = default)
    {
        var (selected, entity) = await ResolveLive(context_id, candidate_choice, ct).ConfigureAwait(false);
        var actions = BuildActionSpace(entity, selected);
        var action = actions.SingleOrDefault(row => row.Choice == action_choice)
            ?? throw new McpException("[INVALID_ACTION] action_choice is outside the current live action space.");

        return new JsonObject
        {
            ["status"] = "SUCCESS",
            ["context_id"] = context_id,
            ["candidate_choice"] = candidate_choice,
            ["action_choice"] = action_choice,
            ["selected"] = JsonSerializer.SerializeToNode(selected, CadJson.Options),
            ["selected_action"] = JsonSerializer.SerializeToNode(action, CadJson.Options),
            ["live_entity"] = entity.DeepClone(),
            ["may_execute_mutation"] = false,
            ["requires_edit_tool_with_expect_fingerprint"] = action.Mutating,
        }.ToJsonString(CadJson.Options);
    }

    [McpServerTool(Name = "cad_context_select", ReadOnly = true, Idempotent = true)]
    [Description("Resolve one numbered Ontology candidate and re-verify its AutoCAD fingerprint. Selection is read-only and does not authorize a CAD mutation.")]
    public async Task<string> Select(
        [Description("context_id returned by cad_context_query")] string context_id,
        [Description("1-based choice number from cad_context_query")] int choice,
        CancellationToken ct = default)
    {
        var (selected, entity) = await ResolveLive(context_id, choice, ct).ConfigureAwait(false);

        return new JsonObject
        {
            ["status"] = "SUCCESS",
            ["context_id"] = context_id,
            ["choice"] = choice,
            ["selected"] = JsonSerializer.SerializeToNode(selected, CadJson.Options),
            ["live_entity"] = entity.DeepClone(),
            ["may_execute_mutation"] = false,
            ["requires_edit_tool_with_expect_fingerprint"] = true,
        }.ToJsonString(CadJson.Options);
    }

}
