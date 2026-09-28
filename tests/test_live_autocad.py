"""Live test against a real, running AutoCAD (Windows only, opt-in).

    set POWER_CAD_LIVE_TESTS=1
    pytest -m autocad -v

A new drawing is created, exercised and closed without saving; your open drawings are not touched.
"""

from __future__ import annotations

import os
import sys

import pytest

pytestmark = [
    pytest.mark.autocad,
    pytest.mark.skipif(
        sys.platform != "win32" or os.environ.get("POWER_CAD_LIVE_TESTS") != "1",
        reason="needs Windows + running AutoCAD + POWER_CAD_LIVE_TESTS=1",
    ),
]

P = {"layer": None, "color": None, "linetype": None}


@pytest.fixture(scope="module")
def acad():
    from power_cad_mcp.backends.com_backend import ComBackend

    backend = ComBackend()
    status = backend.status()
    assert status["connected"], status
    backend.new_drawing()
    yield backend
    backend._executor.submit(lambda: backend.doc.Close(False)).result(timeout=60)
    backend.close()


def test_live_roundtrip(acad, tmp_path):
    print(acad.status()["application"])
    acad.create_layer("PCMCP-TEST", 1, "DASHED")
    acad.set_current_layer("PCMCP-TEST")
    line = acad.add_line((0, 0, 0), (100, 0, 0), P)
    assert line["layer"] == "PCMCP-TEST" and line["length"] == 100
    rect = acad.add_rectangle((0, 0, 0), (50, 30, 0), P)
    assert rect["closed"] and len(rect["points"]) == 4
    arc = acad.add_arc((0, 0, 0), 10, 0, 90, P)
    assert arc["end_angle"] == 90
    acad.add_text("Power CAD MCP", (0, 40, 0), 5, 0, P)
    acad.add_mtext("Line 1\\PLine 2", (0, 60, 0), 50, 3, P)
    dim = acad.add_linear_dimension((0, 0, 0), (100, 0, 0), (50, -10, 0), 0, {**P, "text_height": 2.5})
    assert abs(dim["measurement"] - 100) < 1e-6
    acad.add_hatch(rect["handle"], "ANSI31", 1, 0, P)
    moved = acad.move_entities([line["handle"]], (0, 10, 0))
    assert moved[0]["start"] == [0, 10, 0]
    rotated = acad.rotate_entities([line["handle"]], (0, 10, 0), 90)
    assert rotated[0]["end"][1] == pytest.approx(110)
    circle = acad.add_circle((200, 0, 0), 5, P)
    assert acad.offset_entity(circle["handle"], 1)[0]["radius"] == 6
    blk = acad.create_block("PCMCP_BLK", (200, 0, 0), [circle["handle"]], False)
    assert blk["entity_count"] == 1
    ref = acad.insert_block("PCMCP_BLK", (300, 0, 0), 2, 45, P)
    assert ref["name"] == "PCMCP_BLK"
    assert acad.list_entities(layer="PCMCP-TEST")["total"] >= 8
    acad.zoom_extents()
    assert acad.send_command("_.REGEN")["completed"]
    out = acad.save_drawing(str(tmp_path / "live.dwg"))
    assert os.path.exists(out["path"])
    pdf = acad.export(str(tmp_path / "live.pdf"), "pdf")
    assert os.path.getsize(pdf["path"]) > 0
