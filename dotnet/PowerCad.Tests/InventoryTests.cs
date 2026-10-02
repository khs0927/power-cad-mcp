using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;
using PowerCad.Core.Simulation;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class InventoryTests
{
    private readonly InMemoryCadDocument _doc = InMemoryCadDocument.CreateSheetSample();

    private JsonObject Run(string json = "{}") => (JsonObject)new CommandDispatcher(_doc).Execute("drawing_inventory", JsonNode.Parse(json) as JsonObject);

    private static JsonObject Row(JsonObject inventory, string section, string key, string value) =>
        inventory[section]!.AsArray().Select(n => n!.AsObject()).Single(n => n[key]!.GetValue<string>() == value);

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static int Int(JsonNode? node) => node!.GetValue<int>();

    [Fact]
    public void Lists_layouts_with_tab_order_plot_settings_and_counts()
    {
        var inventory = Run();
        var layouts = inventory["layouts"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(new[] { "Model", "A1 Sheet", "A3 Detail" }, layouts.Select(l => l["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, layouts.Select(l => Int(l["tab_order"])).ToArray());
        Assert.True(layouts[0]["is_model"]!.GetValue<bool>());
        Assert.Equal("*Model_Space", layouts[0]["block"]!.GetValue<string>());
        Assert.Equal(2, Int(layouts[0]["block_reference_count"])); // the sample's door and window
        Assert.Equal(_doc.Execute(tx => tx.ScanModelSpace().Count(), commit: false), Int(layouts[0]["entity_count"]));

        var sheet = layouts[1];
        Assert.False(sheet["is_model"]!.GetValue<bool>());
        Assert.Equal("*Paper_Space", sheet["block"]!.GetValue<string>());
        Assert.Equal(2, Int(sheet["viewport_count"]));
        Assert.Equal(3, Int(sheet["block_reference_count"]));
        Assert.StartsWith("ISO_full_bleed_A1", sheet["plot"]!["media"]!.GetValue<string>());
        Assert.Equal("*Paper_Space0", layouts[2]["block"]!.GetValue<string>());
        Assert.Equal(3, Int(inventory["layout_count"]));
    }

    [Fact]
    public void Lists_every_block_definition_with_attributes_contents_and_insert_counts()
    {
        var inventory = Run();
        Assert.Equal(10, Int(inventory["block_definition_count"])); // 3 layout blocks, 5 blocks, 2 xrefs
        Assert.False(inventory["truncated"]!.GetValue<bool>());

        var title = Row(inventory, "block_definitions", "name", "TITLE_BLOCK");
        Assert.Equal(new[] { "SHEET_NO", "TITLE", "FIRM" }, title["attribute_definitions"]!.AsArray().Select(a => a!["tag"]!.GetValue<string>()).ToArray());
        var firm = title["attribute_definitions"]![2]!;
        Assert.True(firm["constant"]!.GetValue<bool>());
        Assert.Equal("Firm", firm["prompt"]!.GetValue<string>());
        Assert.Equal("POWER CAD", firm["default"]!.GetValue<string>());
        Assert.Equal(12, Int(title["counts_by_type"]!["LINE"]));
        Assert.Equal(3, Int(title["counts_by_type"]!["ATTDEF"]));
        Assert.Equal(1, Int(title["nested_blocks"]!["LOGO"]));
        Assert.Equal(2, Int(title["insert_count"]));
        Assert.Equal(1, Int(title["insert_counts_by_layout"]!["A1 Sheet"]));
        Assert.Equal(1, Int(title["insert_counts_by_layout"]!["A3 Detail"]));

        var anonymous = Row(inventory, "block_definitions", "name", "*U7");
        Assert.True(anonymous["is_anonymous"]!.GetValue<bool>());
        Assert.Equal("DOOR_SINGLE", anonymous["effective_name"]!.GetValue<string>());

        // the dynamic door counts its direct insert and its anonymous representation
        var door = Row(inventory, "block_definitions", "name", "DOOR_SINGLE");
        Assert.True(door["is_dynamic"]!.GetValue<bool>());
        Assert.Equal(1, Int(door["insert_counts_by_layout"]!["Model"]));
        Assert.Equal(1, Int(door["insert_counts_by_layout"]!["A1 Sheet"]));

        Assert.True(Row(inventory, "block_definitions", "name", "*Model_Space")["is_layout"]!.GetValue<bool>());
        var site = Row(inventory, "block_definitions", "name", "SITE");
        Assert.True(site["is_xref"]!.GetValue<bool>());
        Assert.True(site["is_from_external_reference"]!.GetValue<bool>());
        Assert.Equal("attach", site["xref"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Lists_references_per_layout_and_marks_the_nested_recursion_path()
    {
        var inventory = Run();
        var rows = inventory["block_references"]!.AsArray().Select(n => n!.AsObject()).ToList();

        var door = rows.Single(r => r["handle"]!.GetValue<string>() == "3B1");
        Assert.Equal("A1 Sheet", door["layout"]!.GetValue<string>());
        Assert.Equal("*U7", door["block_name"]!.GetValue<string>());
        Assert.Equal("DOOR_SINGLE", door["effective_name"]!.GetValue<string>());
        Assert.True(door["is_dynamic"]!.GetValue<bool>());
        Assert.Equal(90.0, door["rotation"]!.GetValue<double>());
        Assert.Equal("A-DOOR", door["layer"]!.GetValue<string>());

        var title = rows.Single(r => r["handle"]!.GetValue<string>() == "3C0");
        Assert.Equal(0.5, title["scale"]![0]!.GetValue<double>());
        Assert.Equal("DETAIL", title["attributes"]!.AsArray().Single(a => a!["tag"]!.GetValue<string>() == "TITLE")!["value"]!.GetValue<string>());
        Assert.Equal(0, Int(title["depth"]));

        // the logo nested in each title block, with the path from the layout down
        var logos = rows.Where(r => r["handle"]!.GetValue<string>() == "3A0").ToList();
        Assert.Equal(2, logos.Count);
        var logo = logos.Single(r => r["layout"]!.GetValue<string>() == "A3 Detail");
        Assert.Equal(1, Int(logo["depth"]));
        Assert.Equal(new[] { "3C0", "3A0" }, Strings(logo["path"]));
        Assert.Equal(new[] { "TITLE_BLOCK", "LOGO" }, Strings(logo["block_path"]));
        Assert.Equal("3C0", logo["parent_handle"]!.GetValue<string>());
        Assert.Equal("parent_block", logo["coordinates"]!.GetValue<string>());

        // model-space inserts keep their attribute values; XREF instances are never expanded
        var modelDoor = rows.Single(r => r["layout"]!.GetValue<string>() == "Model" && r["block_name"]!.GetValue<string>() == "DOOR_SINGLE");
        Assert.Equal("D1", modelDoor["attributes"]![0]!["value"]!.GetValue<string>());
        Assert.True(rows.Single(r => r["handle"]!.GetValue<string>() == "3B2")["is_xref"]!.GetValue<bool>());
        Assert.DoesNotContain(rows, r => r["parent_handle"]?.GetValue<string>() == "3B2");
        Assert.Equal(6, Int(inventory["top_level_reference_count"]));
        Assert.Equal(2, Int(inventory["nested_reference_count"]));
        Assert.Equal(8, Int(inventory["returned_reference_count"]));
    }

    [Fact]
    public void Reports_xrefs_with_status_type_and_nested_graph()
    {
        var inventory = Run();
        var site = Row(inventory, "xrefs", "name", "SITE");
        Assert.Equal("resolved", site["status"]!.GetValue<string>());
        Assert.True(site["found"]!.GetValue<bool>());
        Assert.Equal("attach", site["type"]!.GetValue<string>());
        Assert.Equal("SURVEY", Assert.Single(Strings(site["nested_xrefs"])));
        Assert.Equal(1, Int(site["insert_count"]));

        var survey = Row(inventory, "xrefs", "name", "SURVEY");
        Assert.Equal("file_not_found", survey["status"]!.GetValue<string>());
        Assert.False(survey["found"]!.GetValue<bool>());
        Assert.Equal("overlay", survey["type"]!.GetValue<string>());
        Assert.True(survey["is_nested"]!.GetValue<bool>());
        Assert.Contains("xref_contents", Strings(inventory["excluded_scopes"]));
    }

    [Fact]
    public void Bounds_set_truncation_flags_but_counts_stay_complete()
    {
        var shallow = Run("""{"max_depth":0}""");
        Assert.True(shallow["depth_limited"]!.GetValue<bool>());
        Assert.All(shallow["block_references"]!.AsArray(), r => Assert.Equal(0, Int(r!["depth"])));

        var few = Run("""{"max_blocks":1,"max_references":1}""");
        Assert.True(few["blocks_truncated"]!.GetValue<bool>());
        Assert.True(few["references_truncated"]!.GetValue<bool>());
        Assert.True(few["truncated"]!.GetValue<bool>());
        Assert.Single(few["block_definitions"]!.AsArray());
        Assert.Single(few["block_references"]!.AsArray());
        Assert.Equal(10, Int(few["block_definition_count"]));
        Assert.Equal(6, Int(few["top_level_reference_count"]));
        Assert.Equal(2, Int(Row(few, "layouts", "name", "A1 Sheet")["viewport_count"]));

        // the payload limit drops rows, never layouts
        var small = _doc.GetDrawingInventory(new InventoryOptions(MaxBytes: 2500));
        Assert.True(small["truncated"]!.GetValue<bool>());
        Assert.Equal(3, small["layouts"]!.AsArray().Count);
        Assert.True(small["block_definitions"]!.AsArray().Count < 10);
    }

    [Fact]
    public void Is_read_only_validated_and_bound_to_the_document()
    {
        var commits = _doc.CommitCount;
        Run();
        Assert.Equal(commits, _doc.CommitCount);

        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<CadException>(() => Run("""{"max_depth":9}""")).Code);
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<CadException>(() => Run("""{"layout":"A1 Sheet"}""")).Code);
        Assert.Equal(ErrorCodes.DocumentChanged, Assert.Throws<CadException>(() => Run("""{"expected_document_id":"other"}""")).Code);
        Assert.Equal(_doc.DocumentId, Run($$"""{"expected_document_id":"{{_doc.DocumentId}}"}""")["document_id"]!.GetValue<string>());
        Assert.Contains("drawing_inventory", CommandDispatcher.Commands);
        Assert.DoesNotContain("drawing_inventory", CommandDispatcher.Mutating);
    }

    [Fact]
    public async Task Server_tool_runs_read_only_through_the_gateway()
    {
        var gateway = new DocumentBoundGateway(new SimulatorGateway(_doc, readOnly: true));
        var tools = new CadTools(gateway);
        var inventory = JsonNode.Parse(await tools.Inventory(max_depth: 1))!.AsObject();
        Assert.Equal("native_inventory", inventory["scope"]!.GetValue<string>());
        Assert.Equal(_doc.DocumentId, inventory["document_id"]!.GetValue<string>());
        Assert.Equal(1, Int(inventory["limits"]!["max_depth"]));
        Assert.Contains("TITLE_BLOCK", inventory["block_definitions"]!.AsArray().Select(b => b!["name"]!.GetValue<string>()));

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Inventory(max_blocks: 0));
        Assert.StartsWith("[INVALID_PARAMS]", ex.Message);
    }
}
