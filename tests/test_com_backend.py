"""COM backend logic against a fake AutoCAD object model (runs on any OS)."""

from __future__ import annotations

import math
import threading

import pytest
from conftest import ToolCaller
from fake_acad import Application, FakeClient
from mcp import Client

from power_cad_mcp.backends.com_backend import ComBackend, dxf_type
from power_cad_mcp.config import Settings
from power_cad_mcp.errors import CadError
from power_cad_mcp.server import create_server

P = {"layer": None, "color": None, "linetype": None}


@pytest.fixture
def fake() -> FakeClient:
    return FakeClient(Application())


@pytest.fixture
def com(fake):
    backend = ComBackend(client=fake, retries=3)
    yield backend
    backend.close()


@pytest.fixture
async def ccall(com, tmp_path):
    server = create_server(com, Settings(backend="autocad", workspace=str(tmp_path)))
    async with Client(server) as client:
        yield ToolCaller(client)


def test_status_and_single_sta_thread(com, fake):
    status = com.status()
    assert status["connected"] and status["application"].startswith("AutoCAD 26.0")
    assert fake.progids_tried[0] == "AutoCAD.Application"
    com.add_line((0, 0, 0), (1, 0, 0), P)
    # every COM access happened on the one initialised worker thread, never on the caller's thread
    assert fake.app.threads == fake.initialized_threads
    assert threading.get_ident() not in fake.app.threads


def test_not_running_reports_cleanly():
    backend = ComBackend(client=FakeClient(running=False))
    try:
        status = backend.status()
        assert status["connected"] is False and "Could not attach" in status["error"]
        with pytest.raises(CadError, match="Could not attach"):
            backend.add_line((0, 0, 0), (1, 1, 0), P)
    finally:
        backend.close()


def test_launch_when_allowed():
    client = FakeClient(running=False)
    backend = ComBackend(client=client, launch=True)
    try:
        assert backend.status()["connected"] is True
    finally:
        backend.close()


def test_read_only_calls_retry_when_busy(com, fake):
    fake.app.busy_failures = 2
    assert com.list_entities()["total"] == 0


def test_mutations_are_not_replayed_when_busy(com, fake):
    fake.app.busy_failures = 1
    with pytest.raises(CadError, match="busy"):
        com.add_circle((0, 0, 0), 1.0, P)
    assert fake.app.ActiveDocument.ModelSpace.Count == 0  # nothing half-created or duplicated


def test_com_errors_become_cad_errors(com):
    with pytest.raises(CadError, match="No entity with handle"):
        com.get_entity("DEAD")
    with pytest.raises(CadError, match="does not exist"):
        com.set_current_layer("Nope")


def test_angles_are_converted_to_radians(com, fake):
    arc = com.add_arc((0, 0, 0), 5, 30, 120, P)
    raw = fake.app.ActiveDocument.HandleToObject(arc["handle"])
    assert math.isclose(raw.StartAngle, math.radians(30)) and math.isclose(raw.EndAngle, math.radians(120))
    assert (arc["start_angle"], arc["end_angle"]) == (30, 120)
    text = com.add_text("A", (0, 0, 0), 2.0, 90, P)
    assert text["rotation"] == 90


def test_polylines(com):
    flat = com.add_polyline([(0, 0, 2), (10, 0, 2), (10, 5, 2)], True, P)
    assert flat["type"] == "LWPOLYLINE" and flat["closed"] and flat["points"][1] == [10, 0, 2]
    spatial = com.add_polyline([(0, 0, 0), (1, 1, 1)], False, P)
    assert spatial["type"] == "POLYLINE" and spatial["points"][1] == [1, 1, 1]


def test_layer_and_linetype_handling(com, fake):
    com.create_layer("Walls", 1, "DASHED", 50)
    com.set_current_layer("Walls")
    line = com.add_line((0, 0, 0), (5, 0, 0), P)
    assert line["layer"] == "Walls"
    styled = com.set_entity_properties(line["handle"], {"layer": "Axis", "color": 4, "linetype": "CENTER"})
    assert (styled["layer"], styled["color"], styled["linetype"]) == ("Axis", 4, "CENTER")
    with pytest.raises(CadError, match="Linetype"):
        com.create_layer("X", None, "NOT_A_LINETYPE")
    with pytest.raises(CadError, match="current layer"):
        com.update_layer("Walls", frozen=True)
    with pytest.raises(CadError, match="in use"):
        com.set_current_layer("0") and com.delete_layer("Axis")
    names = {la["name"]: la for la in com.list_layers()}
    assert names["Walls"]["color"] == 1 and names["0"]["current"]


def test_selection_and_edits(com):
    line = com.add_line((0, 0, 0), (10, 0, 0), P)
    h = line["handle"]
    assert com.move_entities([h], (1, 2, 0))[0]["start"] == [1, 2, 0]
    copy = com.copy_entities([h], (0, 5, 0))[0]
    assert copy["handle"] != h and copy["start"] == [1, 7, 0]
    assert com.rotate_entities([h], (1, 2, 0), 90)[0]["end"] == [1, 12, 0]
    assert com.scale_entities([h], (1, 2, 0), 0.5)[0]["end"] == [1, 7, 0]
    mirrored = com.mirror_entities([h], (0, 0, 0), (0, 1, 0), True)[0]
    assert mirrored["start"] == [-1, 2, 0]
    with pytest.raises(CadError):
        com.get_entity(h)  # source deleted
    circle = com.add_circle((0, 0, 0), 3, P)
    assert com.offset_entity(circle["handle"], 2)[0]["radius"] == 5
    assert com.delete_entities([copy["handle"], circle["handle"]]) == 2


def test_dimensions_hatch_blocks(com, tmp_path):
    aligned = com.add_aligned_dimension((0, 0, 0), (3, 4, 0), (0, 5, 0), P)
    assert aligned["type"] == "DIMENSION" and aligned["measurement"] == 5
    vertical = com.add_linear_dimension((0, 0, 0), (3, 4, 0), (5, 0, 0), 90, {**P, "text_height": 0.5})
    assert math.isclose(vertical["measurement"], 4)
    raw = com.doc.HandleToObject(vertical["handle"])
    assert raw.TextHeight == 0.5 and raw.ArrowheadSize == 0.5

    rect = com.add_rectangle((0, 0, 0), (4, 4, 0), P)
    hatch = com.add_hatch(rect["handle"], "ansi31", 2, 45, {"layer": "Hatch", "color": 8, "linetype": None})
    assert hatch["pattern"] == "ANSI31" and hatch["layer"] == "Hatch"
    open_pl = com.add_polyline([(0, 0, 0), (1, 0, 0), (1, 1, 0)], False, P)
    with pytest.raises(CadError, match="boundary must be closed"):
        com.add_hatch(open_pl["handle"], "ANSI31", 1, 0, P)

    blk = com.create_block("DOOR", (0, 0, 0), [rect["handle"]], False)
    assert blk["entity_count"] == 1
    assert [b["name"] for b in com.list_blocks()] == ["DOOR"]
    ref = com.insert_block("DOOR", (10, 0, 0), 2, 90, P)
    assert (ref["type"], ref["name"], ref["scale"], ref["rotation"]) == ("INSERT", "DOOR", 2, 90)
    with pytest.raises(CadError, match="not defined"):
        com.insert_block("NOPE", (0, 0, 0), 1, 0, P)
    dwg = tmp_path / "chair.dwg"
    dwg.write_text("x")
    assert com.insert_block(str(dwg), (0, 0, 0), 1, 0, P)["name"] == "chair"
    with pytest.raises(CadError, match="already exists"):
        com.create_block("DOOR", (0, 0, 0), [rect["handle"]], False)


def test_documents_save_export(com, fake, tmp_path):
    with pytest.raises(CadError, match="never been saved"):
        com.save_drawing()
    saved = com.save_drawing(str(tmp_path / "a.dwg"))
    assert saved["path"].endswith("a.dwg")
    com.save_drawing()  # in place now works
    com.save_drawing(str(tmp_path / "a2.dxf"))
    assert fake.app.ActiveDocument.last_save_type == 65
    com.save_drawing(str(tmp_path / "b.dwg"))

    pdf = com.export(str(tmp_path / "plot.pdf"), "pdf")
    assert (tmp_path / "plot.pdf").read_bytes().startswith(b"%PDF")
    assert pdf["format"] == "pdf"
    dxf = com.export(str(tmp_path / "copy.dxf"), "dxf")
    assert "note" in dxf and fake.app.ActiveDocument.FullName.endswith("b.dwg")  # re-attached to the original
    bmp = com.export(str(tmp_path / "shot.bmp"), "bmp")
    assert bmp["path"].endswith(".bmp")
    with pytest.raises(CadError, match="Unsupported"):
        com.export(str(tmp_path / "x.step"), "step")

    info = com.new_drawing()
    assert info["entity_count"] == 0 and info["path"] is None
    assert len(fake.app.Documents.items) == 2
    with pytest.raises(CadError, match="not found"):
        com.open_drawing(str(tmp_path / "missing.dwg"))
    opened = com.open_drawing(str(tmp_path / "a.dwg"))
    assert opened["name"] == "a.dwg"


def test_extents_zoom_command(com, fake):
    assert com.extents() is None
    com.add_rectangle((-5, -5, 0), (5, 10, 0), P)
    assert com.extents() == {"min": [-5, -5, 0], "max": [5, 10, 0]}
    com.zoom_extents()
    com.zoom_window((0, 0, 0), (1, 1, 0))
    assert fake.app.zoomed[0] == "extents" and fake.app.zoomed[1][0] == "window"
    assert com.send_command("_.REGEN") == {"sent": "_.REGEN", "completed": True}
    assert fake.app.ActiveDocument.commands == ["_.REGEN\n"]


def test_dxf_type_mapping():
    assert dxf_type("AcDbPolyline") == "LWPOLYLINE"
    assert dxf_type("AcDbRotatedDimension") == "DIMENSION"
    assert dxf_type("AcDbWipeout") == "WIPEOUT"


@pytest.mark.anyio
async def test_mcp_tools_on_com_backend(ccall, fake):
    status = await ccall("cad_status")
    assert status["backend"] == "autocad"
    res = await ccall(
        "draw_batch",
        operations=[
            {"op": "rectangle", "corner1": [0, 0], "corner2": [100, 60], "layer": "Walls", "color": "red"},
            {"op": "circle", "center": [50, 30], "radius": 5},
            {"op": "text", "text": "ROOM", "insert": [40, 40], "height": 5},
            {"op": "dimension", "p1": [0, 0], "p2": [100, 0], "location": [50, -10], "kind": "horizontal"},
        ],
    )
    assert res["created"] == 4, res
    assert (await ccall("list_entities", layer="walls"))["total"] == 1
    sent = await ccall("run_command", command="_.ZOOM _E")
    assert sent["completed"] is True
    assert "blocked" in await ccall.error("run_command", command="_.SHELL")
    assert fake.app.ActiveDocument.commands == ["_.ZOOM _E\n"]
    out = await ccall("save_drawing", path="plans/house")
    assert out["path"].endswith("house.dwg")
