"""Read-only client for the Ontology (``aec_intelligence``) building-data REST API.

The Ontology service is the canonical store of every element, block and sheet parsed from the
user's DWG/DXF archive. Power CAD only *reads* from it, so an automation can pull up the relevant
doors, windows, walls, detail sheets and blocks on its own instead of the user listing them.

The JSON shapes of the ``/v1`` endpoints are still settling, so everything here is deliberately
tolerant: lists may arrive bare or wrapped (``items``/``results``/``hits``/...), and element fields
are read through a set of aliases (``class``/``kind``/``type``, ``name``/``label`` ...).

Only the standard library is used (``urllib``) so the package gains no new dependencies.
"""

from __future__ import annotations

import ipaddress
import json
import re
import socket
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable, Iterable
from typing import Any

from .errors import CadError

__all__ = [
    "OntologyClient",
    "OntologyError",
    "OntologyUnavailable",
    "auto_context",
    "compact_element",
    "drawing_key",
    "in_drawing",
    "infer_task",
    "locate",
    "match_element",
    "rows",
    "targets_summary",
]


class OntologyError(CadError):
    """The Ontology service answered with an error (bad request, missing endpoint, bad JSON...)."""


class OntologyUnavailable(OntologyError):
    """The Ontology service could not be reached at all (not running, wrong URL, timeout)."""


def _is_loopback(host: str | None) -> bool:
    if not host:
        return False
    if host.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(host.strip("[]")).is_loopback
    except ValueError:
        return False


class OntologyClient:
    """Thin JSON-over-HTTP client. Every method raises :class:`OntologyError` on failure."""

    def __init__(self, base_url: str, timeout: float = 10.0, token: str | None = None):
        base_url = (base_url or "").strip().rstrip("/")
        parsed = urllib.parse.urlsplit(base_url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise OntologyError(
                "POWERCAD_ONTOLOGY_URL must be an http(s) URL such as http://127.0.0.1:58000 "
                f"(got {base_url!r})."
            )
        self.base_url = base_url
        self.timeout = float(timeout)
        self.token = token
        # A local Ontology API must never be routed through an outbound HTTP proxy.
        handlers = [urllib.request.ProxyHandler({})] if _is_loopback(parsed.hostname) else []
        self._opener = urllib.request.build_opener(*handlers)

    # ------------------------------------------------------------------ transport
    def _url(self, path: str, params: dict[str, Any] | None = None) -> str:
        url = self.base_url + "/" + path.lstrip("/")
        clean = {k: v for k, v in (params or {}).items() if v is not None and v != ""}
        if clean:
            url += "?" + urllib.parse.urlencode(clean, doseq=True)
        return url

    def request(self, method: str, path: str, params: dict[str, Any] | None = None, body: Any = None) -> Any:
        url = self._url(path, params)
        data = None
        headers = {"Accept": "application/json", "User-Agent": "power-cad-mcp"}
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with self._opener.open(req, timeout=self.timeout) as resp:
                raw = resp.read()
        except urllib.error.HTTPError as exc:
            detail = _error_detail(exc)
            if exc.code == 404 and not detail:
                raise OntologyError(
                    f"Ontology endpoint {method} {path} not found (HTTP 404) at {self.base_url}; "
                    "the Ontology API may be older than this client."
                ) from exc
            raise OntologyError(
                f"Ontology {method} {path} failed with HTTP {exc.code}" + (f": {detail}" if detail else ".")
            ) from exc
        except TimeoutError as exc:
            raise OntologyUnavailable(
                f"Ontology service at {self.base_url} did not answer within {self.timeout:g}s "
                "(raise POWERCAD_ONTOLOGY_TIMEOUT or check the service)."
            ) from exc
        except (urllib.error.URLError, ConnectionError, OSError) as exc:
            reason = getattr(exc, "reason", exc)
            if isinstance(reason, (TimeoutError, socket.timeout)):
                raise OntologyUnavailable(
                    f"Ontology service at {self.base_url} did not answer within {self.timeout:g}s "
                    "(raise POWERCAD_ONTOLOGY_TIMEOUT or check the service)."
                ) from exc
            raise OntologyUnavailable(
                f"Ontology service is not reachable at {self.base_url} ({reason}). Start the Ontology API "
                "(aec_intelligence) or point POWERCAD_ONTOLOGY_URL at it."
            ) from exc
        if not raw.strip():
            return {}
        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise OntologyError(f"Ontology {method} {path} returned non-JSON data.") from exc

    def get(self, path: str, **params: Any) -> Any:
        return self.request("GET", path, params)

    def post(self, path: str, body: Any) -> Any:
        return self.request("POST", path, body=body)

    # ------------------------------------------------------------------ endpoints
    def _pages(
        self,
        path: str,
        params: dict[str, Any],
        *,
        want: int,
        cursor: str | None = None,
        accept: Callable[[dict[str, Any]], bool] | None = None,
        max_pages: int = 10,
    ) -> tuple[list[dict[str, Any]], str | None]:
        """Follow the API's keyset ``next_cursor`` until ``want`` rows pass ``accept``.

        Without ``accept`` exactly one page is read and its ``next_cursor`` is handed back so the
        caller can continue. With client-side filtering several pages may be read (bounded by
        ``max_pages``); the returned cursor then continues after the last page read.
        """
        out: list[dict[str, Any]] = []
        page_size = min(max(want, 1), MAX_PAGE) if accept is None else MAX_PAGE
        for _ in range(max_pages if accept else 1):
            payload = self.get(path, **params, limit=page_size, cursor=cursor)
            for row in rows(payload, "elements", "blocks", "drawings", "sheets"):
                if accept is None or accept(row):
                    out.append(row)
            cursor = payload.get("next_cursor") if isinstance(payload, dict) else None
            if len(out) >= want or not cursor:
                break
        return out[:want], cursor

    def catalog(self, project_id: str | None = None) -> Any:
        return self.get("/v1/catalog", project_id=project_id)

    def elements(
        self,
        kind: str | None = None,
        *,
        project_id: str | None = None,
        storey: str | None = None,
        sheet: str | None = None,
        text: str | None = None,
        drawing_category: str | None = None,
        layer: str | None = None,
        block_name: str | None = None,
        bbox: str | None = None,
        include_properties: bool = True,
        limit: int = 50,
        cursor: str | None = None,
    ) -> dict[str, Any]:
        """``GET /v1/elements``. ``kind`` takes Korean aliases and commas (``Door,창호``).

        The API has no storey/sheet filter, so those two are applied here, on the returned rows.
        """
        params = {
            "kind": kind,
            "project_id": project_id,
            "text": text,
            "drawing_category": category_param(drawing_category),
            "layer": layer,
            "block_name": block_name,
            "bbox": bbox,
            "include_properties": "true" if include_properties else None,
        }
        accept = None
        if storey or sheet:
            want_storey = normalize_storey(storey) if storey else None

            def accept(row: dict[str, Any]) -> bool:
                el = compact_element(row)
                if want_storey and normalize_storey(str(el.get("storey") or "")) != want_storey:
                    return False
                if sheet:
                    where = " ".join(str(el.get(k) or "") for k in ("sheet", "source_file", "drawing_number"))
                    return sheet.lower() in where.lower()
                return True

        found, next_cursor = self._pages("/v1/elements", params, want=limit, cursor=cursor, accept=accept)
        return {"items": [compact_element(r) for r in found], "next_cursor": next_cursor}

    def blocks(
        self,
        category: str | None = None,
        q: str | None = None,
        *,
        project_id: str | None = None,
        limit: int = 50,
        cursor: str | None = None,
    ) -> dict[str, Any]:
        """``GET /v1/blocks?name_like=``. ``category`` (Door, 창호 ...) is matched against the kinds the
        block's instances were classified as, since the API does not filter by category itself."""
        params = {"project_id": project_id, "name_like": q}
        accept = None
        if category:
            wanted = {k.lower() for k in resolve_kind_words(category)}

            def accept(row: dict[str, Any]) -> bool:
                kinds = row.get("instance_kinds") if isinstance(row.get("instance_kinds"), dict) else {}
                names = {str(k).lower() for k in kinds}
                for key in ("classified_as", "category", "kind", "class"):
                    if row.get(key):
                        names.add(str(row[key]).lower())
                return bool(names & wanted)

        found, next_cursor = self._pages("/v1/blocks", params, want=limit, cursor=cursor, accept=accept)
        return {"items": [compact_block(r) for r in found], "next_cursor": next_cursor}

    def drawings(
        self,
        category: str | None = None,
        q: str | None = None,
        *,
        project_id: str | None = None,
        limit: int = 50,
        cursor: str | None = None,
    ) -> dict[str, Any]:
        """``GET /v1/drawings?category=`` flattened to one row per sheet; ``q`` filters those rows."""
        params = {"project_id": project_id, "category": category_param(category)}
        accept = None
        if q:

            def accept(row: dict[str, Any]) -> bool:
                return q.lower() in json.dumps(row, ensure_ascii=False).lower()

        docs, next_cursor = self._pages(
            "/v1/drawings", params, want=limit, cursor=cursor, accept=accept, max_pages=5
        )
        sheets = [sheet for doc in docs for sheet in drawing_rows(doc, filtered=bool(category))]
        if q:
            sheets = [r for r in sheets if q.lower() in json.dumps(r, ensure_ascii=False).lower()] or sheets
        return {"items": sheets[:limit], "next_cursor": next_cursor}

    def element_context(self, element_id: str, hops: int = 1) -> Any:
        if not str(element_id).strip():
            raise OntologyError("element_id must not be empty.")
        quoted = urllib.parse.quote(str(element_id), safe="")
        return self.get(f"/v1/elements/{quoted}/context", hops=min(max(int(hops), 1), 2))

    def search(
        self,
        query: str,
        k: int = 10,
        model: str | None = None,
        *,
        kind: str | None = None,
        storey: str | None = None,
        project_id: str | None = None,
    ) -> list[dict[str, Any]]:
        """``POST /v1/search`` (hybrid lexical + vector + graph expansion)."""
        if not query.strip():
            raise OntologyError("query must not be empty.")
        body: dict[str, Any] = {"query": query, "top_k": min(max(int(k), 1), 100)}
        extras = {"kind": kind, "storey": storey, "project_id": project_id, "model": model}
        body.update({key: value for key, value in extras.items() if value})
        payload = self.post("/v1/search", body)
        return [compact_element(r) for r in rows(payload, "hits", "results")][:k]


def _error_detail(exc: urllib.error.HTTPError) -> str:
    try:
        raw = exc.read().decode("utf-8", "replace")
    except Exception:  # pragma: no cover - defensive
        return ""
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return raw.strip()[:300]
    if isinstance(data, dict):
        for key in ("detail", "error", "message"):
            if data.get(key):
                return str(data[key])[:300]
    return ""


# ---------------------------------------------------------------------- normalising
MAX_PAGE = 500  # the API's MAX_LIMIT
_LIST_KEYS = ("items", "results", "data", "rows", "hits")

# Categories the API does not know under our English name.
_CATEGORY_PARAM = {"schedule": "창호도"}

# Korean words for element kinds (the API resolves these itself for /v1/elements; blocks need it here).
_KIND_WORDS = {
    "Door": "문 출입문 방화문 도어",
    "Window": "창 창호 창문 윈도우",
    "Wall": "벽 벽체 외벽 내벽",
    "Space": "실 공간 방 실명",
    "Column": "기둥",
    "Beam": "보 거더",
    "SteelSection": "철골 형강 강재",
    "Stair": "계단",
    "Slab": "슬래브",
    "Furniture": "가구 집기 비품",
}


def category_param(category: str | None) -> str | None:
    if not category:
        return None
    return _CATEGORY_PARAM.get(category.strip().lower(), category.strip())


def resolve_kind_words(value: str) -> list[str]:
    """``'창호'`` -> ``['창호', 'Window', 'Door']``; ``'Door,Window'`` -> both (plus the raw terms)."""
    out: list[str] = []
    for term in (t.strip() for t in value.split(",") if t.strip()):
        out.append(term)
        if term == "창호":
            out.extend(["Window", "Door"])
        out.extend(k for k, words in _KIND_WORDS.items() if term in words.split())
    return _unique(out)


def normalize_storey(value: str) -> str:
    """'2층', '2F', '02', 'L2' -> '2F'; '지하1층', 'B1' -> 'B1F'; roof -> 'RF'."""
    text = (value or "").strip()
    if m := re.search(r"(?:지하|\bB)\s*0*(\d+)", text, re.IGNORECASE):
        return f"B{int(m.group(1))}F"
    if re.search(r"지붕|옥상|옥탑|roof|^RF$|^R$", text, re.IGNORECASE):
        return "RF"
    if m := re.search(r"0*(\d+)", text):
        return f"{int(m.group(1))}F"
    return text.upper()


def rows(payload: Any, *keys: str) -> list[dict[str, Any]]:
    """Pull the list of records out of a bare list or a wrapper object."""
    if isinstance(payload, list):
        return [r for r in payload if isinstance(r, dict)]
    if isinstance(payload, dict):
        for key in (*keys, *_LIST_KEYS):
            value = payload.get(key)
            if isinstance(value, list):
                return [r for r in value if isinstance(r, dict)]
            if isinstance(value, dict):  # e.g. {"data": {"items": [...]}}
                nested = rows(value, *keys)
                if nested:
                    return nested
    return []


def _pick(row: dict[str, Any], *names: str) -> Any:
    for name in names:
        value = row.get(name)
        if value not in (None, "", [], {}):
            return value
    return None


def _drop_empty(d: dict[str, Any]) -> dict[str, Any]:
    return {k: v for k, v in d.items() if v not in (None, "", [], {})}


def _sub(row: dict[str, Any], key: str) -> dict[str, Any]:
    value = row.get(key)
    return value if isinstance(value, dict) else {}


def compact_element(row: dict[str, Any]) -> dict[str, Any]:
    """Element / search hit in one stable shape (``/v1/elements`` items, ``/v1/search`` hits)."""
    ev = _sub(row, "evidence") or _sub(row, "citation")
    out = {
        "id": _pick(row, "id", "element_id", "object_id", "uid"),
        "class": _pick(row, "class", "element_class", "kind", "type", "ifc_class"),
        "name": _pick(row, "name", "label", "title", "mark"),
        "source_file": _pick(row, "source_file", "file", "document_name", "path")
        or _pick(ev, "source_name", "document_name", "source_path"),
        "sheet": _pick(row, "sheet", "drawing_number", "layout", "layout_or_page")
        or _pick(ev, "layout", "layout_or_page", "page"),
        "storey": _pick(row, "storey", "level", "floor"),
        "drawing_category": _pick(row, "drawing_category"),
        "layer": _pick(row, "layer"),
        "block_name": _pick(row, "block_name", "block"),
        "handle": _pick(row, "handle", "handle_or_id") or _pick(ev, "handle", "handle_or_id"),
        "document_id": _pick(row, "document_id"),
        "project_id": _pick(row, "project_id"),
        "score": _pick(row, "score"),
        "bbox": _pick(row, "bbox"),
        "attributes": _pick(row, "attributes", "attribs"),
        "properties": _pick(row, "properties", "props", "payload"),
    }
    return _drop_empty(out)


def compact_block(row: dict[str, Any]) -> dict[str, Any]:
    tags = _pick(row, "attribute_tags", "attributes", "tags") or []
    if isinstance(tags, dict):
        tags = list(tags)
    files = _pick(row, "example_files", "files", "examples", "source_files")
    if not files and isinstance(row.get("definitions"), list):
        files = _unique(
            str(d.get("document_name"))
            for d in row["definitions"]
            if isinstance(d, dict) and d.get("document_name")
        )
    return _drop_empty(
        {
            "name": _pick(row, "name", "block_name"),
            "category": _pick(row, "category", "classified_as", "class", "kind"),
            "instance_count": _pick(row, "instance_count", "count", "instances"),
            "instance_kinds": _pick(row, "instance_kinds"),
            "attribute_tags": tags,
            "layers": _pick(row, "layers"),
            "example_files": files[:5] if isinstance(files, list) else files,
        }
    )


def compact_drawing(row: dict[str, Any]) -> dict[str, Any]:
    return _drop_empty(
        {
            "drawing_number": _pick(row, "drawing_number", "number", "sheet", "sheet_number"),
            "title": _pick(row, "title", "drawing_title", "name", "label"),
            "category": _pick(row, "category", "drawing_category", "kind", "type"),
            "scale": _pick(row, "scale"),
            "file": _pick(row, "file", "source_file", "path", "document_name"),
        }
    )


def drawing_rows(doc: dict[str, Any], *, filtered: bool = False) -> list[dict[str, Any]]:
    """One ``/v1/drawings`` document -> one row per sheet (drawing number, title, scale, category, file)."""
    sheets = doc.get("sheets")
    if not isinstance(sheets, list):  # already a flat sheet row
        return [compact_drawing(doc)]
    file = _pick(doc, "name", "file", "document_name", "source_key")
    picked = [s for s in sheets if isinstance(s, dict)]
    if filtered and any(s.get("matches_category") for s in picked):
        picked = [s for s in picked if s.get("matches_category")]
    out = []
    for sheet in picked:
        tb = _sub(sheet, "title_block")
        out.append(
            _drop_empty(
                {
                    "drawing_number": _pick(tb, "drawing_number"),
                    "title": _pick(tb, "drawing_title") or _pick(sheet, "view_label"),
                    "category": _pick(sheet, "drawing_category"),
                    "scale": _pick(tb, "scale"),
                    "file": file,
                    "layout": _pick(sheet, "layout"),
                    "document_id": _pick(doc, "document_id", "id"),
                    "element_counts": _pick(sheet, "element_counts"),
                }
            )
        )
    if not out:
        cats = doc.get("drawing_categories")
        out.append(
            _drop_empty(
                {
                    "file": file,
                    "category": ", ".join(cats) if isinstance(cats, list) else cats,
                    "document_id": _pick(doc, "document_id", "id"),
                }
            )
        )
    return out


# ---------------------------------------------------------------- task inference
# Each rule: (regex over the lower-cased task, element classes, drawing categories, wants blocks).
# Korean single-syllable words (문, 창, 실, 보) are guarded so that e.g. 문서/창고/실행/보고 do not match.
_P = r"(?=$|[\s,./()\[\]]|[에의을를은는이가도로과와만])"  # end of word or a Korean particle
_RULES: list[tuple[str, tuple[str, ...], tuple[str, ...], bool]] = [
    (r"(?<![창주질전논소방])문(?![서자제의장구법화])|도어|\bdoors?\b", ("Door",), (), True),
    (r"창호", ("Window", "Door"), (), True),
    (r"창(?![고구작업])|\bwindows?\b|\bsash\b|커튼\s*월|curtain\s*wall", ("Window",), (), True),
    (r"벽|\bwalls?\b|파티션|partition", ("Wall",), (), False),
    (
        r"(?<![가-힣])실"
        + _P
        + r"|[가-힣]실"
        + _P
        + r"|실명|실별|(?<![소지예후전사])방(?![법향식지송])|\brooms?\b|\bspaces?\b",
        ("Space",),
        (),
        False,
    ),
    (r"기둥|\bcolumns?\b", ("Column",), ("structural",), False),
    (
        r"(?<![가-힣])보" + _P + r"|큰보|작은보|거더|\bbeams?\b|\bgirders?\b",
        ("Beam",),
        ("structural",),
        False,
    ),
    (
        r"철골|h\s*-?\s*형강|형강|\bsteel\b|\bh-?beam\b",
        ("SteelSection", "Column", "Beam"),
        ("structural",),
        False,
    ),
    (r"계단|\bstairs?\b", ("Stair",), (), False),
    (r"슬래브|\bslabs?\b", ("Slab",), (), False),
    (r"블록|블럭|\bblocks?\b|심볼|\bsymbols?\b", (), (), True),
    (r"평면|\bplans?\b|\bfloor\s*plan", (), ("plan",), False),
    (r"상세|\bdetails?\b|디테일", (), ("detail",), False),
    (r"단면|\bsections?\b", (), ("section",), False),
    (r"입면|\belevations?\b", (), ("elevation",), False),
    (r"구조|\bstructural\b", (), ("structural",), False),
    (r"창호도|창호\s*일람", (), ("schedule",), False),
    (r"일람표|리스트|목록|스케줄|\bschedules?\b|\blists?\b|\btable\b", (), ("schedule",), False),
]

_STOREY_BASEMENT = re.compile(r"(?:지하\s*(\d{1,2})\s*층|(?<![\w-])B(\d{1,2})F\b)", re.IGNORECASE)
_STOREY = re.compile(r"(?<![\w-])(\d{1,3})\s*(?:층|F\b|st floor|nd floor|rd floor|th floor)", re.IGNORECASE)
_ROOF = re.compile(r"지붕층|옥탑|옥상|\broof\b|\bRF\b", re.IGNORECASE)
_MARK = re.compile(r"(?<![A-Za-z0-9])([A-Z]{1,4}-?\d{1,4}[A-Z]?)(?![A-Za-z0-9])")


def _dedupe(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[Any] = set()
    out = []
    for item in items:
        key = item.get("id") or json.dumps(item, sort_keys=True, ensure_ascii=False)
        if key not in seen:
            seen.add(key)
            out.append(item)
    return out


def _unique(items: Iterable[str]) -> list[str]:
    seen: dict[str, None] = {}
    for item in items:
        seen.setdefault(item, None)
    return list(seen)


def infer_task(task: str) -> dict[str, Any]:
    """Infer element classes, drawing categories, storey and marks from a free-text (KO/EN) task."""
    text = task or ""
    low = text.lower()
    classes: list[str] = []
    categories: list[str] = []
    blocks = False
    for pattern, cls, cats, wants_blocks in _RULES:
        if re.search(pattern, low):
            classes.extend(cls)
            categories.extend(cats)
            blocks = blocks or wants_blocks

    storey = None
    if m := _STOREY_BASEMENT.search(text):
        storey = f"B{m.group(1) or m.group(2)}F"
    elif m := _STOREY.search(text):
        storey = f"{int(m.group(1))}F"
    elif _ROOF.search(text):
        storey = "RF"

    marks = [
        mark
        for mark in _unique(_MARK.findall(text))
        if not re.fullmatch(r"B?\d+F|RF|H|F", mark) and any(ch.isdigit() for ch in mark)
    ]
    return {
        "classes": _unique(classes),
        "drawing_categories": _unique(categories),
        "include_blocks": blocks,
        "storey": storey,
        "marks": marks,
    }


def search_keywords(hints: dict[str, Any], drawing: str | None = None) -> list[tuple[str, str | None]]:
    """Short ``(query, kind)`` pairs that the phrase-matching ``/v1/search`` can hit.

    Marks and the drawing go out unscoped (``kind=None``); a class word goes out with its class as the
    ``kind`` filter, so one-syllable words (창, 실, 보) cannot match 창고 / 화장실 / 보일러.
    """
    pairs: list[tuple[str, str | None]] = [(m, None) for m in hints.get("marks") or [] if m]
    if drawing:
        pairs.append((drawing, None))
    for cls in hints.get("classes") or []:
        pairs.append((_KIND_WORDS.get(cls, cls).split()[0], cls))
    seen: set[tuple[str, str | None]] = set()
    return [p for p in pairs if not (p in seen or seen.add(p))]


# ------------------------------------------------------------------ composite
def auto_context(
    client: OntologyClient,
    task: str,
    drawing: str | None = None,
    *,
    limit: int = 20,
    k: int = 10,
    project_id: str | None = None,
    open_drawing: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Collect everything an automation needs for ``task`` in one bundle.

    ``open_drawing`` is the CAD backend's ``drawing_info()``; when given, every search hit and element
    is tagged ``in_open_drawing`` (its ``source_file`` is the open drawing) so the agent knows which
    handles it can act on directly.

    Raises :class:`OntologyUnavailable` if the service is down; any other per-endpoint failure is
    reported under ``warnings`` so one missing endpoint does not hide the rest.
    """
    if not (task or "").strip():
        raise OntologyError("task must not be empty.")
    drawing = (drawing or "").strip() or None
    project_id = (project_id or "").strip() or None
    hints = infer_task(task)
    warnings: list[str] = []

    def attempt(label: str, fn: Any, *args: Any, **kwargs: Any) -> Any:
        try:
            return fn(*args, **kwargs)
        except OntologyUnavailable:
            raise
        except OntologyError as exc:
            warnings.append(f"{label}: {exc}")
            return None

    storey = hints["storey"]

    def scoped_search(label: str, query: str, kind: str | None) -> list[dict[str, Any]]:
        """Search with the inferred kind/storey; the API matches storey exactly and many drawings carry
        no storey at all, so an empty storey-scoped result is retried without it."""
        found = attempt(label, client.search, query, k, kind=kind, storey=storey, project_id=project_id)
        if not found and storey:
            found = attempt(label, client.search, query, k, kind=kind, project_id=project_id)
        return found or []

    bundle: dict[str, Any] = {"task": task, "drawing": drawing, "project_id": project_id, "inferred": hints}
    query = task if not drawing else f"{task} {drawing}"
    single_kind = hints["classes"][0] if len(hints["classes"]) == 1 else None
    found_hits = scoped_search("search", query, single_kind)
    if not found_hits and single_kind:
        found_hits = attempt("search", client.search, query, k, project_id=project_id) or []
    if not found_hits:
        # The API matches the whole query as one phrase (ILIKE / trigram), so a task sentence rarely
        # hits without real embeddings; retry with the marks, drawing and one word per class.
        queried: list[str] = []
        for word, kind in search_keywords(hints, drawing):
            queried.append(word if not kind else f"{word} ({kind})")
            found_hits.extend(scoped_search(f"search[{word}]", word, kind))
            if len(_dedupe(found_hits)) >= k:
                break
        if queried:
            warnings.append(
                "search: the whole-task search returned nothing or failed; "
                f"used keywords {queried}" + ("." if found_hits else " (no hit either).")
            )
    bundle["search"] = _dedupe(found_hits)[:k]

    mark_list: list[str | None] = list(hints["marks"]) or [None]
    elements: dict[str, list[dict[str, Any]]] = {}
    for cls in hints["classes"]:
        found: list[dict[str, Any]] = []
        for mark in mark_list:
            page = attempt(
                f"elements[{cls}]",
                client.elements,
                cls,
                project_id=project_id,
                storey=storey,
                sheet=drawing,
                text=mark,
                limit=limit,
            )
            found.extend(page["items"] if page else [])
        if not found and (storey or drawing or hints["marks"]):
            # The filters are best-effort hints; fall back to the whole class rather than nothing.
            relaxed = attempt(f"elements[{cls}]", client.elements, cls, project_id=project_id, limit=limit)
            if relaxed and relaxed["items"]:
                warnings.append(
                    f"elements[{cls}]: no match for storey/sheet/mark filters; showing all {cls}."
                )
                found = relaxed["items"]
        elements[cls] = _dedupe(found)[:limit]
    bundle["elements"] = elements

    drawings: dict[str, list[dict[str, Any]]] = {}
    for cat in hints["drawing_categories"]:
        page = attempt(
            f"drawings[{cat}]", client.drawings, cat, q=drawing, project_id=project_id, limit=limit
        )
        drawings[cat] = page["items"] if page else []
    if drawing and not drawings:
        page = attempt("drawings", client.drawings, None, q=drawing, project_id=project_id, limit=limit)
        drawings["match"] = page["items"] if page else []
    bundle["drawings"] = drawings

    blocks: dict[str, list[dict[str, Any]]] = {}
    if hints["include_blocks"]:
        block_classes = [c for c in hints["classes"] if c in ("Door", "Window", "Furniture")]
        for cls in block_classes or ["all"]:
            page = attempt(
                f"blocks[{cls}]",
                client.blocks,
                None if cls == "all" else cls,
                project_id=project_id,
                limit=limit,
            )
            blocks[cls] = page["items"] if page else []
    bundle["blocks"] = blocks

    counts: dict[str, Any] = {
        "search": len(bundle["search"]),
        "elements": {cls: len(v) for cls, v in elements.items()},
        "drawings": {cat: len(v) for cat, v in drawings.items()},
        "blocks": {cat: len(v) for cat, v in blocks.items()},
    }
    if open_drawing:
        bundle["open_drawing"] = _drawing_label(open_drawing)
        tagged = [*bundle["search"], *(el for items in elements.values() for el in items)]
        for item in tagged:
            item["in_open_drawing"] = bool(in_drawing(item.get("source_file"), open_drawing))
        counts["in_open_drawing"] = len(
            {item.get("id") or id(item) for item in tagged if item["in_open_drawing"]}
        )
    bundle["counts"] = counts
    bundle["warnings"] = warnings
    bundle["read_only"] = True
    return bundle


# --------------------------------------------------------------- ontology -> CAD
# Ontology rows carry the DWG/DXF handle the element was parsed from, but a handle is only unique
# inside one drawing. Before an edit tool touches it, the element's source file must be the drawing
# that is open now, the handle must exist there, and the entity must look like the element.
CAD_EXTENSIONS = (".dwg", ".dxf", ".dwt", ".dws")

_GEOMETRY = frozenset(
    {"LINE", "ARC", "CIRCLE", "ELLIPSE", "LWPOLYLINE", "POLYLINE", "SPLINE", "INSERT", "HATCH", "SOLID"}
    | {"MLINE", "3DFACE", "REGION"}
)
_TEXT = frozenset({"TEXT", "MTEXT", "ATTRIB", "ATTDEF"})
# Element classes whose evidence may legitimately be a text entity (room names, labels, marks).
_TEXT_CLASSES = frozenset(
    {"Space", "Room", "Text", "Annotation", "Label", "Note", "Tag", "Mark", "TitleBlock", "DrawingTitle"}
    | {"Grid", "GridLine"}
)
# Entity types that are never a building element.
_NOT_ELEMENT = frozenset(
    {"DIMENSION", "LEADER", "MLEADER", "MULTILEADER", "VIEWPORT", "XLINE", "RAY", "IMAGE", "OLE2FRAME"}
    | {"WIPEOUT"}
)
_PHYSICAL_CLASSES = frozenset(
    {"Door", "Window", "Wall", "Column", "Beam", "SteelSection", "Stair", "Slab", "Furniture", "Opening"}
    | {"CurtainWall"}
)


def drawing_key(name: Any) -> str | None:
    """``'C:\\Proj\\A-201.DWG'`` / ``'a-201.dxf'`` / ``'A-201'`` -> ``'a-201'``: basename, case-folded,
    without a CAD extension, so the DWG an element was parsed from matches the DXF copy that is open."""
    text = str(name or "").strip().strip('"')
    if not text:
        return None
    base = re.split(r"[\\/]", text)[-1].strip().lower()
    for ext in CAD_EXTENSIONS:
        if base.endswith(ext):
            base = base[: -len(ext)]
            break
    return base or None


def _drawing_keys(open_drawing: dict[str, Any] | None) -> set[str]:
    if not isinstance(open_drawing, dict):
        return set()
    keys = (drawing_key(open_drawing.get("name")), drawing_key(open_drawing.get("path")))
    return {key for key in keys if key}


def _drawing_label(open_drawing: dict[str, Any]) -> dict[str, Any]:
    return _drop_empty({"name": open_drawing.get("name"), "path": open_drawing.get("path")})


def in_drawing(source_file: Any, open_drawing: dict[str, Any] | None) -> bool | None:
    """True when ``source_file`` is the open drawing, False when it is another one, None when unknown."""
    key = drawing_key(source_file)
    keys = _drawing_keys(open_drawing)
    if not key or not keys:
        return None
    return key in keys


def entity_summary(entity: dict[str, Any]) -> dict[str, Any]:
    keys = ("handle", "type", "layer", "name", "text", "insert", "center", "start", "end")
    return {key: entity[key] for key in keys if entity.get(key) not in (None, "")}


def plausibility(element: dict[str, Any], entity: dict[str, Any]) -> list[str]:
    """Reasons the live entity does not look like the Ontology element (empty list = plausible)."""
    reasons: list[str] = []
    etype = str(entity.get("type") or "").upper()
    cls = str(element.get("class") or "")
    if cls in _PHYSICAL_CLASSES or cls in _TEXT_CLASSES:
        allowed = _GEOMETRY | _TEXT if cls in _TEXT_CLASSES else _GEOMETRY
        if etype and etype not in allowed:
            reasons.append(f"a {etype} entity is not a plausible {cls}")
    elif etype in _NOT_ELEMENT and cls and cls not in ("Dimension", "Annotation", "Viewport"):
        reasons.append(f"a {etype} entity is not a plausible {cls}")
    block = str(element.get("block_name") or "")
    if block and etype and etype != "INSERT":
        reasons.append(f"element is block {block!r} but the entity is a {etype}")
    elif block and etype == "INSERT":
        name = str(entity.get("name") or "")
        # Dynamic/anonymous references (*U12) carry the effective name only in the Ontology row.
        if name and not name.startswith("*") and name.lower() != block.lower():
            reasons.append(f"element is block {block!r} but the entity inserts {name!r}")
    layer = str(element.get("layer") or "")
    live_layer = str(entity.get("layer") or "")
    if layer and live_layer and layer.lower() != live_layer.lower():
        reasons.append(f"element is on layer {layer!r} but the entity is on {live_layer!r}")
    return reasons


def match_element(
    element: dict[str, Any] | None,
    open_drawing: dict[str, Any] | None,
    lookup: Callable[[str], dict[str, Any] | None],
    *,
    element_id: str | None = None,
) -> dict[str, Any]:
    """Decide whether an Ontology element can be acted on in the open drawing. Never edits anything.

    ``lookup(handle)`` returns the live entity dict or None when the handle does not exist.
    Status: ``matched`` (same drawing, handle exists, entity plausible), ``mismatch`` (handle exists but
    the entity does not look like the element), ``handle_missing`` (same drawing, no such entity / no
    handle recorded), ``other_drawing`` (another file, or the open drawing is unknown) or ``not_found``.
    """
    label = _drawing_label(open_drawing) if isinstance(open_drawing, dict) else None
    if not element:
        return _drop_empty({"element_id": element_id, "status": "not_found", "open_drawing": label})
    handle = str(element.get("handle") or "").strip() or None
    out: dict[str, Any] = {
        "element_id": element.get("id") or element_id,
        "class": element.get("class"),
        "name": element.get("name"),
        "handle": handle,
        "source_file": element.get("source_file"),
        "sheet": element.get("sheet"),
        "open_drawing": label,
    }
    same = in_drawing(element.get("source_file"), open_drawing)
    if not same:
        out["status"] = "other_drawing"
        if same is None:
            out["note"] = (
                "no drawing is open" if not _drawing_keys(open_drawing) else "the element has no source_file"
            )
        return _drop_empty(out)
    if not handle:
        return _drop_empty(out | {"status": "handle_missing", "note": "the element has no handle"})
    entity = lookup(handle)
    if not entity:
        note = "no model-space entity with this handle in the open drawing"
        sheet = str(element.get("sheet") or "")
        if sheet and sheet.lower() != "model":
            note += f" (the element was parsed from layout {sheet!r})"
        return _drop_empty(out | {"status": "handle_missing", "note": note})
    out["entity"] = entity_summary(entity)
    reasons = plausibility(element, entity)
    if reasons:
        return _drop_empty(out | {"status": "mismatch", "reasons": reasons})
    return _drop_empty(out | {"status": "matched"})


def element_of(payload: Any) -> dict[str, Any] | None:
    """The element row inside an ``/v1/elements/{id}/context`` answer (or a bare element row)."""
    if isinstance(payload, dict):
        row = payload.get("element") if isinstance(payload.get("element"), dict) else payload
        element = compact_element(row)
        return element if element.get("id") else None
    return None


def locate(
    client: OntologyClient,
    element_ids: Iterable[str],
    open_drawing: dict[str, Any] | None,
    lookup: Callable[[str], dict[str, Any] | None],
) -> dict[str, Any]:
    """Fetch each element and :func:`match_element` it against the open drawing (read-only)."""
    results = []
    for raw_id in _unique(str(i).strip() for i in element_ids if str(i).strip()):
        try:
            element = element_of(client.element_context(raw_id, hops=1))
            error = None
        except OntologyUnavailable:
            raise
        except OntologyError as exc:
            element, error = None, str(exc)
        result = match_element(element, open_drawing, lookup, element_id=raw_id)
        if error and result["status"] == "not_found":
            result["error"] = error
        results.append(result)
    return _locate_report(results, open_drawing)


def _locate_report(results: list[dict[str, Any]], open_drawing: dict[str, Any] | None) -> dict[str, Any]:
    counts: dict[str, int] = {}
    for r in results:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    return {
        "open_drawing": _drawing_label(open_drawing) if isinstance(open_drawing, dict) else None,
        "counts": counts,
        "matched_handles": [r["handle"] for r in results if r["status"] == "matched"],
        "results": results,
        "read_only": True,
    }


def targets_summary(
    bundle: dict[str, Any],
    open_drawing: dict[str, Any] | None,
    lookup: Callable[[str], dict[str, Any] | None],
    *,
    limit: int = 50,
) -> dict[str, Any]:
    """Locate the elements an auto_context bundle already carries (no extra Ontology calls).

    Only elements of the open drawing are looked up; the rest are just counted as ``other_drawing``.
    Returns the matched handles (with class/name/entity type) the agent can pass to the edit tools.
    """
    by_class = (bundle.get("elements") or {}).values()
    elements = [*(bundle.get("search") or []), *(e for items in by_class for e in items)]
    seen: set[Any] = set()
    counts: dict[str, int] = {}
    targets: list[dict[str, Any]] = []
    other: list[str] = []
    for element in elements:
        key = element.get("id") or (element.get("source_file"), element.get("handle"))
        if key in seen:
            continue
        seen.add(key)
        result = match_element(element, open_drawing, lookup)
        counts[result["status"]] = counts.get(result["status"], 0) + 1
        if result["status"] == "matched" and len(targets) < limit:
            entity = result.get("entity") or {}
            targets.append(
                _drop_empty(
                    {
                        "element_id": result.get("element_id"),
                        "class": result.get("class"),
                        "name": result.get("name"),
                        "handle": result["handle"],
                        "type": entity.get("type"),
                        "layer": entity.get("layer"),
                    }
                )
            )
        elif result["status"] in ("mismatch", "handle_missing") and len(other) < limit:
            other.append(f"{result.get('element_id')}: {result['status']}")
    out: dict[str, Any] = {
        "open_drawing": _drawing_label(open_drawing) if isinstance(open_drawing, dict) else None,
        "counts": counts,
        "targets": targets,
    }
    if other:
        out["not_actionable"] = other
    return out
