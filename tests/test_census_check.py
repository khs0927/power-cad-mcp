"""ontology_census_check: drawing census vs. the Ontology's rows for the same file (fakes only)."""

from __future__ import annotations

import json
import os
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import ezdxf
import pytest
from conftest import ToolCaller
from mcp import Client

from power_cad_mcp.backends.dxf_backend import DxfBackend
from power_cad_mcp.census import census
from power_cad_mcp.census_check import census_check, compare, pick_document
from power_cad_mcp.config import Settings
from power_cad_mcp.ontology import OntologyClient
from power_cad_mcp.server import create_server

STD = json.loads(
    (Path(__file__).parents[1] / "docs/standards/floor_plan_standard.json").read_text(encoding="utf-8")
)


def _plan() -> tuple[ezdxf.document.Drawing, dict[str, str]]:
    """A small ZIUM-layered plan: two door entities, a wall, a window, a dim and a block with an attribute."""
    doc = ezdxf.new("R2018", setup=True)
    for layer in ("DOOR", "WAL1", "창호", "DIM", "수상한레이어"):
        doc.layers.add(layer)
    blk = doc.blocks.new("TAG")
    blk.add_circle((0, 0), 100, dxfattribs={"layer": "SYM"})
    blk.add_attdef("NO", (0, 0), dxfattribs={"layer": "SYM"})
    msp = doc.modelspace()
    h = {
        "door_line": msp.add_line((0, 0), (900, 0), dxfattribs={"layer": "DOOR"}).dxf.handle,
        "door_arc": msp.add_arc((0, 0), 900, 0, 90, dxfattribs={"layer": "DOOR"}).dxf.handle,
        "wall": msp.add_lwpolyline([(0, 0), (5000, 0)], dxfattribs={"layer": "WAL1"}).dxf.handle,
        "window": msp.add_line((2000, 0), (3000, 0), dxfattribs={"layer": "창호"}).dxf.handle,
        "odd": msp.add_line((0, 0), (1, 1), dxfattribs={"layer": "수상한레이어"}).dxf.handle,
    }
    ins = msp.add_blockref("TAG", (100, 100), dxfattribs={"layer": "SYM"})
    ins.add_attrib("NO", "1", (100, 100), dxfattribs={"layer": "SYM"})
    h["tag"] = ins.dxf.handle
    msp.add_linear_dim((0, -500), (0, 0), (3000, 0), dxfattribs={"layer": "DIM"}).render()
    return doc, h


def _el(oid: str, cls: str, handle: str | None, layer: str | None, **extra: Any) -> dict[str, Any]:
    row = {"id": oid, "class": cls, "handle": handle, "layer": layer, "document_id": "doc-1", **extra}
    return {k: v for k, v in row.items() if v is not None}


def _layer_row(name: str, layout: int, block: int = 0) -> dict[str, Any]:
    props = {"name": name, "entity_count": layout, "block_entity_count": block}
    return _el("L-" + name, "Layer", None, None, properties=props)


def _elements(h: dict[str, str]) -> list[dict[str, Any]]:
    return [
        _el("d1", "Door", h["door_line"], "DOOR"),
        _el("w1", "Wall", h["wall"].lower(), "WAL1"),  # handles compare case-insensitively
        _el("win1", "Window", h["window"], "창호"),
        _el("gone", "Door", "FFFF", "DOOR", sheet="Model"),  # deleted from the file since ingestion
        _el("v1", "View", None, None),
        _layer_row("DOOR", 2),
        _layer_row("WAL1", 1),
        _layer_row("창호", 3),  # the Ontology counted more than the file has
        _layer_row("SYM", 1, 2),
    ]


def test_pick_document_matches_file_key_and_prefers_project_and_revision():
    docs = [
        {"document_id": "a", "project_id": "P1", "name": "A-101.dwg", "revision": 2},
        {"document_id": "b", "project_id": "P2", "source_key": r"C:\x\a-101.DXF", "revision": 5},
        {"document_id": "c", "project_id": "P1", "name": "A-102.dwg", "revision": 9},
    ]
    best, others = pick_document(docs, "/tmp/A-101.dxf")
    assert best["document_id"] == "b" and [d["document_id"] for d in others] == ["a"]
    best, _ = pick_document(docs, "a-101.dwg", project_id="P1")
    assert best["document_id"] == "a"
    assert pick_document(docs, "/tmp/A-999.dxf") == (None, [])


def test_compare_handles_layers_and_classes():
    doc, h = _plan()
    report = census(doc, STD)
    assert (
        report["standard_applied"]
        and len(report["entity_index"]) == report["completeness"]["entities_visited"]
    )
    out = compare(report, {"document_id": "doc-1"}, _elements(h))

    hd = out["handles"]
    assert hd["in_both"] == 3 and hd["only_in_ontology_count"] == 1
    assert hd["only_in_ontology"][0]["element_ids"] == ["gone"]
    # The door arc, the odd line, the TAG insert and the dimension were not made elements; the ATTRIB
    # and block contents are not counted as layout entities.
    only = {s["handle"] for s in hd["only_in_census_sample"]}
    assert {h["door_arc"], h["odd"], h["tag"]} <= only and hd["only_in_census_by_type"]["DIMENSION"] == 1
    assert "ATTRIB" not in hd["only_in_census_by_type"]

    layers = {row["layer"]: row for row in out["layers"]}
    assert layers["DOOR"]["status"] == "equal" and layers["DOOR"]["ontology_elements"] == {"Door": 2}
    assert layers["창호"]["status"] == "differs" and layers["창호"]["census_layout"] == 1
    assert layers["창호"]["zium_layer"] == "WIN"
    assert layers["SYM"]["census_block"] == 2 and layers["SYM"]["status"] == "equal"
    assert layers["수상한레이어"]["status"] == "not_in_ontology"

    classes = {row["class"]: row for row in out["classes"]}
    door = classes["Door"]
    assert door["ontology_elements"] == 2 and door["census_entities_on_zium_layers"] == 2
    assert door["census_layers_mapped"] == ["DOOR"] and door["handles_found_in_census"] == "1/2"
    assert classes["Window"]["census_layers_mapped"] == ["창호"]  # 창호 maps to WIN in the merge map
    assert "Stair" not in classes  # nothing on either side
    assert "View" not in classes and any("View 1" in n["item"] for n in out["not_comparable"])
    assert any("block definitions" in n["item"] for n in out["not_comparable"])


def test_compare_marks_what_cannot_be_compared():
    doc, h = _plan()
    report = census(doc)  # no ZIUM standard
    old = {k: v for k, v in report.items() if k not in ("entity_index", "standard_applied")}
    out = compare(old, {"document_id": "doc-1"}, _elements(h))
    assert "handles" not in out and any("entity_index" in n["reason"] for n in out["not_comparable"])
    assert {row["status"] for row in out["layers"]} <= {"not_comparable", "not_in_ontology"}
    assert compare(report, {"document_id": "doc-1"}, _elements(h))["classes"][0]["zium"].startswith(
        "not_comparable"
    )
    door = next(c for c in compare(report, {"d": 1}, _elements(h))["classes"] if c["class"] == "Door")
    assert door["zium"] == "not_comparable: the census was made without the ZIUM standard"
    column = compare(report | {"standard_applied": True}, {"d": 1}, [_el("c", "Column", "1", "COL")])
    assert "COL holds columns and structural walls" in column["classes"][0]["zium"]
    assert compare(report, None, []) == {
        "ingested": False,
        "note": "the Ontology has no document for this file; nothing to compare",
    }


class _Client:
    def __init__(self, docs: list[dict[str, Any]], elements: list[dict[str, Any]]):
        self.docs, self.elements, self.calls = docs, elements, []

    def documents(self, accept=None, *, project_id=None, max_pages=20):
        self.calls.append(("documents", project_id))
        return [d for d in self.docs if accept is None or accept(d)]

    def document_elements(self, document_id, *, project_id=None, max_rows=100_000):
        self.calls.append(("elements", document_id))
        return self.elements, False


def test_census_check_warns_about_other_file_and_later_edits(tmp_path):
    doc, h = _plan()
    path = tmp_path / "A-101.dxf"
    doc.saveas(path)
    report = census(ezdxf.readfile(path), STD)
    docs = [
        {
            "document_id": "doc-1",
            "project_id": "P1",
            "name": "A-101.dwg",
            "updated_at": "2000-01-01T00:00:00Z",
        }
    ]
    client = _Client(docs, _elements(h))
    out = census_check(client, str(path), report | {"file": "B-200.dxf"})
    assert out["ingested"] and out["document"]["document_id"] == "doc-1" and out["read_only"]
    assert client.calls == [("documents", None), ("elements", "doc-1")]
    assert any("B-200.dxf" in w for w in out["warnings"])
    assert any("modified" in w for w in out["warnings"])
    missing = census_check(_Client([], []), str(path), report)
    assert missing["ingested"] is False and missing["document"] is None and "handles" not in missing


# ------------------------------------------------------------- client + tool over HTTP
DOCS = [
    {"document_id": "doc-0", "project_id": "P1", "name": "other.dwg", "sheets": []},
    {"document_id": "doc-1", "project_id": "P1", "name": "A-101.dwg", "revision": 1, "sheets": []},
]


class _Api(BaseHTTPRequestHandler):
    rows: list[dict[str, Any]] = []
    seen: list[dict[str, str]] = []

    def log_message(self, *args: Any) -> None:
        pass

    def do_GET(self) -> None:  # noqa: N802
        url = urllib.parse.urlsplit(self.path)
        qs = {k: v[0] for k, v in urllib.parse.parse_qs(url.query).items()}
        _Api.seen.append({"path": url.path, **qs})
        if url.path == "/v1/drawings":  # one document per page, to exercise the paging
            i = int(qs.get("cursor", 0))
            body = {"items": DOCS[i : i + 1], "next_cursor": str(i + 1) if i + 1 < len(DOCS) else None}
        else:  # an API that ignores document_id: rows of doc-0 come back too; two rows per page
            i = int(qs.get("cursor", 0))
            body = {
                "items": _Api.rows[i : i + 2],
                "next_cursor": str(i + 2) if i + 2 < len(_Api.rows) else None,
            }
        data = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


@pytest.fixture
def api_url():
    server = ThreadingHTTPServer(("127.0.0.1", 0), _Api)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


def _api_rows(h: dict[str, str]) -> list[dict[str, Any]]:
    def row(oid: str, kind: str, handle: str | None, layer: str | None, doc: str = "doc-1", **kw: Any):
        return {"id": oid, "kind": kind, "layer": layer, "document_id": doc, "document_name": "A-101.dwg",
                "evidence": {"handle": handle, "layout": "Model"}, **kw}  # fmt: skip

    return [
        row("d1", "Door", h["door_line"], "DOOR"),
        row("x", "Door", "1", "DOOR", doc="doc-0"),
        row("w1", "Wall", h["wall"], "WAL1"),
        row(
            "L", "Layer", None, None, properties={"name": "DOOR", "entity_count": 2, "block_entity_count": 0}
        ),
    ]


def test_client_reads_every_page_and_drops_other_documents(api_url):
    _, h = _plan()
    _Api.rows, _Api.seen = _api_rows(h), []
    client = OntologyClient(api_url)
    assert [d["document_id"] for d in client.documents()] == ["doc-0", "doc-1"]
    elements, truncated = client.document_elements("doc-1")
    assert [e["id"] for e in elements] == ["d1", "w1", "L"] and not truncated
    assert {s.get("document_id") for s in _Api.seen if s["path"] == "/v1/elements"} == {"doc-1"}
    assert client.document_elements("doc-1", max_rows=1) == (elements[:1], True)


@pytest.mark.anyio
async def test_ontology_census_check_tool(api_url, tmp_path):
    doc, h = _plan()
    doc.saveas(tmp_path / "A-101.dxf")
    _Api.rows = _api_rows(h)
    std = os.path.join(os.path.dirname(__file__), "..", "docs", "standards", "floor_plan_standard.json")
    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=api_url, ontology_timeout=3)
    async with Client(create_server(DxfBackend(), settings)) as client:
        call = ToolCaller(client)
        out = await call("ontology_census_check", path="A-101.dxf", standard=std)
        assert out["ingested"] and out["census"]["source"] == "file" and out["read_only"]
        assert out["handles"]["in_both"] == 2 and out["handles"]["only_in_ontology_count"] == 0
        door = next(c for c in out["classes"] if c["class"] == "Door")
        assert door["census_entities_on_zium_layers"] == 2

        census_dir = await call("drawing_census", path="A-101.dxf", standard=std)
        again = await call("ontology_census_check", path="A-101.dxf", census_json=census_dir["data"])
        assert again["census"]["source"] == "census_json" and again["handles"] == out["handles"]

        (tmp_path / "bad.json").write_text("{}", encoding="utf-8")
        err = await call.error("ontology_census_check", path="A-101.dxf", census_json="bad.json")
        assert "not a census.json" in err
        none = await call("ontology_census_check", path="A-999.dxf", census_json=census_dir["data"])
        assert none["ingested"] is False and any("A-101" in w for w in none["warnings"])
