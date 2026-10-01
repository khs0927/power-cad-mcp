"""End-to-end tests: MCP client -> server -> headless (ezdxf) backend -> DXF on disk."""

from __future__ import annotations

import math
import os

import ezdxf
import pytest

pytestmark = pytest.mark.anyio


async def test_status_and_tool_list(call):
    status = await call("cad_status")
    assert status["backend"] == "dxf" and status["connected"] is True
    tools = {t.name for t in (await call.client.list_tools()).tools}
    assert {"draw_line", "draw_batch", "run_command", "export_drawing", "render_preview"} <= tools
    assert len(tools) >= 40


async def test_layers_lifecycle(call):
    layer = await call("create_layer", name="Walls", color="red", linetype="DASHED", make_current=True)
    assert layer == {
        "name": "Walls",
        "color": 1,
        "linetype": "DASHED",
        "on": True,
        "frozen": False,
        "locked": False,
        "current": True,
    }
    line = await call("draw_line", start=[0, 0], end=[10, 0])
    assert line["layer"] == "Walls"

    upd = await call("update_layer", name="Walls", on=False, locked=True, color=5)
    assert (upd["on"], upd["locked"], upd["color"]) == (False, True, 5)
    assert "current layer" in await call.error("update_layer", name="Walls", frozen=True)

    await call("create_layer", name="Tmp")
    await call("set_current_layer", name="0")
    renamed = await call("update_layer", name="Walls", new_name="Walls-Ext")
    assert renamed["name"] == "Walls-Ext"
    assert "in use" in await call.error("delete_layer", name="Walls-Ext")
    assert await call("delete_layer", name="Tmp") == {"deleted": "Tmp"}
    names = [la["name"] for la in await call("list_layers")]
    assert "Tmp" not in names and "Walls-Ext" in names
    assert "does not exist" in await call.error("set_current_layer", name="Nope")
    assert "Linetype" in await call.error("create_layer", name="X", linetype="NOPE_LT")


async def test_draw_primitives_geometry(call):
    line = await call("draw_line", start=[0, 0], end=[3, 4], color="green")
    assert line["type"] == "LINE" and line["length"] == 5 and line["color"] == 3

    rect = await call("draw_rectangle", corner1=[0, 0], corner2=[20, 10], layer="Frame")
    assert (
        rect["type"] == "LWPOLYLINE"
        and rect["closed"]
        and len(rect["points"]) == 4
        and rect["layer"] == "Frame"
    )

    poly3d = await call("draw_polyline", points=[[0, 0, 0], [1, 1, 5], [2, 0, 0]])
    assert poly3d["type"] == "POLYLINE" and poly3d["points"][1] == [1, 1, 5]

    hexagon = await call("draw_polygon", center=[0, 0], radius=10, sides=6)
    assert len(hexagon["points"]) == 6 and hexagon["points"][0] == [10, 0, 0]

    circle = await call("draw_circle", center=[5, 5], radius=2.5)
    assert circle["center"] == [5, 5, 0] and circle["radius"] == 2.5

    arc = await call("draw_arc", center=[0, 0], radius=4, start_angle=0, end_angle=90)
    assert (arc["start_angle"], arc["end_angle"]) == (0, 90)

    ell = await call("draw_ellipse", center=[0, 0], major_axis=[10, 0], ratio=0.5)
    assert ell["ratio"] == 0.5

    pt = await call("draw_point", location=[1, 2, 3])
    assert pt["location"] == [1, 2, 3]

    txt = await call("add_text", text="안녕 CAD", insert=[1, 1], height=3.5, rotation=45)
    assert txt["text"] == "안녕 CAD" and txt["height"] == 3.5 and txt["rotation"] == 45

    mt = await call("add_mtext", text="Line1\\PLine2", insert=[0, 30], width=40, height=2)
    assert mt["type"] == "MTEXT" and mt["width"] == 40

    dim = await call("add_dimension", p1=[0, 0], p2=[20, 0], location=[10, -5])
    assert dim["type"] == "DIMENSION" and math.isclose(dim["measurement"], 20)
    vdim = await call(
        "add_dimension", p1=[0, 0], p2=[20, 10], location=[30, 5], kind="vertical", text_height=1.5
    )
    assert math.isclose(vdim["measurement"], 10)

    hatch = await call("add_hatch", boundary=rect["handle"], pattern="ANSI31", scale=2)
    assert hatch["type"] == "HATCH" and hatch["pattern"] == "ANSI31"
    solid = await call("add_hatch", boundary=circle["handle"], pattern="SOLID", color="red")
    assert solid["solid"] is True
    assert "LWPOLYLINE or CIRCLE" in await call.error("add_hatch", boundary=line["handle"])

    listing = await call("list_entities")
    assert listing["total"] == 14


async def test_validation_errors(call):
    assert "differ" in await call.error("draw_rectangle", corner1=[0, 0], corner2=[0, 5])
    assert "color" in (await call.error("draw_line", start=[0, 0], end=[1, 1], color="chartreuse")).lower()
    err = await call.error("draw_circle", center=[0, 0], radius=-1)
    assert "radius" in err.lower()
    err = await call.error("draw_line", start=[0], end=[1, 1])
    assert "start" in err
    assert "No model-space entity" in await call.error("get_entity", handle="FFFFF")


async def test_draw_batch(call):
    res = await call(
        "draw_batch",
        operations=[
            {"op": "line", "start": [0, 0], "end": [10, 0], "layer": "A"},
            {"op": "circle", "center": [5, 5], "radius": 1},
            {"op": "nope"},
            {"op": "text", "text": "T", "insert": [0, 0], "bogus": 1},
            {"op": "rectangle", "corner1": [0, 0], "corner2": [4, 4], "color": 2},
        ],
    )
    assert res["created"] == 3 and res["failed"] == 2
    # a rejected item must not leave geometry behind (the bogus TEXT was never created)
    assert (await call("list_entities"))["total"] == 3
    assert (await call("list_entities", entity_type="TEXT"))["total"] == 0
    assert "Unknown op" in res["results"][2]["error"]
    assert "unexpected" in res["results"][3]["error"]
    stopped = await call(
        "draw_batch", operations=[{"op": "nope"}, {"op": "point", "location": [0, 0]}], stop_on_error=True
    )
    assert stopped["created"] == 0 and len(stopped["results"]) == 1


async def test_edit_operations(call):
    line = await call("draw_line", start=[0, 0], end=[10, 0])
    h = line["handle"]

    moved = await call("move_entities", handles=[h], displacement=[5, 5])
    assert moved[0]["start"] == [5, 5, 0] and moved[0]["end"] == [15, 5, 0]

    copies = await call("copy_entities", handles=[h], displacement=[0, 10])
    assert copies[0]["handle"] != h and copies[0]["start"] == [5, 15, 0]

    rotated = await call("rotate_entities", handles=[h], base_point=[5, 5], angle=90)
    assert rotated[0]["end"] == [5, 15, 0]

    scaled = await call("scale_entities", handles=[h], base_point=[5, 5], factor=2)
    assert scaled[0]["end"] == [5, 25, 0]

    mirrored = await call("mirror_entities", handles=[h], p1=[0, 0], p2=[0, 1])
    assert mirrored[0]["start"] == [-5, 5, 0] and mirrored[0]["handle"] != h
    assert (await call("get_entity", handle=h))["start"] == [5, 5, 0]

    circle = await call("draw_circle", center=[0, 0], radius=5)
    off = await call("offset_entity", handle=circle["handle"], distance=2)
    assert off[0]["radius"] == 7
    base = await call("draw_line", start=[0, 0], end=[10, 0])
    off_line = await call("offset_entity", handle=base["handle"], distance=3)
    assert off_line[0]["start"] == [0, 3, 0]
    rect = await call("draw_rectangle", corner1=[0, 0], corner2=[10, 10])
    off_rect = await call("offset_entity", handle=rect["handle"], distance=-1)
    assert len(off_rect[0]["points"]) == 4

    restyled = await call("set_entity_properties", handle=h, layer="Center", color="blue", linetype="CENTER")
    assert (restyled["layer"], restyled["color"], restyled["linetype"]) == ("Center", 5, "CENTER")
    assert "at least one" in await call.error("set_entity_properties", handle=h)

    deleted = await call("delete_entities", handles=[h, copies[0]["handle"]])
    assert deleted == {"deleted": 2}
    assert "No model-space entity" in await call.error("get_entity", handle=h)


async def test_list_filters(call):
    await call("draw_line", start=[0, 0], end=[1, 0], layer="L1")
    await call("draw_rectangle", corner1=[0, 0], corner2=[1, 1], layer="L2")
    await call("draw_polyline", points=[[0, 0, 0], [1, 1, 1]], layer="L2")
    await call("draw_circle", center=[0, 0], radius=1, layer="L2")
    assert (await call("list_entities", layer="L2"))["total"] == 3
    assert (await call("list_entities", entity_type="polyline"))["total"] == 2
    assert (await call("list_entities", entity_type="AcDbLine"))["total"] == 1
    limited = await call("list_entities", limit=2)
    assert limited["total"] == 4 and limited["returned"] == 2


async def test_blocks(call):
    a = await call("draw_circle", center=[0, 0], radius=1)
    b = await call("draw_line", start=[-1, 0], end=[1, 0])
    blk = await call(
        "create_block", name="BOLT", base_point=[0, 0], handles=[a["handle"], b["handle"]], delete_source=True
    )
    assert blk["entity_count"] == 2
    assert (await call("list_entities"))["total"] == 0
    assert any(x["name"] == "BOLT" for x in await call("list_blocks"))
    ref = await call("insert_block", name="BOLT", insert=[10, 10], scale=2, rotation=30, layer="Parts")
    assert (ref["type"], ref["name"], ref["scale"], ref["rotation"], ref["layer"]) == (
        "INSERT",
        "BOLT",
        2,
        30,
        "Parts",
    )
    assert "already exists" in await call.error(
        "create_block", name="BOLT", base_point=[0, 0], handles=[ref["handle"]]
    )
    assert "not defined" in await call.error("insert_block", name="MISSING", insert=[0, 0])


async def test_save_open_roundtrip(call, tmp_path):
    await call("draw_line", start=[0, 0], end=[10, 10], layer="Keep")
    assert "never been saved" in await call.error("save_drawing")
    saved = await call("save_drawing", path="out/plan")  # relative -> workspace, extension added
    assert saved["path"] == str(tmp_path / "out" / "plan.dxf")
    doc = ezdxf.readfile(saved["path"])
    assert [e.dxftype() for e in doc.modelspace()] == ["LINE"]
    assert doc.modelspace()[0].dxf.layer == "Keep"

    await call("new_drawing")
    assert (await call("get_drawing_info"))["entity_count"] == 0
    opened = await call("open_drawing", path=saved["path"])
    assert opened["entity_count"] == 1 and opened["name"] == "plan.dxf"
    await call("draw_circle", center=[0, 0], radius=1)
    await call("save_drawing")
    assert len(ezdxf.readfile(saved["path"]).modelspace()) == 2
    assert "not found" in await call.error("open_drawing", path="missing.dxf")


async def test_zoom_and_extents(call):
    await call("draw_rectangle", corner1=[-10, -5], corner2=[30, 20])
    z = await call("zoom_extents")
    assert z["extents"] == {"min": [-10, -5, 0], "max": [30, 20, 0]}
    assert (await call("zoom_window", p1=[0, 0], p2=[10, 10]))["ok"]


async def test_export_and_preview(call, tmp_path):
    await call("draw_rectangle", corner1=[0, 0], corner2=[100, 50])
    await call("add_text", text="PREVIEW", insert=[10, 20], height=8)
    for fmt in ("dxf", "png", "svg", "pdf"):
        res = await call("export_drawing", path=f"exp/drawing.{fmt}")
        assert os.path.getsize(res["path"]) > 0, fmt
    assert (tmp_path / "exp" / "drawing.png").read_bytes()[:4] == b"\x89PNG"
    assert "make them match" in await call.error("export_drawing", path="exp/x.pdf", format="png")
    result = await call.raw("render_preview")
    assert not result.is_error
    img = result.content[0]
    assert img.type == "image" and img.mime_type == "image/png" and len(img.data) > 100


async def test_run_command_headless_and_blocked(call):
    assert "AutoCAD (COM) backend" in await call.error("run_command", command="LINE 0,0 1,1 ")
    assert "blocked" in await call.error("run_command", command="_.SHELL dir")
    assert "AutoLISP" in await call.error("run_command", command='(command "LINE")')


async def test_drawing_census_tool(call, tmp_path):
    doc = ezdxf.new()
    doc.modelspace().add_line((0, 0), (1, 1), dxfattribs={"layer": "수상한레이어"})
    src = tmp_path / "plan.dxf"
    doc.saveas(src)
    std = os.path.join(os.path.dirname(__file__), "..", "docs", "standards", "floor_plan_standard.json")
    r = await call("drawing_census", path=str(src), standard=std)
    assert r["completeness"]["complete"] and r["unmapped_layers"] == ["수상한레이어"]
    assert os.path.exists(r["report"]) and r["report"].startswith(str(tmp_path / "plan_census"))
