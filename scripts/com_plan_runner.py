"""Run a JSON edit plan against the live AutoCAD drawing over COM, with pre/post verification.

Covers the operations the loaded Power CAD plugin (0.2.0) cannot do yet: reading/creating DIMENSION
entities, deleting, merging polylines, and scaling. Every operation pins its targets by handle AND by
expected geometry, so a plan written for one drawing state is refused on any other.

    python scripts/com_plan_runner.py PLAN.json            # validate only (no changes)
    python scripts/com_plan_runner.py PLAN.json --apply    # validate, apply, verify
    python scripts/com_plan_runner.py --dump-dims [x0 y0 x1 y1]   # list dimensions (MCP 0.2.0 can't)

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

All edits run inside one undo group: a single AutoCAD UNDO reverts the whole plan.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import math
import sys
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


# ---------------------------------------------------------------- validation (no side effects)


def validate(doc, plan) -> list[str]:
    errors = []
    if plan.get("document") and plan["document"] != doc.Name:
        errors.append(f"active document is {doc.Name}, plan is for {plan['document']}")
    for op in plan["ops"]:
        tag = f"[{op['id']}] {op['op']}"
        kind = op["op"]
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
    with contextlib.suppress(Exception):  # mixed widths
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
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    _, doc = acad()

    if a.dump_dims is not None:
        win = a.dump_dims if len(a.dump_dims) == 4 else None
        print(json.dumps(dump_dims(doc, win), ensure_ascii=False, indent=1))
        return 0

    with open(a.plan, encoding="utf-8") as f:
        plan = json.load(f)
    errors = validate(doc, plan)
    if errors:
        print(json.dumps({"ok": False, "stage": "validate", "errors": errors}, ensure_ascii=False, indent=1))
        return 2
    if not a.apply:
        print(json.dumps({"ok": True, "stage": "validate", "ops": len(plan["ops"])}))
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
    print(json.dumps({"ok": True, "stage": "applied", "log": log}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
