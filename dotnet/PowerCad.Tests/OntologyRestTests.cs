using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

/// <summary>
/// ontology_* REST tools against a fake Ontology API (an HttpMessageHandler). The fake mirrors the Python suite's
/// FakeOntology (tests/test_ontology.py), which mirrors aec_intelligence: /v1/elements?kind= with Korean aliases and keyset
/// next_cursor paging, /v1/blocks?name_like=, /v1/drawings?category= (one item per document with its sheets),
/// /v1/elements/{id}/context?hops=, POST /v1/search {query, top_k}; 400 for a bad cursor, 404 for an unknown id.
/// </summary>
public sealed class OntologyRestTests
{
    private const string Base = "http://ontology.test:58000";

    private sealed record Seen(string Method, string Path, Dictionary<string, string> Query, JsonObject? Body, string? Authorization);

    private sealed class FakeOntology : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        /// <summary>Elements added by a test (e.g. ones whose handles exist in a simulated drawing).</summary>
        public List<JsonObject> Extra { get; } = [];

        public static JsonObject Element(string id, string kind, string label, string doc, string layout, string storey,
            string? layer = null, string? block = null, string? handle = null) => El(id, kind, label, doc, layout, storey, layer, block, "평면도", "{}", handle);

        private static JsonObject El(string id, string kind, string label, string doc, string layout, string storey,
            string? layer = null, string? block = null, string? category = null, string attributes = "{}", string? handle = null) => new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["label"] = label,
            ["state"] = "OBSERVED",
            ["project_id"] = "P1",
            ["document_id"] = "doc-" + doc,
            ["document_name"] = doc + ".dwg",
            ["revision"] = 1,
            ["storey"] = storey,
            ["layer"] = layer,
            ["block_name"] = block,
            ["drawing_category"] = category,
            ["attributes"] = JsonNode.Parse(attributes),
            ["bbox"] = new JsonArray(0, 0, 1, 1),
            ["evidence"] = new JsonObject { ["source_name"] = doc + ".dwg", ["handle"] = handle, ["layout"] = layout },
        };

        private static readonly JsonObject[] Elements =
        [
            El("el-d1", "Door", "SD-01", "A-201", "A-201", "2층", "A-DOOR", "DOOR_SINGLE", "평면도", """{"MARK":"SD-01"}""", "2F3"),
            El("el-d2", "Door", "SD-02", "A-101", "A-101", "1층", "A-DOOR", "DOOR_SINGLE", "평면도"),
            El("el-w1", "Window", "AW-02", "A-501", "A-501", "", "A-GLAZ", "AW_WINDOW", "상세도", """{"MARK":"AW-02"}"""),
            El("el-wall1", "Wall", "W-200", "A-201", "A-201", "2층", "A-WALL", null, "평면도"),
        ];

        private static readonly Dictionary<string, string> KindAliases = new() { ["문"] = "Door", ["창호"] = "Window", ["창"] = "Window", ["벽"] = "Wall", ["벽체"] = "Wall" };
        private static readonly Dictionary<string, string> CategoryAliases = new() { ["plan"] = "평면도", ["detail"] = "상세도", ["창호도"] = "창호도", ["window_schedule"] = "창호도" };

        private static readonly JsonArray Blocks = JsonNode.Parse("""
            [
              {"name": "AW_WINDOW", "definitions": [{"id": "b2", "document_id": "doc-A-501", "document_name": "A-501.dwg"}],
               "documents": ["doc-A-501"], "attribute_tags": ["MARK"], "layers": ["A-GLAZ"], "instance_count": 12,
               "instance_kinds": {"Window": 12}, "classified_as": "Window"},
              {"name": "DOOR_SINGLE", "definitions": [{"id": "b1", "document_id": "doc-A-201", "document_name": "A-201.dwg"}],
               "documents": ["doc-A-201"], "attribute_tags": ["MARK", "W"], "layers": ["A-DOOR"], "instance_count": 42,
               "instance_kinds": {"Door": 42}, "classified_as": "Door"},
              {"name": "TITLE", "definitions": [], "documents": [], "attribute_tags": ["DWG_NO"], "layers": [],
               "instance_count": 3, "instance_kinds": {"TitleBlock": 3}, "classified_as": "TitleBlock"}
            ]
            """)!.AsArray();

        private static readonly JsonArray Docs = JsonNode.Parse("""
            [
              {"document_id": "doc-A-201", "project_id": "P1", "name": "A-201.dwg", "drawing_categories": ["평면도"],
               "sheets": [{"layout": "Model", "element_counts": {"Door": 1, "Wall": 1}, "view_id": "v1",
                           "view_label": "2층 평면도", "drawing_category": "평면도",
                           "title_block": {"id": "t1", "drawing_number": "A-201", "drawing_title": "2층 평면도", "scale": "1/100"}}]},
              {"document_id": "doc-A-501", "project_id": "P1", "name": "A-501.dwg", "drawing_categories": ["상세도", "창호도"],
               "sheets": [{"layout": "D1", "element_counts": {"Window": 1}, "drawing_category": "상세도",
                           "title_block": {"drawing_number": "A-501", "drawing_title": "창호상세도", "scale": "1/20"}},
                          {"layout": "S1", "element_counts": {}, "drawing_category": "창호도",
                           "title_block": {"drawing_number": "A-511", "drawing_title": "창호일람표", "scale": "1/50"}}]}
            ]
            """)!.AsArray();

        private static string Cursor(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))).Replace('+', '-').Replace('/', '_');

        /// <summary>Keyset paging like the real API: items sorted by key, cursor = last key of the page.</summary>
        private static JsonObject Page(IEnumerable<JsonObject> items, Dictionary<string, string> qs, string key)
        {
            var sorted = items.OrderBy(r => r[key]!.GetValue<string>(), StringComparer.Ordinal).ToList();
            if (qs.TryGetValue("cursor", out var cursor))
            {
                string after;
                try
                {
                    after = JsonSerializer.Deserialize<string>(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/')))!;
                }
                catch (Exception e) when (e is FormatException or JsonException)
                {
                    throw new ArgumentException("invalid cursor");
                }

                sorted = sorted.Where(r => string.CompareOrdinal(r[key]!.GetValue<string>(), after) > 0).ToList();
            }

            var limit = qs.TryGetValue("limit", out var l) ? int.Parse(l) : 50;
            var page = sorted.Take(limit).ToList();
            var more = sorted.Count > limit;
            return new JsonObject
            {
                ["count"] = page.Count,
                ["items"] = new JsonArray(page.Select(p => (JsonNode)p.DeepClone()).ToArray()),
                ["next_cursor"] = more ? Cursor(page[^1][key]!.GetValue<string>()) : null,
            };
        }

        private static HttpResponseMessage Send(HttpStatusCode code, JsonNode? body, string? raw = null) => new(code)
        {
            Content = new StringContent(raw ?? body?.ToJsonString() ?? "null", Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var qs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
            JsonObject? body = null;
            if (request.Content is not null)
            {
                body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            }

            Requests.Add(new Seen(request.Method.Method, uri.AbsolutePath, qs, body, request.Headers.Authorization?.ToString()));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            try
            {
                return Dispatch(uri.AbsolutePath, qs, body);
            }
            catch (ArgumentException e)
            {
                return Send(HttpStatusCode.BadRequest, new JsonObject { ["detail"] = e.Message });
            }
        }

        private HttpResponseMessage Dispatch(string path, Dictionary<string, string> qs, JsonObject? body)
        {
            string? Q(string k) => qs.TryGetValue(k, out var v) ? v : null;
            switch (path)
            {
                case "/v1/catalog":
                    return Send(HttpStatusCode.OK, new JsonObject
                    {
                        ["project_id"] = Q("project_id"),
                        ["totals"] = new JsonObject { ["objects"] = 4, ["documents"] = 3, ["projects"] = 1 },
                        ["kinds"] = new JsonArray(new JsonObject { ["kind"] = "Door", ["count"] = 2, ["aliases_ko"] = "문 도어" }),
                        ["drawing_categories"] = new JsonArray(new JsonObject { ["category"] = "평면도", ["count"] = 3 }),
                        ["projects"] = new JsonArray(new JsonObject { ["project_id"] = "P1" }),
                    });
                case "/v1/elements":
                {
                    var rawKind = Q("kind") ?? "";
                    var kinds = rawKind.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => KindAliases.GetValueOrDefault(t, t)).ToHashSet();
                    if (rawKind.Contains("창호"))
                    {
                        kinds.UnionWith(["Door", "Window"]);
                    }

                    var cat = Q("drawing_category") is { } c ? CategoryAliases.GetValueOrDefault(c, c) : null;
                    var text = (Q("text") ?? "").ToLowerInvariant();
                    var found = Elements.Concat(Extra).Where(e =>
                        (kinds.Count == 0 || kinds.Contains(e["kind"]!.GetValue<string>()))
                        && (cat is null || e["drawing_category"]?.GetValue<string>() == cat)
                        && (text.Length == 0 || (e["label"]!.GetValue<string>() + e["attributes"]!.ToJsonString()).ToLowerInvariant().Contains(text)));
                    var page = Page(found, qs, "id");
                    page.Insert(0, "filters", new JsonObject { ["kind"] = kinds.Count == 0 ? null : new JsonArray(kinds.Order().Select(k => (JsonNode)k).ToArray()) });
                    return Send(HttpStatusCode.OK, page);
                }

                case "/v1/blocks":
                {
                    var like = (Q("name_like") ?? "").ToLowerInvariant().Replace("*", "");
                    return Send(HttpStatusCode.OK, Page(Blocks.OfType<JsonObject>().Where(b => b["name"]!.GetValue<string>().ToLowerInvariant().Contains(like)), qs, "name"));
                }

                case "/v1/drawings":
                {
                    var cat = Q("category") is { } c ? CategoryAliases.GetValueOrDefault(c, c) : null;
                    var docs = Docs.OfType<JsonObject>()
                        .Where(d => cat is null || d["drawing_categories"]!.AsArray().Any(x => x!.GetValue<string>() == cat))
                        .Select(d => d.DeepClone().AsObject())
                        .ToList();
                    if (cat is not null)
                    {
                        foreach (var sheet in docs.SelectMany(d => d["sheets"]!.AsArray().OfType<JsonObject>()))
                        {
                            sheet["matches_category"] = sheet["drawing_category"]?.GetValue<string>() == cat;
                        }
                    }

                    return Send(HttpStatusCode.OK, Page(docs, qs, "document_id"));
                }

                case "/v1/search":
                {
                    // Like the real API without embeddings: the whole query must occur in the search text; kind and
                    // storey are matched exactly (the fixture's door is on storey "2층", so "2F" misses).
                    var hits = JsonNode.Parse("""
                        [{"object_id": "el-d1", "project_id": "P1", "kind": "Door", "label": "SD-01", "storey": "2층",
                          "score": 0.91, "citation": {"document_name": "A-201.dwg", "handle_or_id": "2F3", "layout_or_page": "A-201"},
                          "properties": {}, "relations": []}]
                        """)!.AsArray();
                    var query = body!["query"]!.GetValue<string>().ToLowerInvariant();
                    var miss = !"sd-01 door 2층 문 출입문 a-201".Contains(query)
                        || (body["kind"] is { } kind && kind.GetValue<string>() != "Door")
                        || (body["storey"] is { } storey && storey.GetValue<string>() != "2층")
                        || body["top_k"]!.GetValue<int>() < 1;
                    return Send(HttpStatusCode.OK, new JsonObject
                    {
                        ["query"] = body["query"]!.GetValue<string>(),
                        ["total_hits"] = miss ? 0 : 1,
                        ["hits"] = miss ? new JsonArray() : hits,
                        ["warnings"] = new JsonArray(),
                    });
                }

                case "/v1/broken":
                    return Send(HttpStatusCode.InternalServerError, new JsonObject { ["detail"] = "database is down" });
                case "/v1/garbage":
                    return Send(HttpStatusCode.OK, null, "<html>oops</html>");
            }

            if (path.StartsWith("/v1/elements/", StringComparison.Ordinal) && path.EndsWith("/context", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path.Split('/')[3]);
                var el = Elements.Concat(Extra).FirstOrDefault(e => e["id"]!.GetValue<string>() == id);
                if (el is null)
                {
                    return Send(HttpStatusCode.NotFound, new JsonObject { ["detail"] = "Object not found" });
                }

                return Send(HttpStatusCode.OK, new JsonObject
                {
                    ["element"] = el.DeepClone(),
                    ["hops"] = int.Parse(Q("hops") ?? "1"),
                    ["edges"] = new JsonArray(new JsonObject { ["subject"] = id, ["predicate"] = "hostedBy", ["object"] = "el-wall1", ["hop"] = 1 }),
                    ["nodes"] = new JsonArray(),
                });
            }

            return Send(HttpStatusCode.NotFound, null, "");
        }
    }

    private readonly FakeOntology _fake = new();

    private OntologyRestClient Client(double timeout = 5, string? token = null) => new(Base, timeout, token, _fake);

    private static string[] Ids(OntologyPage page, string key = "id") => page.Items.Select(i => i[key]!.GetValue<string>()).ToArray();

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private List<string> SearchQueries() =>
        _fake.Requests.Where(r => r.Path == "/v1/search").Select(r => r.Body!["query"]!.GetValue<string>()).ToList();

    // ------------------------------------------------------------------ inference
    [Theory]
    [InlineData("2층 평면도 문 리스트 갱신", "Door", "plan,schedule", true, "2F", "")]
    [InlineData("창호상세도에 AW-02 추가", "Window,Door", "detail", true, null, "AW-02")]
    [InlineData("지하1층 기둥 보 철골 H-형강 확인", "Column,Beam,SteelSection", "structural", false, "B1F", "")]
    [InlineData("회의실 실명 정리하고 블록 정리", "Space", "", true, null, "")]
    [InlineData("벽체 상세 B1F", "Wall", "detail", false, "B1F", "")]
    [InlineData("update the door schedule on 3F", "Door", "schedule", true, "3F", "")]
    [InlineData("add window W-12 to the elevation", "Window", "elevation", true, null, "W-12")]
    [InlineData("창호도 갱신", "Window,Door", "schedule", true, null, "")]
    [InlineData("창문 교체", "Window", "", true, null, "")]
    [InlineData("창고 문서 실행 보고", "", "", false, null, "")]
    public void Infer_task_matches_the_python_heuristics(string task, string classes, string categories, bool blocks, string? storey, string marks)
    {
        static string[] Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var hints = OntologyRest.InferTask(task);
        Assert.Equal(Split(classes), hints.Classes);
        Assert.Equal(Split(categories), hints.DrawingCategories);
        Assert.Equal(blocks, hints.IncludeBlocks);
        Assert.Equal(storey, hints.Storey);
        Assert.Equal(Split(marks), hints.Marks);
    }

    [Theory]
    [InlineData("2층", "2F")]
    [InlineData("2F", "2F")]
    [InlineData("02", "2F")]
    [InlineData("L2", "2F")]
    [InlineData("지하1층", "B1F")]
    [InlineData("B1", "B1F")]
    [InlineData("옥상", "RF")]
    public void Normalize_storey(string value, string expected) => Assert.Equal(expected, OntologyRest.NormalizeStorey(value));

    [Fact]
    public void Kind_words_categories_and_keywords()
    {
        Assert.Equal(["창호", "Window", "Door"], OntologyRest.ResolveKindWords("창호"));
        Assert.Equal(["문", "Door", "벽체", "Wall"], OntologyRest.ResolveKindWords("문, 벽체"));
        Assert.Equal("창호도", OntologyRest.CategoryParam(" Schedule "));
        Assert.Equal("detail", OntologyRest.CategoryParam("detail"));
        Assert.Null(OntologyRest.CategoryParam(""));

        var hints = OntologyRest.InferTask("창호상세도에 AW-02 추가");
        Assert.Equal([("AW-02", null), ("A-501", null), ("창", "Window"), ("문", "Door")], OntologyRest.SearchKeywords(hints, " A-501 "));
    }

    [Fact]
    public void Rows_and_compact_tolerate_shapes()
    {
        Assert.Single(OntologyRest.Rows(JsonNode.Parse("""[{"a": 1}, "x"]""")));
        Assert.Equal(1, OntologyRest.Rows(JsonNode.Parse("""{"items": [{"a": 1}]}"""))[0]["a"]!.GetValue<int>());
        Assert.Equal(2, OntologyRest.Rows(JsonNode.Parse("""{"data": {"results": [{"a": 2}]}}"""))[0]["a"]!.GetValue<int>());
        Assert.Equal(3, OntologyRest.Rows(JsonNode.Parse("""{"elements": [{"a": 3}]}"""), "elements")[0]["a"]!.GetValue<int>());
        Assert.Empty(OntologyRest.Rows(JsonNode.Parse("""{"nothing": 1}""")));
        Assert.Empty(OntologyRest.Rows(null));

        var compact = OntologyRest.CompactElement(Obj("""{"element_id": "x", "class": "Door", "name": "D1", "source_file": "a.dwg", "layer": ""}"""));
        Assert.Equal("""{"id":"x","class":"Door","name":"D1","source_file":"a.dwg"}""", compact.ToJsonString());
    }

    // --------------------------------------------------------------------- client
    [Fact]
    public async Task Elements_take_korean_aliases_and_page_with_the_keyset_cursor()
    {
        var client = Client();
        var page = await client.ElementsAsync("문", limit: 10);
        Assert.Equal(["el-d1", "el-d2"], Ids(page));
        Assert.Null(page.NextCursor);
        var seen = _fake.Requests[^1];
        Assert.Equal("/v1/elements", seen.Path);
        Assert.Equal("문", seen.Query["kind"]);
        Assert.Equal("true", seen.Query["include_properties"]);
        Assert.False(seen.Query.ContainsKey("project_id")); // null filters are not sent
        Assert.False(seen.Query.ContainsKey("cursor"));
        Assert.Equal(
            """{"id":"el-d1","class":"Door","name":"SD-01","source_file":"A-201.dwg","sheet":"A-201","storey":"2층","drawing_category":"평면도","layer":"A-DOOR","block_name":"DOOR_SINGLE","handle":"2F3","document_id":"doc-A-201","project_id":"P1","bbox":[0,0,1,1],"attributes":{"MARK":"SD-01"}}""",
            page.Items[0].ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        // Keyset paging: the cursor the API returns is handed back and accepted.
        var first = await client.ElementsAsync("Door", limit: 1);
        Assert.Equal(["el-d1"], Ids(first));
        Assert.NotNull(first.NextCursor);
        Assert.Equal("1", _fake.Requests[^1].Query["limit"]);
        var second = await client.ElementsAsync("Door", limit: 1, cursor: first.NextCursor);
        Assert.Equal(["el-d2"], Ids(second));
        Assert.Null(second.NextCursor);
        Assert.Equal(first.NextCursor, _fake.Requests[^1].Query["cursor"]);

        // storey/sheet are filtered client-side ("2F" matches "2층") with full pages.
        Assert.Equal(["el-d1"], Ids(await client.ElementsAsync("Door", storey: "2F")));
        Assert.Equal("2층", _fake.Requests[^1].Query["storey"]); // also sent, so a future API can filter server-side
        Assert.Equal("500", _fake.Requests[^1].Query["limit"]);
        Assert.Equal(["el-d2"], Ids(await client.ElementsAsync("Door", sheet: "a-101")));
        Assert.Equal(["AW-02"], Ids(await client.ElementsAsync("창호", text: "AW-02"), "name"));
        Assert.Equal("el-w1", (await client.ElementsAsync(drawingCategory: "detail")).Items[0]["id"]!.GetValue<string>());
        Assert.Equal(["el-wall1"], Ids(await client.ElementsAsync("벽체")));
    }

    [Fact]
    public async Task Blocks_drawings_context_and_search()
    {
        var client = Client();
        Assert.Equal("Door", (await client.CatalogAsync("P1"))!["kinds"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["project_id"] = "P1" }, _fake.Requests[^1].Query);

        var doorBlocks = await client.BlocksAsync("문");
        Assert.Equal(
            """{"name":"DOOR_SINGLE","category":"Door","instance_count":42,"instance_kinds":{"Door":42},"attribute_tags":["MARK","W"],"layers":["A-DOOR"],"example_files":["A-201.dwg"]}""",
            Assert.Single(doorBlocks.Items).ToJsonString());
        Assert.Equal(["AW_WINDOW", "DOOR_SINGLE"], Ids(await client.BlocksAsync("창호"), "name"));
        Assert.Equal(["DOOR_SINGLE"], Ids(await client.BlocksAsync(null, "door*"), "name"));
        Assert.Equal("door*", _fake.Requests[^1].Query["name_like"]);
        var paged = await client.BlocksAsync(limit: 2);
        Assert.Equal(2, paged.Items.Count);
        Assert.NotNull(paged.NextCursor);

        var details = await client.DrawingsAsync("detail");
        var sheet = Assert.Single(details.Items);
        Assert.Equal("A-501", sheet["drawing_number"]!.GetValue<string>());
        Assert.Equal("창호상세도", sheet["title"]!.GetValue<string>());
        Assert.Equal("D1", sheet["layout"]!.GetValue<string>());
        Assert.Equal("A-501.dwg", sheet["file"]!.GetValue<string>());
        _ = await client.DrawingsAsync("schedule");
        Assert.Equal("창호도", _fake.Requests[^1].Query["category"]); // mapped to the API's category name
        Assert.Equal(["A-511"], Ids(await client.DrawingsAsync(q: "A-511"), "drawing_number"));
        Assert.Equal(3, (await client.DrawingsAsync()).Items.Count);

        var ctx = (await client.ElementContextAsync("el-d1", hops: 5))!;
        Assert.Equal("el-wall1", ctx["edges"]![0]!["object"]!.GetValue<string>());
        Assert.Equal(2, ctx["hops"]!.GetValue<int>()); // clamped to 1-2
        var missing = await Assert.ThrowsAsync<OntologyRestException>(() => client.ElementContextAsync("el/x"));
        Assert.Contains("Object not found", missing.Message);
        Assert.StartsWith("[ONTOLOGY_FAILED] Ontology GET /v1/elements/el%2Fx/context failed with HTTP 404: Object not found", missing.Message);
        Assert.Equal("/v1/elements/el%2Fx/context", _fake.Requests[^1].Path); // ids are path-escaped

        var hits = await client.SearchAsync("2층 문", k: 3, kind: "Door");
        Assert.Equal("el-d1", hits[0]["id"]!.GetValue<string>());
        Assert.Equal("A-201.dwg", hits[0]["source_file"]!.GetValue<string>());
        Assert.Equal("2F3", hits[0]["handle"]!.GetValue<string>());
        Assert.Equal("""{"query":"2층 문","top_k":3,"kind":"Door"}""", _fake.Requests[^1].Body!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal("POST", _fake.Requests[^1].Method);
    }

    [Fact]
    public async Task Http_errors_map_to_python_messages()
    {
        var client = Client(token: "secret-token");
        Assert.Contains("HTTP 400: invalid cursor", (await Assert.ThrowsAsync<OntologyRestException>(() => client.ElementsAsync("Door", cursor: "not-a-cursor"))).Message);
        Assert.Equal("Bearer secret-token", _fake.Requests[^1].Authorization);
        Assert.Contains("HTTP 500: database is down", (await Assert.ThrowsAsync<OntologyRestException>(() => client.GetAsync("/v1/broken"))).Message);
        var notFound = await Assert.ThrowsAsync<OntologyRestException>(() => client.GetAsync("/v1/unknown"));
        Assert.Equal($"Ontology endpoint GET /v1/unknown not found (HTTP 404) at {Base}; the Ontology API may be older than this client.", notFound.Detail);
        Assert.False(notFound.Unavailable);
        Assert.Contains("non-JSON", (await Assert.ThrowsAsync<OntologyRestException>(() => client.GetAsync("/v1/garbage"))).Message);

        var bad = new OntologyRestClient("ftp://example", 5);
        var ex = await Assert.ThrowsAsync<OntologyRestException>(() => bad.CatalogAsync());
        Assert.StartsWith("[ONTOLOGY_CONFIG] POWERCAD_ONTOLOGY_URL must be an http(s) URL", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_url_answers_not_configured_on_every_tool()
    {
        var client = OntologyRestClient.FromEnvironment(_ => null);
        Assert.False(client.Configured);
        var tools = new OntologyRestTools(client);
        var calls = new Func<Task<string>>[]
        {
            () => tools.Catalog(),
            () => tools.FindElements(kind: "Door"),
            () => tools.Blocks(),
            () => tools.Drawings(category: "detail"),
            () => tools.ElementContext("x"),
            () => tools.Search("문"),
            () => tools.AutoContext("문 리스트"),
            () => tools.Locate(["x"]),
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<OntologyRestException>(call);
            Assert.StartsWith("[ONTOLOGY_NOT_CONFIGURED]", ex.Message);
            Assert.Contains("POWERCAD_ONTOLOGY_URL", ex.Message);
        }
    }

    [Fact]
    public void Settings_from_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["POWER_CAD_ONTOLOGY_URL"] = "http://10.0.0.5:9000/",
            ["POWERCAD_ONTOLOGY_TIMEOUT"] = "999",
            ["POWERCAD_ONTOLOGY_TOKEN"] = " t0k ",
        };
        var client = OntologyRestClient.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal("http://10.0.0.5:9000", client.BaseUrl);
        Assert.Equal(120.0, client.TimeoutSeconds);
        Assert.Equal("t0k", client.Token);

        env["POWERCAD_ONTOLOGY_URL"] = "http://127.0.0.1:58000"; // wins over the POWER_CAD_ alias
        env["POWERCAD_ONTOLOGY_TIMEOUT"] = "abc";
        client = OntologyRestClient.FromEnvironment(n => env.GetValueOrDefault(n));
        Assert.Equal("http://127.0.0.1:58000", client.BaseUrl);
        Assert.Equal(10.0, client.TimeoutSeconds);
    }

    [Fact]
    public async Task Timeout_and_unreachable_service_are_unavailable()
    {
        _fake.Delay = TimeSpan.FromSeconds(3);
        var slow = await Assert.ThrowsAsync<OntologyRestException>(() => Client(timeout: 1).CatalogAsync());
        Assert.True(slow.Unavailable);
        Assert.Contains("did not answer within 1s", slow.Message);

        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop(); // nothing listens there any more
        }

        var dead = new OntologyRestClient($"http://127.0.0.1:{port}", 2);
        var down = await Assert.ThrowsAsync<OntologyRestException>(() => dead.CatalogAsync());
        Assert.True(down.Unavailable);
        Assert.Matches("not reachable.*POWERCAD_ONTOLOGY_URL|did not answer within 2s", down.Message);
        // auto_context does not swallow an outage into warnings.
        Assert.True((await Assert.ThrowsAsync<OntologyRestException>(() => OntologyRest.AutoContextAsync(dead, "문 리스트"))).Unavailable);
    }

    [Fact]
    public async Task Auto_context_bundle_with_keyword_fallback_and_warnings()
    {
        var client = Client();
        var bundle = await OntologyRest.AutoContextAsync(client, "2층 평면도 문 리스트 갱신");
        Assert.Equal(["Door"], bundle["inferred"]!["classes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(bundle["read_only"]!.GetValue<bool>());
        Assert.Equal(["el-d1"], bundle["elements"]!["Door"]!.AsArray().Select(n => n!["id"]!.GetValue<string>())); // storey 2F matched "2층"
        Assert.Equal(["A-201"], bundle["drawings"]!["plan"]!.AsArray().Select(n => n!["drawing_number"]!.GetValue<string>()));
        Assert.Equal(["A-511"], bundle["drawings"]!["schedule"]!.AsArray().Select(n => n!["drawing_number"]!.GetValue<string>()));
        Assert.Equal(["DOOR_SINGLE"], bundle["blocks"]!["Door"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        // The whole sentence matches nothing; the class keyword "문" (scoped to Door, storey retried without "2F") finds the door.
        Assert.Equal("el-d1", bundle["search"]![0]!["id"]!.GetValue<string>());
        var searches = _fake.Requests.Where(r => r.Path == "/v1/search").Select(r => r.Body!.ToJsonString()).ToList();
        Assert.Equal(5, searches.Count);
        Assert.Equal(
            ["2층 평면도 문 리스트 갱신", "2층 평면도 문 리스트 갱신", "2층 평면도 문 리스트 갱신", "문", "문"],
            SearchQueries());
        var keywordSearch = _fake.Requests.Where(r => r.Path == "/v1/search").ToList();
        Assert.Equal("Door", keywordSearch[3].Body!["kind"]!.GetValue<string>());
        Assert.Equal("2F", keywordSearch[3].Body!["storey"]!.GetValue<string>());
        Assert.Null(keywordSearch[4].Body!["storey"]);
        Assert.Null(keywordSearch[2].Body!["kind"]); // the unscoped whole-task retry
        Assert.Equal("""{"Door":1}""", bundle["counts"]!["elements"]!.ToJsonString());
        Assert.Equal(
            ["search: the whole-task search returned nothing or failed; used keywords ['문 (Door)']."],
            bundle["warnings"]!.AsArray().Select(n => n!.GetValue<string>()));

        bundle = await OntologyRest.AutoContextAsync(client, "창호상세도에 AW-02 추가");
        Assert.Equal(["AW-02"], bundle["inferred"]!["marks"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["AW-02"], bundle["elements"]!["Window"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        // No door carries AW-02, so the class is relaxed to all doors and a warning explains why.
        Assert.Equal(2, bundle["elements"]!["Door"]!.AsArray().Count);
        var warnings = bundle["warnings"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("elements[Door]: no match for storey/sheet/mark filters; showing all Door.", warnings);
        Assert.Contains("search: the whole-task search returned nothing or failed; used keywords ['AW-02', '창 (Window)', '문 (Door)'].", warnings);
        Assert.Equal(["A-501"], bundle["drawings"]!["detail"]!.AsArray().Select(n => n!["drawing_number"]!.GetValue<string>()));
        Assert.Equal(["AW_WINDOW"], bundle["blocks"]!["Window"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));

        bundle = await OntologyRest.AutoContextAsync(client, "블록 정리");
        Assert.Equal(3, bundle["blocks"]!["all"]!.AsArray().Count);
        Assert.Empty(bundle["elements"]!.AsObject());
        Assert.Empty(bundle["warnings"]!.AsArray()); // no class, mark or drawing -> no keyword retry, no warning

        bundle = await OntologyRest.AutoContextAsync(client, "문 위치 확인", drawing: "  A-101 ", projectId: "P1");
        Assert.Equal("A-101", bundle["drawing"]!.GetValue<string>());
        Assert.Equal("P1", bundle["project_id"]!.GetValue<string>());
        Assert.Equal(["el-d2"], bundle["elements"]!["Door"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal(["match"], bundle["drawings"]!.AsObject().Select(kv => kv.Key)); // no category inferred: only the drawing match
        Assert.All(_fake.Requests.Where(r => r.Path == "/v1/elements").TakeLast(1), r => Assert.Equal("P1", r.Query["project_id"]));

        bundle = await OntologyRest.AutoContextAsync(client, "문 리스트", drawing: "   ");
        Assert.Null(bundle["drawing"]);
        Assert.Null(bundle["project_id"]);

        await Assert.ThrowsAsync<OntologyRestException>(() => OntologyRest.AutoContextAsync(client, "   "));
    }

    [Fact]
    public async Task Auto_context_reports_endpoint_failures_as_warnings()
    {
        var handler = new FailingSearch(_fake);
        var bundle = await OntologyRest.AutoContextAsync(new OntologyRestClient(Base, 5, null, handler), "블록 정리");
        Assert.Equal(3, bundle["blocks"]!["all"]!.AsArray().Count);
        Assert.Equal(["search: Ontology POST /v1/search failed with HTTP 503: search index rebuilding"], bundle["warnings"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    private sealed class FailingSearch(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/v1/search"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"detail":"search index rebuilding"}""") })
                : base.SendAsync(request, cancellationToken);
    }

    // ---------------------------------------------------------------- MCP tools
    [Fact]
    public async Task Tools_return_python_shapes()
    {
        var tools = new OntologyRestTools(Client());
        Assert.Equal("P1", Obj(await tools.Catalog())["projects"]![0]!["project_id"]!.GetValue<string>());

        var found = Obj(await tools.FindElements(kind: "벽체"));
        Assert.Equal(1, found["count"]!.GetValue<int>());
        Assert.Equal("W-200", found["elements"]![0]!["name"]!.GetValue<string>());
        Assert.True(found.ContainsKey("next_cursor"));
        Assert.Null(found["next_cursor"]);

        var page = Obj(await tools.FindElements(kind: "Door", limit: 1));
        var cursor = page["next_cursor"]!.GetValue<string>();
        var next = Obj(await tools.FindElements(kind: "Door", limit: 1, cursor: cursor));
        Assert.Equal("el-d2", next["elements"]![0]!["id"]!.GetValue<string>());

        Assert.Equal(42, Obj(await tools.Blocks(category: "Door"))["blocks"]![0]!["instance_count"]!.GetValue<int>());
        Assert.Equal("창호상세도", Obj(await tools.Drawings(category: "상세도"))["drawings"]![0]!["title"]!.GetValue<string>());

        var ctx = Obj(await tools.ElementContext("el-d1", hops: 2));
        Assert.Equal("el-d1", ctx["element"]!["id"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["hops"] = "2" }, _fake.Requests[^1].Query);

        var search = Obj(await tools.Search("문", k: 2));
        Assert.Equal("Door", search["hits"]![0]!["class"]!.GetValue<string>());
        Assert.Equal(1, search["count"]!.GetValue<int>());

        var bundle = Obj(await tools.AutoContext("2층 평면도 문 리스트 갱신"));
        Assert.Equal("""{"Door":1}""", bundle["counts"]!["elements"]!.ToJsonString());

        Assert.Contains("Object not found", (await Assert.ThrowsAsync<OntologyRestException>(() => tools.ElementContext("missing"))).Message);
        Assert.Contains("invalid cursor", (await Assert.ThrowsAsync<OntologyRestException>(() => tools.FindElements(cursor: "bad"))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.FindElements(limit: 501))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.ElementContext("el-d1", hops: 3))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.Search("문", k: 0))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.AutoContext("문", limit: 201))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.AutoContext("문", k: 101))).Message);
        var scoped = Obj(await tools.AutoContext("문 리스트", project_id: "P1", k: 1));
        Assert.Equal("P1", scoped["project_id"]!.GetValue<string>());
        Assert.Equal(1, _fake.Requests.Last(r => r.Path == "/v1/search").Body!["top_k"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- ontology -> CAD (ontology_locate)
    [Theory]
    [InlineData("2F", "2층")]
    [InlineData("2층", "2층")]
    [InlineData("L02", "2층")]
    [InlineData("B1", "지하1층")]
    [InlineData("지하2층", "지하2층")]
    [InlineData("roof", "지붕층")]
    [InlineData("  Mezz ", "Mezz")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Storey_param_is_the_korean_form(string? value, string? expected) => Assert.Equal(expected, OntologyRest.StoreyParam(value));

    [Theory]
    [InlineData("A-201.dwg", "a-201")]
    [InlineData("a-201.DXF", "a-201")]
    [InlineData(@"C:\\Proj\\Sub\\A-201.Dwg", "a-201")]
    [InlineData("/srv/drawings/A-201.dxf", "a-201")]
    [InlineData("A-201", "a-201")]
    [InlineData("A-201.backup.dwg", "a-201.backup")]
    [InlineData("plan.pdf", "plan.pdf")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Drawing_key(string? name, string? key) => Assert.Equal(key, OntologyRest.DrawingKey(name));

    [Fact]
    public void In_drawing()
    {
        var info = new JsonObject { ["name"] = "A-201.dxf", ["path"] = "/tmp/x/A-201.dxf" };
        Assert.True(OntologyRest.InDrawing("A-201.dwg", info)); // the DWG the element came from = its DXF copy
        Assert.True(OntologyRest.InDrawing(@"C:\Proj\a-201.DWG", info));
        Assert.False(OntologyRest.InDrawing("A-501.dwg", info));
        Assert.Null(OntologyRest.InDrawing((string?)null, info));
        Assert.Null(OntologyRest.InDrawing("A-201.dwg", null));
        Assert.Null(OntologyRest.InDrawing("A-201.dwg", new JsonObject { ["name"] = "", ["path"] = null }));
    }

    private static JsonObject Door() => new()
    {
        ["id"] = "el-1", ["class"] = "Door", ["name"] = "SD-01", ["source_file"] = "A-201.dwg", ["handle"] = "1A",
        ["layer"] = "A-DOOR", ["block_name"] = "DOOR_SINGLE",
    };

    private static JsonObject OpenA201() => new() { ["name"] = "A-201.dxf", ["path"] = "/w/A-201.dxf" };

    private static JsonObject With(JsonObject o, string key, JsonNode? value)
    {
        var copy = o.DeepClone().AsObject();
        if (value is null)
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        return copy;
    }

    private sealed class FakeLookup(params JsonObject[] entities)
    {
        public List<string> Calls { get; } = [];

        public Task<JsonObject?> Find(string handle)
        {
            Calls.Add(handle);
            return Task.FromResult(entities.FirstOrDefault(e => e["handle"]!.GetValue<string>() == handle.ToUpperInvariant())?.DeepClone().AsObject());
        }
    }

    private static Task<JsonObject> Match(JsonObject? element, JsonObject? open, FakeLookup lookup, string? id = null) =>
        OntologyRest.MatchElementAsync(element, open, lookup.Find, id);

    [Fact]
    public async Task Match_element_statuses()
    {
        var insert = JsonNode.Parse("""{"handle": "1A", "type": "INSERT", "layer": "a-door", "name": "DOOR_SINGLE", "position": [0, 0, 0], "fingerprint": "fp1", "rotation": 0}""")!.AsObject();
        var ok = await Match(Door(), OpenA201(), new FakeLookup(insert));
        Assert.Equal("matched", ok["status"]!.GetValue<string>());
        Assert.Equal("1A", ok["handle"]!.GetValue<string>());
        Assert.Equal("el-1", ok["element_id"]!.GetValue<string>());
        Assert.Equal("""{"handle":"1A","type":"INSERT","layer":"a-door","name":"DOOR_SINGLE","position":[0,0,0],"fingerprint":"fp1"}""", ok["entity"]!.ToJsonString());
        Assert.Equal(OpenA201().ToJsonString(), ok["open_drawing"]!.ToJsonString());
        Assert.Equal("A-201.dwg", ok["source_file"]!.GetValue<string>());

        // Another drawing: the handle is never even looked up (it would name an unrelated entity).
        var lookup = new FakeLookup(insert);
        var other = await Match(With(Door(), "source_file", "A-501.dwg"), OpenA201(), lookup);
        Assert.Equal("other_drawing", other["status"]!.GetValue<string>());
        Assert.Empty(lookup.Calls);
        var nothingOpen = await Match(Door(), null, lookup);
        Assert.Equal("other_drawing", nothingOpen["status"]!.GetValue<string>());
        Assert.Equal("no drawing is open", nothingOpen["note"]!.GetValue<string>());
        var noSource = await Match(With(Door(), "source_file", null), OpenA201(), lookup);
        Assert.Contains("no source_file", noSource["note"]!.GetValue<string>());
        Assert.Empty(lookup.Calls);

        var gone = await Match(With(Door(), "sheet", "Layout1"), OpenA201(), new FakeLookup());
        Assert.Equal("handle_missing", gone["status"]!.GetValue<string>());
        Assert.Contains("Layout1", gone["note"]!.GetValue<string>());
        var modelGone = await Match(With(Door(), "sheet", "Model"), OpenA201(), new FakeLookup());
        Assert.DoesNotContain("layout", modelGone["note"]!.GetValue<string>());
        var noHandle = await Match(With(Door(), "handle", null), OpenA201(), new FakeLookup());
        Assert.Equal("handle_missing", noHandle["status"]!.GetValue<string>());
        Assert.Equal("the element has no handle", noHandle["note"]!.GetValue<string>());

        var line = JsonNode.Parse("""{"handle": "1A", "type": "LINE", "layer": "A-DOOR"}""")!.AsObject();
        var bad = await Match(Door(), OpenA201(), new FakeLookup(line));
        Assert.Equal("mismatch", bad["status"]!.GetValue<string>());
        Assert.Contains("block 'DOOR_SINGLE'", bad["reasons"]![0]!.GetValue<string>());
        var otherBlock = await Match(Door(), OpenA201(), new FakeLookup(With(insert, "name", "WINDOW")));
        Assert.Equal("mismatch", otherBlock["status"]!.GetValue<string>());
        Assert.Contains("'WINDOW'", otherBlock["reasons"]![0]!.GetValue<string>());
        var anonymous = await Match(Door(), OpenA201(), new FakeLookup(With(insert, "name", "*U12")));
        Assert.Equal("matched", anonymous["status"]!.GetValue<string>()); // dynamic block reference: effective name not visible
        var otherLayer = await Match(Door(), OpenA201(), new FakeLookup(With(insert, "layer", "0")));
        Assert.Equal("mismatch", otherLayer["status"]!.GetValue<string>());
        Assert.Contains("layer", otherLayer["reasons"]![0]!.GetValue<string>());

        // Door drawn as plain geometry (LINE + ARC on A-DOOR) is plausible ...
        var plain = JsonNode.Parse("""{"id": "el-2", "class": "Door", "source_file": "A-201.dwg", "handle": "A3", "layer": "A-DOOR"}""")!.AsObject();
        Assert.Equal("matched", (await Match(plain, OpenA201(), new FakeLookup(With(line, "handle", "A3"))))["status"]!.GetValue<string>());
        // ... but a dimension or a text is not a door; a text is fine for a room name.
        var dim = JsonNode.Parse("""{"handle": "A3", "type": "DIMENSION", "layer": "A-DOOR"}""")!.AsObject();
        Assert.Equal("mismatch", (await Match(plain, OpenA201(), new FakeLookup(dim)))["status"]!.GetValue<string>());
        var text = JsonNode.Parse("""{"handle": "A3", "type": "TEXT", "layer": "A-DOOR", "text": "SD-01"}""")!.AsObject();
        Assert.Equal("mismatch", (await Match(plain, OpenA201(), new FakeLookup(text)))["status"]!.GetValue<string>());
        Assert.Equal("matched", (await Match(With(plain, "class", "Space"), OpenA201(), new FakeLookup(text)))["status"]!.GetValue<string>());

        Assert.Equal(
            """{"element_id":"x","status":"not_found","open_drawing":{"name":"A-201.dxf","path":"/w/A-201.dxf"}}""",
            (await Match(null, OpenA201(), new FakeLookup(), "x")).ToJsonString());
    }

    [Fact]
    public async Task Targets_summary_uses_the_bundle_only()
    {
        var bundle = new JsonObject
        {
            ["search"] = new JsonArray(Door()),
            ["elements"] = new JsonObject
            {
                ["Door"] = new JsonArray(Door(), With(With(Door(), "id", "el-9"), "handle", "FF"), With(With(Door(), "id", "el-5"), "source_file", "B.dwg")),
            },
        };
        var lookup = new FakeLookup(JsonNode.Parse("""{"handle": "1A", "type": "INSERT", "layer": "A-DOOR", "name": "DOOR_SINGLE"}""")!.AsObject());
        var output = await OntologyRest.TargetsSummaryAsync(bundle, OpenA201(), lookup.Find);
        Assert.Equal("""{"matched":1,"handle_missing":1,"other_drawing":1}""", output["counts"]!.ToJsonString()); // el-1 counted once
        Assert.Equal("""[{"element_id":"el-1","class":"Door","name":"SD-01","handle":"1A","type":"INSERT","layer":"A-DOOR"}]""", output["targets"]!.ToJsonString());
        Assert.Equal("""["el-9: handle_missing"]""", output["not_actionable"]!.ToJsonString());
        Assert.Equal(["1A", "FF"], lookup.Calls);
    }

    [Fact]
    public void Open_drawing_from_the_document_identity()
    {
        var live = OntologyCad.Describe(JsonNode.Parse("""{"document_id": "d1", "document": "C:\\Proj\\A-201.dwg"}"""))!;
        Assert.Equal("""{"name":"A-201.dwg","path":"C:\\Proj\\A-201.dwg"}""", live.Drawing.ToJsonString());
        Assert.Equal("d1", live.DocumentId);
        Assert.Equal("""{"name":"Sample-Plan.dwg"}""", OntologyCad.Describe(JsonNode.Parse("""{"document": "Sample-Plan.dwg"}"""))!.Drawing.ToJsonString());
        Assert.Null(OntologyCad.Describe(JsonNode.Parse("""{"backend": "autocad", "document": null}""")));
        Assert.Null(OntologyCad.Describe(null));
    }

    /// <summary>AutoCAD without the plugin / not running: every call fails like the pipe gateway does.</summary>
    private sealed class DeadGateway : ICadGateway
    {
        public int Calls { get; private set; }

        public string Mode => "autocad";

        public Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct)
        {
            Calls++;
            throw new PowerCad.Core.CadException(PowerCad.Core.ErrorCodes.NotConnected, "No running AutoCAD with the Power CAD plugin was found.");
        }

        public JsonArray ListTargets() => [];

        public JsonObject SelectTarget(string target) => throw new NotSupportedException();
    }

    /// <summary>A-201 as the open drawing: a wall polyline, a door block reference and a stray text.</summary>
    private static (PowerCad.Core.Simulation.InMemoryCadDocument Doc, string Wall, string Door, string Text) House()
    {
        var doc = new PowerCad.Core.Simulation.InMemoryCadDocument { Name = @"C:\Proj\A-201.dxf" };
        doc.AddLayer("A-WALL");
        doc.AddLayer("A-DOOR");
        doc.DefineBlock("DOOR_SINGLE", 900);
        var wall = doc.Add(new PowerCad.Core.Commands.CreateSpec(PowerCad.Core.Model.EntityTypes.Polyline, "A-WALL")
        {
            Points = [new(0, 0), new(100, 0), new(100, 50)],
        });
        var door = doc.Add(new PowerCad.Core.Commands.CreateSpec(PowerCad.Core.Model.EntityTypes.Insert, "A-DOOR") { BlockName = "DOOR_SINGLE", A = new(10, 0) });
        var text = doc.Add(new PowerCad.Core.Commands.CreateSpec(PowerCad.Core.Model.EntityTypes.Text, "A-DOOR") { Text = "SD-01", A = new(10, 5), Height = 2.5 });
        return (doc, wall, door, text);
    }

    [Fact]
    public async Task Locate_tool_maps_elements_to_live_handles_read_only()
    {
        var (doc, wall, door, text) = House();
        _fake.Extra.AddRange(
        [
            FakeOntology.Element("el-x-door", "Door", "SD-01", "A-201", "Model", "2층", "A-DOOR", "DOOR_SINGLE", door),
            FakeOntology.Element("el-x-wall", "Wall", "W-1", "A-201", "Model", "2층", "A-WALL", handle: wall),
            FakeOntology.Element("el-x-label", "Door", "SD-01", "A-201", "Model", "2층", "A-DOOR", handle: text),
            FakeOntology.Element("el-x-gone", "Wall", "W-2", "A-201", "Model", "2층", "A-WALL", handle: "FFFF"),
        ]);
        var commits = doc.CommitCount;
        var tools = new OntologyRestTools(Client(), new DocumentBoundGateway(new SimulatorGateway(doc, readOnly: false)));

        var output = Obj(await tools.Locate(["el-x-door", "el-x-wall", "el-x-label", "el-x-gone", "el-w1", "nope", " el-x-door ", ""]));
        var byId = output["results"]!.AsArray().ToDictionary(r => r!["element_id"]!.GetValue<string>(), r => r!.AsObject());
        Assert.Equal(6, byId.Count); // duplicates (and blanks) are located once
        Assert.Equal(6, output["results"]!.AsArray().Count);
        Assert.Equal("matched", byId["el-x-door"]["status"]!.GetValue<string>());
        Assert.Equal("INSERT", byId["el-x-door"]["entity"]!["type"]!.GetValue<string>());
        Assert.Equal(door, byId["el-x-door"]["handle"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(byId["el-x-door"]["entity"]!["fingerprint"]!.GetValue<string>()));
        Assert.Equal("matched", byId["el-x-wall"]["status"]!.GetValue<string>());
        Assert.Equal("mismatch", byId["el-x-label"]["status"]!.GetValue<string>()); // a TEXT is not the door itself
        Assert.Equal("handle_missing", byId["el-x-gone"]["status"]!.GetValue<string>());
        Assert.Equal("other_drawing", byId["el-w1"]["status"]!.GetValue<string>());
        Assert.Equal("A-501.dwg", byId["el-w1"]["source_file"]!.GetValue<string>());
        Assert.Equal("not_found", byId["nope"]["status"]!.GetValue<string>());
        Assert.Contains("Object not found", byId["nope"]["error"]!.GetValue<string>());
        Assert.Equal("""{"name":"A-201.dxf","path":"C:\\Proj\\A-201.dxf"}""", output["open_drawing"]!.ToJsonString());
        Assert.True(output["read_only"]!.GetValue<bool>());
        Assert.Equal(new[] { door, wall }.Order(), output["matched_handles"]!.AsArray().Select(h => h!.GetValue<string>()).Order());
        Assert.Equal("""{"matched":2,"mismatch":1,"handle_missing":1,"other_drawing":1,"not_found":1}""", output["counts"]!.ToJsonString());
        var contexts = _fake.Requests.Where(r => r.Path.EndsWith("/context", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, contexts.Count);
        Assert.All(contexts, r => Assert.Equal("1", r.Query["hops"]));

        var bundle = Obj(await tools.AutoContext("2층 평면도 문 리스트 갱신", project_id: "P1", k: 5));
        var flags = bundle["elements"]!["Door"]!.AsArray().ToDictionary(e => e!["id"]!.GetValue<string>(), e => e!["in_open_drawing"]!.GetValue<bool>());
        Assert.Equal(new Dictionary<string, bool> { ["el-d1"] = true, ["el-x-door"] = true, ["el-x-label"] = true }, flags);
        Assert.True(bundle["search"]![0]!["in_open_drawing"]!.GetValue<bool>());
        Assert.Equal(3, bundle["counts"]!["in_open_drawing"]!.GetValue<int>()); // el-d1 is a hit and an element
        Assert.Equal("A-201.dxf", bundle["open_drawing"]!["name"]!.GetValue<string>());

        var targets = await OntologyRest.TargetsSummaryAsync(bundle, OntologyCad.Describe(doc.Describe())!.Drawing, OntologyCad.Lookup(new SimulatorGateway(doc, true), null, default));
        Assert.Equal(["el-x-door"], targets["targets"]!.AsArray().Select(t => t!["element_id"]!.GetValue<string>()));
        Assert.Equal("""{"handle_missing":1,"matched":1,"mismatch":1}""", targets["counts"]!.ToJsonString()); // el-d1's 2F3 is absent

        Assert.Equal(commits, doc.CommitCount); // locate/auto_context never touched the drawing
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.Locate([]))).Message);
        Assert.StartsWith("[INVALID_ARGUMENT]", (await Assert.ThrowsAsync<McpException>(() => tools.Locate(Enumerable.Range(0, 201).Select(i => $"e{i}").ToArray()))).Message);
    }

    [Fact]
    public async Task Locate_pins_reads_to_the_described_document()
    {
        var (doc, _, door, _) = House();
        var gateway = new SimulatorGateway(doc, readOnly: true);
        var live = OntologyCad.Describe(doc.Describe())!;
        Assert.NotNull(await OntologyCad.Lookup(gateway, live, default)(door));
        Assert.Null(await OntologyCad.Lookup(gateway, live with { DocumentId = "another-drawing" }, default)(door));
        Assert.Null(await OntologyCad.Lookup(gateway, live, default)("FFFF"));
        Assert.Null(await OntologyCad.Lookup(null, live, default)(door));
    }

    [Fact]
    public async Task Without_autocad_locate_and_auto_context_skip_the_drawing()
    {
        var dead = new DeadGateway();
        var tools = new OntologyRestTools(Client(), dead);
        var output = Obj(await tools.Locate(["el-d1"]));
        Assert.Equal("other_drawing", output["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("no drawing is open", output["results"]![0]!["note"]!.GetValue<string>());
        Assert.Empty(output["matched_handles"]!.AsArray());
        Assert.Null(output["open_drawing"]);
        Assert.True(output.ContainsKey("open_drawing"));

        var bundle = Obj(await tools.AutoContext("2층 평면도 문 리스트 갱신"));
        Assert.False(bundle.ContainsKey("open_drawing"));
        Assert.False(bundle["search"]![0]!.AsObject().ContainsKey("in_open_drawing"));
        Assert.False(bundle["counts"]!.AsObject().ContainsKey("in_open_drawing"));
        Assert.Equal(2, dead.Calls);

        // No gateway at all (e.g. a host without CAD tools) behaves the same.
        var bare = Obj(await new OntologyRestTools(Client()).Locate(["el-d1"]));
        Assert.Equal("other_drawing", bare["results"]![0]!["status"]!.GetValue<string>());

        // The Ontology service being down is an error, not a per-id not_found.
        var down = new OntologyRestTools(new OntologyRestClient("http://127.0.0.1:9", 2), dead);
        Assert.True((await Assert.ThrowsAsync<OntologyRestException>(() => down.Locate(["el-d1"]))).Unavailable);
    }
}
