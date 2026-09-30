"""Drawing census: an inventory of a DXF that proves nothing was skipped.

Every entity in the file is put in exactly one bucket (model space by sheet, a paper-space layout, or a
block definition), and the report checks that the buckets add up to the total ezdxf read from the file.
On top of that it lists every table (layers, linetypes, text/dim styles, blocks), every text string,
every hatch setting and every entity type the MCP query tools cannot see (leaders, proxies, xrefs, OLE),
and compares the layers with the ZIUM standard so unmapped layers show up.

DWG files are read through the ODA File Converter when installed; otherwise export DXF first
(AutoCAD ``DXFOUT``).

Usage::

    python -m power_cad_mcp.census drawing.dxf \
        --standard docs/standards/floor_plan_standard.json --out census/
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

import ezdxf
from ezdxf import bbox
from ezdxf.document import Drawing
from ezdxf.entities import DXFEntity

# Structural sub-records carried by their parent (POLYLINE vertices, end-of-sequence markers).
SUB_ENTITY_TYPES = {"SEQEND", "VERTEX"}
TEXT_TYPES = {"TEXT", "MTEXT", "ATTRIB", "ATTDEF"}
# Entity types the power-cad query tools do not return, or that hide content from a plain entity walk.
BLIND_SPOT_TYPES = {
    "LEADER",
    "MULTILEADER",
    "ACAD_PROXY_ENTITY",
    "OLE2FRAME",
    "OLEFRAME",
    "IMAGE",
    "UNDERLAY",
    "PDFUNDERLAY",
    "DWFUNDERLAY",
    "DGNUNDERLAY",
    "WIPEOUT",
    "ACAD_TABLE",
    "REGION",
    "3DSOLID",
    "BODY",
}


def _text_of(e: DXFEntity) -> str | None:
    t = e.dxftype()
    if t == "MTEXT":
        return e.plain_text()
    if t in TEXT_TYPES:
        return e.dxf.get("text", "")
    if t == "DIMENSION":
        override = e.dxf.get("text", "")
        return override if override not in ("", "<>") else None
    if t == "MULTILEADER":
        ctx = getattr(e, "context", None)
        mt = getattr(ctx, "mtext", None) if ctx else None
        return getattr(mt, "default_content", None) if mt else None
    return None


def _height_of(e: DXFEntity) -> float:
    key = "char_height" if e.dxftype() == "MTEXT" else "height"
    return (e.dxf.get(key, 0) if e.dxf.is_supported(key) else 0) or 0


def _sheet_frames(doc: Drawing, standard: dict[str, Any] | None) -> list[dict[str, Any]]:
    """Title-block inserts in model space → sheet rectangles with the A3 scale from the ZIUM formula."""
    sheet = (standard or {}).get("sheet") or {}
    name = sheet.get("block")
    native = sheet.get("native_size")
    if not name or not native:
        return []
    frames = []
    for ins in doc.modelspace().query(f'INSERT[name=="{name}"]'):
        sx, sy = abs(ins.dxf.xscale), abs(ins.dxf.yscale)
        x, y = ins.dxf.insert.x, ins.dxf.insert.y
        frames.append(
            {
                "handle": ins.dxf.handle,
                "insert": [round(x, 1), round(y, 1)],
                "block_scale": round(sx, 4),
                "a3_scale": f"1/{round(sx * 200)}",
                "rect": [x, y, x + native[0] * sx, y + native[1] * sy],
                "rotated": bool(ins.dxf.get("rotation", 0)),
            }
        )
    frames.sort(key=lambda f: (-f["rect"][1], f["rect"][0]))
    for i, f in enumerate(frames, 1):
        f["id"] = f"S{i:02d}"
    return frames


def _sheet_of(e: DXFEntity, frames: list[dict[str, Any]], cache: bbox.Cache) -> str:
    if not frames:
        return "model"
    try:
        box = bbox.extents([e], cache=cache)
    except Exception:  # noqa: BLE001 - odd geometry must not stop the census
        box = None
    if box is None or not box.has_data:
        return "no_extents"
    cx, cy = box.center.x, box.center.y
    for f in frames:
        x0, y0, x1, y1 = f["rect"]
        if x0 <= cx <= x1 and y0 <= cy <= y1:
            return f["id"]
    return "outside_sheets"


def census(doc: Drawing, standard: dict[str, Any] | None = None) -> dict[str, Any]:
    frames = _sheet_frames(doc, standard)
    cache = bbox.Cache()

    buckets: dict[str, Counter] = defaultdict(Counter)  # bucket -> entity type counts
    layer_use: Counter = Counter()
    layer_types: dict[str, Counter] = defaultdict(Counter)
    texts: list[dict[str, Any]] = []
    hatches: Counter = Counter()
    dims: Counter = Counter()
    blind: list[dict[str, Any]] = []
    insert_counts: Counter = Counter()
    seen_handles: set[str] = set()

    def visit(e: DXFEntity, bucket: str) -> None:
        t = e.dxftype()
        buckets[bucket][t] += 1
        seen_handles.add(e.dxf.handle)
        layer = e.dxf.get("layer", "0")
        layer_use[layer] += 1
        layer_types[layer][t] += 1
        if t == "INSERT":
            insert_counts[e.dxf.name] += 1
            for att in e.attribs:
                visit(att, bucket)
        text = _text_of(e)
        if text:
            texts.append(
                {
                    "bucket": bucket,
                    "layer": layer,
                    "type": t,
                    "handle": e.dxf.handle,
                    "height": round(_height_of(e), 1),
                    "text": text.strip(),
                }
            )
        if t == "HATCH":
            hatches[
                (
                    e.dxf.pattern_name,
                    round(e.dxf.get("pattern_scale", 1), 3),
                    round(e.dxf.get("pattern_angle", 0), 1),
                    layer,
                )
            ] += 1
        if t == "DIMENSION":
            dims[e.dxf.get("dimstyle", "Standard")] += 1
        if t in BLIND_SPOT_TYPES:
            blind.append({"bucket": bucket, "type": t, "layer": layer, "handle": e.dxf.handle})

    for e in doc.modelspace():
        visit(e, f"model:{_sheet_of(e, frames, cache)}")
    for layout in doc.layouts:
        if layout.is_modelspace:
            continue
        for e in layout:
            visit(e, f"layout:{layout.name}")
    block_defs = []
    for blk in doc.blocks:
        if blk.name.startswith("*") and blk.name.upper().startswith(("*MODEL_SPACE", "*PAPER_SPACE")):
            continue  # layout blocks were walked above
        ents = list(blk)
        block_defs.append(
            {
                "name": blk.name,
                "anonymous": blk.name.startswith("*") or blk.name.upper().startswith("A$C"),
                "xref": bool(blk.block and blk.block.is_xref),
                "entities": len(ents),
            }
        )
        for e in ents:
            visit(e, f"block:{blk.name}")

    # Completeness proof: every graphical entity in the entity database was visited exactly once.
    all_graphic = [
        e
        for e in doc.entitydb.values()
        if getattr(e, "is_alive", True)
        and e.dxf.hasattr("owner")
        and isinstance(e, ezdxf.entities.DXFGraphic)
        and e.dxftype() not in SUB_ENTITY_TYPES
    ]
    missed = [e for e in all_graphic if e.dxf.handle not in seen_handles]
    visited = sum(sum(c.values()) for c in buckets.values())

    std_layers = set((standard or {}).get("layers", {}))
    merge_map = {
        k: v for k, v in (standard or {}).get("layer_merge_map", {}).items() if not k.startswith("$")
    }
    layers = []
    for lay in doc.layers:
        name = lay.dxf.name
        status = "standard" if name in std_layers else ("mapped" if name in merge_map else "unmapped")
        if status == "mapped" and merge_map[name] not in std_layers:
            # The value is a content rule (e.g. HATCH -> INS or HAT), not a single target layer.
            status = "conditional"
        layers.append(
            {
                "name": name,
                "color": lay.color,
                "linetype": lay.dxf.get("linetype", "Continuous"),
                "lineweight": lay.dxf.get("lineweight", -3),
                "on": lay.is_on(),
                "frozen": lay.is_frozen(),
                "locked": lay.is_locked(),
                "plot": bool(lay.dxf.get("plot", 1)),
                "entities": layer_use.get(name, 0),
                "types": dict(layer_types.get(name, {})),
                "standard_status": status,
                "maps_to": merge_map.get(name),
            }
        )
    undeclared = sorted(set(layer_use) - {lay["name"] for lay in layers})

    blocks_used = {b["name"]: insert_counts.get(b["name"], 0) for b in block_defs}
    return {
        "file": doc.filename,
        "dxf_version": doc.dxfversion,
        "units": doc.units,
        "completeness": {
            "entities_in_db": len(all_graphic),
            "entities_visited": visited,
            "missed": [{"handle": e.dxf.handle, "type": e.dxftype()} for e in missed[:200]],
            "complete": not missed,
        },
        "sheets": [{k: v for k, v in f.items() if k != "rect"} for f in frames],
        "buckets": {k: dict(v) for k, v in sorted(buckets.items())},
        "layers": layers,
        "undeclared_layers": undeclared,
        # Any layer that holds entities (declared in the layer table or not) and has no standard mapping.
        "unmapped_layers": sorted(n for n in layer_use if n not in std_layers and n not in merge_map),
        "empty_layers": [lay["name"] for lay in layers if not lay["entities"]],
        "blocks": [dict(b, inserts=blocks_used[b["name"]]) for b in block_defs],
        "unused_blocks": [n for n, c in blocks_used.items() if c == 0 and not n.startswith("*")],
        "linetypes": [lt.dxf.name for lt in doc.linetypes],
        "text_styles": [
            {"name": s.dxf.name, "font": s.dxf.get("font", ""), "bigfont": s.dxf.get("bigfont", "")}
            for s in doc.styles
        ],
        "dim_styles": {d.dxf.name: dims.get(d.dxf.name, 0) for d in doc.dimstyles},
        "hatches": [
            {"pattern": p, "scale": s, "angle": a, "layer": lay, "count": c}
            for (p, s, a, lay), c in hatches.most_common()
        ],
        "blind_spots": blind,
        "texts": texts,
    }


def to_markdown(r: dict[str, Any]) -> str:
    c = r["completeness"]
    out = [
        f"# 도면 인벤토리 — {Path(r['file'] or '').name}",
        "",
        f"- DXF {r['dxf_version']}, 단위 코드 {r['units']}",
        f"- **완전성: {'통과' if c['complete'] else '누락 있음'}** — "
        f"DB 객체 {c['entities_in_db']}, 방문 {c['entities_visited']}, 누락 {len(c['missed'])}",
        "",
    ]
    if r["sheets"]:
        out += [
            "## 시트 (도곽 기준)",
            "",
            "| ID | 도곽 핸들 | 삽입점 | 배율 | A3 축척 | 객체 수 |",
            "| --- | --- | --- | --- | --- | --- |",
        ]
        for s in r["sheets"]:
            n = sum(r["buckets"].get(f"model:{s['id']}", {}).values())
            out.append(
                f"| {s['id']} | {s['handle']} | {s['insert']} | {s['block_scale']} | {s['a3_scale']} | {n} |"
            )
        for extra in ("model:outside_sheets", "model:no_extents"):
            if extra in r["buckets"]:
                out.append(
                    f"\n- `{extra}`: {sum(r['buckets'][extra].values())}개 — 도곽 밖/범위 없음, 반드시 확인"
                )
        out.append("")
    out += ["## 칸별 객체 수", "", "| 칸 | 합계 | 종류 |", "| --- | --- | --- |"]
    for b, types in r["buckets"].items():
        if b.startswith("block:"):
            continue
        out.append(
            f"| {b} | {sum(types.values())} | {', '.join(f'{t} {n}' for t, n in sorted(types.items()))} |"
        )
    blk_total = sum(sum(v.values()) for k, v in r["buckets"].items() if k.startswith("block:"))
    out.append(f"| 블록 정의 {len(r['blocks'])}개 | {blk_total} | (blocks 절 참조) |")
    out += [
        "",
        "## 레이어",
        "",
        f"- 미편입(표준·편입표 모두 없음, 객체 있음): {', '.join(r['unmapped_layers']) or '없음'}",
        f"- 빈 레이어: {len(r['empty_layers'])}개",
        f"- 레이어 표에 없는데 쓰인 이름: {', '.join(r['undeclared_layers']) or '없음'}",
        "",
        "| 레이어 | 색 | 선종류 | 객체 | 상태 | 편입 | 잠금/동결/꺼짐 |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for lay in sorted(r["layers"], key=lambda x: -x["entities"]):
        flags = (
            "".join(f for f, on in (("L", lay["locked"]), ("F", lay["frozen"]), ("X", not lay["on"])) if on)
            or "-"
        )
        out.append(
            f"| `{lay['name']}` | {lay['color']} | {lay['linetype']} | {lay['entities']} "
            f"| {lay['standard_status']} | {lay['maps_to'] or ''} | {flags} |"
        )
    out += [
        "",
        "## 블록",
        "",
        "| 블록 | 삽입 수 | 내부 객체 | 익명 | XREF |",
        "| --- | --- | --- | --- | --- |",
    ]
    for b in sorted(r["blocks"], key=lambda x: -x["inserts"]):
        if b["name"].startswith("*") and not b["inserts"]:
            continue
        out.append(
            f"| `{b['name']}` | {b['inserts']} | {b['entities']} "
            f"| {'Y' if b['anonymous'] else ''} | {'Y' if b['xref'] else ''} |"
        )
    out += [
        "",
        f"- 정의만 있고 삽입 안 된 블록: {len(r['unused_blocks'])}개",
        "",
        "## 해치",
        "",
        "| 패턴 | 축척 | 각도 | 레이어 | 개수 |",
        "| --- | --- | --- | --- | --- |",
    ]
    out += [
        f"| {h['pattern']} | {h['scale']} | {h['angle']} | `{h['layer']}` | {h['count']} |"
        for h in r["hatches"]
    ]
    out += ["", "## 치수 스타일", ""] + [f"- `{k}`: {v}" for k, v in r["dim_styles"].items()]
    bs = Counter((b["type"], b["bucket"].split(":")[0]) for b in r["blind_spots"])
    out += ["", "## 조회 도구 사각지대 (LEADER·프록시·OLE·이미지 등)", ""]
    out += [f"- {t} ({where}): {n}" for (t, where), n in bs.most_common()] or ["- 없음"]
    out += ["", f"## 문자 {len(r['texts'])}개 — 전체 목록은 census.json의 texts", ""]
    return "\n".join(out) + "\n"


def load(path: Path) -> Drawing:
    """Read a DXF, or a DWG through the ODA File Converter when it is installed (ezdxf odafc add-on)."""
    if not path.exists():
        raise ValueError(f"파일이 없습니다: {path}")
    if path.suffix.lower() == ".dwg":
        from ezdxf.addons import odafc

        if not odafc.is_installed():
            raise ValueError(
                "DWG는 먼저 DXF로 변환하세요 (AutoCAD DXFOUT) 또는 ODA File Converter를 설치하세요."
            )
        return odafc.readfile(str(path))
    return ezdxf.readfile(path)


def run(path: Path, standard: Path | None, out: Path) -> dict[str, Any]:
    """Census ``path``, write census.json + census.md into ``out``, and return a short summary."""
    std = json.loads(standard.read_text(encoding="utf-8")) if standard else None
    r = census(load(path), std)
    out.mkdir(parents=True, exist_ok=True)
    (out / "census.json").write_text(json.dumps(r, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "census.md").write_text(to_markdown(r), encoding="utf-8")
    return {
        "completeness": r["completeness"],
        "sheets": r["sheets"],
        "bucket_totals": {k: sum(v.values()) for k, v in r["buckets"].items() if not k.startswith("block:")},
        "unmapped_layers": r["unmapped_layers"],
        "blind_spots": len(r["blind_spots"]),
        "texts": len(r["texts"]),
        "report": str(out / "census.md"),
        "data": str(out / "census.json"),
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Inventory every entity, table and text in a DXF and prove none was skipped."
    )
    ap.add_argument("dxf", type=Path)
    ap.add_argument(
        "--standard", type=Path, help="ZIUM floor_plan_standard.json (layers, merge map, sheet block)"
    )
    ap.add_argument("--out", type=Path, default=Path("census"))
    a = ap.parse_args(argv)
    try:
        summary = run(a.dxf, a.standard, a.out)
    except ValueError as exc:
        print(exc, file=sys.stderr)
        return 2
    c = summary["completeness"]
    print(
        f"{'COMPLETE' if c['complete'] else 'MISSED ' + str(len(c['missed']))}: "
        f"{c['entities_visited']} visited / {c['entities_in_db']} in file → {summary['report']}"
    )
    return 0 if c["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
