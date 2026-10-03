using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

public interface IVisualReasoner
{
    bool Enabled { get; }
    string Provider { get; }
    string Model { get; }
    Task<JsonObject> AnalyzeAsync(byte[] image, string mimeType, string prompt, CancellationToken ct);
}

public sealed class NvidiaCosmosReasoner(
    HttpClient http,
    Uri endpoint,
    string? apiKey,
    string model,
    int maxTokens = 1200) : IVisualReasoner
{
    public bool Enabled => !string.IsNullOrWhiteSpace(apiKey) || endpoint.IsLoopback;
    public string Provider => "nvidia";
    public string Model => model;

    public static IVisualReasoner FromEnvironment(Func<string, string?> env)
    {
        var endpointText = env("NVIDIA_COSMOS_ENDPOINT")?.Trim();
        if (string.IsNullOrWhiteSpace(endpointText))
            endpointText = "https://integrate.api.nvidia.com/v1/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw new McpException("[NVIDIA_CONFIG] NVIDIA_COSMOS_ENDPOINT must be an absolute URL.");

        var model = env("NVIDIA_COSMOS_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = "nvidia/cosmos-reason2-2b";

        var apiKey = env("NVIDIA_API_KEY")?.Trim();
        var maxTokens = 1200;
        if (int.TryParse(env("NVIDIA_COSMOS_MAX_TOKENS"), out var parsed))
            maxTokens = Math.Clamp(parsed, 128, 4096);

        return new NvidiaCosmosReasoner(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, endpoint, apiKey, model, maxTokens);
    }

    public async Task<JsonObject> AnalyzeAsync(byte[] image, string mimeType, string prompt, CancellationToken ct)
    {
        if (!Enabled)
            return new JsonObject
            {
                ["status"] = "REQUIRES_CONFIGURATION",
                ["provider"] = Provider,
                ["model"] = Model,
                ["error"] = "Set NVIDIA_API_KEY for the hosted NVIDIA endpoint, or point NVIDIA_COSMOS_ENDPOINT at a local NIM."
            };

        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(image)}";
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "You are a read-only architectural CAD visual inspection engine. Return only the requested final JSON. Never authorize drawing mutations and never invent hidden geometry."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUri }
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = prompt
                        }
                    }
                }
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.1
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var compact = responseText.Length > 800 ? responseText[..800] : responseText;
            throw new McpException($"[NVIDIA_API] HTTP {(int)response.StatusCode}: {compact}");
        }

        var root = JsonNode.Parse(responseText)?.AsObject()
            ?? throw new McpException("[NVIDIA_API] Response was not valid JSON.");
        var choices = root["choices"]?.AsArray();
        var message = choices is { Count: > 0 } ? choices[0]?["message"]?.AsObject() : null;
        var finalText = ExtractContent(message?["content"]);
        if (string.IsNullOrWhiteSpace(finalText))
            throw new McpException("[NVIDIA_API] Response did not contain assistant content.");

        return ParseFinalJson(finalText);
    }

    private static string ExtractContent(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (node is JsonArray array)
            return string.Concat(array.Select(item => item?["text"]?.GetValue<string>() ?? ""));
        return "";
    }

    internal static JsonObject ParseFinalJson(string text)
    {
        // Cosmos Reason models can emit <think> traces. They are deliberately discarded:
        // Power CAD persists only the final structured evidence.
        var cleaned = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Trim();
        var first = cleaned.IndexOf('{');
        var last = cleaned.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            try
            {
                return JsonNode.Parse(cleaned[first..(last + 1)])?.AsObject()
                    ?? new JsonObject { ["summary"] = cleaned };
            }
            catch (JsonException)
            {
                // Fall through to final-text-only evidence.
            }
        }
        return new JsonObject { ["summary"] = cleaned };
    }
}

[McpServerToolType]
public sealed class NvidiaVisualTools(ICadGateway gateway, IVisualReasoner reasoner)
{
    [McpServerTool(Name = "cad_visual_inspect", ReadOnly = true, Idempotent = true)]
    [Description("Capture the current AutoCAD view and use NVIDIA Cosmos Reason as a read-only visual observer. Returns candidate architectural object types, normalized boxes and visible issues. Visual output is advisory evidence only and cannot authorize edits.")]
    public Task<string> Inspect(
        [Description("Inspection question or focus, e.g. identify doors/windows/columns and obvious drafting anomalies")] string? task = null,
        [Description("Window [[xmin,ymin],[xmax,ymax]]")] double[][]? window = null,
        [Description("Show these entity handles")] string[]? handles = null,
        [Description("Show everything in model space")] bool? extents = null,
        [Description("Extra snapshot margin")] double? margin = null,
        [Description("Image width, default 1600")] int? width = null,
        [Description("Image height")] int? height = null,
        CancellationToken ct = default) =>
        Analyze("inspect", task ?? "Identify the visible architectural objects and drafting anomalies.", null, window, handles, extents, margin, width, height, ct);

    [McpServerTool(Name = "cad_visual_verify", ReadOnly = true, Idempotent = true)]
    [Description("Visually verify the current AutoCAD view against an expected change after deterministic CAD read-back. NVIDIA Cosmos Reason is a secondary verifier only; it never makes or approves a mutation.")]
    public Task<string> Verify(
        [Description("What should now be visibly true, e.g. door D3 moved into the wall opening without overlapping the dimension text")] string expected_change,
        [Description("Additional inspection focus")] string? task = null,
        [Description("Window [[xmin,ymin],[xmax,ymax]]")] double[][]? window = null,
        [Description("Show these entity handles")] string[]? handles = null,
        [Description("Show everything in model space")] bool? extents = null,
        [Description("Extra snapshot margin")] double? margin = null,
        [Description("Image width, default 1600")] int? width = null,
        [Description("Image height")] int? height = null,
        CancellationToken ct = default) =>
        Analyze("verify", task ?? "Check only visible evidence for the expected change and report conflicts.", expected_change, window, handles, extents, margin, width, height, ct);

    private async Task<string> Analyze(
        string mode,
        string task,
        string? expectedChange,
        double[][]? window,
        string[]? handles,
        bool? extents,
        double? margin,
        int? width,
        int? height,
        CancellationToken ct)
    {
        if (!reasoner.Enabled)
        {
            return new JsonObject
            {
                ["status"] = "REQUIRES_CONFIGURATION",
                ["provider"] = reasoner.Provider,
                ["model"] = reasoner.Model,
                ["may_execute_mutation"] = false,
                ["error"] = "NVIDIA visual reasoning is disabled. Set NVIDIA_API_KEY or configure a local NVIDIA_COSMOS_ENDPOINT."
            }.ToJsonString(CadJson.Options);
        }

        var parameters = new JsonObject
        {
            ["window"] = window is null ? null : JsonSerializer.SerializeToNode(window, CadJson.Options),
            ["handles"] = handles is null ? null : JsonSerializer.SerializeToNode(handles, CadJson.Options),
            ["extents"] = extents ?? (window is null && handles is null ? true : null),
            ["margin"] = margin,
            ["width"] = width ?? 1600,
            ["height"] = height,
        };
        foreach (var key in parameters.Where(row => row.Value is null).Select(row => row.Key).ToArray())
            parameters.Remove(key);

        JsonObject snapshot;
        try
        {
            snapshot = (await gateway.SendAsync("snapshot", parameters, ct).ConfigureAwait(false))?.AsObject()
                ?? throw new McpException("[CAD_SNAPSHOT] CAD backend returned no snapshot.");
        }
        catch (CadException e)
        {
            throw new McpException($"[{e.Code}] {e.Message}", e);
        }

        var base64 = snapshot["image_base64"]?.GetValue<string>()
            ?? throw new McpException("[CAD_SNAPSHOT] CAD backend returned no image.");
        var mime = snapshot["mime_type"]?.GetValue<string>() ?? "image/png";
        var bytes = Convert.FromBase64String(base64);
        var imageHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var prompt = $"""
            Inspect this architectural CAD screenshot.
            Mode: {mode}
            Task: {task}
            Expected visible change: {expectedChange ?? "not specified"}

            Return JSON only with this schema:
            {{
              "scene_type": "floor_plan|section|elevation|detail|schedule|unknown",
              "verdict": "PASS|REVIEW|NOT_APPLICABLE",
              "summary": "short final observation",
              "objects": [
                {{
                  "type": "Wall|Door|Window|Column|Beam|Stair|Room|Dimension|Text|Grid|Hatch|Furniture|Equipment|Other",
                  "label": "visible label if any",
                  "bbox_norm": [0.0,0.0,1.0,1.0],
                  "confidence": 0.0,
                  "visible_evidence": "what is actually visible"
                }}
              ],
              "issues": [
                {{
                  "kind": "overlap|misalignment|missing|unexpected|illegible|other",
                  "severity": "info|warning|error",
                  "description": "visible issue",
                  "bbox_norm": [0.0,0.0,1.0,1.0]
                }}
              ]
            }}
            bbox_norm is [x_min,y_min,x_max,y_max] in image coordinates normalized to 0..1.
            Do not infer hidden CAD topology. Do not claim legal/code compliance. If uncertain use REVIEW.
            """;

        var evidence = await reasoner.AnalyzeAsync(bytes, mime, prompt, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["status"] = evidence["status"]?.DeepClone() ?? "SUCCESS",
            ["provider"] = reasoner.Provider,
            ["model"] = reasoner.Model,
            ["authority"] = "visual_advisory",
            ["canonical"] = false,
            ["may_execute_mutation"] = false,
            ["image_sha256"] = imageHash,
            ["snapshot"] = new JsonObject
            {
                ["width"] = snapshot["width"]?.DeepClone(),
                ["height"] = snapshot["height"]?.DeepClone(),
                ["mime_type"] = mime,
                ["window"] = snapshot["window"]?.DeepClone()
            },
            ["evidence"] = evidence.DeepClone()
        }.ToJsonString(CadJson.Options);
    }
}
