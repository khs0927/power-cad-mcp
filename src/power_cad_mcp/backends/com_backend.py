"""Live AutoCAD backend over COM (Windows, pywin32).

Connects to the AutoCAD instance that is already running (AutoCAD 2027 = ``AutoCAD.Application.26``;
the version-independent ProgID is tried first so other releases work too).

COM objects are apartment-bound, while the MCP runtime calls tools from arbitrary worker threads, so
every AutoCAD call is marshalled onto one dedicated STA thread. Calls rejected because AutoCAD is busy
(``RPC_E_CALL_REJECTED``) are retried with back-off.
"""

from __future__ import annotations

import functools
import math
import os
import threading
import time
from collections.abc import Callable, Sequence
from concurrent.futures import ThreadPoolExecutor
from typing import Any

from ..colors import BYLAYER
from ..errors import CadError
from ..geometry import rnd, rpt
from .base import CadBackend, Entity

DEFAULT_PROGIDS = (
    "AutoCAD.Application",
    "AutoCAD.Application.26",  # AutoCAD 2027
    "AutoCAD.Application.25",  # AutoCAD 2025 / 2026
    "AutoCAD.Application.24",  # AutoCAD 2021 - 2024
)

# HRESULTs
RPC_E_CALL_REJECTED = -2147418111
RPC_E_SERVERCALL_RETRYLATER = -2147417846
BUSY_CODES = {RPC_E_CALL_REJECTED, RPC_E_SERVERCALL_RETRYLATER}
DISCONNECTED_CODES = {-2147023174, -2147417848, -2147023170}  # RPC server unavailable / disconnected

# AcSaveAsType
AC_2018_DWG = 64
AC_2018_DXF = 65
AC_SELECTION_SET_ALL = 5
AC_HATCH_PREDEFINED = 0
AC_PLOT_EXTENTS = 1
AC_SCALE_TO_FIT = 0

OBJECT_TYPES = {
    "AcDbLine": "LINE",
    "AcDbCircle": "CIRCLE",
    "AcDbArc": "ARC",
    "AcDbPolyline": "LWPOLYLINE",
    "AcDb2dPolyline": "POLYLINE",
    "AcDb3dPolyline": "POLYLINE",
    "AcDbText": "TEXT",
    "AcDbMText": "MTEXT",
    "AcDbBlockReference": "INSERT",
    "AcDbPoint": "POINT",
    "AcDbEllipse": "ELLIPSE",
    "AcDbHatch": "HATCH",
    "AcDbSpline": "SPLINE",
    "AcDbSolid": "SOLID",
    "AcDbRay": "RAY",
    "AcDbXline": "XLINE",
    "AcDbLeader": "LEADER",
    "AcDbMLeader": "MULTILEADER",
    "AcDbTable": "ACAD_TABLE",
    "AcDb3dSolid": "3DSOLID",
    "AcDbRegion": "REGION",
}


def dxf_type(object_name: str) -> str:
    if object_name in OBJECT_TYPES:
        return OBJECT_TYPES[object_name]
    if "Dimension" in object_name:
        return "DIMENSION"
    return object_name[4:].upper() if object_name.startswith("AcDb") else object_name.upper()


class Win32Client:
    """Thin adapter over pywin32 so the backend can be unit-tested with a fake."""

    def __init__(self) -> None:
        try:
            import pythoncom
            import win32com.client
        except ImportError as exc:  # pragma: no cover - Windows only
            raise CadError(
                "The AutoCAD backend needs Windows with pywin32 installed (pip install pywin32)."
            ) from exc
        self._pythoncom = pythoncom
        self._client = win32com.client

    def co_initialize(self) -> None:  # pragma: no cover - Windows only
        self._pythoncom.CoInitialize()
        try:
            from win32com.server.util import wrap

            _MessageFilter._com_interfaces_ = [self._pythoncom.IID_IMessageFilter]
            self._pythoncom.CoRegisterMessageFilter(
                wrap(_MessageFilter(), self._pythoncom.IID_IMessageFilter)
            )
        except Exception:  # the Python-level retry in ComBackend still covers read-only calls
            pass

    def get_active(self, progid: str) -> Any:  # pragma: no cover - Windows only
        return self._client.GetActiveObject(progid)

    def dispatch(self, progid: str) -> Any:  # pragma: no cover - Windows only
        return self._client.Dispatch(progid)

    def point(self, p: Sequence[float]) -> Any:  # pragma: no cover - Windows only
        return self._client.VARIANT(
            self._pythoncom.VT_ARRAY | self._pythoncom.VT_R8, tuple(float(v) for v in p)
        )

    def doubles(self, values: Sequence[float]) -> Any:  # pragma: no cover - Windows only
        return self.point(values)

    def objects(self, objs: Sequence[Any]) -> Any:  # pragma: no cover - Windows only
        return self._client.VARIANT(self._pythoncom.VT_ARRAY | self._pythoncom.VT_DISPATCH, tuple(objs))


class _MessageFilter:  # pragma: no cover - Windows only
    """COM IMessageFilter: transparently re-issue calls AutoCAD rejects while it is busy."""

    _com_interfaces_: list = []  # filled with pythoncom.IID_IMessageFilter at registration
    _public_methods_ = ["HandleInComingCall", "RetryRejectedCall", "MessagePending"]

    def HandleInComingCall(self, call_type, caller, tick_count, interface_info):  # noqa: N802
        return 0  # SERVERCALL_ISHANDLED

    def RetryRejectedCall(self, callee, tick_count, reject_type):  # noqa: N802
        if reject_type == 2 and tick_count < 60_000:  # SERVERCALL_RETRYLATER, give up after 60 s
            return 200  # retry after 200 ms
        return -1  # cancel the call

    def MessagePending(self, callee, tick_count, pending_type):  # noqa: N802
        return 2  # PENDINGMSG_WAITDEFPROCESS


def _hresult(exc: BaseException) -> int | None:
    code = getattr(exc, "hresult", None)
    if isinstance(code, int):
        return code
    if exc.args and isinstance(exc.args[0], int):
        return exc.args[0]
    return None


def _com_message(exc: BaseException) -> str:
    args = getattr(exc, "args", ())
    if len(args) >= 3 and isinstance(args[2], tuple) and len(args[2]) > 2 and args[2][2]:
        return str(args[2][2])
    if len(args) >= 2 and isinstance(args[1], str):
        return args[1]
    return str(exc)


def com_call(fn: Callable | None = None, *, read_only: bool = False) -> Callable:
    """Run the method on the backend's STA thread and translate COM errors into CadError.

    Read-only methods are additionally re-run when AutoCAD rejects a call because it is busy; mutating
    methods are not (a partial re-run could duplicate geometry) and rely on the COM message filter.
    """

    def decorate(func: Callable) -> Callable:
        @functools.wraps(func)
        def wrapper(self: ComBackend, *args: Any, **kwargs: Any) -> Any:
            if threading.get_ident() == self._thread_id:
                return func(self, *args, **kwargs)
            future = self._executor.submit(self._guarded, func, read_only, *args, **kwargs)
            return future.result(timeout=self.timeout)

        return wrapper

    return decorate(fn) if fn is not None else decorate


class ComBackend(CadBackend):
    name = "autocad"

    def __init__(
        self,
        progids: Sequence[str] | None = None,
        launch: bool = False,
        client: Any | None = None,
        timeout: float = 120.0,
        retries: int = 10,
    ) -> None:
        self.progids = tuple(progids or DEFAULT_PROGIDS)
        self.launch = launch
        self.timeout = timeout
        self.retries = retries
        self._client = client
        self._app: Any = None
        self._thread_id: int | None = None
        self._executor = ThreadPoolExecutor(
            max_workers=1, thread_name_prefix="acad-com", initializer=self._init_thread
        )

    # ---- threading / connection ---------------------------------------------
    def _init_thread(self) -> None:
        self._thread_id = threading.get_ident()
        if self._client is None:
            try:
                self._client = Win32Client()
            except CadError:
                return  # surfaced on first call
        init = getattr(self._client, "co_initialize", None)
        if init:
            init()

    def _guarded(self, fn: Callable, read_only: bool, *args: Any, **kwargs: Any) -> Any:
        if self._client is None:
            self._client = Win32Client()  # raises a helpful CadError off-Windows
        delay = 0.2
        attempts = self.retries + 1 if read_only else 1
        for attempt in range(attempts):
            try:
                return fn(self, *args, **kwargs)
            except CadError:
                raise
            except Exception as exc:
                code = _hresult(exc)
                if code in BUSY_CODES and attempt < attempts - 1:
                    time.sleep(delay)
                    delay = min(delay * 2, 2.0)
                    continue
                if code in DISCONNECTED_CODES:
                    self._app = None
                    raise CadError("Lost the connection to AutoCAD. Is it still running?") from exc
                if code in BUSY_CODES:
                    raise CadError(
                        "AutoCAD stayed busy. Finish or cancel the active command (press Esc) and retry."
                    ) from exc
                raise CadError(f"AutoCAD error: {_com_message(exc)}") from exc
        raise AssertionError("unreachable")  # pragma: no cover

    @property
    def app(self) -> Any:
        if self._app is not None:
            try:
                _ = self._app.Name
                return self._app
            except Exception:
                self._app = None
        errors = []
        for progid in self.progids:
            try:
                self._app = self._client.get_active(progid)
                return self._app
            except Exception as exc:
                errors.append(f"{progid}: {_com_message(exc)}")
        if self.launch:
            try:
                self._app = self._client.dispatch(self.progids[0])
                self._app.Visible = True
                return self._app
            except Exception as exc:
                errors.append(f"launch {self.progids[0]}: {_com_message(exc)}")
        raise CadError(
            "Could not attach to a running AutoCAD. Start AutoCAD (2027 or other) and open a drawing, "
            "or set POWER_CAD_LAUNCH=1 to let the server start it. Details: " + "; ".join(errors)
        )

    @property
    def doc(self) -> Any:
        app = self.app
        if app.Documents.Count == 0:
            app.Documents.Add()
        return app.ActiveDocument

    @property
    def ms(self) -> Any:
        return self.doc.ModelSpace

    def close(self) -> None:
        self._app = None
        self._executor.shutdown(wait=False, cancel_futures=True)

    # ---- conversions ---------------------------------------------------------
    def _pt(self, p: Sequence[float]) -> Any:
        x, y, *z = p
        return self._client.point((x, y, z[0] if z else 0.0))

    def _obj(self, handle: str) -> Any:
        try:
            return self.doc.HandleToObject(str(handle).upper())
        except Exception as exc:
            raise CadError(f"No entity with handle {handle!r}.") from exc

    def _objs(self, handles: Sequence[str]) -> list[Any]:
        if not handles:
            raise CadError("At least one entity handle is required.")
        return [self._obj(h) for h in handles]

    def _ensure_layer(self, name: str) -> Any:
        return self.doc.Layers.Add(name)  # returns the existing layer when it already exists

    def _ensure_linetype(self, name: str) -> None:
        if name.upper() in ("BYLAYER", "BYBLOCK", "CONTINUOUS"):
            return
        doc = self.doc
        try:
            doc.Linetypes.Item(name)
            return
        except Exception:
            pass
        for lin in ("acadiso.lin", "acad.lin"):
            try:
                doc.Linetypes.Load(name, lin)
                return
            except Exception:
                continue
        raise CadError(f"Linetype {name!r} is not loaded and was not found in acadiso.lin/acad.lin.")

    def _apply(self, obj: Any, props: dict[str, Any]) -> Any:
        if props.get("layer"):
            self._ensure_layer(props["layer"])
            obj.Layer = props["layer"]
        if props.get("color") is not None:
            obj.color = int(props["color"])
        if props.get("linetype"):
            self._ensure_linetype(props["linetype"])
            obj.Linetype = props["linetype"]
        if props.get("lineweight") is not None:
            obj.Lineweight = int(props["lineweight"])
        return obj

    def _new(self, obj: Any, props: dict[str, Any]) -> Entity:
        self._apply(obj, props)
        if props.get("text_height"):  # dimensions: scale text and arrows together
            obj.TextHeight = props["text_height"]
            obj.ArrowheadSize = props["text_height"]
        obj.Update()
        return self._describe(obj)

    def _selection_all(self) -> Any:
        doc = self.doc
        name = "POWERCAD_MCP_SS"
        _safe(lambda: doc.SelectionSets.Item(name).Delete(), None)
        ss = doc.SelectionSets.Add(name)
        ss.Select(AC_SELECTION_SET_ALL)
        return ss

    def _describe(self, obj: Any) -> Entity:
        kind = dxf_type(obj.ObjectName)
        d: Entity = {
            "handle": obj.Handle,
            "type": kind,
            "layer": obj.Layer,
            "color": _safe(lambda: int(obj.color), BYLAYER),
            "linetype": _safe(lambda: obj.Linetype, "ByLayer"),
        }
        try:
            if kind == "LINE":
                d.update(start=rpt(obj.StartPoint), end=rpt(obj.EndPoint), length=rnd(obj.Length))
            elif kind == "CIRCLE":
                d.update(center=rpt(obj.Center), radius=rnd(obj.Radius))
            elif kind == "ARC":
                d.update(
                    center=rpt(obj.Center),
                    radius=rnd(obj.Radius),
                    start_angle=rnd(math.degrees(obj.StartAngle)),
                    end_angle=rnd(math.degrees(obj.EndAngle)),
                )
            elif kind == "LWPOLYLINE":
                c = list(obj.Coordinates)
                z = _safe(lambda: float(obj.Elevation), 0.0)
                d.update(
                    points=[rpt((c[i], c[i + 1], z)) for i in range(0, len(c), 2)], closed=bool(obj.Closed)
                )
            elif kind == "POLYLINE":
                c = list(obj.Coordinates)
                d.update(points=[rpt(c[i : i + 3]) for i in range(0, len(c), 3)], closed=bool(obj.Closed))
            elif kind == "ELLIPSE":
                d.update(center=rpt(obj.Center), major_axis=rpt(obj.MajorAxis), ratio=rnd(obj.RadiusRatio))
            elif kind == "POINT":
                d.update(location=rpt(obj.Coordinates))
            elif kind == "TEXT":
                d.update(
                    text=obj.TextString,
                    insert=rpt(obj.InsertionPoint),
                    height=rnd(obj.Height),
                    rotation=rnd(math.degrees(obj.Rotation)),
                )
            elif kind == "MTEXT":
                d.update(
                    text=obj.TextString,
                    insert=rpt(obj.InsertionPoint),
                    height=rnd(obj.Height),
                    width=rnd(obj.Width),
                )
            elif kind == "INSERT":
                d.update(
                    name=_safe(lambda: obj.EffectiveName, None) or obj.Name,
                    insert=rpt(obj.InsertionPoint),
                    scale=rnd(obj.XScaleFactor),
                    rotation=rnd(math.degrees(obj.Rotation)),
                )
            elif kind == "DIMENSION":
                d.update(measurement=rnd(obj.Measurement), text=_safe(lambda: obj.TextOverride, "") or "<>")
            elif kind == "HATCH":
                d.update(pattern=obj.PatternName, solid=obj.PatternName.upper() == "SOLID")
        except Exception as exc:  # geometry is best-effort; identity fields above are enough to act on
            d["geometry_error"] = _com_message(exc)
        return d

    # ---- session / documents -------------------------------------------------
    def status(self) -> dict[str, Any]:
        try:
            return self._status()
        except CadError as exc:
            return {"backend": self.name, "connected": False, "error": str(exc)}

    @com_call(read_only=True)
    def _status(self) -> dict[str, Any]:
        app = self.app
        return {
            "backend": self.name,
            "connected": True,
            "application": f"{app.Name} {app.Version}",
            "open_documents": app.Documents.Count,
            "drawing": self.drawing_info() if app.Documents.Count else None,
        }

    @com_call
    def new_drawing(self, template: str | None = None) -> dict[str, Any]:
        docs = self.app.Documents
        docs.Add(template) if template else docs.Add()
        return self.drawing_info()

    @com_call
    def open_drawing(self, path: str) -> dict[str, Any]:
        path = os.path.abspath(path)
        if not os.path.exists(path):
            raise CadError(f"File not found: {path}")
        self.app.Documents.Open(path)
        return self.drawing_info()

    @com_call
    def save_drawing(self, path: str | None = None) -> dict[str, Any]:
        doc = self.doc
        if path:
            path = os.path.abspath(path)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            if path.lower().endswith(".dxf"):
                doc.SaveAs(path, AC_2018_DXF)
            else:
                doc.SaveAs(path)
        else:
            if not doc.FullName:
                raise CadError("This drawing has never been saved; provide a path (.dwg or .dxf).")
            doc.Save()
        return {"saved": True, "path": doc.FullName}

    @com_call(read_only=True)
    def drawing_info(self) -> dict[str, Any]:
        doc = self.doc
        return {
            "name": doc.Name,
            "path": doc.FullName or None,
            "saved": bool(_safe(lambda: doc.Saved, False)),
            "units": _safe(lambda: int(doc.GetVariable("INSUNITS")), None),
            "current_layer": doc.ActiveLayer.Name,
            "entity_count": doc.ModelSpace.Count,
            "extents": self.extents(),
        }

    # ---- layers --------------------------------------------------------------
    def _layer_dict(self, layer: Any, current: str) -> dict[str, Any]:
        return {
            "name": layer.Name,
            "color": _safe(lambda: int(layer.color), 7),
            "linetype": layer.Linetype,
            "on": bool(layer.LayerOn),
            "frozen": bool(layer.Freeze),
            "locked": bool(layer.Lock),
            "current": layer.Name.lower() == current.lower(),
        }

    def _layer(self, name: str) -> Any:
        try:
            return self.doc.Layers.Item(name)
        except Exception as exc:
            raise CadError(f"Layer {name!r} does not exist.") from exc

    @com_call(read_only=True)
    def list_layers(self):
        doc = self.doc
        current = doc.ActiveLayer.Name
        layers = doc.Layers
        return [self._layer_dict(layers.Item(i), current) for i in range(layers.Count)]

    @com_call
    def create_layer(self, name, color=None, linetype=None, lineweight=None):
        layer = self._ensure_layer(name)
        if color is not None:
            layer.color = color if color not in (0, BYLAYER) else 7
        if linetype:
            self._ensure_linetype(linetype)
            layer.Linetype = linetype
        if lineweight is not None:
            layer.Lineweight = lineweight
        return self._layer_dict(layer, self.doc.ActiveLayer.Name)

    @com_call
    def update_layer(
        self, name, *, color=None, linetype=None, on=None, frozen=None, locked=None, new_name=None
    ):
        layer = self._layer(name)
        current = self.doc.ActiveLayer.Name
        if color is not None:
            layer.color = color if color not in (0, BYLAYER) else 7
        if linetype:
            self._ensure_linetype(linetype)
            layer.Linetype = linetype
        if on is not None:
            layer.LayerOn = on
        if frozen is not None:
            if frozen and layer.Name.lower() == current.lower():
                raise CadError("Cannot freeze the current layer.")
            layer.Freeze = frozen
        if locked is not None:
            layer.Lock = locked
        if new_name and new_name != layer.Name:
            if layer.Name == "0":
                raise CadError("Layer 0 cannot be renamed.")
            layer.Name = new_name
        return self._layer_dict(layer, self.doc.ActiveLayer.Name)

    @com_call
    def set_current_layer(self, name):
        layer = self._layer(name)
        if layer.Freeze:
            raise CadError(f"Layer {name!r} is frozen and cannot be made current.")
        self.doc.ActiveLayer = layer
        return self._layer_dict(layer, layer.Name)

    @com_call
    def delete_layer(self, name):
        layer = self._layer(name)
        if layer.Name in ("0", "Defpoints"):
            raise CadError(f"Layer {name!r} cannot be deleted.")
        if layer.Name.lower() == self.doc.ActiveLayer.Name.lower():
            raise CadError("Cannot delete the current layer.")
        try:
            layer.Delete()
        except Exception as exc:
            raise CadError(f"Layer {name!r} is in use and cannot be deleted.") from exc

    # ---- drawing primitives --------------------------------------------------
    @com_call
    def add_line(self, start, end, props):
        return self._new(self.ms.AddLine(self._pt(start), self._pt(end)), props)

    @com_call
    def add_polyline(self, points, closed, props):
        if len({p[2] for p in points}) > 1:
            obj = self.ms.Add3DPoly(self._client.doubles([c for p in points for c in p]))
        else:
            obj = self.ms.AddLightWeightPolyline(self._client.doubles([c for p in points for c in p[:2]]))
            if points[0][2]:
                obj.Elevation = points[0][2]
        obj.Closed = bool(closed)
        return self._new(obj, props)

    @com_call
    def add_circle(self, center, radius, props):
        return self._new(self.ms.AddCircle(self._pt(center), radius), props)

    @com_call
    def add_arc(self, center, radius, start_angle, end_angle, props):
        obj = self.ms.AddArc(self._pt(center), radius, math.radians(start_angle), math.radians(end_angle))
        return self._new(obj, props)

    @com_call
    def add_ellipse(self, center, major_axis, ratio, props):
        return self._new(self.ms.AddEllipse(self._pt(center), self._pt(major_axis), ratio), props)

    @com_call
    def add_point(self, location, props):
        return self._new(self.ms.AddPoint(self._pt(location)), props)

    @com_call
    def add_text(self, text, insert, height, rotation, props):
        obj = self.ms.AddText(text, self._pt(insert), height)
        if rotation:
            obj.Rotation = math.radians(rotation)
        return self._new(obj, props)

    @com_call
    def add_mtext(self, text, insert, width, height, props):
        obj = self.ms.AddMText(self._pt(insert), width, text)
        obj.Height = height
        return self._new(obj, props)

    @com_call
    def add_aligned_dimension(self, p1, p2, text_position, props):
        obj = self.ms.AddDimAligned(self._pt(p1), self._pt(p2), self._pt(text_position))
        return self._new(obj, props)

    @com_call
    def add_linear_dimension(self, p1, p2, dim_line_point, rotation, props):
        obj = self.ms.AddDimRotated(
            self._pt(p1), self._pt(p2), self._pt(dim_line_point), math.radians(rotation)
        )
        return self._new(obj, props)

    @com_call
    def add_hatch(self, boundary_handle, pattern, scale, angle, props):
        boundary = self._obj(boundary_handle)
        hatch = self.ms.AddHatch(AC_HATCH_PREDEFINED, pattern.upper(), True)
        try:
            hatch.AppendOuterLoop(self._client.objects([boundary]))
            if pattern.upper() != "SOLID":
                hatch.PatternScale = scale
                hatch.PatternAngle = math.radians(angle)
            hatch.Evaluate()
        except Exception as exc:
            _safe(hatch.Delete, None)
            raise CadError(
                f"Could not hatch {boundary_handle}: {_com_message(exc)} (boundary must be closed)."
            ) from exc
        return self._new(hatch, props)

    # ---- blocks --------------------------------------------------------------
    @com_call(read_only=True)
    def list_blocks(self):
        blocks = self.doc.Blocks
        result = []
        for i in range(blocks.Count):
            b = blocks.Item(i)
            if b.Name.startswith("*") or _safe(lambda b=b: b.IsLayout, False):
                continue
            result.append(
                {
                    "name": b.Name,
                    "base_point": rpt(b.Origin),
                    "entity_count": b.Count,
                    "xref": bool(_safe(lambda b=b: b.IsXRef, False)),
                }
            )
        return result

    @com_call
    def create_block(self, name, base_point, handles, delete_source):
        doc = self.doc
        exists = True
        try:
            doc.Blocks.Item(name)
        except Exception:
            exists = False
        if exists:
            raise CadError(f"Block {name!r} already exists.")
        objs = self._objs(handles)
        block = doc.Blocks.Add(self._pt(base_point), name)
        doc.CopyObjects(self._client.objects(objs), block)
        if delete_source:
            for o in objs:
                o.Delete()
        return {"name": name, "base_point": rpt(base_point), "entity_count": block.Count}

    @com_call
    def insert_block(self, name, insert, scale, rotation, props):
        source = name
        try:
            self.doc.Blocks.Item(name)
        except Exception:
            if not (os.path.exists(name) and name.lower().endswith((".dwg", ".dxf"))):
                raise CadError(
                    f"Block {name!r} is not defined in this drawing (or pass a .dwg file path)."
                ) from None
            source = os.path.abspath(name)
        obj = self.ms.InsertBlock(self._pt(insert), source, scale, scale, scale, math.radians(rotation))
        return self._new(obj, props)

    # ---- query / edit --------------------------------------------------------
    @com_call(read_only=True)
    def list_entities(self, layer=None, entity_type=None, limit=200):
        from .dxf_backend import type_matches

        ms = self.ms
        items, total = [], 0
        for i in range(ms.Count):
            obj = ms.Item(i)
            if layer and obj.Layer.lower() != layer.lower():
                continue
            if entity_type and not type_matches(dxf_type(obj.ObjectName), entity_type):
                continue
            total += 1
            if len(items) < limit:
                items.append(self._describe(obj))
        return {"total": total, "returned": len(items), "entities": items}

    @com_call(read_only=True)
    def get_entity(self, handle):
        return self._describe(self._obj(handle))

    @com_call(read_only=True)
    def model_space_entity(self, handle):
        """The model-space entity with this handle, else None (missing handle, a non-entity object such
        as a layer record, or an entity inside a block definition / paper-space layout)."""
        try:
            obj = self.doc.HandleToObject(str(handle).upper())
            if obj.OwnerID != self.doc.ModelSpace.ObjectID:
                return None
            return self._describe(obj)
        except Exception:  # noqa: BLE001 - any COM failure means "no such model-space entity"
            return None

    @com_call
    def delete_entities(self, handles):
        objs = self._objs(handles)
        for o in objs:
            o.Delete()
        return len(objs)

    @com_call
    def move_entities(self, handles, displacement):
        objs = self._objs(handles)
        for o in objs:
            o.Move(self._pt((0, 0, 0)), self._pt(displacement))
        return [self._describe(o) for o in objs]

    @com_call
    def copy_entities(self, handles, displacement):
        copies = []
        for o in self._objs(handles):
            c = o.Copy()
            c.Move(self._pt((0, 0, 0)), self._pt(displacement))
            copies.append(c)
        return [self._describe(c) for c in copies]

    @com_call
    def rotate_entities(self, handles, base, angle):
        objs = self._objs(handles)
        for o in objs:
            o.Rotate(self._pt(base), math.radians(angle))
        return [self._describe(o) for o in objs]

    @com_call
    def scale_entities(self, handles, base, factor):
        objs = self._objs(handles)
        for o in objs:
            o.ScaleEntity(self._pt(base), factor)
        return [self._describe(o) for o in objs]

    @com_call
    def mirror_entities(self, handles, p1, p2, delete_source):
        if tuple(p1) == tuple(p2):
            raise CadError("Mirror line points must differ.")
        result = []
        for o in self._objs(handles):
            result.append(o.Mirror(self._pt(p1), self._pt(p2)))
            if delete_source:
                o.Delete()
        return [self._describe(m) for m in result]

    @com_call
    def offset_entity(self, handle, distance):
        created = self._obj(handle).Offset(distance)
        return [self._describe(o) for o in created]

    @com_call
    def set_entity_properties(self, handle, props):
        obj = self._apply(self._obj(handle), props)
        obj.Update()
        return self._describe(obj)

    @com_call(read_only=True)
    def extents(self):
        doc = self.doc
        if doc.ModelSpace.Count == 0:
            return None
        lo, hi = list(doc.GetVariable("EXTMIN")), list(doc.GetVariable("EXTMAX"))
        if lo[0] > hi[0] or lo[1] > hi[1]:
            return None
        return {"min": rpt(lo), "max": rpt(hi)}

    # ---- view / misc ---------------------------------------------------------
    @com_call
    def zoom_extents(self):
        self.app.ZoomExtents()

    @com_call
    def zoom_window(self, p1, p2):
        self.app.ZoomWindow(self._pt(p1), self._pt(p2))

    @com_call
    def send_command(self, command):
        cmd = command if command.endswith((" ", "\n", "\r")) else command + "\n"
        self.doc.SendCommand(cmd)
        idle = self._wait_idle(30.0)
        return {"sent": command, "completed": idle}

    def _wait_idle(self, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                if self.app.GetAcadState().IsQuiescent:
                    return True
            except Exception:
                pass
            time.sleep(0.1)
        return False

    @com_call
    def export(self, path, fmt):
        fmt = fmt.lower()
        path = os.path.abspath(path)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        doc = self.doc
        note = None
        if fmt == "pdf":
            layout = doc.ActiveLayout
            layout.RefreshPlotDeviceInfo()
            layout.ConfigName = "DWG To PDF.pc3"
            layout.PlotType = AC_PLOT_EXTENTS
            layout.StandardScale = AC_SCALE_TO_FIT
            layout.CenterPlot = True
            doc.SetVariable("BACKGROUNDPLOT", 0)
            if not doc.Plot.PlotToFile(path, "DWG To PDF.pc3"):
                raise CadError("AutoCAD reported that plotting to PDF failed.")
        elif fmt in ("dwg", "dxf"):
            original = doc.FullName
            doc.SaveAs(path, AC_2018_DXF if fmt == "dxf" else AC_2018_DWG)
            if original and original.lower().endswith(".dwg") and os.path.abspath(original) != path:
                doc.SaveAs(original, AC_2018_DWG)  # re-attach the session to the original file
                note = "The original drawing was saved again to keep it as the active file."
        elif fmt in ("bmp", "wmf", "png"):
            base, _ = os.path.splitext(path)
            doc.Export(base, "BMP" if fmt in ("bmp", "png") else "WMF", self._selection_all())
            produced = base + (".bmp" if fmt in ("bmp", "png") else ".wmf")
            if fmt == "png":
                try:
                    from PIL import Image
                except ImportError as exc:
                    raise CadError(
                        "PNG export needs Pillow (pip install pillow); use 'bmp' instead."
                    ) from exc
                with Image.open(produced) as im:
                    im.save(path)
                os.remove(produced)
            else:
                path = produced
        else:
            raise CadError(f"Unsupported export format {fmt!r} for AutoCAD (pdf, dwg, dxf, png, bmp, wmf).")
        result = {"exported": True, "path": path, "format": fmt}
        if note:
            result["note"] = note
        return result


def _safe(fn: Callable[[], Any], default: Any) -> Any:
    try:
        return fn()
    except Exception:
        return default
