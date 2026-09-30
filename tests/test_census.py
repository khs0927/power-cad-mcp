import json
from pathlib import Path

import ezdxf

from power_cad_mcp.census import census, main, to_markdown

STD = json.loads(
    (Path(__file__).parents[1] / "docs/standards/floor_plan_standard.json").read_text(encoding="utf-8")
)


def _drawing():
    doc = ezdxf.new("R2018", setup=True)
    title = doc.blocks.new("ZIUM_sheet_architect")
    title.add_lwpolyline([(0, 0), (84000, 0), (84000, 59400), (0, 59400)], close=True)
    title.add_attdef("NO", (70000, 3000), dxfattribs={"height": 300})
    msp = doc.modelspace()
    for layer in ("구역계", "COL", "수상한레이어"):
        doc.layers.add(layer)
    ins = msp.add_blockref(
        "ZIUM_sheet_architect", (0, 0), dxfattribs={"xscale": 0.5, "yscale": 0.5, "layer": "A-FORM"}
    )
    ins.add_attrib("NO", "A001", (35000, 1500))
    msp.add_blockref("ZIUM_sheet_architect", (100000, 0), dxfattribs={"xscale": 0.75, "yscale": 0.75})
    msp.add_line((1000, 1000), (5000, 1000), dxfattribs={"layer": "COL"})
    msp.add_text("배 치 도", dxfattribs={"layer": "TIT", "height": 480}).set_placement((2000, 2000))
    msp.add_mtext("T120 비드법보온판", dxfattribs={"layer": "실명"}).set_location((110000, 5000))
    msp.add_lwpolyline([(0, 0), (10, 0)], dxfattribs={"layer": "수상한레이어"}).translate(900000, 0, 0)
    msp.add_leader([(1000, 1000), (2000, 3000)], dxfattribs={"layer": "실명"})
    h = msp.add_hatch(dxfattribs={"layer": "G"})
    h.set_pattern_fill("LINE", scale=20, angle=90)
    h.paths.add_polyline_path([(0, 0), (1000, 0), (1000, 500)])
    msp.add_linear_dim((0, -500), (0, 0), (3000, 0), dimstyle="Standard").render()
    doc.layouts.get("Layout1").add_circle((0, 0), 5)
    return doc


def test_census_accounts_for_every_entity_and_sheet():
    r = census(_drawing(), STD)
    c = r["completeness"]
    assert c["complete"] and c["entities_visited"] >= c["entities_in_db"]
    assert [s["a3_scale"] for s in r["sheets"]] == ["1/100", "1/150"]
    assert "model:outside_sheets" in r["buckets"]  # the far-away polyline is not silently dropped
    assert "수상한레이어" in r["unmapped_layers"] and "구역계" not in r["unmapped_layers"]
    assert any(b["type"] == "LEADER" for b in r["blind_spots"])
    assert {"A001", "배 치 도", "T120 비드법보온판"} <= {t["text"] for t in r["texts"]}
    assert r["hatches"][0]["pattern"] == "LINE" and r["hatches"][0]["angle"] == 90
    assert r["dim_styles"]["Standard"] == 1
    assert "layout:Layout1" in r["buckets"]
    md = to_markdown(r)
    assert "완전성: 통과" in md and "S02" in md


def test_cli_writes_report_and_refuses_dwg(tmp_path):
    p = tmp_path / "a.dxf"
    _drawing().saveas(p)
    assert (
        main([str(p), "--standard", "docs/standards/floor_plan_standard.json", "--out", str(tmp_path / "o")])
        == 0
    )
    assert (tmp_path / "o/census.md").exists() and (tmp_path / "o/census.json").exists()
    assert main([str(tmp_path / "x.dwg")]) == 2
