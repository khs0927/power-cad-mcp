"""Reconcile a drawing census (``census.py``) with what the Ontology holds for the same file.

The census is the local, complete inventory of one DXF; the Ontology is the building-data store the
file was ingested into. They count different things — the census counts *entities*, the Ontology
*elements* (a door drawn as LINE + ARC is two entities and one or two elements; a door block is one
entity) — so this module only compares what really lines up and labels everything else
``not_comparable`` instead of inventing an equivalence:

* **ingestion** — is there an Ontology document whose name / source path is this file
  (basename, case-insensitive, ``.dwg`` = ``.dxf``)?
* **handles** — every Ontology element that carries a handle is looked up in the census entity
  index: handles only in the Ontology mean the file changed since ingestion (or another revision was
  ingested); layout entities only in the census are entities the Ontology did not turn into elements.
* **layers** — the Ontology's ``Layer`` rows count entities per layer in layouts and in block
  definitions, the same scope as the census buckets, so those numbers are compared one to one.
* **classes** — per Ontology class: element count, the census entities on the layers those elements
  sit on, and the census entities on the ZIUM standard layers for that class (``DOOR``, ``WIN`` ...),
  when the census was run with the standard. These are shown side by side, never called equal.

Everything here is pure (dicts in, dict out) except :func:`census_check`, which reads the API.
"""

from __future__ import annotations

import os
from collections import Counter, defaultdict
from datetime import datetime, timezone
from typing import Any

from .ontology import OntologyClient, drawing_key

__all__ = ["ZIUM_CLASS_LAYERS", "census_check", "compare", "pick_document"]

# Ontology class -> ZIUM standard layers (docs/standards/floor_plan_standard.json) that hold only that
# class. Elevation lines (DOOR_ELE, WINELE) are left out; COL mixes columns and structural walls.
ZIUM_CLASS_LAYERS: dict[str, tuple[str, ...]] = {
    "Door": ("DOOR",),
    "Window": ("WIN", "WINBAR"),
    "Wall": ("WAL1", "WAL2", "WAL3"),
    "Stair": ("STAIR",),
    "Space": ("실명",),
}
_ZIUM_MIXED = {
    "Column": "ZIUM layer COL holds columns and structural walls together (S-WALL maps to COL)",
}
# Ontology rows that describe the file rather than an entity in it.
_META_CLASSES = {"Document", "View", "Layer", "BlockDefinition", "Sheet", "Drawing"}
# Census rows that belong to a parent entity (an INSERT's attributes) and are never an element.
_SUB_TYPES = {"ATTRIB"}
_ENTITY_NOT_ELEMENT = "an entity count is not an element count (a door of LINE + ARC is 2 entities)"


def _norm_handle(value: Any) -> str | None:
    text = str(value or "").strip().upper().lstrip("0")
    return text or None


def _when(value: Any) -> datetime | None:
    try:
        parsed = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def document_summary(doc: dict[str, Any]) -> dict[str, Any]:
    keys = ("document_id", "project_id", "name", "source_key", "revision", "updated_at", "element_counts")
    return {k: doc[k] for k in keys if doc.get(k) not in (None, "", {}, [])}


def pick_document(
    docs: list[dict[str, Any]], path: str, project_id: str | None = None
) -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    """The ingested document for ``path`` and the other documents with the same file key.

    Matches ``name`` or ``source_key`` by :func:`drawing_key`. With several (other projects or
    revisions) the one in ``project_id`` wins, then the highest revision, then the newest update.
    """
    key = drawing_key(path)
    same = [
        d
        for d in docs
        if key and key in {drawing_key(d.get(f)) for f in ("name", "source_key", "file", "document_name")}
    ]
    if project_id:
        same.sort(key=lambda d: d.get("project_id") != project_id)
    if not same:
        return None, []

    def rank(d: dict[str, Any]) -> tuple[bool, float, float]:
        when = _when(d.get("updated_at"))
        rev = d.get("revision")
        return (
            bool(project_id) and d.get("project_id") == project_id,
            float(rev) if isinstance(rev, (int, float)) else -1.0,
            when.timestamp() if when else 0.0,
        )

    best = max(same, key=rank)
    return best, [d for d in same if d is not best]


def _zium_layer(layer: dict[str, Any]) -> str | None:
    """The standard layer a census layer row resolves to, or None (unmapped / content-dependent)."""
    status = layer.get("standard_status")
    if status == "standard":
        return layer.get("name")
    if status == "mapped":
        return layer.get("maps_to")
    return None


def compare(
    report: dict[str, Any],
    document: dict[str, Any] | None,
    elements: list[dict[str, Any]],
    *,
    limit: int = 50,
) -> dict[str, Any]:
    """Compare a census report with one Ontology document's elements (``compact_element`` rows)."""
    not_comparable: list[dict[str, str]] = []
    warnings: list[str] = []
    out: dict[str, Any] = {"ingested": document is not None}
    if document is None:
        out["note"] = "the Ontology has no document for this file; nothing to compare"
        return out

    index = report.get("entity_index")
    if not isinstance(index, list):
        index = None
        not_comparable.append(
            {
                "item": "handles, per-layer counts",
                "reason": "the census has no entity_index (made by an older drawing_census); re-run it",
            }
        )
    rows = [r for r in index or [] if isinstance(r, list) and len(r) >= 4]
    census_handles: dict[str, list[str]] = {}
    for handle, etype, layer, bucket in (r[:4] for r in rows):
        h = _norm_handle(handle)
        if h:
            census_handles.setdefault(h, [str(handle), etype, layer, bucket])

    # ------------------------------------------------------------- handles
    with_handle: dict[str, list[dict[str, Any]]] = defaultdict(list)
    no_handle: Counter = Counter()
    for el in elements:
        h = _norm_handle(el.get("handle"))
        if h:
            with_handle[h].append(el)
        else:
            no_handle[str(el.get("class") or "?")] += 1
    if index is not None:
        only_onto = [
            {
                "handle": els[0].get("handle"),
                "classes": sorted({str(e.get("class")) for e in els}),
                "element_ids": [e.get("id") for e in els][:5],
                "layer": els[0].get("layer"),
                "sheet": els[0].get("sheet"),
            }
            for h, els in sorted(with_handle.items())
            if h not in census_handles
        ]
        layout_rows = [r for r in rows if not str(r[3]).startswith("block:") and r[1] not in _SUB_TYPES]
        only_census = [r for r in layout_rows if _norm_handle(r[0]) not in with_handle]
        out["handles"] = {
            "ontology_elements_with_handle": sum(len(v) for v in with_handle.values()),
            "ontology_handles": len(with_handle),
            "census_layout_entities": len(layout_rows),
            "in_both": sum(1 for h in with_handle if h in census_handles),
            "only_in_ontology": only_onto[:limit],
            "only_in_ontology_count": len(only_onto),
            "only_in_census_count": len(only_census),
            "only_in_census_by_type": dict(Counter(r[1] for r in only_census).most_common()),
            "only_in_census_by_layer": dict(Counter(r[2] for r in only_census).most_common(limit)),
            "only_in_census_sample": [
                {"handle": r[0], "type": r[1], "layer": r[2], "bucket": r[3]} for r in only_census[:limit]
            ],
            "note": "only_in_ontology: the file changed since ingestion (or another revision was ingested). "
            "only_in_census: layout entities the Ontology did not make an element of (dimensions, plain "
            "lines, hatches ...) — expected, not an error. Block-definition contents and ATTRIBs are "
            "left out: the Ontology does not make elements of them.",
        }
        block_rows = len(rows) - len(layout_rows)
        if block_rows:
            not_comparable.append(
                {
                    "item": f"{block_rows} census entities inside block definitions / ATTRIBs",
                    "reason": "the Ontology records block contents per definition, not as elements",
                }
            )
    if no_handle:
        not_comparable.append(
            {
                "item": ", ".join(f"{c} {n}" for c, n in sorted(no_handle.items())),
                "reason": "Ontology rows without a handle (file / layer / view records or derived "
                "elements) have no entity to look up",
            }
        )

    # ------------------------------------------------------------- layers
    census_layers = {str(lay.get("name")): lay for lay in report.get("layers") or [] if lay.get("name")}
    layer_split: dict[str, Counter] = defaultdict(Counter)
    for _, etype, layer, bucket in (r[:4] for r in rows):
        if etype in _SUB_TYPES:
            continue
        layer_split[str(layer)]["block" if str(bucket).startswith("block:") else "layout"] += 1
    onto_layers = {
        str((el.get("properties") or {}).get("name")): el.get("properties") or {}
        for el in elements
        if el.get("class") == "Layer" and (el.get("properties") or {}).get("name") is not None
    }
    by_layer_class: dict[str, Counter] = defaultdict(Counter)
    for el in elements:
        if el.get("class") not in _META_CLASSES and el.get("layer") is not None:
            by_layer_class[str(el["layer"])][str(el.get("class"))] += 1
    layers = []
    used = {n for n, lay in census_layers.items() if lay.get("entities")} | set(layer_split)
    for name in sorted(used | set(onto_layers) | set(by_layer_class)):
        lay = census_layers.get(name, {})
        props = onto_layers.get(name)
        row: dict[str, Any] = {"layer": name, "census_entities": lay.get("entities", 0)}
        if index is not None:
            row["census_layout"] = layer_split[name]["layout"]
            row["census_block"] = layer_split[name]["block"]
        if props is not None:
            row["ontology_layout"] = props.get("entity_count")
            row["ontology_block"] = props.get("block_entity_count")
            if index is not None and isinstance(props.get("entity_count"), int):
                same = (row["census_layout"], row["census_block"]) == (
                    props.get("entity_count"),
                    props.get("block_entity_count") or 0,
                )
                row["status"] = "equal" if same else "differs"
            else:
                row["status"] = "not_comparable"
        else:
            row["status"] = "not_in_ontology"
        if by_layer_class.get(name):
            row["ontology_elements"] = dict(by_layer_class[name])
        zium = _zium_layer(lay) if lay else None
        if zium:
            row["zium_layer"] = zium
        layers.append(row)
    out["layers"] = layers
    if not onto_layers:
        not_comparable.append(
            {"item": "per-layer entity counts", "reason": "the Ontology returned no Layer rows for this file"}
        )

    # ------------------------------------------------------------- classes
    by_class: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for el in elements:
        cls = str(el.get("class") or "?")
        if cls not in _META_CLASSES:
            by_class[cls].append(el)
    have_standard = report.get("standard_applied")
    if have_standard is None:  # census.json from before the flag: infer it from the layer statuses
        have_standard = any(
            lay.get("standard_status") in ("standard", "mapped", "conditional")
            for lay in census_layers.values()
        )
    zium_of = {name: _zium_layer(lay) for name, lay in census_layers.items()}
    classes = []
    for cls in sorted(set(by_class) | set(ZIUM_CLASS_LAYERS)):
        els = by_class.get(cls, [])
        handles = {_norm_handle(e.get("handle")) for e in els} - {None}
        on_layers = sorted({str(e["layer"]) for e in els if e.get("layer") is not None})
        row = {"class": cls, "ontology_elements": len(els)}
        if els:
            row["ontology_layers"] = on_layers
            row["census_entities_on_those_layers"] = sum(
                census_layers.get(n, {}).get("entities", 0) for n in on_layers
            )
            if index is not None and handles:
                row["handles_found_in_census"] = (
                    f"{sum(1 for h in handles if h in census_handles)}/{len(handles)}"
                )
        if cls in ZIUM_CLASS_LAYERS:
            targets = ZIUM_CLASS_LAYERS[cls]
            if not have_standard:
                row["zium"] = "not_comparable: the census was made without the ZIUM standard"
            else:
                src = sorted(n for n, z in zium_of.items() if z in targets)
                row["zium_layers"] = list(targets)
                row["census_layers_mapped"] = src
                row["census_entities_on_zium_layers"] = sum(census_layers[n].get("entities", 0) for n in src)
                if not src:
                    row["zium"] = f"no layer of this drawing maps to {'/'.join(targets)}"
        elif cls in _ZIUM_MIXED:
            row["zium"] = "not_comparable: " + _ZIUM_MIXED[cls]
        elif els:
            row["zium"] = "not_comparable: no ZIUM standard layer for this class"
        if els or row.get("census_entities_on_zium_layers"):
            classes.append(row)
    out["classes"] = classes
    out["classes_note"] = (
        "counts are side by side, not a match test: " + _ENTITY_NOT_ELEMENT + "; use `handles` for an "
        "exact check"
    )

    completeness = report.get("completeness") or {}
    if completeness and not completeness.get("complete", True):
        warnings.append("the census itself reports missed entities; its counts are a lower bound")
    out["not_comparable"] = not_comparable
    out["warnings"] = warnings
    return out


def census_check(
    client: OntologyClient,
    path: str,
    report: dict[str, Any],
    *,
    project_id: str | None = None,
    census_source: str = "file",
    limit: int = 50,
) -> dict[str, Any]:
    """Find ``path`` in the Ontology, read its elements and :func:`compare` them with ``report``."""
    project_id = (project_id or "").strip() or None
    key = drawing_key(path)
    docs = client.documents(
        lambda d: key in {drawing_key(d.get(f)) for f in ("name", "source_key", "file", "document_name")},
        project_id=project_id,
    )
    document, others = pick_document(docs, path, project_id)
    elements: list[dict[str, Any]] = []
    warnings: list[str] = []
    if document is not None and document.get("document_id"):
        elements, truncated = client.document_elements(
            str(document["document_id"]), project_id=document.get("project_id") or project_id
        )
        if truncated:
            warnings.append(f"stopped after {len(elements)} Ontology elements; counts are partial")
    result = compare(report, document, elements, limit=limit)
    census_key = drawing_key(report.get("file"))
    if census_key and key and census_key != key:
        warnings.append(f"the census is of {report.get('file')!r}, not of {os.path.basename(path)!r}")
    if document is not None:
        ingested_at = _when(document.get("updated_at"))
        try:
            mtime = datetime.fromtimestamp(os.path.getmtime(path), timezone.utc)
        except OSError:
            mtime = None
        if ingested_at and mtime and mtime > ingested_at:
            warnings.append(
                f"the file was modified ({mtime.isoformat(timespec='seconds')}) after the Ontology "
                f"ingested it ({ingested_at.isoformat(timespec='seconds')}); differences may be edits"
            )
    completeness = report.get("completeness") or {}
    return {
        "file": path,
        "drawing_key": key,
        "project_id": project_id,
        "ingested": document is not None,
        "document": document_summary(document) if document else None,
        "other_documents": [document_summary(d) for d in others],
        "census": {
            "source": census_source,
            "complete": completeness.get("complete"),
            "entities": completeness.get("entities_visited"),
        },
        "ontology_elements": len(elements),
        **{k: v for k, v in result.items() if k != "ingested"},
        "warnings": warnings + result.get("warnings", []),
        "read_only": True,
    }
