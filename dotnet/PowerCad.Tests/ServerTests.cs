using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PowerCad.Core.Simulation;
using PowerCad.Core.Transport;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class ServerTests : IDisposable
{
    private sealed class FakeOntologyClient(JsonObject result) : IOntologyContextClient
    {
        public bool Enabled => true;

        public Task<JsonObject> QueryGlobalMemoryAsync(string question, int topK, string? projectId, CancellationToken ct) =>
            Task.FromResult((JsonObject)result.DeepClone());
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
    private readonly string _home = Directory.CreateTempSubdirectory("powercad-srv-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task Tools_run_against_the_simulator()
    {
        var tools = new CadTools(new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false));
        var door = Obj(await tools.Query(block_name: "DOOR*"))["entities"]![0]!;
        var handle = door["handle"]!.GetValue<string>();

        var preview = Obj(await tools.ModifyOpening(handle, expect_fingerprint: door["fingerprint"]!.GetValue<string>(), width: 1000, dry_run: true));
        Assert.False(preview["committed"]!.GetValue<bool>());

        var applied = Obj(await tools.ModifyOpening(handle, expect_fingerprint: door["fingerprint"]!.GetValue<string>(), width: 1000));
        Assert.True(applied["committed"]!.GetValue<bool>());

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ModifyOpening(handle, expect_fingerprint: door["fingerprint"]!.GetValue<string>(), width: 800));
        Assert.StartsWith("[STALE_TARGET]", ex.Message);
        Assert.Contains("Hint:", ex.Message);

        var text = Obj(await tools.ReplaceText(targets: [new TextTarget(Obj(await tools.Query(text_contains: "거실"))["entities"]![0]!["handle"]!.GetValue<string>(), ExpectText: "거실")], new_text: "LIVING"));
        Assert.Single(text["changes"]!.AsArray());

        using var steps = JsonDocument.Parse("""[{"command":"create","params":{"entities":[{"type":"circle","center":[0,0],"radius":1}]}}]""");
        Assert.Single(Obj(await tools.Batch(steps.RootElement))["created"]!.AsArray());
    }

    [Fact]
    public async Task Ontology_context_is_numbered_live_verified_and_non_mutating()
    {
        var gateway = new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false);
        var cad = new CadTools(gateway);
        var door = Obj(await cad.Query(block_name: "DOOR*"))["entities"]![0]!.AsObject();
        var handle = door["handle"]!.GetValue<string>();
        var memory = new JsonObject
        {
            ["route"] = "GLOBAL_MEMORY",
            ["hits"] = new JsonArray
            {
                new JsonObject
                {
                    ["project_id"] = "P-1",
                    ["object_id"] = "aec://object/door-1",
                    ["type"] = "Door",
                    ["geometry_ref"] = $"aec://artifact/source/geometry/{handle}",
                    ["score"] = 0.95,
                },
            },
        };
        var store = new OntologyCandidateStore();
        var queryTools = new OntologyContextTools(gateway, new FakeOntologyClient(memory), store);

        var query = Obj(await queryTools.Query("door", max_choices: 5));
        Assert.False(query["may_execute_mutation"]!.GetValue<bool>());
        var option = query["options"]![0]!.AsObject();
        Assert.Equal(1, option["choice"]!.GetValue<int>());
        Assert.Equal(handle, option["handle"]!.GetValue<string>());

        // Simulate an MCP runtime resolving a fresh tool instance for the next request.
        var selectTools = new OntologyContextTools(gateway, new FakeOntologyClient(memory), store);
        var selected = Obj(await selectTools.Select(query["context_id"]!.GetValue<string>(), 1));
        Assert.False(selected["may_execute_mutation"]!.GetValue<bool>());
        Assert.True(selected["requires_edit_tool_with_expect_fingerprint"]!.GetValue<bool>());
        Assert.Equal(handle, selected["selected"]!["handle"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ontology_context_exposes_numbered_live_action_space()
    {
        var gateway = new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false);
        var cad = new CadTools(gateway);
        var door = Obj(await cad.Query(block_name: "DOOR*"))["entities"]![0]!.AsObject();
        var handle = door["handle"]!.GetValue<string>();
        var memory = new JsonObject
        {
            ["route"] = "GLOBAL_MEMORY",
            ["hits"] = new JsonArray
            {
                new JsonObject
                {
                    ["project_id"] = "P-ACTION",
                    ["object_id"] = "aec://object/door-action",
                    ["type"] = "Door",
                    ["geometry_ref"] = $"aec://artifact/source/geometry/{handle}",
                    ["score"] = 1.0,
                },
            },
        };
        var context = new OntologyContextTools(gateway, new FakeOntologyClient(memory), new OntologyCandidateStore());
        var query = Obj(await context.Query("door"));
        var contextId = query["context_id"]!.GetValue<string>();

        var actions = Obj(await context.Actions(contextId, 1));
        Assert.False(actions["may_execute_mutation"]!.GetValue<bool>());
        var operations = actions["actions"]!.AsArray()
            .Select(row => row!["operation"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(["cad_get", "cad_move", "cad_modify_opening"], operations);

        var selected = Obj(await context.SelectAction(contextId, 1, 3));
        Assert.Equal("cad_modify_opening", selected["selected_action"]!["operation"]!.GetValue<string>());
        Assert.False(selected["may_execute_mutation"]!.GetValue<bool>());
        Assert.True(selected["requires_edit_tool_with_expect_fingerprint"]!.GetValue<bool>());

        var invalid = await Assert.ThrowsAsync<McpException>(() => context.SelectAction(contextId, 1, 99));
        Assert.StartsWith("[INVALID_ACTION]", invalid.Message);
    }

    [Fact]
    public async Task Sion_client_sends_bearer_token_when_configured()
    {
        string? authorization = null;
        var handler = new StubHttpHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            var body = new JsonObject
            {
                ["canonical"] = false,
                ["read_only"] = true,
                ["result"] = new JsonObject
                {
                    ["route"] = "GLOBAL_MEMORY",
                    ["query"] = "door",
                    ["hits"] = new JsonArray(),
                },
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        });
        var client = new SionAecClient(
            new HttpClient(handler),
            new Uri("https://sion.example/"),
            "remote-sion-token-123456789");

        _ = await client.QueryGlobalMemoryAsync("door", 5, null, CancellationToken.None);
        Assert.Equal("Bearer remote-sion-token-123456789", authorization);
    }

    [Fact]
    public void Remote_Sion_configuration_requires_token()
    {
        var ex = Assert.Throws<McpException>(() =>
            ContextClientFactory.FromEnvironment(name => name == "POWER_CAD_SION_URL" ? "https://sion.example/" : null));
        Assert.StartsWith("[SION_CONFIG]", ex.Message);
    }

    [Fact]
    public async Task Golden_Sion_to_PowerCad_context_path_revalidates_before_transaction()
    {
        var gateway = new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false);
        var cad = new CadTools(gateway);
        var door = Obj(await cad.Query(block_name: "DOOR*"))["entities"]![0]!.AsObject();
        var handle = door["handle"]!.GetValue<string>();
        string? requested = null;

        var handler = new StubHttpHandler(request =>
        {
            requested = request.RequestUri?.PathAndQuery;
            var body = new JsonObject
            {
                ["source"] = "khs0927/Ontology",
                ["canonical"] = false,
                ["read_only"] = true,
                ["result"] = new JsonObject
                {
                    ["route"] = "GLOBAL_MEMORY",
                    ["query"] = "door near lobby",
                    ["hits"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["project_id"] = "P-GOLDEN",
                            ["object_id"] = "aec://object/door-golden",
                            ["type"] = "Door",
                            ["geometry_ref"] = $"aec://artifact/source/geometry/{handle}",
                            ["score"] = 0.99,
                        },
                    },
                },
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        });
        var sion = new SionAecClient(new HttpClient(handler), new Uri("http://127.0.0.1:8000/"));
        var store = new OntologyCandidateStore();
        var context = new OntologyContextTools(gateway, sion, store);

        var query = Obj(await context.Query("door near lobby", project_id: "P-GOLDEN", max_choices: 5));
        Assert.Contains("/api/v1/aec/query?", requested);
        Assert.Contains("project_id=P-GOLDEN", requested);
        Assert.False(query["may_execute_mutation"]!.GetValue<bool>());

        var selected = Obj(await context.Select(query["context_id"]!.GetValue<string>(), 1));
        var chosen = selected["selected"]!.AsObject();
        Assert.Equal(handle, chosen["handle"]!.GetValue<string>());
        var fingerprint = chosen["fingerprint"]!.GetValue<string>();

        var preview = Obj(await cad.ModifyOpening(handle, expect_fingerprint: fingerprint, width: 1000, dry_run: true));
        Assert.False(preview["committed"]!.GetValue<bool>());

        var applied = Obj(await cad.ModifyOpening(handle, expect_fingerprint: fingerprint, width: 1000));
        Assert.True(applied["committed"]!.GetValue<bool>());
        Assert.True(applied["checks_passed"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Sion_client_rejects_context_not_marked_read_only()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var body = new JsonObject
            {
                ["canonical"] = true,
                ["read_only"] = false,
                ["result"] = new JsonObject(),
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        });
        var client = new SionAecClient(new HttpClient(handler), new Uri("http://127.0.0.1:8000/"));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.QueryGlobalMemoryAsync("door", 5, null, CancellationToken.None));
        Assert.StartsWith("[SION_CONTRACT]", ex.Message);
    }

    [Fact]
    public async Task Ontology_context_selection_rejects_stale_live_entity()
    {
        var gateway = new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false);
        var cad = new CadTools(gateway);
        var door = Obj(await cad.Query(block_name: "DOOR*"))["entities"]![0]!.AsObject();
        var handle = door["handle"]!.GetValue<string>();
        var fingerprint = door["fingerprint"]!.GetValue<string>();
        var memory = new JsonObject
        {
            ["route"] = "GLOBAL_MEMORY",
            ["hits"] = new JsonArray
            {
                new JsonObject
                {
                    ["project_id"] = "P-1",
                    ["object_id"] = "aec://object/door-1",
                    ["type"] = "Door",
                    ["geometry_ref"] = $"aec://artifact/source/geometry/{handle}",
                    ["score"] = 1.0,
                },
            },
        };
        var context = new OntologyContextTools(gateway, new FakeOntologyClient(memory), new OntologyCandidateStore());
        var query = Obj(await context.Query("door"));

        _ = await cad.ModifyOpening(handle, expect_fingerprint: fingerprint, width: 1000);
        var ex = await Assert.ThrowsAsync<McpException>(() => context.Select(query["context_id"]!.GetValue<string>(), 1));
        Assert.StartsWith("[STALE_CONTEXT]", ex.Message);
    }

    [Fact]
    public async Task Read_only_gateway_refuses_edits()
    {
        var tools = new CadTools(new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: true));
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ReplaceText(find: "거실", replace: "x"));
        Assert.StartsWith("[READ_ONLY]", ex.Message);
    }

    [Fact]
    public async Task Pipe_gateway_discovers_the_plugin_and_forwards_calls()
    {
        var gateway = new PipeGateway(new DiscoveryStore(_home), pinnedTarget: null, readOnly: false);
        var tools = new CadTools(gateway);
        var none = await Assert.ThrowsAsync<McpException>(() => tools.Status(CancellationToken.None));
        Assert.StartsWith("[NOT_CONNECTED]", none.Message);

        var doc = InMemoryCadDocument.CreateSample();
        var (server, info) = TransportTests.StartFakePlugin(_home, doc, Environment.ProcessId);
        await using (server)
        {
            Assert.Equal("simulator", Obj(await tools.Status(CancellationToken.None))["backend"]!.GetValue<string>());
            var targets = JsonNode.Parse(tools.ListTargets())!.AsArray();
            Assert.Equal(info.Target, targets.Single()!["target"]!.GetValue<string>());
            Assert.True(targets.Single()!["selected"]!.GetValue<bool>());

            var wall = Obj(await tools.Query(layers: ["A-WALL"], max_results: 1))["entities"]![0]!;
            var moved = Obj(await tools.Move([new EntityTarget(wall["handle"]!.GetValue<string>(), wall["fingerprint"]!.GetValue<string>())], displacement: [0, 250]));
            Assert.Equal(1, moved["checks_passed"]!.GetValue<int>());
            Assert.Equal(1, doc.CommitCount - 11); // the sample itself took 11 commits

            Assert.Contains(info.Target, tools.SelectTarget(info.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Throws<McpException>(() => tools.SelectTarget("autocad-2027-1"));
        }
    }

    [Fact]
    public async Task Stdio_server_end_to_end()
    {
        var serverDll = typeof(CadTools).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "power-cad",
            Command = "dotnet",
            Arguments = [serverDll, "--simulate"],
            EnvironmentVariables = new Dictionary<string, string?> { ["POWERCAD_ONTOLOGY_URL"] = "", ["POWER_CAD_ONTOLOGY_URL"] = "" },
        });
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            ["cad_batch", "cad_bind_document", "cad_block_library", "cad_context_action_select", "cad_context_actions", "cad_context_query", "cad_context_select", "cad_copy", "cad_create", "cad_delete", "cad_export_block", "cad_export_hatch_pattern", "cad_extract_snapshot", "cad_get", "cad_get_document_identity", "cad_import_block", "cad_inspect", "cad_inventory", "cad_layers", "cad_list_targets", "cad_measure", "cad_modify_opening", "cad_move", "cad_offset", "cad_plan_create", "cad_plan_execute", "cad_plan_get", "cad_query", "cad_query_page", "cad_replace_text", "cad_review_snapshot", "cad_save", "cad_select_target", "cad_set_layer", "cad_set_properties", "cad_snapshot", "cad_status", "cad_transform", "cad_zoom", "ontology_auto_context", "ontology_blocks", "ontology_catalog", "ontology_drawings", "ontology_element_context", "ontology_find_elements", "ontology_locate", "ontology_search"],
            tools.Select(t => t.Name).Order().ToArray());
        Assert.True(tools.Single(t => t.Name == "cad_query").ProtocolTool.Annotations?.ReadOnlyHint);

        var identity = await client.CallToolAsync("cad_get_document_identity");
        var documentId = Obj(((TextContentBlock)identity.Content[0]).Text)["document_id"]!.GetValue<string>();
        var bound = await client.CallToolAsync("cad_bind_document", new Dictionary<string, object?> { ["document_id"] = documentId });
        Assert.NotEqual(true, bound.IsError);

        var status = await client.CallToolAsync("cad_status");
        Assert.NotEqual(true, status.IsError);
        Assert.Contains("Sample-Plan.dwg", ((TextContentBlock)status.Content[0]).Text);
        Assert.True(tools.Single(t => t.Name == "cad_inventory").ProtocolTool.Annotations?.ReadOnlyHint);

        var inventory = await client.CallToolAsync("cad_inventory", new Dictionary<string, object?> { ["max_depth"] = 1 });
        Assert.NotEqual(true, inventory.IsError);
        Assert.Contains("TITLE_BLOCK", ((TextContentBlock)inventory.Content[0]).Text);

        var edit = await client.CallToolAsync("cad_replace_text", new Dictionary<string, object?> { ["find"] = "주방", ["replace"] = "부엌" });
        Assert.NotEqual(true, edit.IsError);
        Assert.Contains("부엌", ((TextContentBlock)edit.Content[0]).Text);

        Assert.True(tools.Single(t => t.Name == "ontology_auto_context").ProtocolTool.Annotations?.ReadOnlyHint);
        var ontology = await client.CallToolAsync("ontology_catalog");
        Assert.True(ontology.IsError);
        Assert.Contains("ONTOLOGY_NOT_CONFIGURED", ((TextContentBlock)ontology.Content[0]).Text);

        var bad = await client.CallToolAsync("cad_move", new Dictionary<string, object?> { ["targets"] = new[] { new { handle = "FFFF" } }, ["displacement"] = new[] { 1.0, 0 } });
        Assert.True(bad.IsError);
        Assert.Contains("NOT_FOUND", ((TextContentBlock)bad.Content[0]).Text);
    }

    [Fact]
    public async Task Snapshot_returns_an_image_block_and_new_tools_work()
    {
        var tools = new CadTools(new SimulatorGateway(InMemoryCadDocument.CreateSample(), readOnly: false));
        var shot = await tools.Snapshot(extents: true, width: 200);
        Assert.IsType<ImageContentBlock>(shot.Content[0]);
        Assert.Equal("image/png", ((ImageContentBlock)shot.Content[0]).MimeType);
        Assert.DoesNotContain("image_base64", ((TextContentBlock)shot.Content[1]).Text);

        var layer = Obj(await tools.SetLayer("A-DIMS", color: JsonDocument.Parse("\"cyan\"").RootElement, linetype: "CENTER"));
        Assert.Equal("created", layer["other_changes"]![0]!["action"]!.GetValue<string>());
        var layers = Obj(await tools.Layers(["A-DIMS"]));
        Assert.Equal(4, layers["layers"]![0]!["color"]!.GetValue<int>());

        var wall = Obj(await tools.Query(layers: ["A-WALL"], max_results: 1))["entities"]![0]!;
        var copied = Obj(await tools.Copy([new EntityTarget(wall["handle"]!.GetValue<string>())], displacement: [0, 100], count: 3));
        Assert.Equal(3, copied["created"]!.AsArray().Count);
        var gone = Obj(await tools.Delete([new EntityTarget(copied["created"]![0]!["handle"]!.GetValue<string>())]));
        Assert.Single(gone["deleted"]!.AsArray());
        Assert.Contains("text_styles", await tools.Inspect());
    }

    [Fact]
    public void Options_parse_flags_and_environment()
    {
        var o = ServerOptions.Parse(["--target", "autocad-2027-5", "--read-only"], n => n == "POWER_CAD_SIMULATE" ? "1" : null);
        Assert.True(o.Simulate);
        Assert.True(o.ReadOnly);
        Assert.Equal("autocad-2027-5", o.Target);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--bogus"], _ => null));
    }
}
