"""Run a JSON edit plan against the live AutoCAD drawing over COM, with pre/post verification.

Covers the operations the loaded Power CAD plugin (0.2.0) cannot do yet: reading/creating DIMENSION
entities, deleting, merging polylines, and scaling. Every operation pins its targets by handle AND by
expected geometry, so a plan written for one drawing state is refused on any other.

    python scripts/com_plan_runner.py PLAN.json            # validate only (no changes)
    python scripts/com_plan_runner.py PLAN.json --apply    # validate, apply, verify
    python scripts/com_plan_runner.py --dump-dims [x0 y0 x1 y1]   # list dimensions (MCP 0.2.0 can't)
    python scripts/com_plan_runner.py PLAN.json --ontology-url http://127.0.0.1:58000   # see `elements`

Plan format (see docs/playbooks/house_plan_fix.plan.json):
    {"document": "Drawing2.dwg", "ops": [ {"op": ..., "id": ..., ...}, ... ]}

ops
    replace_polylines  expect: {handle: [[x,y],...]}  new: [{points, closed, layer?}]
                       Deletes the expected polylines, creates the new ones with the first source's
                       layer/linetype/lineweight/color.
    scale              handles, base [x,y], factor, expect: {handle: [x,y] reference point}
                       (insertion point, else center, else start point)
    set_text           handle, expect_text, new_text
    add_dimension      kind rotated|aligned, p1, p2, line_point, rotation_deg?, style?, layer?
    delete             expect: {handle: object_name}

Naming targets by Ontology element id (optional, any op)
    "elements": {"@door1": "obs_...", "2F3": "obs_..."}
    Each key stands for one handle of the op (expect keys, `handles`, `handle`):
      - a key starting with "@" is a placeholder; it is replaced by the handle the element resolves to,
        e.g. {"op": "delete", "elements": {"@d": "obs_1"}, "expect": {"@d": "AcDbBlockReference"}};
      - any other key is a handle the plan already pins; the element must resolve to that same handle.
    Before validation every id is fetched from the Ontology REST API (--ontology-url, else
    POWERCAD_ONTOLOGY_URL, else http://127.0.0.1:58000) and matched against the open drawing with the
    same rules as the ontology_locate tool (source file = open drawing, handle exists, entity plausible).
    The plan is refused unless every id is `matched`; the expected-geometry checks then run as usual on
    the resolved handles. Plans without `elements` never contact the Ontology.

All edits run inside one undo group: a single AutoCAD UNDO reverts the whole plan.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import math
import os
import sys
from collections.abc import Callable
from typing import Any

TOL = 0.5  # drawing units (mm)


def acad():
    import win32com.client  # noqa: PLC0415 - Windows only

    app = win32com.client.GetActiveObject("AutoCAD.Application")
    return app, app.ActiveDocument


def vpt(p):
    import pythoncom  # noqa: PLC0415
    import win32com.client  # noqa: PLC0415

    x, y = p[0], p[1]
    z = p[2] if len(p) > 2 else 0.0
    return win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, (float(x), float(y), float(z)))


def vdoubles(vals):
    import pythoncom  # noqa: PLC0415
    import win32com.client  # noqa: PLC0415

    return win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, tuple(float(v) for v in vals))


def poly_points(obj) -> list[list[float]]:
    c = list(obj.Coordinates)
    return [[c[i], c[i + 1]] for i in range(0, len(c), 2)]


def same_points(a, b, closed_ring: bool = True) -> bool:
    """Equal vertex lists, allowing a different start vertex / direction for closed rings."""
    if len(a) != len(b):
        return False
    close = lambda p, q: abs(p[0] - q[0]) <= TOL and abs(p[1] - q[1]) <= TOL  # noqa: E731
    n = len(a)
    variants = [b, list(reversed(b))] if closed_ring else [b]
    for v in variants:
        for s in range(n if closed_ring else 1):
            if all(close(a[i], v[(i + s) % n]) for i in range(n)):
                return True
    return False


def near(p, q) -> bool:
    return abs(p[0] - q[0]) <= TOL and abs(p[1] - q[1]) <= TOL


def ref_point(obj) -> list[float]:
    """Reference point used to pin an entity: insertion point, else center, else start point."""
    for attr in ("InsertionPoint", "Center", "StartPoint"):
        try:
            return list(getattr(obj, attr))[:2]
        except Exception:  # noqa: BLE001 - property not on this type
            continue
    raise ValueError(f"{obj.ObjectName} has no reference point")


def get(doc, handle):
    try:
        return doc.HandleToObject(handle)
    except Exception:  # noqa: BLE001
        return None


# ---------------------------------------------------------------- Ontology element ids -> handles


def _ontology():
    """power_cad_mcp.ontology (stdlib only), from the installed package or this checkout's src/."""
    try:
        from power_cad_mcp import ontology  # noqa: PLC0415
    except ImportError:
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src"))
        from power_cad_mcp import ontology  # noqa: PLC0415
    return ontology


def open_drawing_info(doc) -> dict[str, Any]:
    """Name/path of the active document, the same fields the COM backend's drawing_info reports."""
    path = None
    with contextlib.suppress(Exception):
        path = doc.FullName or None
    return {"name": doc.Name, "path": path}


def com_lookup(doc) -> Callable[[str], dict[str, Any] | None]:
    """lookup(handle) -> {handle, type, layer, name} like the backend's get_entity, None if missing."""
    from power_cad_mcp.backends.com_backend import dxf_type  # noqa: PLC0415 - no COM import at module level

    def lookup(handle: str) -> dict[str, Any] | None:
        # Model space only: a handle of a layer record, a block-definition entity or a paper-space
        # entity is not something the plan may edit, and any COM failure reads as "not there".
        try:
            o = get(doc, handle)
            if o is None or o.OwnerID != doc.ModelSpace.ObjectID:
                return None
            out = {"handle": o.Handle, "type": dxf_type(o.ObjectName), "layer": o.Layer}
            if out["type"] == "INSERT":
                name = None
                with contextlib.suppress(Exception):
                    name = o.EffectiveName
                out["name"] = name or o.Name
            return out
        except Exception:  # noqa: BLE001
            return None

    return lookup


def _handle_slots(op: dict[str, Any]) -> list[str]:
    """Every handle an op names (expect keys, handles, handle), in plan order."""
    slots: list[str] = []
    if isinstance(op.get("expect"), dict):
        slots += list(op["expect"])
    slots += list(op.get("handles") or [])
    if op.get("handle"):
        slots.append(op["handle"])
    return slots


def _duplicate_handles(op: dict[str, Any], mapping: dict[str, str] | None = None) -> list[str]:
    """Handles named twice within one slot (expect keys, or `handles`), compared case-insensitively.

    AutoCAD handles are case-insensitive, so ``aa4`` and ``AA4`` are the same entity. The same handle in
    `expect` *and* `handles` (e.g. scale) is one target named in two places, not a duplicate.
    """
    mapping = mapping or {}
    groups = [list(op["expect"]) if isinstance(op.get("expect"), dict) else [], list(op.get("handles") or [])]
    dups: list[str] = []
    for group in groups:
        seen: set[str] = set()
        for h in group:
            key = str(mapping.get(h, h)).upper()
            if key in seen and key not in dups:
                dups.append(key)
            seen.add(key)
    return dups


def _substitute(op: dict[str, Any], mapping: dict[str, str]) -> dict[str, Any]:
    sub = lambda h: mapping.get(h, h)  # noqa: E731
    out = dict(op)
    if isinstance(op.get("expect"), dict):
        out["expect"] = {sub(h): v for h, v in op["expect"].items()}
    if op.get("handles"):
        out["handles"] = [sub(h) for h in op["handles"]]
    if op.get("handle"):
        out["handle"] = sub(op["handle"])
    return out


def resolve_elements(
    plan: dict[str, Any],
    client: Any,
    open_drawing: dict[str, Any] | None,
    lookup: Callable[[str], dict[str, Any] | None],
) -> tuple[dict[str, Any], list[dict[str, Any]], list[str]]:
    """Replace Ontology element ids (op["elements"]) by verified handles of the open drawing.

    Returns (resolved plan, resolution log, errors). Read-only: only the Ontology GET endpoint and
    ``lookup`` are called. Any error means the plan must be refused.
    """
    ops = plan.get("ops") or []
    named = [op for op in ops if op.get("elements")]
    if not named:
        return plan, [], []
    errors: list[str] = []
    ids = [str(eid) for op in named if isinstance(op["elements"], dict) for eid in op["elements"].values()]
    report = _ontology().locate(client, ids, open_drawing, lookup)
    # locate answers in request order (stripped, de-duplicated); key by the requested id so an API that
    # answers with another element's row cannot be mistaken for a missing one.
    requested = list(dict.fromkeys(i.strip() for i in ids if i.strip()))
    by_id = dict(zip(requested, report["results"], strict=True))

    resolved_ops, log = [], []
    for op in ops:
        elements = op.get("elements")
        if not elements:
            resolved_ops.append(op)
            continue
        tag = f"[{op.get('id')}] {op.get('op')}"
        if not isinstance(elements, dict):
            errors.append(f"{tag}: elements must be an object {{handle or @alias: element_id}}")
            resolved_ops.append(op)
            continue
        slots = _handle_slots(op)
        mapping: dict[str, str] = {}
        for key, eid in elements.items():
            r = by_id.get(str(eid).strip(), {"status": "not_found"})
            entry = {"op": op.get("id"), "key": key, "element_id": eid, "status": r["status"]}
            if r.get("handle"):
                entry["handle"] = r["handle"]
            log.append(entry)
            if key not in slots:
                errors.append(f"{tag}: elements key {key!r} is not used by the op (expect/handles/handle)")
            if str(r.get("error") or "").startswith("API returned element"):
                errors.append(f"{tag}: element {eid}: {r['error']}")
                continue
            if r["status"] != "matched":
                detail = "; ".join(r.get("reasons") or []) or r.get("note") or r.get("error") or ""
                errors.append(
                    f"{tag}: element {eid} is {r['status']}, not matched" + (f" ({detail})" if detail else "")
                )
                continue
            handle = str(r["handle"])
            if not key.startswith("@") and key.upper() != handle.upper():
                errors.append(f"{tag}: element {eid} resolves to handle {handle}, but the plan pins {key}")
                continue
            mapping[key] = handle
        dangling = [h for h in slots if h.startswith("@") and h not in elements]
        if dangling:
            errors.append(f"{tag}: {dangling} have no entry in elements")
        new = _substitute(op, mapping)
        if _duplicate_handles(op, mapping):
            errors.append(f"{tag}: two targets resolve to the same handle")
        new.pop("elements", None)
        resolved_ops.append(new)
    return {**plan, "ops": resolved_ops}, log, errors


# ---------------------------------------------------------------- validation (no side effects)


def validate(doc, plan) -> list[str]:
    errors = []
    if plan.get("document") and plan["document"] != doc.Name:
        errors.append(f"active document is {doc.Name}, plan is for {plan['document']}")
    for op in plan["ops"]:
        tag = f"[{op['id']}] {op['op']}"
        kind = op["op"]
        dups = _duplicate_handles(op)
        if dups:
            errors.append(f"{tag}: duplicate target handles {dups} (handles are case-insensitive)")
            continue
        if kind == "replace_polylines":
            for h, pts in op["expect"].items():
                o = get(doc, h)
                if o is None or o.ObjectName != "AcDbPolyline":
                    errors.append(f"{tag}: {h} missing or not a polyline")
                elif not same_points(poly_points(o), pts):
                    errors.append(f"{tag}: {h} geometry changed: {poly_points(o)}")
        elif kind == "scale":
            if sorted(op["expect"]) != sorted(op["handles"]):
                errors.append(f"{tag}: every scaled handle needs an expected reference point")
            for h, ip in op["expect"].items():
                o = get(doc, h)
                if o is None:
                    errors.append(f"{tag}: {h} missing")
                    continue
                cur = ref_point(o)
                if not near(cur, ip):
                    errors.append(f"{tag}: {h} moved: {cur}")
        elif kind == "set_text":
            o = get(doc, op["handle"])
            if o is None or o.TextString != op["expect_text"]:
                errors.append(f"{tag}: {op['handle']} text is not {op['expect_text']!r}")
        elif kind == "delete":
            for h, name in op["expect"].items():
                o = get(doc, h)
                if o is None or o.ObjectName != name:
                    errors.append(f"{tag}: {h} missing or not {name}")
        elif kind == "add_dimension":
            if near(op["p1"], op["p2"]):
                errors.append(f"{tag}: p1 == p2")
        else:
            errors.append(f"{tag}: unknown op")
    return errors


# ---------------------------------------------------------------- apply + verify


def copy_props(src, dst) -> None:
    dst.Layer = src.Layer
    dst.Linetype = src.Linetype
    dst.Lineweight = src.Lineweight
    dst.TrueColor = src.TrueColor
    with contextlib.suppress(Exception):  # mixed widths may not expose ConstantWidth
        dst.ConstantWidth = src.ConstantWidth


def apply(doc, plan) -> list[dict[str, Any]]:
    ms = doc.ModelSpace
    log = []
    for op in plan["ops"]:
        kind = op["op"]
        entry = {"id": op["id"], "op": kind}
        if kind == "replace_polylines":
            srcs = [get(doc, h) for h in op["expect"]]
            created = []
            for spec in op["new"]:
                flat = [c for p in spec["points"] for c in p[:2]]
                pl = ms.AddLightWeightPolyline(vdoubles(flat))
                pl.Closed = bool(spec.get("closed", True))
                copy_props(srcs[0], pl)
                if spec.get("layer"):
                    pl.Layer = spec["layer"]
                created.append(pl.Handle)
            for s in srcs:
                s.Delete()
            entry.update(deleted=list(op["expect"]), created=created)
            # post-check
            for h, spec in zip(created, op["new"], strict=True):
                if not same_points(poly_points(get(doc, h)), spec["points"]):
                    raise RuntimeError(f"[{op['id']}] created polyline {h} does not match spec")
        elif kind == "scale":
            base = vpt(op["base"])
            for h in op["handles"]:
                get(doc, h).ScaleEntity(base, float(op["factor"]))
            entry.update(scaled=op["handles"])
            f, b = op["factor"], op["base"]
            for h, ip in op["expect"].items():
                want = [b[0] + f * (ip[0] - b[0]), b[1] + f * (ip[1] - b[1])]
                got = ref_point(get(doc, h))
                if not near(got, want):
                    raise RuntimeError(f"[{op['id']}] {h} at {got}, expected {want}")
        elif kind == "set_text":
            o = get(doc, op["handle"])
            o.TextString = op["new_text"]
            if get(doc, op["handle"]).TextString != op["new_text"]:
                raise RuntimeError(f"[{op['id']}] text not applied")
            entry.update(handle=op["handle"], before=op["expect_text"], after=op["new_text"])
        elif kind == "delete":
            for h in op["expect"]:
                get(doc, h).Delete()
            entry.update(deleted=list(op["expect"]))
        elif kind == "add_dimension":
            if op.get("kind", "rotated") == "aligned":
                dim = ms.AddDimAligned(vpt(op["p1"]), vpt(op["p2"]), vpt(op["line_point"]))
            else:
                dim = ms.AddDimRotated(
                    vpt(op["p1"]),
                    vpt(op["p2"]),
                    vpt(op["line_point"]),
                    math.radians(op.get("rotation_deg", 0)),
                )
            if op.get("style"):
                dim.StyleName = op["style"]
            if op.get("layer"):
                dim.Layer = op["layer"]
            dim.Update()
            entry.update(created=dim.Handle, measurement=round(dim.Measurement, 3))
        log.append(entry)
    return log


def dump_dims(doc, window=None):
    out = []
    for o in doc.ModelSpace:
        if "Dimension" not in o.ObjectName:
            continue
        tp = list(o.TextPosition)[:2]
        if window and not (window[0] <= tp[0] <= window[2] and window[1] <= tp[1] <= window[3]):
            continue
        out.append(
            {
                "handle": o.Handle,
                "type": o.ObjectName,
                "layer": o.Layer,
                "style": o.StyleName,
                "measurement": round(o.Measurement, 3),
                "text_position": [round(v, 1) for v in tp],
                "text_override": o.TextOverride,
            }
        )
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("plan", nargs="?")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--dump-dims", nargs="*", type=float)
    ap.add_argument(
        "--ontology-url", help="Ontology REST API for `elements` (default: POWERCAD_ONTOLOGY_URL)"
    )
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    _, doc = acad()

    if a.dump_dims is not None:
        win = a.dump_dims if len(a.dump_dims) == 4 else None
        print(json.dumps(dump_dims(doc, win), ensure_ascii=False, indent=1))
        return 0

    with open(a.plan, encoding="utf-8") as f:
        plan = json.load(f)
    resolved: list[dict[str, Any]] = []
    if any(op.get("elements") for op in plan["ops"]):
        onto = _ontology()
        url = a.ontology_url or os.environ.get("POWERCAD_ONTOLOGY_URL") or "http://127.0.0.1:58000"
        try:
            client = onto.OntologyClient(url, token=os.environ.get("POWERCAD_ONTOLOGY_TOKEN"))
            plan, resolved, errors = resolve_elements(plan, client, open_drawing_info(doc), com_lookup(doc))
        except Exception as exc:  # noqa: BLE001 - any resolution failure refuses the plan, no traceback
            errors = [str(exc) if isinstance(exc, onto.OntologyError) else f"{type(exc).__name__}: {exc}"]
        if errors:
            out = {"ok": False, "stage": "resolve", "errors": errors, "resolved": resolved}
            print(json.dumps(out, ensure_ascii=False, indent=1))
            return 2
    errors = validate(doc, plan)
    if errors:
        out = {"ok": False, "stage": "validate", "errors": errors}
        if resolved:
            out["resolved"] = resolved
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 2
    if not a.apply:
        out = {"ok": True, "stage": "validate", "ops": len(plan["ops"])}
        if resolved:
            out["resolved"] = resolved
        print(json.dumps(out, ensure_ascii=False))
        return 0

    doc.StartUndoMark()
    try:
        log = apply(doc, plan)
    except Exception as exc:  # noqa: BLE001
        doc.EndUndoMark()
        print(
            json.dumps(
                {
                    "ok": False,
                    "stage": "apply",
                    "error": str(exc),
                    "recover": "run UNDO once in AutoCAD to revert this plan",
                },
                ensure_ascii=False,
            )
        )
        return 3
    doc.EndUndoMark()
    doc.Regen(1)
    out = {"ok": True, "stage": "applied", "log": log}
    if resolved:
        out["resolved"] = resolved
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
