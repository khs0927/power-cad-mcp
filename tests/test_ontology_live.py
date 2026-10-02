"""Contract check of the ontology_* tools against a running Ontology API (aec_intelligence /v1).

Skipped unless POWERCAD_ONTOLOGY_LIVE_URL points at an API with at least one ingested drawing, e.g.
the Ontology repo's docker compose stack (http://127.0.0.1:58000). Read-only: it only calls GET
endpoints and POST /v1/search. The assertions are about shapes, not about a particular drawing, so it
also runs against the real Drive data.
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest
from conftest import ToolCaller
from mcp import Client

from power_cad_mcp.backends.dxf_backend import DxfBackend
from power_cad_mcp.config import Settings
from power_cad_mcp.server import create_server

LIVE_URL = os.getenv("POWERCAD_ONTOLOGY_LIVE_URL")
pytestmark = pytest.mark.skipif(not LIVE_URL, reason="POWERCAD_ONTOLOGY_LIVE_URL not set")


def _fixture_dir() -> Path | None:
    """The Ontology repo's fixtures: POWERCAD_ONTOLOGY_FIXTURES or an Ontology checkout next to this repo."""
    candidates = [os.getenv("POWERCAD_ONTOLOGY_FIXTURES")]
    candidates += [str(parent / "Ontology" / "fixtures") for parent in Path(__file__).resolve().parents[1:3]]
    return next((Path(c) for c in candidates if c and Path(c).is_dir()), None)


# Element kinds auto_context knows how to ask for; the first one present in the catalog is used.
_TASK_FOR_KIND = {
    "Door": "문 리스트 확인",
    "Window": "창호 리스트 확인",
    "Wall": "벽체 확인",
    "Space": "실 목록 확인",
    "Column": "기둥 확인",
}


@pytest.mark.anyio
async def test_live_ontology_contract(tmp_path):
    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=LIVE_URL, ontology_timeout=30)
    async with Client(create_server(DxfBackend(), settings)) as client:
        call = ToolCaller(client)

        catalog = await call("ontology_catalog")
        assert catalog["totals"]["objects"] > 0, "the API has no ingested drawing"
        kinds = {k["kind"]: k["count"] for k in catalog["kinds"]}
        kind = next((k for k in _TASK_FOR_KIND if kinds.get(k)), None)
        assert kind, f"none of {sorted(_TASK_FOR_KIND)} in the catalog: {sorted(kinds)}"

        found = await call("ontology_find_elements", kind=kind, limit=1)
        assert found["count"] == 1
        element = found["elements"][0]
        assert element["class"] == kind and element["id"] and element["source_file"]
        # Catalog counts span every project, so ask the API itself whether a second row exists.
        two = await call("ontology_find_elements", kind=kind, limit=2)
        if two["count"] == 2:
            assert found["next_cursor"], "a full page with more rows behind it must carry next_cursor"
            nxt = await call("ontology_find_elements", kind=kind, limit=1, cursor=found["next_cursor"])
            assert nxt["elements"][0]["id"] != element["id"]

        ctx = await call("ontology_element_context", element_id=element["id"])
        assert ctx["element"]["id"] == element["id"]

        drawings = await call("ontology_drawings")
        assert drawings["count"] > 0 and all("file" in d for d in drawings["drawings"])

        blocks = await call("ontology_blocks")
        assert {"count", "blocks", "next_cursor"} <= set(blocks)

        hits = await call("ontology_search", query=os.path.basename(element["source_file"]), k=3)
        assert hits["count"] > 0

        bundle = await call("ontology_auto_context", task=_TASK_FOR_KIND[kind])
        assert bundle["read_only"] is True and bundle["counts"]["elements"][kind] > 0
        assert bundle["counts"]["search"] > 0, bundle["warnings"]

        assert "Object not found" in await call.error("ontology_element_context", element_id="obs_missing")


@pytest.mark.anyio
async def test_live_ontology_locate(tmp_path):
    """ontology_locate maps live Ontology rows onto the open drawing without editing it.

    With the Ontology fixture simple_house.dxf at hand (the E2E stack ingests it), the fixture is
    opened in the headless backend and its elements must resolve to live handles; otherwise the
    element belongs to a drawing that is not open and must be reported as other_drawing.
    """
    settings = Settings(backend="dxf", workspace=str(tmp_path), ontology_url=LIVE_URL, ontology_timeout=30)
    async with Client(create_server(DxfBackend(), settings)) as client:
        call = ToolCaller(client)
        fixtures = _fixture_dir()
        fixture = fixtures / "simple_house.dxf" if fixtures else None
        elements: list[dict] = []
        if fixture and fixture.is_file():
            page = await call("ontology_find_elements", limit=500)
            elements = [
                e
                for e in page["elements"]
                if os.path.splitext(os.path.basename(e.get("source_file", "")))[0].lower() == "simple_house"
                and e.get("handle")
            ]
        if elements:
            before = (await call("open_drawing", path=str(fixture)))["entity_count"]
            ids = [e["id"] for e in elements[:20]] + ["obs_missing"]
            out = await call("ontology_locate", element_ids=ids)
            statuses = {r["element_id"]: r["status"] for r in out["results"]}
            assert statuses.pop("obs_missing") == "not_found"
            assert "other_drawing" not in statuses.values(), out
            assert out["counts"].get("matched", 0) > 0, out
            for r in out["results"]:
                if r["status"] == "matched":
                    assert (await call("get_entity", handle=r["handle"]))["handle"] == r["handle"]
            assert (await call("get_drawing_info"))["entity_count"] == before  # only read
        else:
            found = await call("ontology_find_elements", limit=1)
            element = found["elements"][0]
            out = await call("ontology_locate", element_ids=[element["id"]])
            # The open drawing is the unsaved Drawing1.dxf, never the element's source file.
            assert out["results"][0]["status"] == "other_drawing" and out["matched_handles"] == []
