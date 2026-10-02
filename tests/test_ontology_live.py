"""Contract check of the ontology_* tools against a running Ontology API (aec_intelligence /v1).

Skipped unless POWERCAD_ONTOLOGY_LIVE_URL points at an API with at least one ingested drawing, e.g.
the Ontology repo's docker compose stack (http://127.0.0.1:58000). Read-only: it only calls GET
endpoints and POST /v1/search. The assertions are about shapes, not about a particular drawing, so it
also runs against the real Drive data.
"""

from __future__ import annotations

import os

import pytest
from conftest import ToolCaller
from mcp import Client

from power_cad_mcp.backends.dxf_backend import DxfBackend
from power_cad_mcp.config import Settings
from power_cad_mcp.server import create_server

LIVE_URL = os.getenv("POWERCAD_ONTOLOGY_LIVE_URL")
pytestmark = pytest.mark.skipif(not LIVE_URL, reason="POWERCAD_ONTOLOGY_LIVE_URL not set")

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
        if kinds[kind] > 1:
            assert found["next_cursor"], "a full page of a larger kind must carry next_cursor"
            nxt = await call("ontology_find_elements", kind=kind, limit=1, cursor=found["next_cursor"])
            assert nxt["elements"][0]["id"] != element["id"]

        ctx = await call("ontology_element_context", element_id=element["id"])
        assert ctx["element"]["id"] == element["id"]

        drawings = await call("ontology_drawings")
        assert drawings["count"] > 0 and all("file" in d for d in drawings["drawings"])

        blocks = await call("ontology_blocks")
        assert {"count", "blocks", "next_cursor"} <= set(blocks)

        hits = await call("ontology_search", query=element["source_file"], k=3)
        assert hits["count"] > 0

        bundle = await call("ontology_auto_context", task=_TASK_FOR_KIND[kind])
        assert bundle["read_only"] is True and bundle["counts"]["elements"][kind] > 0
        assert bundle["counts"]["search"] > 0, bundle["warnings"]

        assert "Object not found" in await call.error("ontology_element_context", element_id="obs_missing")
