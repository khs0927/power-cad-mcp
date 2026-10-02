using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PowerCad.Core;

namespace PowerCad.Server;

// Read-only client for the Ontology (aec_intelligence) building-data REST API and the ontology_* MCP tools.
// This is a port of the Python package's src/power_cad_mcp/ontology.py (+ its tool registration in server.py):
// same tool names, parameters, Korean kind aliases, keyset cursor paging, error texts, task inference and
// auto_context bundle. The JSON shapes of the /v1 endpoints are still settling, so everything here is tolerant:
// lists may arrive bare or wrapped (items/results/hits/...), and element fields are read through aliases.
// It is independent of OntologyContext.cs (Sion / aec-mcp stdio, cad_context_* tools).

/// <summary>The Ontology REST service failed. <see cref="Unavailable"/> means it could not be reached (or is not configured).</summary>
public sealed class OntologyRestException(string detail, bool unavailable = false, string? code = null, Exception? inner = null)
    : McpException($"[{code ?? (unavailable ? "ONTOLOGY_UNAVAILABLE" : "ONTOLOGY_FAILED")}] {detail}", inner)
{
    /// <summary>The message without the [CODE] prefix (what the Python client raises).</summary>
    public string Detail { get; } = detail;

    public bool Unavailable { get; } = unavailable;
}

/// <summary>One page of normalised rows plus the keyset cursor that continues after it.</summary>
public sealed record OntologyPage(List<JsonObject> Items, string? NextCursor)
{
    public JsonArray ItemsJson() => new(Items.Select(i => (JsonNode)i.DeepClone()).ToArray());
}

/// <summary>Thin JSON-over-HTTP client. Every call throws <see cref="OntologyRestException"/> on failure.</summary>
public sealed class OntologyRestClient
{
    public const int MaxPage = 500; // the API's MAX_LIMIT

    private readonly HttpClient? _http;
    private readonly string? _configError;

    public OntologyRestClient(string? baseUrl, double timeoutSeconds = 10.0, string? token = null, HttpMessageHandler? handler = null)
    {
        TimeoutSeconds = timeoutSeconds;
        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return; // not configured: every call answers [ONTOLOGY_NOT_CONFIGURED]
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            _configError = $"POWERCAD_ONTOLOGY_URL must be an http(s) URL such as http://127.0.0.1:58000 (got '{trimmed}').";
            return;
        }

        BaseUrl = trimmed;
        // A local Ontology API must never be routed through an outbound HTTP proxy.
        handler ??= new SocketsHttpHandler { UseProxy = !uri.IsLoopback };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    public string? BaseUrl { get; }

    public double TimeoutSeconds { get; }

    public string? Token { get; }

    public bool Configured => BaseUrl is not null;

    /// <summary>POWERCAD_ONTOLOGY_URL (or POWER_CAD_ONTOLOGY_URL), POWERCAD_ONTOLOGY_TIMEOUT (1-120 s, default 10), POWERCAD_ONTOLOGY_TOKEN.</summary>
    public static OntologyRestClient FromEnvironment(Func<string, string?> env, HttpMessageHandler? handler = null)
    {
        static string? First(Func<string, string?> env, params string[] names) =>
            names.Select(env).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        var url = First(env, "POWERCAD_ONTOLOGY_URL", "POWER_CAD_ONTOLOGY_URL");
        var timeout = 10.0;
        if (First(env, "POWERCAD_ONTOLOGY_TIMEOUT") is { } raw
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            timeout = Math.Clamp(parsed, 1.0, 120.0);
        }

        return new OntologyRestClient(url, timeout, First(env, "POWERCAD_ONTOLOGY_TOKEN"), handler);
    }

    /// <summary>Throws when POWERCAD_ONTOLOGY_URL is unset or not an http(s) URL.</summary>
    public void EnsureConfigured()
    {
        if (_configError is not null)
        {
            throw new OntologyRestException(_configError, code: "ONTOLOGY_CONFIG");
        }

        if (_http is null)
        {
            throw new OntologyRestException(
                "Ontology REST API is not configured. Set POWERCAD_ONTOLOGY_URL (e.g. http://127.0.0.1:58000, the Ontology "
                + "`docker compose up api` default) to enable the ontology_* tools.",
                unavailable: true,
                code: "ONTOLOGY_NOT_CONFIGURED");
        }
    }

    private string Seconds => TimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ transport
    private string Url(string path, IEnumerable<KeyValuePair<string, string?>>? query)
    {
        var url = BaseUrl + "/" + path.TrimStart('/');
        var clean = (query ?? []).Where(kv => !string.IsNullOrEmpty(kv.Value)).ToList();
        if (clean.Count > 0)
        {
            url += "?" + string.Join("&", clean.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!)));
        }

        return url;
    }

    public async Task<JsonNode?> RequestAsync(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        JsonNode? body = null,
        CancellationToken ct = default)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(method, Url(path, query));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("power-cad-server");
        if (Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(OntologyRest.Relaxed), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new OntologyRestException(
                $"Ontology service at {BaseUrl} did not answer within {Seconds}s (raise POWERCAD_ONTOLOGY_TIMEOUT or check the service).",
                unavailable: true,
                inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new OntologyRestException(
                $"Ontology service is not reachable at {BaseUrl} ({e.InnerException?.Message ?? e.Message}). Start the Ontology API "
                + "(aec_intelligence) or point POWERCAD_ONTOLOGY_URL at it.",
                unavailable: true,
                inner: e);
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                throw new OntologyRestException($"Ontology service at {BaseUrl} dropped the connection ({e.Message}).", unavailable: true, inner: e);
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var detail = ErrorDetail(text);
                if (code == 404 && detail.Length == 0)
                {
                    throw new OntologyRestException(
                        $"Ontology endpoint {method.Method} {path} not found (HTTP 404) at {BaseUrl}; the Ontology API may be older than this client.");
                }

                throw new OntologyRestException($"Ontology {method.Method} {path} failed with HTTP {code}" + (detail.Length > 0 ? $": {detail}" : "."));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                throw new OntologyRestException($"Ontology {method.Method} {path} returned non-JSON data.", inner: e);
            }
        }
    }

    private static string ErrorDetail(string raw)
    {
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return Cut(raw.Trim());
        }

        if (data is JsonObject obj)
        {
            foreach (var key in new[] { "detail", "error", "message" })
            {
                if (OntologyRest.Truthy(obj[key]))
                {
                    return Cut(OntologyRest.Str(obj[key]));
                }
            }
        }

        return "";

        static string Cut(string s) => s.Length > 300 ? s[..300] : s;
    }

    public Task<JsonNode?> GetAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, path, query, null, ct);

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, path, null, body, ct);

    // ------------------------------------------------------------------ endpoints
    /// <summary>
    /// Follow the API's keyset next_cursor until <paramref name="want"/> rows pass <paramref name="accept"/>. Without
    /// accept exactly one page is read and its next_cursor is handed back; with client-side filtering several pages
    /// may be read (bounded by <paramref name="maxPages"/>) and the cursor continues after the last page read.
    /// </summary>
    private async Task<OntologyPage> PagesAsync(
        string path,
        List<KeyValuePair<string, string?>> query,
        int want,
        string? cursor,
        Func<JsonObject, bool>? accept,
        int maxPages,
        CancellationToken ct)
    {
        var found = new List<JsonObject>();
        var pageSize = accept is null ? Math.Min(Math.Max(want, 1), MaxPage) : MaxPage;
        for (var i = 0; i < (accept is null ? 1 : maxPages); i++)
        {
            var q = new List<KeyValuePair<string, string?>>(query)
            {
                new("limit", pageSize.ToString(CultureInfo.InvariantCulture)),
                new("cursor", cursor),
            };
            var payload = await GetAsync(path, q, ct).ConfigureAwait(false);
            found.AddRange(OntologyRest.Rows(payload, "elements", "blocks", "drawings", "sheets").Where(row => accept is null || accept(row)));
            cursor = payload is JsonObject obj && OntologyRest.Truthy(obj["next_cursor"]) ? OntologyRest.Str(obj["next_cursor"]) : null;
            if (found.Count >= want || cursor is null)
            {
                break;
            }
        }

        return new OntologyPage(found.Take(Math.Max(want, 0)).ToList(), cursor);
    }

    public Task<JsonNode?> CatalogAsync(string? projectId = null, CancellationToken ct = default) =>
        GetAsync("/v1/catalog", [new("project_id", projectId)], ct);

    /// <summary>GET /v1/elements. kind takes Korean aliases and commas (Door,창호). storey/sheet are filtered here, on the returned rows.</summary>
    public async Task<OntologyPage> ElementsAsync(
        string? kind = null,
        string? projectId = null,
        string? storey = null,
        string? sheet = null,
        string? text = null,
        string? drawingCategory = null,
        string? layer = null,
        string? blockName = null,
        string? bbox = null,
        bool includeProperties = true,
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
    {
        List<KeyValuePair<string, string?>> query =
        [
            new("kind", kind),
            new("project_id", projectId),
            // Not filtered by the API today (filtered below, client-side); sent so an API that learns it can filter server-side.
            new("storey", OntologyRest.StoreyParam(storey)),
            new("text", text),
            new("drawing_category", OntologyRest.CategoryParam(drawingCategory)),
            new("layer", layer),
            new("block_name", blockName),
            new("bbox", bbox),
            new("include_properties", includeProperties ? "true" : null),
        ];
        Func<JsonObject, bool>? accept = null;
        if (!string.IsNullOrEmpty(storey) || !string.IsNullOrEmpty(sheet))
        {
            var wantStorey = string.IsNullOrEmpty(storey) ? null : OntologyRest.NormalizeStorey(storey);
            accept = row =>
            {
                var el = OntologyRest.CompactElement(row);
                if (!string.IsNullOrEmpty(wantStorey) && OntologyRest.NormalizeStorey(OntologyRest.StrOrEmpty(el["storey"])) != wantStorey)
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(sheet))
                {
                    var where = string.Join(" ", new[] { "sheet", "source_file", "drawing_number" }.Select(k => OntologyRest.StrOrEmpty(el[k])));
                    return where.Contains(sheet, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            };
        }

        var page = await PagesAsync("/v1/elements", query, limit, cursor, accept, 10, ct).ConfigureAwait(false);
        return page with { Items = page.Items.Select(OntologyRest.CompactElement).ToList() };
    }

    /// <summary>GET /v1/blocks?name_like=. category (Door, 창호 ...) is matched against the kinds the block's instances were classified as.</summary>
    public async Task<OntologyPage> BlocksAsync(
        string? category = null,
        string? q = null,
        string? projectId = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
    {
        List<KeyValuePair<string, string?>> query = [new("project_id", projectId), new("name_like", q)];
        Func<JsonObject, bool>? accept = null;
        if (!string.IsNullOrEmpty(category))
        {
            var wanted = OntologyRest.ResolveKindWords(category).Select(k => k.ToLowerInvariant()).ToHashSet();
            accept = row =>
            {
                var names = new HashSet<string>();
                if (row["instance_kinds"] is JsonObject kinds)
                {
                    names.UnionWith(kinds.Select(kv => kv.Key.ToLowerInvariant()));
                }

                foreach (var key in new[] { "classified_as", "category", "kind", "class" })
                {
                    if (OntologyRest.Truthy(row[key]))
                    {
                        names.Add(OntologyRest.Str(row[key]).ToLowerInvariant());
                    }
                }

                return names.Overlaps(wanted);
            };
        }

        var page = await PagesAsync("/v1/blocks", query, limit, cursor, accept, 10, ct).ConfigureAwait(false);
        return page with { Items = page.Items.Select(OntologyRest.CompactBlock).ToList() };
    }

    /// <summary>GET /v1/drawings?category= flattened to one row per sheet; q filters those rows.</summary>
    public async Task<OntologyPage> DrawingsAsync(
        string? category = null,
        string? q = null,
        string? projectId = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
    {
        List<KeyValuePair<string, string?>> query = [new("project_id", projectId), new("category", OntologyRest.CategoryParam(category))];
        Func<JsonObject, bool>? accept = null;
        if (!string.IsNullOrEmpty(q))
        {
            accept = row => OntologyRest.Dump(row).Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        var docs = await PagesAsync("/v1/drawings", query, limit, cursor, accept, 5, ct).ConfigureAwait(false);
        var sheets = docs.Items.SelectMany(doc => OntologyRest.DrawingRows(doc, filtered: !string.IsNullOrEmpty(category))).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var matching = sheets.Where(r => OntologyRest.Dump(r).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count > 0)
            {
                sheets = matching;
            }
        }

        return new OntologyPage(sheets.Take(limit).ToList(), docs.NextCursor);
    }

    public Task<JsonNode?> ElementContextAsync(string elementId, int hops = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(elementId))
        {
            throw new OntologyRestException("element_id must not be empty.");
        }

        if (elementId.Trim().All(c => c == '.'))
        {
            throw new OntologyRestException("element_id must not consist only of dots.", code: "INVALID_ARGUMENT");
        }

        var quoted = Uri.EscapeDataString(elementId);
        return GetAsync($"/v1/elements/{quoted}/context", [new("hops", Math.Clamp(hops, 1, 2).ToString(CultureInfo.InvariantCulture))], ct);
    }

    /// <summary>POST /v1/search (hybrid lexical + vector + graph expansion).</summary>
    public async Task<List<JsonObject>> SearchAsync(
        string query,
        int k = 10,
        string? model = null,
        string? kind = null,
        string? storey = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new OntologyRestException("query must not be empty.");
        }

        var body = new JsonObject { ["query"] = query, ["top_k"] = Math.Clamp(k, 1, 100) };
        foreach (var (key, value) in new[] { ("kind", kind), ("storey", storey), ("project_id", projectId), ("model", model) })
        {
            if (!string.IsNullOrEmpty(value))
            {
                body[key] = value;
            }
        }

        var payload = await PostAsync("/v1/search", body, ct).ConfigureAwait(false);
        return OntologyRest.Rows(payload, "hits", "results").Select(OntologyRest.CompactElement).Take(Math.Max(k, 0)).ToList();
    }
}

/// <summary>What <see cref="OntologyRest.InferTask"/> reads out of a free-text (KO/EN) task.</summary>
public sealed record OntologyTaskHints(List<string> Classes, List<string> DrawingCategories, bool IncludeBlocks, string? Storey, List<string> Marks)
{
    public JsonObject ToJson() => new()
    {
        ["classes"] = new JsonArray(Classes.Select(c => (JsonNode)c).ToArray()),
        ["drawing_categories"] = new JsonArray(DrawingCategories.Select(c => (JsonNode)c).ToArray()),
        ["include_blocks"] = IncludeBlocks,
        ["storey"] = Storey,
        ["marks"] = new JsonArray(Marks.Select(c => (JsonNode)c).ToArray()),
    };
}

/// <summary>Normalising, task inference and the auto_context bundle (ports of the Python module-level functions).</summary>
public static partial class OntologyRest
{
    internal static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] ListKeys = ["items", "results", "data", "rows", "hits"];

    // Categories the API does not know under our English name.
    private static readonly Dictionary<string, string> CategoryParams = new() { ["schedule"] = "창호도" };

    // Korean words for element kinds (the API resolves these itself for /v1/elements; blocks need it here).
    private static readonly (string Kind, string Words)[] KindWords =
    [
        ("Door", "문 출입문 방화문 도어"),
        ("Window", "창 창호 창문 윈도우"),
        ("Wall", "벽 벽체 외벽 내벽"),
        ("Space", "실 공간 방 실명"),
        ("Column", "기둥"),
        ("Beam", "보 거더"),
        ("SteelSection", "철골 형강 강재"),
        ("Stair", "계단"),
        ("Slab", "슬래브"),
        ("Furniture", "가구 집기 비품"),
    ];

    // ------------------------------------------------------------------ JSON helpers
    /// <summary>Python truthiness of a JSON value: null, "", [], {}, 0 and false are falsy.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Number => v.TryGetValue<double>(out var d) ? d != 0 : true,
            _ => true,
        },
        _ => true,
    };

    /// <summary>A value "not in (None, '', [], {})" - unlike <see cref="Truthy"/>, 0 and false are kept.</summary>
    private static bool Present(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Null => false,
            _ => true,
        },
        _ => true,
    };

    internal static string Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString(Relaxed) ?? "";

    /// <summary>Python's <c>str(value or "")</c>.</summary>
    internal static string StrOrEmpty(JsonNode? node) => Truthy(node) ? Str(node) : "";

    internal static string Dump(JsonNode node) => node.ToJsonString(Relaxed);

    private static JsonNode? Pick(JsonObject row, params string[] names)
    {
        foreach (var name in names)
        {
            if (Present(row[name]))
            {
                return row[name];
            }
        }

        return null;
    }

    private static JsonObject Sub(JsonObject row, string key) => row[key] as JsonObject ?? [];

    private static JsonObject DropEmpty(params (string Key, JsonNode? Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            if (Present(value))
            {
                obj[key] = value!.Parent is null ? value : value.DeepClone();
            }
        }

        return obj;
    }

    private static List<string> Unique(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(seen.Add).ToList();
    }

    /// <summary>Pull the list of records out of a bare list or a wrapper object.</summary>
    public static List<JsonObject> Rows(JsonNode? payload, params string[] keys)
    {
        if (payload is JsonArray list)
        {
            return list.OfType<JsonObject>().ToList();
        }

        if (payload is JsonObject obj)
        {
            foreach (var key in keys.Concat(ListKeys))
            {
                var value = obj[key];
                if (value is JsonArray array)
                {
                    return array.OfType<JsonObject>().ToList();
                }

                if (value is JsonObject inner) // e.g. {"data": {"items": [...]}}
                {
                    var nested = Rows(inner, keys);
                    if (nested.Count > 0)
                    {
                        return nested;
                    }
                }
            }
        }

        return [];
    }

    public static string? CategoryParam(string? category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return null;
        }

        var trimmed = category.Trim();
        return CategoryParams.TryGetValue(trimmed.ToLowerInvariant(), out var mapped) ? mapped : trimmed;
    }

    /// <summary>'창호' -> ['창호', 'Window', 'Door']; 'Door,Window' -> both (plus the raw terms).</summary>
    public static List<string> ResolveKindWords(string value)
    {
        var output = new List<string>();
        foreach (var term in value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            output.Add(term);
            if (term == "창호")
            {
                output.AddRange(["Window", "Door"]);
            }

            output.AddRange(KindWords.Where(k => k.Words.Split(' ').Contains(term)).Select(k => k.Kind));
        }

        return Unique(output);
    }

    private static int Digits(string value) =>
        value.Aggregate(0, (acc, ch) => checked((acc * 10) + (int)char.GetNumericValue(ch)));

    [GeneratedRegex(@"(?:지하|\bB)\s*0*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreyBasementLoose();

    [GeneratedRegex(@"지붕|옥상|옥탑|roof|^RF$|^R$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreyRoofLoose();

    [GeneratedRegex(@"0*(\d+)")]
    private static partial Regex StoreyNumber();

    /// <summary>'2층', '2F', '02', 'L2' -> '2F'; '지하1층', 'B1' -> 'B1F'; roof -> 'RF'.</summary>
    public static string NormalizeStorey(string? value)
    {
        var text = (value ?? "").Trim();
        if (StoreyBasementLoose().Match(text) is { Success: true } b)
        {
            return $"B{Digits(b.Groups[1].Value)}F";
        }

        if (StoreyRoofLoose().IsMatch(text))
        {
            return "RF";
        }

        if (StoreyNumber().Match(text) is { Success: true } n)
        {
            return $"{Digits(n.Groups[1].Value)}F";
        }

        return text.ToUpperInvariant();
    }

    [GeneratedRegex(@"^B(\d+)F\z")]
    private static partial Regex BasementStoreyNorm();

    [GeneratedRegex(@"^(\d+)F\z")]
    private static partial Regex StoreyNorm();

    /// <summary>
    /// Storey as the storey query parameter, in the Korean form the drawings carry: '2F' -> '2층', 'B1' -> '지하1층',
    /// 'roof' -> '지붕층'. Unrecognised text is passed through unchanged.
    /// </summary>
    public static string? StoreyParam(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var norm = NormalizeStorey(value);
        if (BasementStoreyNorm().Match(norm) is { Success: true } b)
        {
            return $"지하{b.Groups[1].Value}층";
        }

        if (norm == "RF")
        {
            return "지붕층";
        }

        if (StoreyNorm().Match(norm) is { Success: true } n)
        {
            return $"{n.Groups[1].Value}층";
        }

        return value.Trim();
    }

    /// <summary>Element / search hit in one stable shape (/v1/elements items, /v1/search hits).</summary>
    public static JsonObject CompactElement(JsonObject row)
    {
        var ev = Sub(row, "evidence");
        if (ev.Count == 0)
        {
            ev = Sub(row, "citation");
        }

        return DropEmpty(
            ("id", Pick(row, "id", "element_id", "object_id", "uid")),
            ("class", Pick(row, "class", "element_class", "kind", "type", "ifc_class")),
            ("name", Pick(row, "name", "label", "title", "mark")),
            ("source_file", Pick(row, "source_file", "file", "document_name", "path") ?? Pick(ev, "source_name", "document_name", "source_path")),
            ("sheet", Pick(row, "sheet", "drawing_number", "layout", "layout_or_page") ?? Pick(ev, "layout", "layout_or_page", "page")),
            ("storey", Pick(row, "storey", "level", "floor")),
            ("drawing_category", Pick(row, "drawing_category")),
            ("layer", Pick(row, "layer")),
            ("block_name", Pick(row, "block_name", "block")),
            ("handle", Pick(row, "handle", "handle_or_id") ?? Pick(ev, "handle", "handle_or_id")),
            ("document_id", Pick(row, "document_id")),
            ("project_id", Pick(row, "project_id")),
            ("score", Pick(row, "score")),
            ("bbox", Pick(row, "bbox")),
            ("attributes", Pick(row, "attributes", "attribs")),
            ("properties", Pick(row, "properties", "props", "payload")));
    }

    public static JsonObject CompactBlock(JsonObject row)
    {
        JsonNode? tags = Pick(row, "attribute_tags", "attributes", "tags");
        if (tags is JsonObject tagObject)
        {
            tags = new JsonArray(tagObject.Select(kv => (JsonNode)kv.Key).ToArray());
        }

        JsonNode? files = Pick(row, "example_files", "files", "examples", "source_files");
        if (!Truthy(files) && row["definitions"] is JsonArray definitions)
        {
            files = new JsonArray(Unique(definitions.OfType<JsonObject>()
                    .Where(d => Truthy(d["document_name"]))
                    .Select(d => Str(d["document_name"])))
                .Select(f => (JsonNode)f).ToArray());
        }

        if (files is JsonArray fileList && fileList.Count > 5)
        {
            files = new JsonArray(fileList.Take(5).Select(f => f?.DeepClone()).ToArray());
        }

        return DropEmpty(
            ("name", Pick(row, "name", "block_name")),
            ("category", Pick(row, "category", "classified_as", "class", "kind")),
            ("instance_count", Pick(row, "instance_count", "count", "instances")),
            ("instance_kinds", Pick(row, "instance_kinds")),
            ("attribute_tags", tags),
            ("layers", Pick(row, "layers")),
            ("example_files", files),
            ("effective_names", Pick(row, "effective_names")),
            ("is_xref", Truthy(row["is_xref"]) ? true : null),
            ("is_anonymous", Truthy(row["is_anonymous"]) ? true : null));
    }

    public static JsonObject CompactDrawing(JsonObject row) => DropEmpty(
        ("drawing_number", Pick(row, "drawing_number", "number", "sheet", "sheet_number")),
        ("title", Pick(row, "title", "drawing_title", "name", "label")),
        ("category", Pick(row, "category", "drawing_category", "kind", "type")),
        ("scale", Pick(row, "scale")),
        ("file", Pick(row, "file", "source_file", "path", "document_name")));

    /// <summary>One /v1/drawings document -> one row per sheet (drawing number, title, scale, category, file).</summary>
    public static List<JsonObject> DrawingRows(JsonObject doc, bool filtered = false)
    {
        if (doc["sheets"] is not JsonArray sheets) // already a flat sheet row
        {
            return [CompactDrawing(doc)];
        }

        var file = Pick(doc, "name", "file", "document_name", "source_key");
        var picked = sheets.OfType<JsonObject>().ToList();
        if (filtered && picked.Any(s => Truthy(s["matches_category"])))
        {
            picked = picked.Where(s => Truthy(s["matches_category"])).ToList();
        }

        var output = new List<JsonObject>();
        foreach (var sheet in picked)
        {
            var tb = Sub(sheet, "title_block");
            output.Add(DropEmpty(
                ("drawing_number", Pick(tb, "drawing_number")),
                ("title", Pick(tb, "drawing_title") ?? Pick(sheet, "view_label")),
                ("category", Pick(sheet, "drawing_category")),
                ("scale", Pick(tb, "scale")),
                ("file", file),
                ("layout", Pick(sheet, "layout")),
                ("document_id", Pick(doc, "document_id", "id")),
                ("element_counts", Pick(sheet, "element_counts"))));
        }

        if (output.Count == 0)
        {
            var cats = doc["drawing_categories"];
            JsonNode? category = cats is JsonArray list ? string.Join(", ", list.Select(Str)) : cats;
            output.Add(DropEmpty(("file", file), ("category", category), ("document_id", Pick(doc, "document_id", "id"))));
        }

        return output;
    }

    // ---------------------------------------------------------------- task inference
    // Each rule: (regex over the lower-cased task, element classes, drawing categories, wants blocks).
    // Korean single-syllable words (문, 창, 실, 보) are guarded so that e.g. 문서/창고/실행/보고 do not match.
    private const string P = @"(?=$|[\s,./()\[\]]|[에의을를은는이가도로과와만])"; // end of word or a Korean particle

    private static readonly (Regex Pattern, string[] Classes, string[] Categories, bool Blocks)[] Rules =
    [
        (R(@"(?<![창주질전논소방])문(?![서자제의장구법화])|도어|\bdoors?\b"), ["Door"], [], true),
        (R(@"창호"), ["Window", "Door"], [], true),
        (R(@"창(?![고구작업])|\bwindows?\b|\bsash\b|커튼\s*월|curtain\s*wall"), ["Window"], [], true),
        (R(@"벽|\bwalls?\b|파티션|partition"), ["Wall"], [], false),
        (R(@"(?<![가-힣])실" + P + @"|[가-힣]실" + P + @"|실명|실별|(?<![소지예후전사])방(?![법향식지송])|\brooms?\b|\bspaces?\b"), ["Space"], [], false),
        (R(@"기둥|\bcolumns?\b"), ["Column"], ["structural"], false),
        (R(@"(?<![가-힣])보" + P + @"|큰보|작은보|거더|\bbeams?\b|\bgirders?\b"), ["Beam"], ["structural"], false),
        (R(@"철골|h\s*-?\s*형강|형강|\bsteel\b|\bh-?beam\b"), ["SteelSection", "Column", "Beam"], ["structural"], false),
        (R(@"계단|\bstairs?\b"), ["Stair"], [], false),
        (R(@"슬래브|\bslabs?\b"), ["Slab"], [], false),
        (R(@"블록|블럭|\bblocks?\b|심볼|\bsymbols?\b"), [], [], true),
        (R(@"평면|\bplans?\b|\bfloor\s*plan"), [], ["plan"], false),
        (R(@"상세|\bdetails?\b|디테일"), [], ["detail"], false),
        (R(@"단면|\bsections?\b"), [], ["section"], false),
        (R(@"입면|\belevations?\b"), [], ["elevation"], false),
        (R(@"구조|\bstructural\b"), [], ["structural"], false),
        (R(@"창호도|창호\s*일람"), [], ["schedule"], false),
        (R(@"일람표|리스트|목록|스케줄|\bschedules?\b|\blists?\b|\btable\b"), [], ["schedule"], false),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?:지하\s*(\d{1,2})\s*층|(?<![\w-])B(\d{1,2})F\b)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreyBasement();

    [GeneratedRegex(@"(?<![\w-])(\d{1,3})\s*(?:층|F\b|st floor|nd floor|rd floor|th floor)", RegexOptions.IgnoreCase)]
    private static partial Regex Storey();

    [GeneratedRegex(@"지붕층|옥탑|옥상|\broof\b|\bRF\b", RegexOptions.IgnoreCase)]
    private static partial Regex Roof();

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Z]{1,4}-?\d{1,4}[A-Z]?)(?![A-Za-z0-9])")]
    private static partial Regex Mark();

    [GeneratedRegex(@"^(?:B?\d+F|RF|H|F)\z")]
    private static partial Regex StoreyLikeMark();

    /// <summary>Infer element classes, drawing categories, storey and marks from a free-text (KO/EN) task.</summary>
    public static OntologyTaskHints InferTask(string? task)
    {
        var text = task ?? "";
        var low = text.ToLowerInvariant();
        var classes = new List<string>();
        var categories = new List<string>();
        var blocks = false;
        foreach (var (pattern, cls, cats, wantsBlocks) in Rules)
        {
            if (pattern.IsMatch(low))
            {
                classes.AddRange(cls);
                categories.AddRange(cats);
                blocks = blocks || wantsBlocks;
            }
        }

        string? storey = null;
        if (StoreyBasement().Match(text) is { Success: true } b)
        {
            storey = $"B{(b.Groups[1].Success ? b.Groups[1].Value : b.Groups[2].Value)}F";
        }
        else if (Storey().Match(text) is { Success: true } s)
        {
            storey = $"{Digits(s.Groups[1].Value)}F";
        }
        else if (Roof().IsMatch(text))
        {
            storey = "RF";
        }

        var marks = Unique(Mark().Matches(text).Select(m => m.Groups[1].Value))
            .Where(mark => !StoreyLikeMark().IsMatch(mark) && mark.Any(char.IsDigit))
            .ToList();
        return new OntologyTaskHints(Unique(classes), Unique(categories), blocks, storey, marks);
    }

    /// <summary>
    /// Short queries that the phrase-matching /v1/search can hit, as (word, kind) pairs: marks and the drawing (kind null),
    /// then one word per inferred class with that class as the kind filter.
    /// </summary>
    public static List<(string Word, string? Kind)> SearchKeywords(OntologyTaskHints hints, string? drawing = null)
    {
        var words = hints.Marks.Select(m => (Word: m, Kind: (string?)null)).ToList();
        drawing = drawing?.Trim();
        if (!string.IsNullOrEmpty(drawing))
        {
            words.Add((drawing, null));
        }

        foreach (var cls in hints.Classes)
        {
            var known = KindWords.FirstOrDefault(k => k.Kind == cls).Words;
            words.Add((known is null ? cls : known.Split(' ')[0], cls));
        }

        var seen = new HashSet<(string, string?)>();
        return words.Where(w => !string.IsNullOrEmpty(w.Word) && seen.Add(w)).ToList();
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static List<JsonObject> Dedupe(IEnumerable<JsonObject> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<JsonObject>();
        foreach (var item in items)
        {
            var key = Truthy(item["id"]) ? "id:" + Str(item["id"]) : "json:" + CadJson.Canonical(item)!.ToJsonString(Relaxed);
            if (seen.Add(key))
            {
                output.Add(item);
            }
        }

        return output;
    }

    private static JsonArray Array(IEnumerable<JsonObject> items) => new(items.Select(i => (JsonNode)i.DeepClone()).ToArray());

    /// <summary>
    /// Collect everything an automation needs for <paramref name="task"/> in one bundle. Throws when the service is down
    /// or not configured; any other per-endpoint failure is reported under warnings so one missing endpoint does not hide the rest.
    /// </summary>
    public static async Task<JsonObject> AutoContextAsync(
        OntologyRestClient client,
        string task,
        string? drawing = null,
        int limit = 20,
        int k = 10,
        string? projectId = null,
        JsonObject? openDrawing = null,
        CancellationToken ct = default)
    {
        client.EnsureConfigured();
        drawing = string.IsNullOrWhiteSpace(drawing) ? null : drawing.Trim();
        projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        if (string.IsNullOrWhiteSpace(task))
        {
            throw new OntologyRestException("task must not be empty.");
        }

        var hints = InferTask(task);
        var warnings = new List<string>();

        async Task<T?> Attempt<T>(string label, Func<Task<T>> call)
            where T : class
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (OntologyRestException e) when (!e.Unavailable)
            {
                warnings.Add($"{label}: {e.Detail}");
                return null;
            }
        }

        var storey = hints.Storey;

        // Search with the inferred kind/storey; the API matches storey exactly and many drawings carry no storey at all,
        // so an empty storey-scoped result is retried without it.
        async Task<List<JsonObject>> ScopedSearch(string label, string text, string? kind)
        {
            var found = await Attempt(label, () => client.SearchAsync(text, k, kind: kind, storey: storey, projectId: projectId, ct: ct)).ConfigureAwait(false);
            if ((found is null || found.Count == 0) && storey is not null)
            {
                found = await Attempt(label, () => client.SearchAsync(text, k, kind: kind, projectId: projectId, ct: ct)).ConfigureAwait(false);
            }

            return found ?? [];
        }

        var query = drawing is null ? task : $"{task} {drawing}";
        var singleKind = hints.Classes.Count == 1 ? hints.Classes[0] : null;
        var foundHits = await ScopedSearch("search", query, singleKind).ConfigureAwait(false);
        if (foundHits.Count == 0 && singleKind is not null)
        {
            foundHits = await Attempt("search", () => client.SearchAsync(query, k, projectId: projectId, ct: ct)).ConfigureAwait(false) ?? [];
        }

        if (foundHits.Count == 0)
        {
            // The API matches the whole query as one phrase (ILIKE / trigram), so a task sentence rarely hits without
            // real embeddings; retry with the marks, drawing and one word per class (scoped to that class).
            var queried = new List<string>();
            foreach (var (word, kind) in SearchKeywords(hints, drawing))
            {
                queried.Add(kind is null ? word : $"{word} ({kind})");
                foundHits.AddRange(await ScopedSearch($"search[{word}]", word, kind).ConfigureAwait(false));
                if (Dedupe(foundHits).Count >= k)
                {
                    break;
                }
            }

            if (queried.Count > 0)
            {
                warnings.Add("search: the whole-task search returned nothing or failed; "
                    + $"used keywords {PyList(queried)}" + (foundHits.Count > 0 ? "." : " (no hit either)."));
            }
        }

        var search = Dedupe(foundHits).Take(k).ToList();

        var markList = hints.Marks.Count > 0 ? hints.Marks.Select(m => (string?)m).ToList() : [null];
        var elements = new JsonObject();
        var elementCounts = new JsonObject();
        foreach (var cls in hints.Classes)
        {
            var found = new List<JsonObject>();
            foreach (var mark in markList)
            {
                var page = await Attempt($"elements[{cls}]", () => client.ElementsAsync(
                    cls, projectId: projectId, storey: storey, sheet: drawing, text: mark, limit: limit, ct: ct)).ConfigureAwait(false);
                found.AddRange(page?.Items ?? []);
            }

            if (found.Count == 0 && (storey is not null || drawing is not null || hints.Marks.Count > 0))
            {
                // The filters are best-effort hints; fall back to the whole class rather than nothing.
                var relaxed = await Attempt($"elements[{cls}]", () => client.ElementsAsync(cls, projectId: projectId, limit: limit, ct: ct)).ConfigureAwait(false);
                if (relaxed is { Items.Count: > 0 })
                {
                    warnings.Add($"elements[{cls}]: no match for storey/sheet/mark filters; showing all {cls}.");
                    found = relaxed.Items;
                }
            }

            var kept = Dedupe(found).Take(limit).ToList();
            elements[cls] = Array(kept);
            elementCounts[cls] = kept.Count;
        }

        var drawings = new JsonObject();
        var drawingCounts = new JsonObject();
        foreach (var cat in hints.DrawingCategories)
        {
            var page = await Attempt($"drawings[{cat}]", () => client.DrawingsAsync(cat, drawing, projectId, limit: limit, ct: ct)).ConfigureAwait(false);
            drawings[cat] = Array(page?.Items ?? []);
            drawingCounts[cat] = page?.Items.Count ?? 0;
        }

        if (drawing is not null && drawings.Count == 0)
        {
            var page = await Attempt("drawings", () => client.DrawingsAsync(null, drawing, projectId, limit: limit, ct: ct)).ConfigureAwait(false);
            drawings["match"] = Array(page?.Items ?? []);
            drawingCounts["match"] = page?.Items.Count ?? 0;
        }

        var blocks = new JsonObject();
        var blockCounts = new JsonObject();
        if (hints.IncludeBlocks)
        {
            var blockClasses = hints.Classes.Where(c => c is "Door" or "Window" or "Furniture").ToList();
            foreach (var cls in blockClasses.Count > 0 ? blockClasses : ["all"])
            {
                var page = await Attempt($"blocks[{cls}]", () => client.BlocksAsync(cls == "all" ? null : cls, projectId: projectId, limit: limit, ct: ct)).ConfigureAwait(false);
                blocks[cls] = Array(page?.Items ?? []);
                blockCounts[cls] = page?.Items.Count ?? 0;
            }
        }

        var bundle = new JsonObject
        {
            ["task"] = task,
            ["drawing"] = drawing,
            ["project_id"] = projectId,
            ["inferred"] = hints.ToJson(),
            ["search"] = Array(search),
            ["elements"] = elements,
            ["drawings"] = drawings,
            ["blocks"] = blocks,
        };
        var counts = new JsonObject
        {
            ["search"] = search.Count,
            ["elements"] = elementCounts,
            ["drawings"] = drawingCounts,
            ["blocks"] = blockCounts,
        };
        if (openDrawing is { Count: > 0 })
        {
            // Tag every hit/element whose source_file is the open drawing, so the agent knows which handles it can act on.
            bundle["open_drawing"] = DrawingLabel(openDrawing);
            var tagged = ((JsonArray)bundle["search"]!).OfType<JsonObject>()
                .Concat(elements.Select(kv => kv.Value).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>()))
                .ToList();
            var inOpen = new HashSet<object>();
            foreach (var item in tagged)
            {
                var flag = InDrawing(item["source_file"], openDrawing) == true;
                item["in_open_drawing"] = flag;
                if (flag)
                {
                    inOpen.Add(Truthy(item["id"]) ? "id:" + Str(item["id"]) : item);
                }
            }

            counts["in_open_drawing"] = inOpen.Count;
        }

        bundle["counts"] = counts;
        bundle["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray());
        bundle["read_only"] = true;
        return bundle;
    }
}

/// <summary>
/// Read-only ontology_* tools over the Ontology REST API (parity with the Python server's tools). The CAD gateway is
/// optional: with it, ontology_auto_context tags elements of the open drawing and ontology_locate checks live handles.
/// </summary>
[McpServerToolType]
public sealed partial class OntologyRestTools(OntologyRestClient ontology, ICadGateway? gateway = null)
{
    private static string Json(JsonNode node) => node.ToJsonString(CadJson.Options);

    private static void Range(string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new McpException($"[INVALID_ARGUMENT] {name} must be between {min} and {max}.");
        }
    }

    private static JsonObject PageResult(string key, OntologyPage page) => new()
    {
        ["count"] = page.Items.Count,
        [key] = page.ItemsJson(),
        ["next_cursor"] = page.NextCursor,
    };

    [McpServerTool(Name = "ontology_catalog", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Table of contents of the Ontology building-data store: element counts by kind (with Korean aliases), drawing categories, "
        + "layers, blocks, storeys and projects. Use it to see what can be pulled up automatically.")]
    public async Task<string> Catalog(
        [Description("Ontology project_id filter")] string? project_id = null,
        CancellationToken ct = default)
    {
        var data = await ontology.CatalogAsync(project_id, ct).ConfigureAwait(false);
        return Json(data is JsonObject obj ? obj : new JsonObject { ["catalog"] = data?.DeepClone() });
    }

    [McpServerTool(Name = "ontology_find_elements", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("List building elements by kind (doors as doors, windows as windows, walls as walls...) with source file, sheet, layer, "
        + "block name, handle, attributes and properties. Page with next_cursor.")]
    public async Task<string> FindElements(
        [Description("Door, Window, Wall, Space, Column, Beam, SteelSection ... or Korean (문, 창호, 벽); comma separated for several")] string? kind = null,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("e.g. 2F, 2층, B1F (filtered client-side)")] string? storey = null,
        [Description("Sheet / layout / file name substring (filtered client-side)")] string? sheet = null,
        [Description("Substring of label or attribute values, e.g. AW-02")] string? text = null,
        [Description("평면도/상세도/... or plan/detail/section/elevation/structural")] string? drawing_category = null,
        [Description("Layer name, wildcards allowed (A-WAL*)")] string? layer = null,
        [Description("Block name, wildcards allowed (DOOR*)")] string? block_name = null,
        [Description("min_x,min_y,max_x,max_y in drawing coordinates")] string? bbox = null,
        bool include_properties = true,
        [Description("1-500")] int limit = 50,
        [Description("next_cursor from a previous page")] string? cursor = null,
        CancellationToken ct = default)
    {
        Range(nameof(limit), limit, 1, 500);
        var page = await ontology.ElementsAsync(
            kind, project_id, storey, sheet, text, drawing_category, layer, block_name, bbox, include_properties, limit, cursor, ct).ConfigureAwait(false);
        return Json(PageResult("elements", page));
    }

    [McpServerTool(Name = "ontology_blocks", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("List CAD block definitions organised by category, with instance counts, attribute tags, layers and example files.")]
    public async Task<string> Blocks(
        [Description("Kind the block's instances are classified as: Door, Window, 창호 ...")] string? category = null,
        [Description("Block name substring or wildcard (DOOR*)")] string? name_like = null,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("1-500")] int limit = 50,
        [Description("next_cursor from a previous page")] string? cursor = null,
        CancellationToken ct = default)
    {
        Range(nameof(limit), limit, 1, 500);
        var page = await ontology.BlocksAsync(category, name_like, project_id, limit, cursor, ct).ConfigureAwait(false);
        return Json(PageResult("blocks", page));
    }

    [McpServerTool(Name = "ontology_drawings", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("List sheets (drawing number, title, scale, category, file, layout), e.g. every detail drawing.")]
    public async Task<string> Drawings(
        [Description("plan, detail, section, elevation, structural, schedule, or Korean (평면도, 상세도, 단면도, 입면도, 구조도, 창호도 ...)")] string? category = null,
        [Description("Filter by drawing number / title / file name")] string? q = null,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("1-500")] int limit = 50,
        [Description("next_cursor from a previous page")] string? cursor = null,
        CancellationToken ct = default)
    {
        Range(nameof(limit), limit, 1, 500);
        var page = await ontology.DrawingsAsync(category, q, project_id, limit, cursor, ct).ConfigureAwait(false);
        return Json(PageResult("drawings", page));
    }

    [McpServerTool(Name = "ontology_element_context", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("One element plus its graph neighbours (storey, space, host wall, sheet, block definition ...).")]
    public async Task<string> ElementContext(
        [Description("Element id from ontology_find_elements / ontology_search")] string element_id,
        [Description("1-2")] int hops = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(element_id))
        {
            throw new McpException("[INVALID_ARGUMENT] element_id must not be empty.");
        }

        RejectDotId(element_id, nameof(element_id));

        Range(nameof(hops), hops, 1, 2);
        var data = await ontology.ElementContextAsync(element_id, hops, ct).ConfigureAwait(false);
        return Json(data is JsonObject obj ? obj : new JsonObject { ["context"] = data?.DeepClone() });
    }

    [McpServerTool(Name = "ontology_search", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Hybrid lexical + vector + graph search over every parsed drawing.")]
    public async Task<string> Search(
        [Description("Korean or English question / keywords")] string query,
        [Description("1-100")] int k = 10,
        [Description("Optional kind filter (Door, Window ...)")] string? kind = null,
        string? storey = null,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("Embedding model hint; ignored by APIs that do not support it")] string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            throw new McpException("[INVALID_ARGUMENT] query must not be empty.");
        }

        Range(nameof(k), k, 1, 100);
        var hits = await ontology.SearchAsync(query, k, model, kind, storey, project_id, ct).ConfigureAwait(false);
        return Json(new JsonObject { ["count"] = hits.Count, ["hits"] = new JsonArray(hits.Select(h => (JsonNode)h).ToArray()) });
    }

    [McpServerTool(Name = "ontology_auto_context", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description("Pull up every relevant element for a task without the user listing them: infers element classes "
        + "(Door/Window/Wall/Space/Column/Beam/SteelSection...), drawing categories (plan/detail/...), storey and marks from the text, "
        + "then returns search hits, elements by class, sheets by category and blocks in one bundle. When a drawing is open, each "
        + "hit/element carries `in_open_drawing`; pass the ids to ontology_locate before editing by handle.")]
    public async Task<string> AutoContext(
        [Description("What the automation will do, e.g. '2층 평면도 문 리스트 갱신' or '창호상세도에 AW-02 추가'")] string task,
        [Description("Optional sheet number / file name to focus on")] string? drawing = null,
        [Description("1-200")] int limit = 20,
        [Description("Ontology project_id filter")] string? project_id = null,
        [Description("Number of search hits to keep (1-100)")] int k = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(task))
        {
            throw new McpException("[INVALID_ARGUMENT] task must not be empty.");
        }

        Range(nameof(limit), limit, 1, 200);
        Range(nameof(k), k, 1, 100);
        ontology.EnsureConfigured(); // no AutoCAD round trip when the service is not configured
        OntologyLiveDrawing? live;
        string? drawingError = null;
        try
        {
            live = await OntologyCad.OpenDrawingAsync(gateway, ct).ConfigureAwait(false);
        }
        catch (CadException e) when (e.Code == ErrorCodes.DocumentChanged)
        {
            live = null; // bound to another drawing than the active one: tag nothing as in the open drawing
            drawingError = e.Message;
        }

        var bundle = await OntologyRest.AutoContextAsync(ontology, task, drawing, limit, k, project_id, live?.Drawing, ct).ConfigureAwait(false);
        if (drawingError is not null)
        {
            bundle["open_drawing_error"] = drawingError;
        }

        return Json(bundle);
    }
}
