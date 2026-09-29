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
        });
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            ["cad_batch", "cad_block_library", "cad_copy", "cad_create", "cad_delete", "cad_export_block", "cad_export_hatch_pattern", "cad_get", "cad_import_block", "cad_inspect", "cad_layers", "cad_list_targets", "cad_modify_opening", "cad_move", "cad_query", "cad_replace_text", "cad_select_target", "cad_set_layer", "cad_set_properties", "cad_snapshot", "cad_status", "cad_transform", "cad_zoom"],
            tools.Select(t => t.Name).Order().ToArray());
        Assert.True(tools.Single(t => t.Name == "cad_query").ProtocolTool.Annotations?.ReadOnlyHint);

        var status = await client.CallToolAsync("cad_status");
        Assert.NotEqual(true, status.IsError);
        Assert.Contains("Sample-Plan.dwg", ((TextContentBlock)status.Content[0]).Text);

        var edit = await client.CallToolAsync("cad_replace_text", new Dictionary<string, object?> { ["find"] = "주방", ["replace"] = "부엌" });
        Assert.NotEqual(true, edit.IsError);
        Assert.Contains("부엌", ((TextContentBlock)edit.Content[0]).Text);

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
