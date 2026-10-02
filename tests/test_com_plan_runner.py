"""scripts/com_plan_runner.py: Ontology element ids in a plan resolve to verified handles before validation.

Runs on Linux: the runner imports COM lazily, and the fakes below stand in for the AutoCAD document and
the Ontology REST client.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
from typing import Any

import pytest

from power_cad_mcp.ontology import OntologyError, OntologyUnavailable

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("com_plan_runner", ROOT / "scripts" / "com_plan_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class Obj:
    def __init__(self, handle: str, object_name: str, layer: str, **kw: Any):
        self.Handle, self.ObjectName, self.Layer = handle, object_name, layer
        for k, v in kw.items():
            setattr(self, k, v)


class Doc:
    Name = "A-201.dwg"
    FullName = r"C:\proj\A-201.dwg"

    def __init__(self, *objs: Obj):
        self.objs = {o.Handle: o for o in objs}

    def HandleToObject(self, handle: str) -> Obj:  # noqa: N802 - COM name
        return self.objs[handle.upper()]


class Client:
    """OntologyClient stand-in: only element_context is used by the resolver."""

    def __init__(self, elements: dict[str, dict[str, Any]], down: bool = False):
        self.elements, self.down, self.calls = elements, down, []

    def element_context(self, element_id: str, hops: int = 1) -> dict[str, Any]:
        self.calls.append(element_id)
        if self.down:
            raise OntologyUnavailable("Ontology service is not reachable")
        if element_id not in self.elements:
            raise OntologyError("Ontology GET failed with HTTP 404: Object not found")
        return {"element": self.elements[element_id], "edges": [], "nodes": []}


def _row(
    oid: str, kind: str, handle: str, layer: str, source: str = "A-201.dwg", **kw: Any
) -> dict[str, Any]:
    return {"id": oid, "kind": kind, "label": oid, "layer": layer,
            "evidence": {"source_name": source, "handle": handle, "layout": "Model"}, **kw}  # fmt: skip


WALL_PTS = [[0, 0], [100, 0], [100, 10], [0, 10]]
DOC = Doc(
    Obj("AA4", "AcDbPolyline", "A-WALL", Coordinates=[c for p in WALL_PTS for c in p]),
    Obj("2F3", "AcDbBlockReference", "A-DOOR", Name="*U12", EffectiveName="DOOR_SINGLE"),
    Obj("3B0", "AcDbText", "A-ANNO", TextString="SD-01"),
)
ONTO = Client(
    {
        "obs_wall": _row("obs_wall", "Wall", "AA4", "A-WALL"),
        "obs_door": _row("obs_door", "Door", "2F3", "A-DOOR", block_name="DOOR_SINGLE"),
        "obs_label": _row("obs_label", "Door", "3B0", "A-ANNO"),  # a TEXT is not the door
        "obs_other": _row("obs_other", "Wall", "AA4", "A-WALL", source="A-501.dwg"),
        "obs_gone": _row("obs_gone", "Wall", "FFF", "A-WALL"),
    }
)


def _resolve(plan: dict[str, Any], client: Client = ONTO, doc: Doc = DOC):
    return runner.resolve_elements(plan, client, runner.open_drawing_info(doc), runner.com_lookup(doc))


def test_plan_without_elements_is_untouched_and_offline():
    client = Client({}, down=True)
    plan = {"ops": [{"id": "x", "op": "delete", "expect": {"AA4": "AcDbPolyline"}}]}
    assert _resolve(plan, client) == (plan, [], [])
    assert client.calls == []


def test_aliases_resolve_to_handles_and_geometry_checks_still_run():
    plan = {
        "document": "A-201.dwg",
        "ops": [
            {"id": "w", "op": "replace_polylines", "elements": {"@wall": "obs_wall"},
             "expect": {"@wall": WALL_PTS}, "new": [{"points": WALL_PTS}]},
            {"id": "d", "op": "delete", "elements": {"@door": "obs_door"},
             "expect": {"@door": "AcDbBlockReference"}},
            {"id": "s", "op": "scale", "elements": {"@door": "obs_door"}, "handles": ["@door"],
             "base": [0, 0], "factor": 2, "expect": {"@door": [0, 0]}},
        ],
    }  # fmt: skip
    resolved, log, errors = _resolve(plan)
    assert errors == []
    ops = resolved["ops"]
    assert ops[0]["expect"] == {"AA4": WALL_PTS} and "elements" not in ops[0]
    assert ops[1]["expect"] == {"2F3": "AcDbBlockReference"}
    assert ops[2]["handles"] == ["2F3"] and list(ops[2]["expect"]) == ["2F3"]
    assert [(e["key"], e["handle"], e["status"]) for e in log] == [
        ("@wall", "AA4", "matched"),
        ("@door", "2F3", "matched"),
        ("@door", "2F3", "matched"),
    ]
    assert plan["ops"][0]["expect"] == {"@wall": WALL_PTS}  # the input plan is not mutated
    # Resolution does not replace the expected-geometry checks: a stale shape is still refused.
    stale = {**ops[0], "expect": {"AA4": [[0, 0], [99, 0], [99, 10], [0, 10]]}}
    assert any("geometry changed" in e for e in runner.validate(DOC, {"ops": [stale, ops[1]]}))
    assert runner.validate(DOC, {"ops": ops[:2]}) == []


def test_handle_keys_must_agree_with_the_element():
    ok = {
        "ops": [
            {"id": "t", "op": "delete", "elements": {"aa4": "obs_wall"}, "expect": {"aa4": "AcDbPolyline"}}
        ]
    }
    assert _resolve(ok)[2] == []
    bad = {"ops": [{"id": "t", "op": "delete", "elements": {"2F3": "obs_wall"}, "expect": {"2F3": "X"}}]}
    assert _resolve(bad)[2] == ["[t] delete: element obs_wall resolves to handle AA4, but the plan pins 2F3"]


@pytest.mark.parametrize(
    ("element_id", "status", "detail"),
    [
        ("obs_label", "mismatch", "TEXT entity is not a plausible Door"),
        ("obs_other", "other_drawing", ""),
        ("obs_gone", "handle_missing", "no model-space entity"),
        ("obs_missing", "not_found", "Object not found"),
    ],
)
def test_unmatched_elements_refuse_the_plan(element_id, status, detail):
    plan = {"ops": [{"id": "t", "op": "set_text", "elements": {"@x": element_id}, "handle": "@x",
                     "expect_text": "SD-01", "new_text": "SD-02"}]}  # fmt: skip
    resolved, log, errors = _resolve(plan)
    assert len(errors) == 1 and f"element {element_id} is {status}, not matched" in errors[0]
    assert detail in errors[0]
    assert log[0]["status"] == status


def test_structural_errors():
    plan = {
        "ops": [
            {"id": "a", "op": "delete", "elements": {"@x": "obs_wall"}, "expect": {"@y": "AcDbPolyline"}},
            {"id": "b", "op": "replace_polylines", "elements": {"@p": "obs_wall", "@q": "obs_wall"},
             "expect": {"@p": WALL_PTS, "@q": WALL_PTS}, "new": []},
            {"id": "c", "op": "delete", "elements": ["obs_wall"], "expect": {}},
        ]
    }  # fmt: skip
    errors = _resolve(plan)[2]
    assert "[a] delete: elements key '@x' is not used by the op (expect/handles/handle)" in errors
    assert "[a] delete: ['@y'] have no entry in elements" in errors
    assert "[b] replace_polylines: two targets resolve to the same handle" in errors
    assert any(e.startswith("[c] delete: elements must be an object") for e in errors)


def test_service_down_raises_for_the_caller_to_report():
    plan = {
        "ops": [{"id": "a", "op": "delete", "elements": {"@x": "obs_wall"}, "expect": {"@x": "AcDbPolyline"}}]
    }
    with pytest.raises(OntologyUnavailable):
        _resolve(plan, Client({}, down=True))


def test_com_lookup_and_drawing_info():
    lookup = runner.com_lookup(DOC)
    assert lookup("2f3") == {"handle": "2F3", "type": "INSERT", "layer": "A-DOOR", "name": "DOOR_SINGLE"}
    assert lookup("AA4")["type"] == "LWPOLYLINE" and lookup("nope") is None
    assert runner.open_drawing_info(DOC) == {"name": "A-201.dwg", "path": r"C:\proj\A-201.dwg"}
