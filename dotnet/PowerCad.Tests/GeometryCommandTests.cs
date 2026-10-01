using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Model;
using PowerCad.Core.Simulation;
using Xunit;

namespace PowerCad.Tests;

/// <summary>measure / offset / save.</summary>
public sealed class GeometryCommandTests
{
    private readonly InMemoryCadDocument _doc = InMemoryCadDocument.CreateSample();
    private readonly CommandDispatcher _cad;

    public GeometryCommandTests() => _cad = new CommandDispatcher(_doc);

    private JsonObject Run(string command, string json = "{}") => (JsonObject)_cad.Execute(command, JsonNode.Parse(json) as JsonObject);

    private CadException Fails(string command, string json)
    {
        var commits = _doc.CommitCount;
        var e = Assert.Throws<CadException>(() => Run(command, json));
        Assert.Equal(commits, _doc.CommitCount);
        return e;
    }

    private JsonObject Created(string entity) => Run("create", $$"""{"entities":[{{entity}}]}""")["created"]![0]!.AsObject();

    private static string H(JsonNode e) => e["handle"]!.GetValue<string>();

    private static double D(JsonNode? n) => n!.GetValue<double>();

    // ---------------------------------------------------------------- measure
    [Fact]
    public void Measures_lengths_areas_and_paths()
    {
        var room = Created("""{"type":"polyline","points":[[0,0],[3900,0],[3900,4200],[0,4200]],"closed":true}""");
        var line = Created("""{"type":"line","start":[0,0],"end":[3000,4000]}""");
        var circle = Created("""{"type":"circle","center":[0,0],"radius":100}""");
        var text = Created("""{"type":"text","text":"x","position":[0,0]}""");

        var m = Run("measure", $$"""{"handles":["{{H(room)}}","{{H(line)}}","{{H(circle)}}","{{H(text)}}"],"points":[[0,0],[3,4],[3,10]]}""");
        var byHandle = m["entities"]!.AsArray().ToDictionary(e => H(e!), e => e!);
        Assert.Equal(16380000, D(byHandle[H(room)]["area"]));
        Assert.Equal(16200, D(byHandle[H(room)]["length"]));
        Assert.Equal(1950, D(byHandle[H(room)]["centroid"]![0]));
        Assert.Equal(5000, D(byHandle[H(line)]["length"]));
        Assert.Equal(Math.Round(Math.PI * 10000, 6), D(byHandle[H(circle)]["area"]), 3);
        Assert.Single(m["skipped"]!.AsArray()); // the text has no length or area
        Assert.Equal(11, D(m["path"]!["length"]));
        Assert.Equal(ErrorCodes.InvalidParams, Fails("measure", "{}").Code);
        Assert.Equal(ErrorCodes.NotFound, Fails("measure", """{"handles":["FFFFFF"]}""").Code);
    }

    [Fact]
    public void Arc_segments_count_in_length_and_area()
    {
        // half disc of radius 1: chord along the X axis, bulge 1 on the first segment (CCW arc over the top)
        var e = new EntityState("1", EntityTypes.Polyline, "0", new JsonObject
        {
            ["points"] = new JsonArray(new JsonArray(1, 0), new JsonArray(-1, 0)),
            ["closed"] = true,
            ["bulges"] = new JsonArray(1, 0),
        });
        var (pts, closed, bulges) = Geometry2D.Polyline(e);
        Assert.Equal(Math.PI / 2, Geometry2D.SignedArea(pts, bulges), 9);
        Assert.Equal(Math.PI + 2, Geometry2D.PolylineLength(pts, closed, bulges), 9);
    }

    // ----------------------------------------------------------------- offset
    [Fact]
    public void Offsets_lines_circles_and_closed_polylines()
    {
        var line = Created("""{"type":"line","layer":"A-WALL","start":[0,0],"end":[1000,0],"color":3}""");
        var r = Run("offset", $$"""{"targets":[{"handle":"{{H(line)}}","expect_fingerprint":"{{line["fingerprint"]}}"}],"distance":100,"side":"left","count":2}""");
        var made = r["created"]!.AsArray();
        Assert.Equal(2, made.Count);
        Assert.Equal(100, D(made[0]!["start"]![1]));
        Assert.Equal(200, D(made[1]!["start"]![1]));
        Assert.Equal("A-WALL", made[0]!["layer"]!.GetValue<string>());
        Assert.Equal(3, made[0]!["color"]!.GetValue<int>());

        var below = Run("offset", $$"""{"targets":[{"handle":"{{H(line)}}"}],"distance":50,"through":[500,-10]}""")["created"]![0]!;
        Assert.Equal(-50, D(below["start"]![1]));

        var circle = Created("""{"type":"circle","center":[0,0],"radius":100}""");
        Assert.Equal(80, D(Run("offset", $$"""{"handles":["{{H(circle)}}"],"distance":20,"side":"inside"}""")["created"]![0]!["radius"]));

        // clockwise square: "outside" must still grow it
        var square = Created("""{"type":"polyline","points":[[0,0],[0,100],[100,100],[100,0]],"closed":true}""");
        var outer = Run("offset", $$"""{"handles":["{{H(square)}}"],"distance":10,"side":"outside"}""")["created"]![0]!;
        var pts = outer["points"]!.AsArray().Select(p => (D(p![0]), D(p[1]))).ToList();
        Assert.Contains((-10.0, -10.0), pts);
        Assert.Contains((110.0, 110.0), pts);

        var inner = Run("offset", $$"""{"handles":["{{H(square)}}"],"distance":10,"through":[50,50]}""")["created"]![0]!;
        Assert.Contains((10.0, 10.0), inner["points"]!.AsArray().Select(p => (D(p![0]), D(p[1]))));
    }

    [Fact]
    public void Offset_refuses_bad_requests_and_changes_nothing()
    {
        var line = Created("""{"type":"line","start":[0,0],"end":[1000,0]}""");
        var square = Created("""{"type":"polyline","points":[[0,0],[100,0],[100,100],[0,100]],"closed":true}""");
        var text = Created("""{"type":"text","text":"x","position":[0,0]}""");
        Assert.Equal(ErrorCodes.InvalidParams, Fails("offset", $$"""{"handles":["{{H(line)}}"],"distance":10}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("offset", $$"""{"handles":["{{H(line)}}"],"distance":10,"side":"inside"}""").Code);
        Assert.Equal(ErrorCodes.InvalidParams, Fails("offset", $$"""{"handles":["{{H(square)}}"],"distance":60,"side":"inside"}""").Code);
        Assert.Equal(ErrorCodes.Unsupported, Fails("offset", $$"""{"handles":["{{H(text)}}"],"distance":10,"side":"left"}""").Code);
        Assert.Equal(ErrorCodes.StaleTarget, Fails("offset", $$"""{"targets":[{"handle":"{{H(line)}}","expect_fingerprint":"0000"}],"distance":10,"side":"left"}""").Code);

        // inside a batch, a failing later step also rolls back the offset
        Assert.Throws<CadException>(() => Run("batch", $$$"""{"steps":[{"command":"offset","params":{"handles":["{{{H(line)}}}"],"distance":10,"side":"left"}},{"command":"delete","params":{"handles":["FFFFFF"]}}]}"""));
        Assert.Equal(3, Run("query", """{"within":[[-5000,-5000],[5000,5000]],"types":["LINE","LWPOLYLINE","TEXT"],"within_mode":"overlap"}""")["entities"]!.AsArray()
            .Count(e => e!["handle"]!.GetValue<string>() is var h && (h == H(line) || h == H(square) || h == H(text))));
    }

    // ------------------------------------------------------------------- save
    [Fact]
    public void Saves_copies_and_guards_overwrite_and_in_place_saves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "powercad-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copy = Path.Combine(dir, "copy.dxf");
            var r = Run("save", $$"""{"path":{{JsonValue.Create(copy)!.ToJsonString()}}}""");
            Assert.Equal("dxf", r["format"]!.GetValue<string>());
            Assert.True(File.Exists(copy));
            Assert.Equal("Sample-Plan.dwg", _doc.Name); // a copy leaves the open drawing alone

            Assert.Equal(ErrorCodes.InvalidParams, Fails("save", $$"""{"path":{{JsonValue.Create(copy)!.ToJsonString()}}}""").Code);
            Run("save", $$"""{"path":{{JsonValue.Create(copy)!.ToJsonString()}},"overwrite":true}""");

            Assert.Equal(ErrorCodes.InvalidParams, Fails("save", """{"path":"relative.dwg"}""").Code);
            Assert.Equal(ErrorCodes.InvalidParams, Fails("save", $$"""{"path":{{JsonValue.Create(Path.Combine(dir, "a.dwg"))!.ToJsonString()}},"format":"dxf"}""").Code);

            var own = Path.Combine(dir, "plan.dwg");
            Assert.Equal(ErrorCodes.InvalidParams, Fails("save", $$"""{"mode":"save","path":{{JsonValue.Create(own)!.ToJsonString()}}}""").Code);
            Run("save", $$"""{"mode":"save","path":{{JsonValue.Create(own)!.ToJsonString()}},"user_confirmed":true}""");
            Assert.Equal(own, _doc.Name);

            var readOnly = new CommandDispatcher(_doc, new DispatcherOptions { ReadOnly = true });
            Assert.Equal(ErrorCodes.ReadOnly, Assert.Throws<CadException>(() =>
                readOnly.Execute("save", new JsonObject { ["mode"] = "save", ["user_confirmed"] = true })).Code);
            readOnly.Execute("save", new JsonObject { ["path"] = Path.Combine(dir, "ro-copy.dwg") }); // copies stay allowed
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
