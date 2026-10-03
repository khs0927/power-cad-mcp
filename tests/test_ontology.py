# ruff: noqa: E501  (fixture rows mirror the API and read better unwrapped)
"""Ontology client + ontology_* tools against a fake Ontology REST API (http.server in a thread).

The fake mirrors the shapes of ``aec_intelligence.operational`` (``catalog.py`` / ``api.py``):
``/v1/elements?kind=`` with Korean aliases and keyset ``next_cursor`` paging, ``/v1/blocks?name_like=``,
``/v1/drawings?category=`` returning one item per document with its sheets, ``/v1/elements/{id}/context
?hops=``, ``POST /v1/search {query, top_k}``; 400 for a bad cursor, 404 for an unknown id.
"""

from __future__ import annotations

import base64
import json
import socket
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

import pytest
from conftest import ToolCaller
from mcp import Client

from power_cad_mcp.backends.dxf_backend import DxfBackend
from power_cad_mcp.config import Settings
from power_cad_mcp.ontology import (
    OntologyClient,
    OntologyError,
    OntologyUnavailable,
    auto_context,
    block_candidates,
    compact_element,
    drawing_key,
    in_drawing,
    infer_task,
    match_element,
    normalize_storey,
    rows,
    targets_summary,
)
from power_cad_mcp.server import create_server


def _el(oid: str, kind: str, label: str, doc: str, layout: str, storey: str, **extra: Any) -> dict[str, Any]:
    return {
        "id": oid,
        "kind": kind,
        "label": label,
        "state": "OBSERVED",
        "project_id": "P1",
        "document_id": "doc-" + doc,
        "document_name": doc + ".dwg",
        "revision": 1,
        "storey": storey,
        "layer": extra.pop("layer", None),
        "block_name": extra.pop("block_name", None),
        "drawing_category": extra.pop("drawing_category", None),
        "attributes": extra.pop("attributes", {}),
        "bbox": [0, 0, 1, 1],
        "evidence": {"source_name": doc + ".dwg", "handle": extra.pop("handle", None), "layout": layout},
        **extra,
    }


ELEMENTS = [
    _el("el-d1", "Door", "SD-01", "A-201", "A-201", "2층", layer="A-DOOR", block_name="DOOR_SINGLE",
        drawing_category="평면도", attributes={"MARK": "SD-01"}, handle="2F3"),
    _el("el-d2", "Door", "SD-02", "A-101", "A-101", "1층", layer="A-DOOR", block_name="DOOR_SINGLE",
        drawing_category="평면도"),
    _el("el-w1", "Window", "AW-02", "A-501", "A-501", "", layer="A-GLAZ", block_name="AW_WINDOW",
        drawing_category="상세도", attributes={"MARK": "AW-02"}),
    _el("el-wall1", "Wall", "W-200", "A-201", "A-201", "2층", layer="A-WALL", drawing_category="평면도"),
]  # fmt: skip
KIND_ALIASES = {"문": "Door", "창호": "Window", "창": "Window", "벽": "Wall", "벽체": "Wall"}
CATEGORY_ALIASES = {"plan": "평면도", "detail": "상세도", "창호도": "창호도", "window_schedule": "창호도"}
BLOCKS = [
    {"name": "AW_WINDOW", "definitions": [{"id": "b2", "document_id": "doc-A-501", "document_name": "A-501.dwg"}],
     "documents": ["doc-A-501"], "attribute_tags": ["MARK"], "layers": ["A-GLAZ"], "instance_count": 12,
     "instance_kinds": {"Window": 12}, "classified_as": "Window"},
    {"name": "DOOR_SINGLE", "definitions": [{"id": "b1", "document_id": "doc-A-201", "document_name": "A-201.dwg"}],
     "documents": ["doc-A-201"], "attribute_tags": ["MARK", "W"], "layers": ["A-DOOR"], "instance_count": 42,
     "instance_kinds": {"Door": 42}, "classified_as": "Door"},
    {"name": "TITLE", "definitions": [], "documents": [], "attribute_tags": ["DWG_NO"], "layers": [],
     "instance_count": 3, "instance_kinds": {"TitleBlock": 3}, "classified_as": "TitleBlock"},
]  # fmt: skip
DOCS = [
    {"document_id": "doc-A-201", "project_id": "P1", "name": "A-201.dwg", "drawing_categories": ["평면도"],
     "sheets": [{"layout": "Model", "element_counts": {"Door": 1, "Wall": 1}, "view_id": "v1",
                 "view_label": "2층 평면도", "drawing_category": "평면도",
                 "title_block": {"id": "t1", "drawing_number": "A-201", "drawing_title": "2층 평면도",
                                 "scale": "1/100"}}]},
    {"document_id": "doc-A-501", "project_id": "P1", "name": "A-501.dwg", "drawing_categories": ["상세도", "창호도"],
     "sheets": [{"layout": "D1", "element_counts": {"Window": 1}, "drawing_category": "상세도",
                 "title_block": {"drawing_number": "A-501", "drawing_title": "창호상세도", "scale": "1/20"}},
                {"layout": "S1", "element_counts": {}, "drawing_category": "창호도",
                 "title_block": {"drawing_number": "A-511", "drawing_title": "창호일람표", "scale": "1/50"}}]},
]  # fmt: skip


def _cursor(value: str) -> str:
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode()


def _page(items: list[dict[str, Any]], qs: dict[str, str], key: str) -> dict[str, Any]:
    """Keyset paging like the real API: items sorted by `key`, cursor = last key of the page."""
    items = sorted(items, key=lambda r: r[key])
    if qs.get("cursor"):
        try:
            after = json.loads(base64.urlsafe_b64decode(qs["cursor"].encode()))
        except Exception as exc:
            raise ValueError("invalid cursor") from exc
        items = [r for r in items if r[key] > after]
    limit = int(qs.get("limit", 50))
    page, more = items[:limit], len(items) > limit
    return {"count": len(page), "items": page, "next_cursor": _cursor(page[-1][key]) if more else None}


class FakeOntology(BaseHTTPRequestHandler):
    requests: list[tuple[str, str, dict[str, str], Any]] = []
    delay = 0.0

    def log_message(self, *args: Any) -> None:  # keep pytest output clean
        pass

    def _send(self, code: int, body: Any, raw: bytes | None = None) -> None:
        data = raw if raw is not None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _route(self, method: str) -> None:
        url = urllib.parse.urlsplit(self.path)
        qs = {k: v[0] for k, v in urllib.parse.parse_qs(url.query).items()}
        body = None
        if method == "POST":
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"null")
        FakeOntology.requests.append((method, url.path, qs, body))
        if FakeOntology.delay:
            time.sleep(FakeOntology.delay)
        try:
            self._dispatch(url.path, qs, body)
        except ValueError as exc:
            self._send(400, {"detail": str(exc)})

    def _dispatch(self, path: str, qs: dict[str, str], body: Any) -> None:
        if path == "/v1/catalog":
            return self._send(
                200,
                {
                    "project_id": qs.get("project_id"),
                    "totals": {"objects": 4, "documents": 3, "projects": 1},
                    "kinds": [{"kind": "Door", "count": 2, "aliases_ko": "문 도어"}],
                    "drawing_categories": [{"category": "평면도", "count": 3}],
                    "projects": [{"project_id": "P1"}],
                },
            )
        if path == "/v1/elements":
            kinds = {KIND_ALIASES.get(t, t) for t in qs.get("kind", "").split(",") if t}
            if "창호" in qs.get("kind", ""):
                kinds |= {"Door", "Window"}
            cat = CATEGORY_ALIASES.get(qs.get("drawing_category", ""), qs.get("drawing_category"))
            text = (qs.get("text") or "").lower()
            found = [
                e
                for e in ELEMENTS
                if (not kinds or e["kind"] in kinds)
                and (not cat or e["drawing_category"] == cat)
                and (not text or text in (e["label"] + json.dumps(e["attributes"])).lower())
            ]
            page = _page(found, qs, "id")
            return self._send(200, {"filters": {"kind": sorted(kinds) or None}, **page})
        if path == "/v1/blocks":
            like = (qs.get("name_like") or "").lower().replace("*", "")
            return self._send(200, _page([b for b in BLOCKS if like in b["name"].lower()], qs, "name"))
        if path == "/v1/drawings":
            cat = CATEGORY_ALIASES.get(qs.get("category", ""), qs.get("category"))
            docs = [json.loads(json.dumps(d)) for d in DOCS if not cat or cat in d["drawing_categories"]]
            for d in docs:
                for s in d["sheets"]:
                    if cat:
                        s["matches_category"] = s.get("drawing_category") == cat
            return self._send(200, _page(docs, qs, "document_id"))
        if path.startswith("/v1/elements/") and path.endswith("/context"):
            eid = urllib.parse.unquote(path.split("/")[3])
            el = next((e for e in ELEMENTS if e["id"] == eid), None)
            if el is None:
                return self._send(404, {"detail": "Object not found"})
            edges = [{"subject": eid, "predicate": "hostedBy", "object": "el-wall1", "hop": 1}]
            return self._send(
                200, {"element": el, "hops": int(qs.get("hops", 1)), "edges": edges, "nodes": []}
            )
        if path == "/v1/search":
            # Like the real API without embeddings: every query token must be a token of the search
            # text (so 창 does not hit 창고), and kind/storey are exact-match filters.
            hits = [
                {"object_id": "el-d1", "project_id": "P1", "kind": "Door", "label": "SD-01", "storey": "2층",
                 "score": 0.91, "citation": {"document_name": "A-201.dwg", "handle_or_id": "2F3",
                                             "layout_or_page": "A-201"}, "properties": {}, "relations": [],
                 "tokens": {"sd-01", "door", "2층", "문", "출입문", "a-201"}}
            ]  # fmt: skip
            tokens = set(str(body.get("query") or "").lower().split())
            hits = [
                {k: v for k, v in h.items() if k != "tokens"}
                for h in hits
                if tokens
                and tokens <= h["tokens"]
                and body.get("kind", h["kind"]) == h["kind"]
                and body.get("storey", h["storey"]) == h["storey"]
                and body.get("project_id", h["project_id"]) == h["project_id"]
            ][: body.get("top_k", 10)]
            return self._send(
                200, {"query": body["query"], "total_hits": len(hits), "hits": hits, "warnings": []}
            )
        if path == "/v1/broken":
            return self._send(500, {"detail": "database is down"})
        if path == "/v1/garbage":
            return self._send(200, None, raw=b"<html>oops</html>")
        return self._send(404, None, raw=b"")

    def do_GET(self) -> None:  # noqa: N802
        self._route("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._route("POST")


@pytest.fixture
def fake_url():
    FakeOntology.requests = []
    FakeOntology.delay = 0.0
    server = ThreadingHTTPServer(("127.0.0.1", 0), FakeOntology)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


@pytest.fixture
def dead_url() -> str:
    with socket.socket() as sock:  # grab a free port, then close it so nothing listens there
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    return f"http://127.0.0.1:{port}"


def _paths(method: str = "GET") -> list[str]:
    return [p for m, p, _, _ in FakeOntology.requests if m == method]


# ------------------------------------------------------------------ inference
@pytest.mark.parametrize(
    ("task", "classes", "categories", "blocks", "storey", "marks"),
    [
        ("2층 평면도 문 리스트 갱신", ["Door"], ["plan", "schedule"], True, "2F", []),
        ("창호상세도에 AW-02 추가", ["Window", "Door"], ["detail"], True, None, ["AW-02"]),
        (
            "지하1층 기둥 보 철골 H-형강 확인",
            ["Column", "Beam", "SteelSection"],
            ["structural"],
            False,
            "B1F",
            [],
        ),
        ("회의실 실명 정리하고 블록 정리", ["Space"], [], True, None, []),
        ("벽체 상세 B1F", ["Wall"], ["detail"], False, "B1F", []),
        ("update the door schedule on 3F", ["Door"], ["schedule"], True, "3F", []),
        ("add window W-12 to the elevation", ["Window"], ["elevation"], True, None, ["W-12"]),
        ("창호도 갱신", ["Window", "Door"], ["schedule"], True, None, []),
        ("창문 교체", ["Window"], [], True, None, []),
        ("창고 문서 실행 보고", [], [], False, None, []),
    ],
)
def test_infer_task(task, classes, categories, blocks, storey, marks):
    hints = infer_task(task)
    assert hints["classes"] == classes
    assert hints["drawing_categories"] == categories
    assert hints["include_blocks"] is blocks
    assert hints["storey"] == storey
    assert hints["marks"] == marks


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        ("2층", "2F"),
        ("2F", "2F"),
        ("02", "2F"),
        ("L2", "2F"),
        ("지하1층", "B1F"),
        ("B1", "B1F"),
        ("옥상", "RF"),
    ],
)
def test_normalize_storey(value, expected):
    assert normalize_storey(value) == expected


def test_rows_and_compact_tolerate_shapes():
    assert rows([{"a": 1}, "x"]) == [{"a": 1}]
    assert rows({"items": [{"a": 1}]}) == [{"a": 1}]
    assert rows({"data": {"results": [{"a": 2}]}}) == [{"a": 2}]
    assert rows({"elements": [{"a": 3}]}, "elements") == [{"a": 3}]
    assert rows({"nothing": 1}) == [] and rows(None) == []
    # a provisional/alternative shape still maps onto the same keys
    assert compact_element({"element_id": "x", "class": "Door", "name": "D1", "source_file": "a.dwg"}) == {
        "id": "x",
        "class": "Door",
        "name": "D1",
        "source_file": "a.dwg",
    }


# --------------------------------------------------------------------- client
def test_client_elements(fake_url):
    client = OntologyClient(fake_url, timeout=5)
    page = client.elements("문", limit=10)
    assert [d["id"] for d in page["items"]] == ["el-d1", "el-d2"] and page["next_cursor"] is None
    _, path, qs, _ = FakeOntology.requests[-1]
    assert path == "/v1/elements" and qs["kind"] == "문" and qs["include_properties"] == "true"
    assert "class" not in qs and "project_id" not in qs  # None filters are not sent
    door = page["items"][0]
    assert door == {
        "id": "el-d1",
        "class": "Door",
        "name": "SD-01",
        "source_file": "A-201.dwg",
        "sheet": "A-201",
        "storey": "2층",
        "drawing_category": "평면도",
        "layer": "A-DOOR",
        "block_name": "DOOR_SINGLE",
        "handle": "2F3",
        "document_id": "doc-A-201",
        "project_id": "P1",
        "bbox": [0, 0, 1, 1],
        "attributes": {"MARK": "SD-01"},
    }

    # Keyset paging: the cursor the API returns is handed back and accepted.
    first = client.elements("Door", limit=1)
    assert [d["id"] for d in first["items"]] == ["el-d1"] and first["next_cursor"]
    second = client.elements("Door", limit=1, cursor=first["next_cursor"])
    assert [d["id"] for d in second["items"]] == ["el-d2"] and second["next_cursor"] is None

    # storey/sheet are filtered client-side ("2F" matches "2층"); several pages are followed.
    assert [d["id"] for d in client.elements("Door", storey="2F")["items"]] == ["el-d1"]
    # ...and the storey is also sent, in the Korean form, for an API that filters server-side.
    assert FakeOntology.requests[-1][2]["storey"] == "2층"
    client.elements("Door", storey="B1")
    assert FakeOntology.requests[-1][2]["storey"] == "지하1층"
    assert [d["id"] for d in client.elements("Door", sheet="a-101")["items"]] == ["el-d2"]
    assert "storey" not in FakeOntology.requests[-1][2]
    assert [d["name"] for d in client.elements("창호", text="AW-02")["items"]] == ["AW-02"]
    assert client.elements(drawing_category="detail")["items"][0]["id"] == "el-w1"


def test_client_blocks_drawings_context_search(fake_url):
    client = OntologyClient(fake_url, timeout=5)
    assert client.catalog("P1")["kinds"][0]["kind"] == "Door"
    assert FakeOntology.requests[-1][2] == {"project_id": "P1"}

    door_blocks = client.blocks("문")["items"]
    assert door_blocks == [
        {
            "name": "DOOR_SINGLE",
            "category": "Door",
            "instance_count": 42,
            "instance_kinds": {"Door": 42},
            "attribute_tags": ["MARK", "W"],
            "layers": ["A-DOOR"],
            "example_files": ["A-201.dwg"],
        }
    ]
    assert [b["name"] for b in client.blocks("창호")["items"]] == ["AW_WINDOW", "DOOR_SINGLE"]
    assert [b["name"] for b in client.blocks(None, "door*")["items"]] == ["DOOR_SINGLE"]
    assert FakeOntology.requests[-1][2]["name_like"] == "door*"
    paged = client.blocks(limit=2)
    assert len(paged["items"]) == 2 and paged["next_cursor"]

    details = client.drawings("detail")["items"]
    assert details == [
        {
            "drawing_number": "A-501",
            "title": "창호상세도",
            "category": "상세도",
            "scale": "1/20",
            "file": "A-501.dwg",
            "layout": "D1",
            "document_id": "doc-A-501",
            "element_counts": {"Window": 1},
        }
    ]
    client.drawings("schedule")
    assert FakeOntology.requests[-1][2]["category"] == "창호도"  # mapped to the API's category name
    assert [d["drawing_number"] for d in client.drawings(q="A-511")["items"]] == ["A-511"]
    assert len(client.drawings()["items"]) == 3

    ctx = client.element_context("el-d1", hops=2)
    assert ctx["edges"][0]["object"] == "el-wall1" and ctx["hops"] == 2
    with pytest.raises(OntologyError, match="Object not found"):
        client.element_context("el/x")
    assert FakeOntology.requests[-1][1] == "/v1/elements/el%2Fx/context"  # ids are path-escaped

    hits = client.search("2층 문", k=3, kind="Door")
    assert hits[0]["id"] == "el-d1" and hits[0]["source_file"] == "A-201.dwg" and hits[0]["handle"] == "2F3"
    assert FakeOntology.requests[-1][3] == {"query": "2층 문", "top_k": 3, "kind": "Door"}


def test_client_http_errors(fake_url):
    client = OntologyClient(fake_url, timeout=5)
    with pytest.raises(OntologyError, match="HTTP 400: invalid cursor"):
        client.elements("Door", cursor="not-a-cursor")
    with pytest.raises(OntologyError, match="HTTP 500: database is down"):
        client.get("/v1/broken")
    with pytest.raises(OntologyError, match="not found"):
        client.get("/v1/unknown")
    with pytest.raises(OntologyError, match="non-JSON"):
        client.get("/v1/garbage")
    with pytest.raises(OntologyError, match="http"):
        OntologyClient("ftp://example", 5)


def test_client_service_down(dead_url):
    # Linux refuses a closed port at once; Windows retries the connect until the timeout.
    with pytest.raises(
        OntologyUnavailable, match=r"not reachable.*POWERCAD_ONTOLOGY_URL|did not answer within 2s"
    ):
        OntologyClient(dead_url, timeout=2).catalog()


def test_client_timeout(fake_url):
    FakeOntology.delay = 1.5
    with pytest.raises(OntologyUnavailable, match="did not answer within 1s"):
        OntologyClient(fake_url, timeout=1).catalog()


def test_auto_context_bundle(fake_url):
    client = OntologyClient(fake_url, timeout=5)
    bundle = auto_context(client, "2층 평면도 문 리스트 갱신")
    assert bundle["inferred"]["classes"] == ["Door"] and bundle["read_only"] is True
    assert [d["id"] for d in bundle["elements"]["Door"]] == ["el-d1"]  # storey 2F matched "2층"
    element_qs = [qs for m, path, qs, _ in FakeOntology.requests if path == "/v1/elements"]
    assert element_qs and element_qs[0]["storey"] == "2층"  # sent as a param, filtered client-side too
    assert [d["drawing_number"] for d in bundle["drawings"]["plan"]] == ["A-201"]
    assert [d["drawing_number"] for d in bundle["drawings"]["schedule"]] == ["A-511"]
    assert [b["name"] for b in bundle["blocks"]["Door"]] == ["DOOR_SINGLE"]
    # The whole sentence matches nothing (scoped to kind/storey, then unscoped); the class keyword "문"
    # finds the door once the exact-match storey "2F" (the data says "2층") is dropped.
    assert bundle["search"][0]["id"] == "el-d1"
    assert [r[3] for r in FakeOntology.requests if r[1] == "/v1/search"] == [
        {"query": "2층 평면도 문 리스트 갱신", "top_k": 10, "kind": "Door", "storey": "2F"},
        {"query": "2층 평면도 문 리스트 갱신", "top_k": 10, "kind": "Door"},
        {"query": "2층 평면도 문 리스트 갱신", "top_k": 10},
        {"query": "문", "top_k": 10, "kind": "Door", "storey": "2F"},
        {"query": "문", "top_k": 10, "kind": "Door"},
    ]
    assert bundle["counts"]["elements"] == {"Door": 1}
    assert bundle["warnings"] == [
        "search: the whole-task search returned nothing or failed; used keywords ['문 (Door)']."
    ]
    assert "in_open_drawing" not in bundle["search"][0] and "open_drawing" not in bundle

    bundle = auto_context(client, "창호상세도에 AW-02 추가")
    assert [w["name"] for w in bundle["elements"]["Window"]] == ["AW-02"]
    # No door carries AW-02, so the class is relaxed to all doors and a warning explains why.
    assert len(bundle["elements"]["Door"]) == 2 and any("showing all Door" in w for w in bundle["warnings"])
    assert [d["drawing_number"] for d in bundle["drawings"]["detail"]] == ["A-501"]
    assert [b["name"] for b in bundle["blocks"]["Window"]] == ["AW_WINDOW"]

    bundle = auto_context(client, "블록 정리")
    assert len(bundle["blocks"]["all"]) == 3 and bundle["elements"] == {}
    # Nothing to fall back on: no keyword was queried, so no keyword warning either.
    assert bundle["search"] == [] and bundle["warnings"] == []

    # Class words go out with their kind, so "창" (Window) cannot hit unrelated rows; the stop-early
    # loop lists only the keywords actually sent.
    FakeOntology.requests = []
    bundle = auto_context(client, "문 위치, 창문 교체", drawing="  ")
    assert bundle["drawing"] is None
    sent = [r[3] for r in FakeOntology.requests if r[1] == "/v1/search"]
    assert {"query": "창", "top_k": 10, "kind": "Window"} in sent
    assert bundle["warnings"][0].endswith("used keywords ['문 (Door)', '창 (Window)'].")

    bundle = auto_context(client, "문 위치 확인", drawing="A-101")
    assert [d["id"] for d in bundle["elements"]["Door"]] == ["el-d2"]
    with pytest.raises(OntologyError):
        auto_context(client, "   ")


def test_auto_context_service_down(dead_url):
    with pytest.raises(OntologyUnavailable):
        auto_context(OntologyClient(dead_url, timeout=2), "문 리스트")


def test_settings_from_env(monkeypatch):
    monkeypatch.setenv("POWERCAD_ONTOLOGY_URL", "http://10.0.0.5:9000/")
    monkeypatch.setenv("POWERCAD_ONTOLOGY_TIMEOUT", "999")
    monkeypatch.setenv("POWERCAD_ONTOLOGY_AUTO_CONTEXT", "1")
    s = Settings.from_env()
    assert (s.ontology_url, s.ontology_timeout, s.ontology_auto_context) == (
        "http://10.0.0.5:9000/",
        120.0,
        True,
    )
    monkeypatch.delenv("POWERCAD_ONTOLOGY_URL")
    monkeypatch.setenv("POWERCAD_ONTOLOGY_TIMEOUT", "abc")
    s = Settings.from_env()
    assert s.ontology_url == "http://127.0.0.1:58000" and s.ontology_timeout == 10.0


# ---------------------------------------------------------------- MCP tools
def _server(url: str, tmp_path, **kw: Any):
    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=url, ontology_timeout=3, **kw)
    return create_server(DxfBackend(), settings)


@pytest.mark.anyio
async def test_ontology_tools(fake_url, tmp_path):
    async with Client(_server(fake_url, tmp_path)) as client:
        call = ToolCaller(client)
        tools = {t.name for t in (await client.list_tools()).tools}
        assert {
            "ontology_catalog",
            "ontology_find_elements",
            "ontology_blocks",
            "ontology_drawings",
            "ontology_element_context",
            "ontology_search",
            "ontology_auto_context",
        } <= tools

        assert (await call("ontology_catalog"))["projects"] == [{"project_id": "P1"}]
        found = await call("ontology_find_elements", kind="벽체")
        assert (
            found["count"] == 1 and found["elements"][0]["name"] == "W-200" and found["next_cursor"] is None
        )
        page = await call("ontology_find_elements", kind="Door", limit=1)
        assert page["next_cursor"]
        nxt = await call("ontology_find_elements", kind="Door", limit=1, cursor=page["next_cursor"])
        assert nxt["elements"][0]["id"] == "el-d2"
        blocks = await call("ontology_blocks", category="Door")
        assert blocks["blocks"][0]["instance_count"] == 42
        assert (await call("ontology_drawings", category="상세도"))["drawings"][0]["title"] == "창호상세도"
        ctx = await call("ontology_element_context", element_id="el-d1", hops=2)
        assert ctx["element"]["id"] == "el-d1" and FakeOntology.requests[-1][2] == {"hops": "2"}
        assert (await call("ontology_search", query="문", k=2))["hits"][0]["class"] == "Door"
        bundle = await call("ontology_auto_context", task="2층 평면도 문 리스트 갱신")
        assert bundle["counts"]["elements"] == {"Door": 1}
        assert "Object not found" in await call.error("ontology_element_context", element_id="missing")
        assert "invalid cursor" in await call.error("ontology_find_elements", cursor="bad")

        # Pre-step on the automation entry point (draw_batch), requested explicitly.
        out = await call(
            "draw_batch",
            operations=[{"op": "line", "start": [0, 0], "end": [1, 0]}],
            task="창호상세도에 AW-02 추가",
            auto_context=True,
        )
        assert out["created"] == 1 and out["ontology_context"]["inferred"]["marks"] == ["AW-02"]
        # Without the flag nothing is fetched.
        before = len(FakeOntology.requests)
        out = await call("draw_batch", operations=[{"op": "point", "location": [0, 0]}], task="문")
        assert "ontology_context" not in out and len(FakeOntology.requests) == before
        assert "needs a `task`" in await call.error(
            "draw_batch", operations=[{"op": "point", "location": [0, 0]}], auto_context=True
        )


@pytest.mark.anyio
async def test_ontology_tools_service_down(dead_url, tmp_path):
    async with Client(_server(dead_url, tmp_path, ontology_auto_context=True)) as client:
        call = ToolCaller(client)
        for name, args in [
            ("ontology_catalog", {}),
            ("ontology_find_elements", {"kind": "Door"}),
            ("ontology_blocks", {}),
            ("ontology_drawings", {"category": "detail"}),
            ("ontology_element_context", {"element_id": "x"}),
            ("ontology_search", {"query": "문"}),
            ("ontology_auto_context", {"task": "문 리스트"}),
        ]:
            assert "not reachable" in await call.error(name, **args)
        # With the env flag on, a batch without a task is unchanged (no lookup, no error).
        out = await call("draw_batch", operations=[{"op": "point", "location": [0, 0]}])
        assert out["created"] == 1 and "ontology_context" not in out
        # The env-enabled pre-step never blocks drawing: it reports the outage and carries on.
        out = await call("draw_batch", operations=[{"op": "point", "location": [0, 0]}], task="문 리스트")
        assert out["created"] == 1
        assert out["ontology_context"]["available"] is False
        assert "not reachable" in out["ontology_context"]["error"]


# ------------------------------------------------------- ontology id -> CAD handle
@pytest.mark.parametrize(
    ("name", "key"),
    [
        ("A-201.dwg", "a-201"),
        ("a-201.DXF", "a-201"),
        (r"C:\\Proj\\Sub\\A-201.Dwg", "a-201"),
        ("/srv/drawings/A-201.dxf", "a-201"),
        ("A-201", "a-201"),
        ("A-201.backup.dwg", "a-201.backup"),
        ("plan.pdf", "plan.pdf"),
        ("", None),
        (None, None),
    ],
)
def test_drawing_key(name, key):
    assert drawing_key(name) == key


def test_in_drawing():
    info = {"name": "A-201.dxf", "path": "/tmp/x/A-201.dxf"}
    assert in_drawing("A-201.dwg", info) is True  # the DWG the element came from = its DXF copy
    assert in_drawing("a-201.DWG", info) is True
    # Folder on both sides: the full path decides (same name in another folder is another drawing).
    assert in_drawing("C:\\Proj\\a-201.DWG", info) is False
    assert in_drawing("/TMP/x/a-201.DWG", info) is True
    assert in_drawing("C:\\Proj\\A-201.dwg", {"name": "A-201.dwg", "path": "c://PROJ//a-201.dxf"}) is True
    assert in_drawing("A-501.dwg", info) is False
    assert in_drawing(None, info) is None and in_drawing("A-201.dwg", None) is None
    assert in_drawing("A-201.dwg", {"name": "", "path": None}) is None


DOOR = {"id": "el-1", "class": "Door", "name": "SD-01", "source_file": "A-201.dwg", "handle": "1A",
        "layer": "A-DOOR", "block_name": "DOOR_SINGLE"}  # fmt: skip
OPEN = {"name": "A-201.dxf", "path": "/w/A-201.dxf"}


def _lookup(entities: dict[str, dict[str, Any]]):
    calls: list[str] = []

    def lookup(handle: str) -> dict[str, Any] | None:
        calls.append(handle)
        return entities.get(handle.upper())

    lookup.calls = calls  # type: ignore[attr-defined]
    return lookup


def test_match_element_statuses():
    insert = {"handle": "1A", "type": "INSERT", "layer": "a-door", "name": "DOOR_SINGLE", "insert": [0, 0, 0]}
    ok = match_element(DOOR, OPEN, _lookup({"1A": insert}))
    assert ok["status"] == "matched" and ok["handle"] == "1A" and ok["element_id"] == "el-1"
    assert ok["entity"] == {"handle": "1A", "type": "INSERT", "layer": "a-door", "name": "DOOR_SINGLE",
                            "insert": [0, 0, 0]}  # fmt: skip
    assert ok["open_drawing"] == OPEN and ok["source_file"] == "A-201.dwg"

    # Another drawing: the handle is never even looked up (it would name an unrelated entity).
    lookup = _lookup({"1A": insert})
    other = match_element(DOOR | {"source_file": "A-501.dwg"}, OPEN, lookup)
    assert other["status"] == "other_drawing" and lookup.calls == []
    nothing_open = match_element(DOOR, None, lookup)
    assert nothing_open["status"] == "other_drawing" and nothing_open["note"] == "no drawing is open"
    no_source = match_element({k: v for k, v in DOOR.items() if k != "source_file"}, OPEN, lookup)
    assert no_source["status"] == "other_drawing" and "no source_file" in no_source["note"]

    gone = match_element(DOOR | {"sheet": "Layout1"}, OPEN, _lookup({}))
    assert gone["status"] == "handle_missing" and "Layout1" in gone["note"]
    no_handle = match_element({k: v for k, v in DOOR.items() if k != "handle"}, OPEN, _lookup({}))
    assert no_handle["status"] == "handle_missing"

    line = {"handle": "1A", "type": "LINE", "layer": "A-DOOR"}
    bad = match_element(DOOR, OPEN, _lookup({"1A": line}))
    assert bad["status"] == "mismatch" and "block 'DOOR_SINGLE'" in bad["reasons"][0]
    other_block = match_element(DOOR, OPEN, _lookup({"1A": insert | {"name": "WINDOW"}}))
    assert other_block["status"] == "mismatch" and "'WINDOW'" in other_block["reasons"][0]
    anonymous = match_element(DOOR, OPEN, _lookup({"1A": insert | {"name": "*U12"}}))
    assert anonymous["status"] == "matched"  # dynamic block reference: effective name not visible
    other_layer = match_element(DOOR, OPEN, _lookup({"1A": insert | {"layer": "0"}}))
    assert other_layer["status"] == "mismatch" and "layer" in other_layer["reasons"][0]

    # Door drawn as plain geometry (LINE + ARC on A-DOOR, like the Ontology fixture) is plausible ...
    plain = {"id": "el-2", "class": "Door", "source_file": "A-201.dwg", "handle": "A3", "layer": "A-DOOR"}
    assert match_element(plain, OPEN, _lookup({"A3": line | {"handle": "A3"}}))["status"] == "matched"
    # ... but a dimension or a text is not a door; a text is fine for a room name.
    dim = {"handle": "A3", "type": "DIMENSION", "layer": "A-DOOR"}
    assert match_element(plain, OPEN, _lookup({"A3": dim}))["status"] == "mismatch"
    text = {"handle": "A3", "type": "TEXT", "layer": "A-DOOR", "text": "SD-01"}
    assert match_element(plain, OPEN, _lookup({"A3": text}))["status"] == "mismatch"
    room = plain | {"class": "Space"}
    assert match_element(room, OPEN, _lookup({"A3": text}))["status"] == "matched"

    assert match_element(None, OPEN, _lookup({}), element_id="x") == {
        "element_id": "x",
        "status": "not_found",
        "open_drawing": OPEN,
    }


UNPROVABLE = "open drawing is unsaved/default-named; cannot prove it is the source file"


def test_match_element_drawing_identity():
    insert = {"handle": "1A", "type": "INSERT", "layer": "A-DOOR", "name": "DOOR_SINGLE"}
    lookup = _lookup({"1A": insert})
    full = DOOR | {"source_file": "C:\\Proj\\A-201.dwg"}
    # Same base name, different folders: another drawing, the handle is never looked up.
    other = match_element(full, {"name": "A-201.dwg", "path": "D:\\Copy\\A-201.dwg"}, lookup)
    assert other["status"] == "other_drawing" and "note" not in other and lookup.calls == []
    same = match_element(full, {"name": "A-201.dwg", "path": "c:/proj//a-201.DXF"}, lookup)
    assert same["status"] == "matched"
    # Base-name fallback when one side has no folder.
    assert (
        match_element(DOOR, {"name": "A-201.dwg", "path": "D:\\Copy\\A-201.dwg"}, lookup)["status"]
        == "matched"
    )
    # Unsaved or default-named open drawing: never matched.
    unsaved = match_element(DOOR, {"name": "A-201.dwg", "path": None}, lookup)
    assert unsaved["status"] == "other_drawing" and unsaved["note"] == UNPROVABLE
    drawing1 = DOOR | {"source_file": "Drawing1.dwg"}
    default = match_element(drawing1, {"name": "Drawing1.dwg", "path": "C:\\x\\Drawing1.dwg"}, lookup)
    assert default["status"] == "other_drawing" and default["note"] == UNPROVABLE
    assert match_element(DOOR, {"name": "Drawing3.dwg", "path": None}, lookup).get("note") is None


def test_match_element_needs_a_positive_signal():
    bare = {"id": "el-7", "class": "Thing", "source_file": "A-201.dwg", "handle": "1A"}
    line = {"handle": "1A", "type": "LINE", "layer": "X"}
    out = match_element(bare, OPEN, _lookup({"1A": line}))
    assert out["status"] == "unverified"
    assert (
        out["note"] == "nothing ties the entity to the element (no known class, matching layer or block name)"
    )
    assert match_element(bare | {"layer": "x"}, OPEN, _lookup({"1A": line}))["status"] == "matched"
    summary = targets_summary({"elements": {"Thing": [bare]}}, OPEN, _lookup({"1A": line}))
    assert summary["counts"] == {"unverified": 1} and summary["targets"] == []
    assert summary["not_actionable"] == ["el-7: unverified"]


def test_dot_element_ids_are_rejected():
    client = OntologyClient("http://127.0.0.1:9")
    for bad in (".", "..", " ... "):
        with pytest.raises(OntologyError, match="not a valid element id"):
            client.element_context(bad)


def test_targets_summary_uses_bundle_only():
    bundle = {
        "search": [DOOR],
        "elements": {
            "Door": [
                DOOR,
                DOOR | {"id": "el-9", "handle": "FF"},
                DOOR | {"id": "el-5", "source_file": "B.dwg"},
            ]
        },
    }
    insert = {"handle": "1A", "type": "INSERT", "layer": "A-DOOR", "name": "DOOR_SINGLE"}
    lookup = _lookup({"1A": insert})
    out = targets_summary(bundle, OPEN, lookup)
    assert out["counts"] == {"matched": 1, "handle_missing": 1, "other_drawing": 1}  # el-1 counted once
    assert out["targets"] == [
        {"element_id": "el-1", "class": "Door", "name": "SD-01", "handle": "1A", "type": "INSERT",
         "layer": "A-DOOR"}
    ]  # fmt: skip
    assert out["not_actionable"] == ["el-9: handle_missing"] and lookup.calls == ["1A", "FF"]


def test_auto_context_project_and_open_drawing(fake_url):
    client = OntologyClient(fake_url, timeout=5)
    bundle = auto_context(
        client,
        "2층 평면도 문 리스트 갱신",
        project_id="P1",
        k=3,
        open_drawing={"name": "a-201.dxf", "path": "/w/a-201.dxf"},
    )
    gets = [qs for m, path, qs, _ in FakeOntology.requests if m == "GET"]
    posts = [body for m, _, _, body in FakeOntology.requests if m == "POST"]
    assert gets and all(qs.get("project_id") == "P1" for qs in gets)  # elements, drawings, blocks
    assert posts and all(b["project_id"] == "P1" and b["top_k"] == 3 for b in posts)
    assert bundle["project_id"] == "P1" and bundle["open_drawing"] == {
        "name": "a-201.dxf",
        "path": "/w/a-201.dxf",
    }
    assert bundle["search"][0]["in_open_drawing"] is True
    assert bundle["elements"]["Door"][0]["in_open_drawing"] is True
    assert bundle["counts"]["in_open_drawing"] == 1  # el-d1 appears as a hit and as an element

    # An unsaved drawing of the same name proves nothing: not tagged as the open drawing.
    unsaved = auto_context(
        client, "2층 평면도 문 리스트 갱신", open_drawing={"name": "a-201.dxf", "path": None}
    )
    assert unsaved["elements"]["Door"][0]["in_open_drawing"] is False

    bundle = auto_context(client, "문 리스트", open_drawing={"name": "A-501.dxf"})
    assert {e["id"]: e["in_open_drawing"] for e in bundle["elements"]["Door"]} == {
        "el-d1": False,
        "el-d2": False,
    }


def _draw_house(backend: DxfBackend, path: str) -> dict[str, str]:
    """A-201 as an open DXF: a wall polyline, a door block reference and a stray text."""
    backend.create_layer("A-WALL")
    backend.create_layer("A-DOOR")
    wall = backend.add_polyline([(0, 0, 0), (100, 0, 0), (100, 50, 0)], False, {"layer": "A-WALL"})
    leaf = backend.add_line((0, 0, 0), (9, 0, 0), {"layer": "A-DOOR"})
    backend.create_block("DOOR_SINGLE", (0, 0, 0), [leaf["handle"]], True)
    door = backend.insert_block("DOOR_SINGLE", (10, 0, 0), 1.0, 0.0, {"layer": "A-DOOR"})
    text = backend.add_text("SD-01", (10, 5, 0), 2.5, 0.0, {"layer": "A-DOOR"})
    backend.save_drawing(path)
    return {"wall": wall["handle"], "door": door["handle"], "text": text["handle"]}


@pytest.mark.anyio
async def test_ontology_locate_tool(fake_url, tmp_path, monkeypatch):
    backend = DxfBackend()
    handles = _draw_house(backend, str(tmp_path / "A-201.dxf"))
    extra = [
        _el("el-x-door", "Door", "SD-01", "A-201", "Model", "2층", layer="A-DOOR", block_name="DOOR_SINGLE",
            handle=handles["door"]),
        _el("el-x-wall", "Wall", "W-1", "A-201", "Model", "2층", layer="A-WALL", handle=handles["wall"]),
        _el("el-x-label", "Door", "SD-01", "A-201", "Model", "2층", layer="A-DOOR", handle=handles["text"]),
        _el("el-x-gone", "Wall", "W-2", "A-201", "Model", "2층", layer="A-WALL", handle="FFFF"),
    ]  # fmt: skip
    monkeypatch.setattr(sys.modules[__name__], "ELEMENTS", ELEMENTS + extra)
    before = backend.list_entities(limit=100)

    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=fake_url, ontology_timeout=3)
    async with Client(create_server(backend, settings)) as client:
        call = ToolCaller(client)
        ids = ["el-x-door", "el-x-wall", "el-x-label", "el-x-gone", "el-w1", "nope", "el-x-door"]
        out = await call("ontology_locate", element_ids=ids)
        by_id = {r["element_id"]: r for r in out["results"]}
        assert len(out["results"]) == 6  # duplicates are located once
        assert by_id["el-x-door"]["status"] == "matched"
        assert (
            by_id["el-x-door"]["entity"]["type"] == "INSERT"
            and by_id["el-x-door"]["handle"] == handles["door"]
        )
        assert by_id["el-x-wall"]["status"] == "matched"
        assert by_id["el-x-label"]["status"] == "mismatch"  # a TEXT is not the door itself
        assert by_id["el-x-gone"]["status"] == "handle_missing"
        assert by_id["el-w1"]["status"] == "other_drawing" and by_id["el-w1"]["source_file"] == "A-501.dwg"
        assert by_id["nope"]["status"] == "not_found" and "Object not found" in by_id["nope"]["error"]
        assert out["open_drawing"]["name"] == "A-201.dxf" and out["read_only"] is True
        assert out["match_scope"] == "drawing_identity_and_live_entity_plausibility"
        assert out["source_revision_verified"] is False
        assert out["may_execute_mutation"] is False
        assert sorted(out["matched_handles"]) == sorted([handles["door"], handles["wall"]])
        assert out["counts"] == {
            "matched": 2,
            "mismatch": 1,
            "handle_missing": 1,
            "other_drawing": 1,
            "not_found": 1,
        }
        # Located handles work with the edit tools as-is (read here, to keep the drawing unchanged).
        assert (await call("get_entity", handle=out["matched_handles"][0]))["type"] in (
            "INSERT",
            "LWPOLYLINE",
        )

        bundle = await call("ontology_auto_context", task="2층 평면도 문 리스트 갱신", project_id="P1", k=5)
        flags = {e["id"]: e["in_open_drawing"] for e in bundle["elements"]["Door"]}
        assert flags == {"el-d1": True, "el-x-door": True, "el-x-label": True}
        assert bundle["open_drawing"]["name"] == "A-201.dxf"

        out = await call(
            "draw_batch",
            operations=[{"op": "point", "location": [500, 500]}],
            task="2층 평면도 문 리스트 갱신",
            auto_context=True,
        )
        targets = out["ontology_targets"]
        assert [t["element_id"] for t in targets["targets"]] == ["el-x-door"]
        assert targets["targets"][0]["handle"] == handles["door"]
        assert targets["source_revision_verified"] is False
        assert targets["may_execute_mutation"] is False
        assert targets["counts"] == {
            "handle_missing": 1,
            "matched": 1,
            "mismatch": 1,
        }  # el-d1's 2F3 is absent
        created = out["results"][0]["entity"]["handle"]

    after = [e for e in backend.list_entities(limit=100)["entities"] if e["handle"] != created]
    assert after == before["entities"]  # locate/auto_context never touched the drawing


@pytest.mark.anyio
async def test_ontology_locate_without_open_file(fake_url, tmp_path):
    async with Client(_server(fake_url, tmp_path)) as client:
        out = await ToolCaller(client)("ontology_locate", element_ids=["el-d1"])
        # An unsaved Drawing1 is not the element's A-201.dwg, so the handle 2F3 is not offered.
        assert out["results"][0]["status"] == "other_drawing" and out["matched_handles"] == []


# ------------------------------------------------------------ block candidates
class _BlockClient:
    """Stub with OntologyClient.blocks' signature; records the (category, q) queries."""

    def __init__(self, items: list[dict[str, Any]], fail: str | None = None):
        self.items, self.fail, self.calls = items, fail, []

    def blocks(self, category=None, q=None, *, project_id=None, limit=50):
        self.calls.append((category, q))
        if self.fail and self.fail == category:
            raise OntologyError("HTTP 500")
        return {"items": self.items, "next_cursor": None}


def test_block_candidates_rules():
    items = [
        {"name": "*U5", "effective_names": ["DOOR_SINGLE"], "category": "Door", "example_files": ["A-101.dwg"]},
        {"name": "*U9", "category": "Door"},
        {"name": "XREF_BASE", "is_xref": True, "category": "Door"},
        {"name": "DOOR_FIRE", "category": "Door", "example_files": ["A-201.dwg"]},
        {"name": "DOOR_SLIDE", "category": "Door", "example_files": ["A-301.dwg", "A-302.dwg"]},
    ]  # fmt: skip
    live = [{"name": "door_single", "base_point": [0, 0, 0], "entity_count": 3}, {"name": "DOOR_LOCAL"}]
    client = _BlockClient(items)
    out = block_candidates(client, "DOOR*", live, OPEN, project_id=" P1 ")
    assert client.calls == [(None, "DOOR*")]
    insertable = {c["insert_name"]: c for c in out["insertable"]}
    # Dynamic block stored as *U5: matched through its effective name, inserted by the drawing's name.
    assert (
        insertable["door_single"]["ontology_name"] == "*U5" and insertable["door_single"]["entity_count"] == 3
    )
    assert insertable["DOOR_LOCAL"]["note"] == "defined in the open drawing; not in the Ontology"
    reasons = {c["ontology_name"]: c["reason"] for c in out["not_insertable"]}
    assert set(reasons) == {"*U9", "XREF_BASE"}
    other = {c["ontology_name"]: c for c in out["other_files"]}
    assert other["DOOR_SLIDE"]["example_files"] == ["A-301.dwg", "A-302.dwg"]
    assert "only in the files" in other["DOOR_SLIDE"]["note"]
    assert "parsed it from this drawing" in other["DOOR_FIRE"]["note"]  # A-201 is open but lacks it
    assert out["counts"] == {"ontology_blocks": 5, "insertable": 2, "other_files": 2, "not_insertable": 2}
    assert out["project_id"] == "P1" and out["read_only"] is True and "next_step" in out

    # The open drawing's definition of that name is an xref: not insertable, and not re-listed as local.
    xref_live = [{"name": "DOOR_FIRE", "xref": True}]
    out = block_candidates(_BlockClient(items), "DOOR*", xref_live, OPEN)
    assert "DOOR_FIRE" not in {c.get("insert_name") for c in out["insertable"]}
    fire = next(c for c in out["not_insertable"] if c["ontology_name"] == "DOOR_FIRE")
    assert "external reference" in fire["reason"]

    # A task selects blocks by class; a failing query is a warning, an unreadable drawing is "unverified".
    client = _BlockClient(items[3:], fail="Window")
    out = block_candidates(client, "창호 블록 배치", None, None)
    assert client.calls == [("Window", None), ("Door", None)]
    assert out["insertable"] == [] and [c["ontology_name"] for c in out["unverified"]] == [
        "DOOR_FIRE",
        "DOOR_SLIDE",
    ]
    assert any(w.startswith("blocks[category=Window]") for w in out["warnings"])
    assert any("block table could not be read" in w for w in out["warnings"])

    client = _BlockClient([])
    out = block_candidates(client, "place something here", [], OPEN)
    assert client.calls == [(None, None)] and "listing all blocks" in out["warnings"][0]
    with pytest.raises(OntologyError):
        block_candidates(client, "  ", [], OPEN)


@pytest.mark.anyio
async def test_ontology_block_candidates_tool(fake_url, tmp_path):
    backend = DxfBackend()
    _draw_house(backend, str(tmp_path / "A-201.dxf"))  # defines DOOR_SINGLE
    before = backend.list_entities(limit=100)
    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=fake_url, ontology_timeout=3)
    async with Client(create_server(backend, settings)) as client:
        call = ToolCaller(client)
        out = await call("ontology_block_candidates", name_or_task="창호 블록 배치")
        assert [c["insert_name"] for c in out["insertable"]] == ["DOOR_SINGLE"]
        assert out["other_files"][0]["ontology_name"] == "AW_WINDOW"
        assert out["other_files"][0]["example_files"] == ["A-501.dwg"]
        assert out["open_drawing"]["name"] == "A-201.dxf"
        assert backend.list_entities(limit=100) == before  # read-only

        # The insert_name is a real definition: insert_block takes it as-is.
        name = out["insertable"][0]["insert_name"]
        assert (await call("insert_block", name=name, insert=[50, 50]))["type"] == "INSERT"
        assert "not defined" in await call.error("insert_block", name="AW_WINDOW", insert=[0, 0])
