using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Simulation;
using Xunit;

namespace PowerCad.Tests;

public sealed class DispatcherTests
{
    private readonly InMemoryCadDocument _doc = InMemoryCadDocument.CreateSample();
    private readonly CommandDispatcher _cad;

    public DispatcherTests() => _cad = new CommandDispatcher(_doc);

    private JsonObject Run(string command, string json = "{}") => (JsonObject)_cad.Execute(command, JsonNode.Parse(json) as JsonObject);

    private CadException Fails(string command, string json)
    {
        var commits = _doc.CommitCount;
        var e = Assert.Throws<CadException>(() => Run(command, json));
        Assert.Equal(commits, _doc.CommitCount); // a failed command never commits
        return e;
    }

    private JsonObject One(string json) => Run("query", json)["entities"]!.AsArray().Single()!.AsObject();

    private static string H(JsonObject e) => e["handle"]!.GetValue<string>();

    private static string F(JsonObject e) => e["fingerprint"]!.GetValue<string>();

    // ------------------------------------------------------------------ query
    [Fact]
    public void Status_lists_commands()
    {
        var s = Run("status");
        Assert.Equal("simulator", s["backend"]!.GetValue<string>());
        Assert.Contains("modify_opening", s["commands"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Query_filters_and_caps()
    {
        Assert.Equal(5, Run("query", """{"layers":["a-wall"]}""")["total"]!.GetValue<int>());
        Assert.Equal("거실", One("""{"text_contains":"거실"}""")["text"]!.GetValue<string>());
        Assert.Equal("D1", One("""{"types":["door"],"block_name":"DOOR*"}""")["attributes"]!["DOOR_NO"]!.GetValue<string>());
        Assert.Equal(1, Run("query", """{"text_contains":"d1"}""")["total"]!.GetValue<int>()); // attribute text is searchable
        var capped = Run("query", """{"max_results":2}""");
        Assert.True(capped["truncated"]!.GetValue<bool>());
        Assert.Equal(2, capped["returned"]!.GetValue<int>());
        Assert.Equal(2, Run("query", """{"within":[[0,3000],[9000,5000]],"types":["TEXT"]}""")["total"]!.GetValue<int>());
        Assert.Equal(ErrorCodes.InvalidParams, Fails("query", """{"layer":"A-WALL"}""").Code); // typo is rejected, not ignored
    }

    [Fact]
    public void Fingerprint_is_stable_and_changes_with_geometry()
    {
        var a = One("""{"text_contains":"거실"}""");
        var b = One("""{"text_contains":"거실"}""");
        Assert.Equal(F(a), F(b));
        Run("move", $$$"""{"handles":["{{{H(a)}}}"],"displacement":[1,0]}""");
        Assert.NotEqual(F(a), F(One("""{"text_contains":"거실"}""")));
    }

    // ------------------------------------------------------------ text change
    [Fact]
    public void Replace_text_by_target_verifies_and_reports_diff()
    {
        var room = One("""{"text_contains":"침실"}""");
        var r = Run("replace_text", $$$"""{"targets":[{"handle":"{{{H(room)}}}","expect_text":"침실 1","expect_fingerprint":"{{{F(room)}}}"}],"new_text":"안방"}""");
        Assert.True(r["committed"]!.GetValue<bool>());
        var change = r["changes"]!.AsArray().Single()!;
        Assert.Equal("침실 1", change["changed"]!["text"]!["before"]!.GetValue<string>());
        Assert.Equal("안방", change["changed"]!["text"]!["after"]!.GetValue<string>());
        Assert.Equal(1, r["checks_passed"]!.GetValue<int>());
        Assert.Equal("안방", One($$$"""{"handles":["{{{H(room)}}}"]}""")["text"]!.GetValue<string>());
    }

    [Fact]
    public void Replace_text_refuses_stale_expectations()
    {
        var room = One("""{"text_contains":"침실"}""");
        Assert.Equal(ErrorCodes.StaleTarget, Fails("replace_text", $$$"""{"targets":[{"handle":"{{{H(room)}}}","expect_text":"침실 2"}],"new_text":"x"}""").Code);
        Assert.Equal(ErrorCodes.StaleTarget, Fails("replace_text", $$$"""{"targets":[{"handle":"{{{H(room)}}}","expect_fingerprint":"0000000000000000"}],"new_text":"x"}""").Code);
        Assert.Equal(ErrorCodes.Unsupported, Fails("replace_text", $$$"""{"targets":[{"handle":"{{{H(One("""{"types":["LINE"],"max_results":1}"""))}}}"}],"new_text":"x"}""").Code);
    }

    [Fact]
    public void Find_replace_is_bounded_and_respects_locked_layers()
    {
        var r = Run("replace_text", """{"find":"KITCHEN","replace":"DINING"}""");
        Assert.Equal("주방 DINING", r["changes"]!.AsArray().Single()!["changed"]!["text"]!["after"]!.GetValue<string>());

        Assert.Equal(ErrorCodes.TooManyMatches, Fails("replace_text", """{"find":"실","replace":"室","max_changes":1,"layers":["A-ANNO"]}""").Code);
        Assert.Equal(ErrorCodes.NotFound, Fails("replace_text", """{"find":"없는문자"}""").Code);
        Assert.Equal(ErrorCodes.LockedLayer, Fails("replace_text", """{"find":"도면번호","replace":"DWG"}""").Code);
    }

    [Fact]
    public void Dry_run_reports_without_changing_the_drawing()
    {
        var commits = _doc.CommitCount;
        var r = Run("replace_text", """{"find":"거실","replace":"LIVING","dry_run":true}""");
        Assert.True(r["dry_run"]!.GetValue<bool>());
        Assert.False(r["committed"]!.GetValue<bool>());
        Assert.Single(r["changes"]!.AsArray());
        Assert.Equal(commits, _doc.CommitCount);
        Assert.Equal(1, Run("query", """{"text_contains":"거실"}""")["total"]!.GetValue<int>());
    }

    [Fact]
    public void Failed_postcondition_rolls_back_everything()
    {
        _doc.TextFilter = t => t.Replace("😀", "?"); // CAD font cannot store the emoji
        var e = Fails("replace_text", """{"find":"거실","replace":"거실😀"}""");
        Assert.Equal(ErrorCodes.VerifyFailed, e.Code);
        Assert.Contains("rolled back", e.Message);
        Assert.Equal("거실", One("""{"text_contains":"거실"}""")["text"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ move
    [Fact]
    public void Move_verifies_reference_points()
    {
        var wall = One("""{"types":["LINE"],"within":[[5999,-1],[6001,1]]}""");
        var text = One("""{"text_contains":"거실"}""");
        var r = Run("move", $$$"""{"targets":[{"handle":"{{{H(wall)}}}","expect_fingerprint":"{{{F(wall)}}}"},{"handle":"{{{H(text)}}}"}],"from":[0,0],"to":[100,-50]}""");
        Assert.Equal(2, r["changes"]!.AsArray().Count);
        Assert.Equal(2, r["checks_passed"]!.GetValue<int>());
        var moved = One($$$"""{"handles":["{{{H(wall)}}}"]}""");
        Assert.Equal(6100, moved["start"]![0]!.GetValue<double>());
        Assert.Equal(7950, moved["end"]![1]!.GetValue<double>());

        Assert.Equal(ErrorCodes.StaleTarget, Fails("move", $$$"""{"targets":[{"handle":"{{{H(wall)}}}","expect_fingerprint":"{{{F(wall)}}}"}],"displacement":[1,1]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("move", $$$"""{"handles":["{{{H(wall)}}}"],"displacement":[0,0]}""").Code);
        Assert.Equal(ErrorCodes.NotFound, Fails("move", """{"handles":["FFFF"],"displacement":[1,0]}""").Code);
        var locked = One("""{"layers":["A-ANNO-LOCKED"]}""");
        Assert.Equal(ErrorCodes.LockedLayer, Fails("move", $$$"""{"handles":["{{{H(locked)}}}"],"displacement":[1,0]}""").Code);
    }

    // ------------------------------------------------------ doors / openings
    [Fact]
    public void Door_width_uses_the_dynamic_parameter()
    {
        var door = One("""{"block_name":"DOOR_SINGLE"}""");
        Assert.Equal(900, door["width"]!.GetValue<double>());
        var r = Run("modify_opening", $$$"""{"handle":"{{{H(door)}}}","expect_fingerprint":"{{{F(door)}}}","width":1000,"attributes":{"door_no":"D1-A"}}""");
        var changed = r["changes"]!.AsArray().Single()!["changed"]!.AsObject();
        Assert.Equal(1000, changed["width"]!["after"]!.GetValue<double>());
        Assert.True(changed.ContainsKey("dynamic"));
        Assert.True(changed.ContainsKey("attributes"));
        Assert.False(changed.ContainsKey("scale")); // dynamic width, not scaling
    }

    [Fact]
    public void Plain_window_width_falls_back_to_scale()
    {
        var window = One("""{"block_name":"WINDOW_1200"}""");
        var r = Run("modify_opening", $$$"""{"handle":"{{{H(window)}}}","width":900}""");
        var after = One($$$"""{"handles":["{{{H(window)}}}"]}""");
        Assert.Equal(0.75, after["scale"]![0]!.GetValue<double>());
        Assert.Equal(900, after["width"]!.GetValue<double>());
        Assert.Equal(1, r["checks_passed"]!.GetValue<int>());
    }

    [Fact]
    public void Door_slides_along_its_wall_and_flips()
    {
        var door = One("""{"block_name":"DOOR_SINGLE"}"""); // at (6000,1000), rotation 90 => wall runs along +Y
        Run("modify_opening", $$$"""{"handle":"{{{H(door)}}}","slide":500,"flip_hand":true}""");
        var after = One($$$"""{"handles":["{{{H(door)}}}"]}""");
        Assert.Equal(6000, after["position"]![0]!.GetValue<double>(), 6);
        Assert.Equal(1500, after["position"]![1]!.GetValue<double>(), 6);
        Assert.Equal(-1, after["scale"]![0]!.GetValue<double>());
        Assert.Equal(900, after["width"]!.GetValue<double>()); // flipping keeps the width

        Run("modify_opening", $$$"""{"handle":"{{{H(door)}}}","rotation":-90}""");
        Assert.Equal(270, One($$$"""{"handles":["{{{H(door)}}}"]}""")["rotation"]!.GetValue<double>());
    }

    [Fact]
    public void Opening_errors_are_explicit()
    {
        var door = One("""{"block_name":"DOOR_SINGLE"}""");
        var line = One("""{"types":["LINE"],"max_results":1}""");
        Assert.Equal(ErrorCodes.Unsupported, Fails("modify_opening", $$$"""{"handle":"{{{H(line)}}}","width":1}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("modify_opening", $$$"""{"handle":"{{{H(door)}}}"}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("modify_opening", $$$"""{"handle":"{{{H(door)}}}","attributes":{"NOPE":"x"}}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("modify_opening", $$$"""{"handle":"{{{H(door)}}}","slide":1,"position":[0,0]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("modify_opening", $$$"""{"handle":"{{{H(door)}}}","width":-5}""").Code);
    }

    // -------------------------------------------------------- create / batch
    [Fact]
    public void Create_verifies_every_new_entity()
    {
        var r = Run("create", """
            {"entities":[
              {"type":"line","start":[0,0],"end":[10,0],"layer":"NEW"},
              {"type":"polyline","points":[[0,0],[5,0],[5,5]],"closed":true},
              {"type":"circle","center":[1,1],"radius":2},
              {"type":"arc","center":[0,0],"radius":3,"start_angle":0,"end_angle":90},
              {"type":"mtext","text":"메모","position":[0,20],"width":100},
              {"type":"insert","name":"DOOR_SINGLE","position":[100,0],"rotation":0}
            ]}
            """);
        var created = r["created"]!.AsArray();
        Assert.Equal(6, created.Count);
        Assert.Equal(6, r["checks_passed"]!.GetValue<int>());
        Assert.Equal("NEW", created[0]!["layer"]!.GetValue<string>());
        Assert.Equal("D1", created[5]!["attributes"]!["DOOR_NO"]!.GetValue<string>());

        Assert.Equal(ErrorCodes.NotFound, Fails("create", """{"entities":[{"type":"insert","name":"NOPE","position":[0,0]}]}""").Code);
        Assert.Equal(ErrorCodes.LockedLayer, Fails("create", """{"entities":[{"type":"text","text":"x","position":[0,0],"layer":"A-ANNO-LOCKED"}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"line","start":[0,0],"end":[0,0]}]}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("create", """{"entities":[{"type":"spline"}]}""").Code);
    }

    [Fact]
    public void Batch_is_all_or_nothing()
    {
        var door = One("""{"block_name":"DOOR_SINGLE"}""");
        var e = Fails("batch", $$$"""
            {"steps":[
              {"command":"replace_text","params":{"find":"거실","replace":"LIVING"}},
              {"command":"modify_opening","params":{"handle":"{{{H(door)}}}","width":1000}},
              {"command":"move","params":{"handles":["FFFF"],"displacement":[1,0]}}
            ]}
            """);
        Assert.Equal(ErrorCodes.NotFound, e.Code);
        Assert.Contains("steps[2]", e.Message);
        Assert.Equal(1, Run("query", """{"text_contains":"거실"}""")["total"]!.GetValue<int>());
        Assert.Equal(900, One("""{"block_name":"DOOR_SINGLE"}""")["width"]!.GetValue<double>());

        var ok = Run("batch", $$$"""
            {"steps":[
              {"command":"replace_text","params":{"find":"거실","replace":"LIVING"}},
              {"command":"modify_opening","params":{"handle":"{{{H(door)}}}","width":1000}}
            ]}
            """);
        Assert.Equal(2, ok["changes"]!.AsArray().Count);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("batch", """{"steps":[{"command":"query","params":{}}]}""").Code);
    }

    [Fact]
    public void Read_only_mode_refuses_edits()
    {
        var cad = new CommandDispatcher(_doc, new DispatcherOptions { ReadOnly = true });
        Assert.Equal(ErrorCodes.ReadOnly, Assert.Throws<CadException>(() => cad.Execute("replace_text", JsonNode.Parse("""{"find":"a"}""") as JsonObject)).Code);
        Assert.NotNull(cad.Execute("query", null));
        Assert.Equal(ErrorCodes.UnknownCommand, Assert.Throws<CadException>(() => cad.Execute("erase_all", null)).Code);
    }
}
