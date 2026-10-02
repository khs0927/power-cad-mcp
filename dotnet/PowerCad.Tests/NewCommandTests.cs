using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;
using PowerCad.Core.Simulation;
using Xunit;

namespace PowerCad.Tests;

/// <summary>delete / set_properties / copy / transform / layers / inspect / view and the new create types.</summary>
public sealed class NewCommandTests
{
    private readonly InMemoryCadDocument _doc = InMemoryCadDocument.CreateSample();
    private readonly CommandDispatcher _cad;

    public NewCommandTests() => _cad = new CommandDispatcher(_doc);

    private JsonObject Run(string command, string json = "{}") => (JsonObject)_cad.Execute(command, JsonNode.Parse(json) as JsonObject);

    private CadException Fails(string command, string json)
    {
        var commits = _doc.CommitCount;
        var e = Assert.Throws<CadException>(() => Run(command, json));
        Assert.Equal(commits, _doc.CommitCount);
        return e;
    }

    private JsonObject One(string json) => Run("query", json)["entities"]!.AsArray().Single()!.AsObject();

    private static string H(JsonNode e) => e["handle"]!.GetValue<string>();

    private static string F(JsonNode e) => e["fingerprint"]!.GetValue<string>();

    private JsonObject Created(string entity) => Run("create", $$"""{"entities":[{{entity}}]}""")["created"]![0]!.AsObject();

    // ------------------------------------------------------------------ create
    [Fact]
    public void Hatch_loop_matches_regardless_of_start_vertex_and_direction()
    {
        var want = new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 50, 0), new Vec3(0, 50, 0) };
        EntityState Loop(string pts) => new("1", "HATCH", "HAT", new JsonObject { ["points"] = JsonNode.Parse(pts) });

        Assert.True(CreateSpec.LoopMatches(Loop("[[100,0,0],[100,50,0],[0,50,0],[0,0,0]]"), want)); // rotated start
        Assert.True(CreateSpec.LoopMatches(Loop("[[0,0,0],[0,50,0],[100,50,0],[100,0,0]]"), want)); // reversed
        Assert.True(CreateSpec.LoopMatches(Loop("[[0,50,0],[100,50,0],[100,0,0],[0,0,0]]"), want)); // both
        Assert.False(CreateSpec.LoopMatches(Loop("[[0,0,0],[100,0,0],[0,50,0],[100,50,0]]"), want)); // different shape
        Assert.False(CreateSpec.LoopMatches(Loop("[[0,0,0],[100,0,0],[100,50,0]]"), want)); // different count
    }

    [Fact]
    public void Exports_a_block_to_the_library_and_imports_it_into_another_drawing()
    {
        var lib = Path.Combine(Path.GetTempPath(), "powercad-lib-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("POWER_CAD_BLOCK_LIBRARY", lib);
        try
        {
            var block = Run("inspect")["blocks"]!.AsArray()[0]!["name"]!.GetValue<string>();
            var card = Run("export_block", $$"""{"name":"{{block}}","description":"test","tags":["t"]}""");
            Assert.True(File.Exists(card["path"]!.GetValue<string>()));
            Assert.True(File.Exists(Path.ChangeExtension(card["path"]!.GetValue<string>(), ".json")));
            Assert.Single(BlockLibrary.List());

            var other = new CommandDispatcher(InMemoryCadDocument.CreateSample());
            var again = (JsonObject)other.Execute("import_block", JsonNode.Parse($$"""{"name":"{{block}}"}""") as JsonObject);
            Assert.False(again["imported"]!.GetValue<bool>()); // the sample already defines it

            var renamed = (JsonObject)other.Execute("import_block", JsonNode.Parse($$"""{"path":"{{card["path"]!.GetValue<string>().Replace("\\", "\\\\")}}","replace":true}""") as JsonObject);
            Assert.True(renamed["imported"]!.GetValue<bool>());

            Fails("export_block", """{"name":"NO_SUCH_BLOCK"}""");
        }
        finally
        {
            Environment.SetEnvironmentVariable("POWER_CAD_BLOCK_LIBRARY", null);
            if (Directory.Exists(lib))
            {
                Directory.Delete(lib, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(400, 45)]
    [InlineData(2.5, 30)]
    public void Pat_file_undoes_hatch_scale_and_angle(double scale, double angle)
    {
        // ANSI31 as a hatch evaluates it: 45 degree lines 3.175 apart, rotated by the hatch angle and scaled
        var r = (45 + angle) * Math.PI / 180;
        var lines = new JsonArray(new JsonObject
        {
            ["angle"] = 45 + angle,
            ["base"] = new JsonArray(0.0, 0.0),
            ["offset"] = new JsonArray(-Math.Sin(r) * 3.175 * scale, Math.Cos(r) * 3.175 * scale),
        });
        var pat = PatFile.Build("ansi31x", "test", scale, angle, lines);
        Assert.Equal("*ANSI31X, test\r\n45, 0, 0, 0, 3.175\r\n", pat);
    }

    [Fact]
    public void Exports_a_hatch_pattern_to_a_pat_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "powercad-pat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hatch = Created("""{"type":"hatch","points":[[0,0],[100,0],[100,50],[0,50]],"pattern":"ANSI31","scale":20,"angle":15}""");
            var folder = dir.Replace("\\", "\\\\");
            var res = Run("export_hatch_pattern", $$"""{"handle":"{{H(hatch)}}","name":"KHAT_TEST","folder":"{{folder}}"}""");
            Assert.Equal("*KHAT_TEST, from ANSI31\r\n45, 0, 0, 0, 3.175\r\n", File.ReadAllText(res["path"]!.GetValue<string>()).TrimEnd('\r', '\n') + "\r\n");
            Fails("export_hatch_pattern", $$"""{"handle":"{{H(hatch)}}","name":"KHAT_TEST","folder":"{{folder}}"}"""); // exists
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Creates_leaders_with_arrow_choice()
    {
        var dot = Created("""{"type":"leader","layer":"DIMLE","points":[[0,0],[500,500],[1500,500]],"arrow":"dot"}""");
        Assert.Equal("LEADER", dot["type"]!.GetValue<string>());
        Assert.Equal("_DOT", dot["arrow"]!.GetValue<string>());
        Assert.Equal(3, dot["points"]!.AsArray().Count);

        var plain = Created("""{"type":"leader","points":[[0,0],[800,0]],"arrow":"none"}""");
        Assert.Equal("none", plain["arrow"]!.GetValue<string>());

        Fails("create", """{"entities":[{"type":"leader","points":[[0,0]]}]}""");

        // a leader's style is a dimension style (arrow size), not a text style
        var styled = Created("""{"type":"leader","points":[[0,0],[800,0]],"style":"ISO-25"}""");
        Assert.Equal("ISO-25", styled["style"]!.GetValue<string>());
        Assert.Contains("Dimension style", Fails("create", """{"entities":[{"type":"leader","points":[[0,0],[800,0]],"style":"NOPE"}]}""").Message);
    }

    [Fact]
    public void Creates_dimensions_hatches_points_and_styled_text()
    {
        var dim = Created("""{"type":"dimension","layer":"A-DIMS","p1":[0,0],"p2":[3900,0],"offset":-1000,"text":"3,900"}""");
        Assert.Equal("rotated", dim["kind"]!.GetValue<string>());
        Assert.Equal(3900, dim["measurement"]!.GetValue<double>());
        Assert.Equal(-1000, dim["dimline"]![1]!.GetValue<double>());
        Assert.Equal("3,900", dim["text_override"]!.GetValue<string>());

        var vertical = Created("""{"type":"dimension","p1":[0,0],"p2":[0,4200],"offset":1000}""");
        Assert.Equal(90, vertical["rotation"]!.GetValue<double>());
        Assert.Equal(-1000, vertical["dimline"]![0]!.GetValue<double>()); // +offset of a vertical dim goes to -X

        var aligned = Created("""{"type":"dimension","kind":"aligned","p1":[0,0],"p2":[3000,4000],"line_point":[0,500]}""");
        Assert.Equal(5000, aligned["measurement"]!.GetValue<double>());

        var hatch = Created("""{"type":"hatch","layer":"HAT2","points":[[0,0],[100,0],[100,50],[0,50],[0,0]],"pattern":"ansi31","scale":2}""");
        Assert.Equal("ANSI31", hatch["pattern"]!.GetValue<string>());
        Assert.Equal(4, hatch["points"]!.AsArray().Count);
        Assert.Equal(5000, hatch["area"]!.GetValue<double>());

        var text = Created("""{"type":"text","text":"안방","position":[1950,2100],"height":300,"justify":"middle","color":"red","lineweight":0.25}""");
        Assert.Equal("middle", text["justify"]!.GetValue<string>());
        Assert.Equal(1, text["color"]!.GetValue<int>());
        Assert.Equal(0.25, text["lineweight"]!.GetValue<double>());
        Assert.Equal(1950, text["alignment_point"]![0]!.GetValue<double>());

        Assert.Equal("POINT", Created("""{"type":"point","position":[1,2]}""")["type"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.NotFound, Fails("create", """{"entities":[{"type":"text","text":"x","position":[0,0],"style":"NoSuchStyle"}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"text","text":"x","position":[0,0],"justify":"upper"}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"line","start":[0,0],"end":[1,0],"color":"sparkly"}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"line","start":[0,0],"end":[1,0],"lineweight":0.33}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"dimension","p1":[0,0],"p2":[1,0]}]}""").Code);
    }

    [Fact]
    public void Arc_angles_are_verified_modulo_360()
    {
        var arc = Created("""{"type":"arc","center":[0,0],"radius":900,"start_angle":270,"end_angle":360}""");
        Assert.Equal(360, arc["end_angle"]!.GetValue<double>());
    }

    // ------------------------------------------------------------------ delete
    [Fact]
    public void Delete_is_verified_previewable_and_refuses_stale_or_locked_targets()
    {
        var wall = One("""{"layers":["A-WALL"],"types":["LINE"],"max_results":1}""");
        var target = $$$"""{"targets":[{"handle":"{{{H(wall)}}}","expect_fingerprint":"{{{F(wall)}}}"}]}""";

        var preview = Run("delete", target.Replace("]}", "],\"dry_run\":true}", StringComparison.Ordinal));
        Assert.False(preview["committed"]!.GetValue<bool>());
        Assert.Single(Run("query", $$$"""{"handles":["{{{H(wall)}}}"]}""")["entities"]!.AsArray());

        var r = Run("delete", target);
        Assert.Equal(H(wall), H(r["deleted"]![0]!));
        Assert.Empty(Run("query", $$$"""{"handles":["{{{H(wall)}}}"]}""")["entities"]!.AsArray());
        Assert.Equal(ErrorCodes.NotFound, Fails("delete", target).Code);

        var locked = One("""{"layers":["A-ANNO-LOCKED"]}""");
        Assert.Equal(ErrorCodes.LockedLayer, Fails("delete", $$$"""{"handles":["{{{H(locked)}}}"]}""").Code);
    }

    [Fact]
    public void Batch_can_create_then_delete_and_rolls_back_as_a_whole()
    {
        var r = Run("batch", """{"steps":[{"command":"create","params":{"entities":[{"type":"circle","center":[0,0],"radius":5}]}},{"command":"set_layer","params":{"name":"TMP","color":3}}]}""");
        var circle = H(r["created"]![0]!);
        var both = Run("batch", $$$"""{"steps":[{"command":"delete","params":{"handles":["{{{circle}}}"]}},{"command":"set_layer","params":{"name":"TMP","color":5}}]}""");
        Assert.Single(both["deleted"]!.AsArray());
        Assert.Equal(ErrorCodes.NotFound, Fails("batch", """{"steps":[{"command":"set_layer","params":{"name":"TMP","color":1}},{"command":"delete","params":{"handles":["FFFFF"]}}]}""").Code);
        Assert.Equal(5, Run("layers", """{"names":["TMP"]}""")["layers"]![0]!["color"]!.GetValue<int>()); // rolled back, still 5
    }

    // ---------------------------------------------------------- set_properties
    [Fact]
    public void Set_properties_changes_layer_color_and_text_format_keeping_the_anchor()
    {
        var room = One("""{"text_contains":"거실"}""");
        var anchor = room["position"]!.DeepClone();
        var r = Run("set_properties", $$$"""{"targets":[{"handle":"{{{H(room)}}}","expect_fingerprint":"{{{F(room)}}}"}],"layer":"@실명","color":"#ff8000","height":250,"justify":"middle","width_factor":0.9}""");
        Assert.True(r["checks_passed"]!.GetValue<int>() >= 5);
        var after = One($$$"""{"handles":["{{{H(room)}}}"]}""");
        Assert.Equal("@실명", after["layer"]!.GetValue<string>());
        Assert.Equal("#ff8000", after["color"]!.GetValue<string>());
        Assert.Equal("middle", after["justify"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(anchor, after["alignment_point"]));
        Assert.Equal(0.9, after["width_factor"]!.GetValue<double>());

        var bylayer = Run("set_properties", $$$"""{"handles":["{{{H(room)}}}"],"color":"bylayer"}""");
        Assert.Null(One($$$"""{"handles":["{{{H(room)}}}"]}""")["color"]);
        Assert.NotNull(bylayer["changes"]);

        var wall = One("""{"layers":["A-WALL"],"types":["LINE"],"max_results":1}""");
        Assert.Equal(ErrorCodes.Unsupported, Fails("set_properties", $$$"""{"handles":["{{{H(wall)}}}"],"height":100}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("set_properties", $$$"""{"handles":["{{{H(wall)}}}"]}""").Code);
        Assert.Equal(ErrorCodes.NotFound, Fails("set_properties", $$$"""{"handles":["{{{H(wall)}}}"],"linetype":"NOPE"}""").Code);
        Run("set_properties", $$$"""{"handles":["{{{H(wall)}}}"],"linetype":"dashed","lineweight":"0.50mm"}""");
        var w2 = One($$$"""{"handles":["{{{H(wall)}}}"]}""");
        Assert.Equal("DASHED", w2["linetype"]!.GetValue<string>());
        Assert.Equal(0.5, w2["lineweight"]!.GetValue<double>());
    }

    // -------------------------------------------------------- copy / transform
    [Fact]
    public void Copy_makes_a_verified_linear_array()
    {
        var c = Created("""{"type":"circle","center":[0,0],"radius":10}""");
        var r = Run("copy", $$$"""{"targets":[{"handle":"{{{H(c)}}}","expect_fingerprint":"{{{F(c)}}}"}],"displacement":[100,0],"count":3}""");
        var xs = r["created"]!.AsArray().Select(e => e!["center"]![0]!.GetValue<double>()).ToArray();
        Assert.Equal([100.0, 200.0, 300.0], xs);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("copy", $$$"""{"handles":["{{{H(c)}}}"],"displacement":[0,0]}""").Code);
    }

    [Fact]
    public void Transform_rotates_scales_and_mirrors_with_readable_text()
    {
        var line = Created("""{"type":"line","start":[100,0],"end":[200,0]}""");
        Run("transform", $$$"""{"handles":["{{{H(line)}}}"],"op":"rotate","base":[0,0],"angle":90}""");
        var rotated = One($$$"""{"handles":["{{{H(line)}}}"]}""");
        Assert.Equal(100, rotated["start"]![1]!.GetValue<double>(), 6);
        Assert.Equal(0, rotated["start"]![0]!.GetValue<double>(), 6);

        var circle = Created("""{"type":"circle","center":[10,10],"radius":5}""");
        Run("transform", $$$"""{"handles":["{{{H(circle)}}}"],"op":"scale","base":[0,0],"factor":2}""");
        var scaled = One($$$"""{"handles":["{{{H(circle)}}}"]}""");
        Assert.Equal(10, scaled["radius"]!.GetValue<double>());
        Assert.Equal(20, scaled["center"]![0]!.GetValue<double>());

        var text = Created("""{"type":"text","text":"A","position":[10,5],"height":2}""");
        var m = Run("transform", $$$"""{"handles":["{{{H(text)}}}"],"op":"mirror","axis":[[0,0],[0,1]],"copy":true}""");
        var mirrored = m["created"]![0]!;
        Assert.Equal(-10, mirrored["position"]![0]!.GetValue<double>());
        Assert.Equal(0, mirrored["rotation"]!.GetValue<double>()); // still readable
        Assert.Equal(10, One($$$"""{"handles":["{{{H(text)}}}"]}""")["position"]![0]!.GetValue<double>()); // original kept

        var arc = Created("""{"type":"arc","center":[5,0],"radius":1,"start_angle":0,"end_angle":90}""");
        Run("transform", $$$"""{"handles":["{{{H(arc)}}}"],"op":"mirror","axis":[[0,0],[0,1]]}""");
        var a2 = One($$$"""{"handles":["{{{H(arc)}}}"]}""");
        Assert.Equal(-5, a2["center"]![0]!.GetValue<double>());
        Assert.Equal(90, a2["start_angle"]!.GetValue<double>());
        Assert.Equal(180, a2["end_angle"]!.GetValue<double>());

        Assert.Equal(ErrorCodes.InvalidParams, Fails("transform", $$$"""{"handles":["{{{H(arc)}}}"],"op":"shear","base":[0,0]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("transform", $$$"""{"handles":["{{{H(arc)}}}"],"op":"mirror","axis":[[0,0],[0,0]]}""").Code);
    }

    // ------------------------------------------------------------------ layers
    [Fact]
    public void Set_layer_creates_updates_and_guards_unlocking()
    {
        var r = Run("set_layer", """{"name":"CEN","color":"red","linetype":"CENTER","lineweight":0.18,"make_current":true}""");
        Assert.Equal("created", r["other_changes"]![0]!["action"]!.GetValue<string>());
        var cen = Run("layers", """{"names":["cen"]}""")["layers"]![0]!;
        Assert.Equal(1, cen["color"]!.GetValue<int>());
        Assert.Equal("CENTER", cen["linetype"]!.GetValue<string>());
        Assert.True(cen["current"]!.GetValue<bool>());
        Assert.Equal("CEN", Created("""{"type":"line","start":[0,0],"end":[1,0]}""")["layer"]!.GetValue<string>()); // new entities go to CLAYER

        Assert.Equal(ErrorCodes.LockedLayer, Fails("set_layer", """{"name":"A-ANNO-LOCKED","locked":false}""").Code);
        Run("set_layer", """{"name":"A-ANNO-LOCKED","locked":false,"user_confirmed_unlock":true}""");
        Assert.False(Run("layers", """{"names":["A-ANNO-LOCKED"]}""")["layers"]![0]!["locked"]!.GetValue<bool>());

        Assert.Equal(ErrorCodes.NotFound, Fails("set_layer", """{"name":"NOPE","create":false,"color":1}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("set_layer", """{"name":"X","color":"bylayer"}""").Code);
    }

    // ----------------------------------------------------- inspect/query/view
    [Fact]
    public void Inspect_query_grouping_and_view()
    {
        var info = Run("inspect");
        Assert.Contains("DOOR_SINGLE", info["blocks"]!.AsArray().Select(b => b!["name"]!.GetValue<string>()));
        Assert.True(Run("inspect", """{"sections":["dim_styles"]}""").ContainsKey("dim_styles"));
        Assert.False(Run("inspect", """{"sections":["dim_styles"]}""").ContainsKey("blocks"));

        var groups = Run("query", """{"group_by":"layer"}""");
        Assert.Equal(5, groups["groups"]!["A-WALL"]!.GetValue<int>());
        var compact = Run("query", """{"compact":true,"max_results":1}""")["entities"]![0]!.AsObject();
        Assert.False(compact.ContainsKey("props"));
        Assert.True(compact.ContainsKey("fingerprint"));

        var z = Run("zoom", """{"extents":true}""");
        Assert.True(z["zoomed"]!.GetValue<bool>());
        Assert.NotNull(_doc.LastView);
        var s = Run("snapshot", """{"window":[[0,0],[100,50]],"width":400}""");
        Assert.Equal("image/png", s["mime_type"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.InvalidParams, Fails("zoom", """{"extents":true,"window":[[0,0],[1,1]]}""").Code);
    }
}
