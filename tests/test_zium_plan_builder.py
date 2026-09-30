import importlib.util
import json
from collections import Counter
from pathlib import Path

import pytest

pytest.importorskip("shapely")

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("zium_plan_builder", ROOT / "scripts" / "zium_plan_builder.py")
zpb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(zpb)


def build(**override):
    std = json.loads((ROOT / "docs" / "standards" / "zium_plan_sheet.json").read_text(encoding="utf-8"))
    plan = json.loads((ROOT / "examples" / "zium_plan_test.json").read_text(encoding="utf-8"))
    plan.update(override)
    return zpb.Builder(std, plan).build()


def test_example_reproduces_the_drawn_test_sheet():
    ents = build()
    assert len(ents) == 194
    c = Counter((e["type"], e["layer"]) for e in ents)
    assert sum(1 for e in ents if e.get("name") == "BUBBLE") == 6            # top and left only
    assert c[("polyline", "DEFPOINTS")] == 7       # boundary + 1500 + 5 x 400
    assert c[("dimension", "DIM")] == 24
    assert all(e["style"] == "80" for e in ents if e["type"] == "dimension")
    assert c[("hatch", ".")] == c[("polyline", "COL")]   # one SOLID per concrete mass


def test_sheet_origin_moves_everything():
    a, b = build(), build(sheet_origin=[548827.7 + 33600, 327230.0])
    assert a[0]["position"][0] + 33600 == pytest.approx(b[0]["position"][0])


def test_standard_layers_switch():
    layers = {e["layer"] for e in build(use_standard_layers=True)}
    assert {"SYM", "SYM_T", "HAT", "WIN", "WINBAR", "실명"} <= layers
    assert not {"COMM", "글자", ".", "WID", "WIN-창문살", "문자-실명"} & layers


def test_bulge_arc_ends_on_target():
    pts = zpb.arc_points((0, 0), (10, 0), 0.502)
    assert pts[-1] == pytest.approx((10, 0), abs=1e-9)
    assert pts[2][1] < 0                            # positive bulge = counter-clockwise, below the chord
