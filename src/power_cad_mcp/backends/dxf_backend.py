"""Headless backend built on ezdxf.

Works on any OS without AutoCAD: the drawing lives in memory and is written to DXF (or rendered to
PNG/PDF/SVG when matplotlib is installed). Used for CI tests, previews and offline drafting.
"""

from __future__ import annotations

import math
import os
from collections.abc import Sequence
from typing import Any

import ezdxf
from ezdxf import bbox
from ezdxf.math import Matrix44, Vec3, offset_vertices_2d

from ..colors import BYLAYER
from ..errors import CadError
from ..geometry import rnd, rpt
from .base import CadBackend, Entity

DXF_VERSION = "R2018"
RENDER_FORMATS = {"png", "pdf", "svg"}


class DxfBackend(CadBackend):
    name = "dxf"

    def __init__(self, path: str | None = None):
        self._path: str | None = None
        if path and os.path.exists(path):
            self.open_drawing(path)
        else:
            self._new()
            self._path = path

    # ---- helpers -------------------------------------------------------------
    def _new(self) -> None:
        self.doc = ezdxf.new(DXF_VERSION, setup=True)
        self.msp = self.doc.modelspace()

    def _current_layer(self) -> str:
        return self.doc.header.get("$CLAYER", "0")

    def _attribs(self, props: dict[str, Any], **extra: Any) -> dict[str, Any]:
        attribs: dict[str, Any] = {"layer": props.get("layer") or self._current_layer()}
        layer = attribs["layer"]
        if layer not in self.doc.layers:
            self.doc.layers.add(layer)
        if props.get("color") is not None:
            attribs["color"] = int(props["color"])
        if props.get("linetype"):
            self._require_linetype(props["linetype"])
            attribs["linetype"] = props["linetype"]
        if props.get("lineweight") is not None:
            attribs["lineweight"] = int(props["lineweight"])
        attribs.update(extra)
        return attribs

    def _require_linetype(self, name: str) -> None:
        if name.upper() not in ("BYLAYER", "BYBLOCK") and name not in self.doc.linetypes:
            available = ", ".join(lt.dxf.name for lt in self.doc.linetypes)
            raise CadError(f"Linetype {name!r} is not loaded. Available: {available}")

    def _entity(self, handle: str):
        entity = self.doc.entitydb.get(str(handle).upper())
        if entity is None or not entity.is_alive or entity.dxf.owner != self.msp.block_record_handle:
            raise CadError(f"No model-space entity with handle {handle!r}.")
        return entity

    def _entities(self, handles: Sequence[str]) -> list:
        if not handles:
            raise CadError("At least one entity handle is required.")
        return [self._entity(h) for h in handles]

    def _transform(self, handles: Sequence[str], m: Matrix44) -> list[Entity]:
        entities = self._entities(handles)
        for e in entities:
            try:
                e.transform(m)
            except (NotImplementedError, ezdxf.DXFError) as exc:  # pragma: no cover - exotic entities
                raise CadError(f"Cannot transform {e.dxftype()} {e.dxf.handle}: {exc}") from exc
        return [describe(e) for e in entities]

    # ---- session / documents -------------------------------------------------
    def status(self) -> dict[str, Any]:
        return {
            "backend": self.name,
            "connected": True,
            "application": f"ezdxf {ezdxf.__version__} (headless, no AutoCAD)",
            "drawing": self.drawing_info(),
        }

    def new_drawing(self, template: str | None = None) -> dict[str, Any]:
        if template:
            self.open_drawing(template)
        else:
            self._new()
        self._path = None
        return self.drawing_info()

    def open_drawing(self, path: str) -> dict[str, Any]:
        if not os.path.exists(path):
            raise CadError(f"File not found: {path}")
        if not path.lower().endswith(".dxf"):
            raise CadError(
                "The headless backend can only open .dxf files (use the AutoCAD backend for .dwg)."
            )
        try:
            self.doc = ezdxf.readfile(path)
        except (OSError, ezdxf.DXFStructureError) as exc:
            raise CadError(f"Cannot read DXF {path}: {exc}") from exc
        self.msp = self.doc.modelspace()
        self._path = os.path.abspath(path)
        return self.drawing_info()

    def save_drawing(self, path: str | None = None) -> dict[str, Any]:
        target = path or self._path
        if not target:
            raise CadError("This drawing has never been saved; provide a path ending in .dxf.")
        if not target.lower().endswith(".dxf"):
            raise CadError("The headless backend saves .dxf files only.")
        target = os.path.abspath(target)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        self.doc.saveas(target)
        self._path = target
        return {"saved": True, "path": target}

    def drawing_info(self) -> dict[str, Any]:
        return {
            "name": os.path.basename(self._path) if self._path else "Drawing1.dxf",
            "path": self._path,
            "dxf_version": self.doc.dxfversion,
            "units": self.doc.units,
            "current_layer": self._current_layer(),
            "entity_count": len(self.msp),
            "extents": self.extents(),
        }

    # ---- layers --------------------------------------------------------------
    def list_layers(self) -> list[dict[str, Any]]:
        cur = self._current_layer()
        return [
            {
                "name": layer.dxf.name,
                "color": abs(layer.dxf.color),
                "linetype": layer.dxf.linetype,
                "on": layer.is_on(),
                "frozen": layer.is_frozen(),
                "locked": layer.is_locked(),
                "current": layer.dxf.name == cur,
            }
            for layer in self.doc.layers
        ]

    def _layer(self, name: str):
        if name not in self.doc.layers:
            raise CadError(f"Layer {name!r} does not exist.")
        return self.doc.layers.get(name)

    def _layer_dict(self, name: str) -> dict[str, Any]:
        return next(la for la in self.list_layers() if la["name"].lower() == name.lower())

    def create_layer(self, name, color=None, linetype=None, lineweight=None):
        if linetype:
            self._require_linetype(linetype)
        layers = self.doc.layers
        layer = layers.get(name) if name in layers else layers.add(name)
        if color is not None:
            layer.color = color if color not in (0, BYLAYER) else 7
        if linetype:
            layer.dxf.linetype = linetype
        if lineweight is not None:
            layer.dxf.lineweight = lineweight
        return self._layer_dict(name)

    def update_layer(
        self, name, *, color=None, linetype=None, on=None, frozen=None, locked=None, new_name=None
    ):
        layer = self._layer(name)
        if color is not None:
            layer.color = color if color not in (0, BYLAYER) else 7
        if linetype:
            self._require_linetype(linetype)
            layer.dxf.linetype = linetype
        if on is not None:
            layer.on() if on else layer.off()
        if frozen is not None:
            if frozen and layer.dxf.name == self._current_layer():
                raise CadError("Cannot freeze the current layer.")
            layer.freeze() if frozen else layer.thaw()
        if locked is not None:
            layer.lock() if locked else layer.unlock()
        if new_name and new_name != layer.dxf.name:
            if layer.dxf.name == "0":
                raise CadError("Layer 0 cannot be renamed.")
            was_current = layer.dxf.name == self._current_layer()
            layer.rename(new_name)
            if was_current:
                self.doc.header["$CLAYER"] = new_name
            name = new_name
        return self._layer_dict(name)

    def set_current_layer(self, name):
        layer = self._layer(name)
        if layer.is_frozen():
            raise CadError(f"Layer {name!r} is frozen and cannot be made current.")
        self.doc.header["$CLAYER"] = layer.dxf.name
        return self._layer_dict(name)

    def delete_layer(self, name):
        layer = self._layer(name)
        if layer.dxf.name in ("0", "Defpoints"):
            raise CadError(f"Layer {name!r} cannot be deleted.")
        if layer.dxf.name == self._current_layer():
            raise CadError("Cannot delete the current layer.")
        lname = layer.dxf.name.lower()
        for block in self.doc.blocks:
            if any(e.dxf.get("layer", "0").lower() == lname for e in block):
                raise CadError(f"Layer {name!r} is in use and cannot be deleted.")
        self.doc.layers.remove(layer.dxf.name)

    # ---- drawing primitives --------------------------------------------------
    def add_line(self, start, end, props):
        return describe(self.msp.add_line(start, end, dxfattribs=self._attribs(props)))

    def add_polyline(self, points, closed, props):
        zs = {p[2] for p in points}
        if len(zs) > 1:
            e = self.msp.add_polyline3d(points, close=closed, dxfattribs=self._attribs(props))
        else:
            e = self.msp.add_lwpolyline(
                [(p[0], p[1]) for p in points],
                close=closed,
                dxfattribs=self._attribs(props, elevation=points[0][2]),
            )
        return describe(e)

    def add_circle(self, center, radius, props):
        return describe(self.msp.add_circle(center, radius, dxfattribs=self._attribs(props)))

    def add_arc(self, center, radius, start_angle, end_angle, props):
        return describe(
            self.msp.add_arc(center, radius, start_angle, end_angle, dxfattribs=self._attribs(props))
        )

    def add_ellipse(self, center, major_axis, ratio, props):
        return describe(self.msp.add_ellipse(center, major_axis, ratio, dxfattribs=self._attribs(props)))

    def add_point(self, location, props):
        return describe(self.msp.add_point(location, dxfattribs=self._attribs(props)))

    def add_text(self, text, insert, height, rotation, props):
        e = self.msp.add_text(text, height=height, rotation=rotation, dxfattribs=self._attribs(props))
        e.set_placement(insert)
        return describe(e)

    def add_mtext(self, text, insert, width, height, props):
        attribs = self._attribs(props, insert=insert, char_height=height)
        if width > 0:
            attribs["width"] = width
        return describe(self.msp.add_mtext(text, dxfattribs=attribs))

    def add_aligned_dimension(self, p1, p2, text_position, props):
        # distance = signed offset of the text point from the measured line
        v = Vec3(p2) - Vec3(p1)
        if v.magnitude == 0:
            raise CadError("Dimension points must differ.")
        normal = v.orthogonal().normalize()
        distance = (Vec3(text_position) - Vec3(p1)).dot(normal) or 1.0
        dim = self.msp.add_aligned_dim(
            p1=p1,
            p2=p2,
            distance=distance,
            dimstyle="Standard",
            override=_dim_override(props),
            dxfattribs=self._attribs(props),
        )
        return describe(dim.render().dimension)

    def add_linear_dimension(self, p1, p2, dim_line_point, rotation, props):
        dim = self.msp.add_linear_dim(
            base=dim_line_point,
            p1=p1,
            p2=p2,
            angle=rotation,
            dimstyle="Standard",
            override=_dim_override(props),
            dxfattribs=self._attribs(props),
        )
        return describe(dim.render().dimension)

    def add_hatch(self, boundary_handle, pattern, scale, angle, props):
        boundary = self._entity(boundary_handle)
        kind = boundary.dxftype()
        hatch = self.msp.add_hatch(dxfattribs=self._attribs(props))
        if kind == "LWPOLYLINE":
            if not boundary.closed:
                raise CadError("Hatch boundary polyline must be closed.")
            hatch.paths.add_polyline_path(list(boundary.get_points("xyb")), is_closed=True)
            hatch.dxf.elevation = (0, 0, boundary.dxf.elevation)
        elif kind == "CIRCLE":
            edge = hatch.paths.add_edge_path()
            c = boundary.dxf.center
            edge.add_arc((c.x, c.y), boundary.dxf.radius, 0, 360)
        else:
            self.msp.delete_entity(hatch)
            raise CadError(f"Hatch boundary must be a closed LWPOLYLINE or CIRCLE, got {kind}.")
        if pattern.upper() == "SOLID":
            hatch.set_solid_fill(color=props.get("color") or 256)
        else:
            try:
                hatch.set_pattern_fill(pattern.upper(), scale=scale, angle=angle)
            except (KeyError, ezdxf.DXFValueError) as exc:
                self.msp.delete_entity(hatch)
                raise CadError(f"Unknown hatch pattern {pattern!r}.") from exc
        return describe(hatch)

    # ---- blocks --------------------------------------------------------------
    def list_blocks(self):
        result = []
        for block in self.doc.blocks:
            if block.name.startswith("*") or block.block_record.is_any_layout:
                continue
            result.append(
                {
                    "name": block.name,
                    "base_point": rpt(block.block.dxf.base_point),
                    "entity_count": len(block),
                }
            )
        return result

    def create_block(self, name, base_point, handles, delete_source):
        if name in self.doc.blocks:
            raise CadError(f"Block {name!r} already exists.")
        entities = self._entities(handles)
        block = self.doc.blocks.new(name, base_point=base_point)
        for e in entities:
            block.add_entity(e.copy())
        if delete_source:
            for e in entities:
                self.msp.delete_entity(e)
        return {"name": name, "base_point": rpt(base_point), "entity_count": len(block)}

    def insert_block(self, name, insert, scale, rotation, props):
        if name not in self.doc.blocks:
            raise CadError(f"Block {name!r} is not defined in this drawing.")
        e = self.msp.add_blockref(
            name,
            insert,
            dxfattribs=self._attribs(props, xscale=scale, yscale=scale, zscale=scale, rotation=rotation),
        )
        return describe(e)

    # ---- query / edit --------------------------------------------------------
    def list_entities(self, layer=None, entity_type=None, limit=200):
        items = []
        total = 0
        for e in self.msp:
            if layer and e.dxf.get("layer", "0").lower() != layer.lower():
                continue
            if entity_type and not type_matches(e.dxftype(), entity_type):
                continue
            total += 1
            if len(items) < limit:
                items.append(describe(e))
        return {"total": total, "returned": len(items), "entities": items}

    def get_entity(self, handle):
        return describe(self._entity(handle))

    def delete_entities(self, handles):
        entities = self._entities(handles)
        for e in entities:
            self.msp.delete_entity(e)
        return len(entities)

    def move_entities(self, handles, displacement):
        return self._transform(handles, Matrix44.translate(*displacement))

    def copy_entities(self, handles, displacement):
        copies = []
        for e in self._entities(handles):
            c = e.copy()
            self.msp.add_entity(c)
            copies.append(c.dxf.handle)
        return self._transform(copies, Matrix44.translate(*displacement))

    def rotate_entities(self, handles, base, angle):
        b = Vec3(base)
        m = Matrix44.chain(
            Matrix44.translate(*-b), Matrix44.z_rotate(math.radians(angle)), Matrix44.translate(*b)
        )
        return self._transform(handles, m)

    def scale_entities(self, handles, base, factor):
        b = Vec3(base)
        m = Matrix44.chain(Matrix44.translate(*-b), Matrix44.scale(factor), Matrix44.translate(*b))
        return self._transform(handles, m)

    def mirror_entities(self, handles, p1, p2, delete_source):
        a, b = Vec3(p1), Vec3(p2)
        if a.isclose(b):
            raise CadError("Mirror line points must differ.")
        ang = (b - a).angle
        m = Matrix44.chain(
            Matrix44.translate(*-a),
            Matrix44.z_rotate(-ang),
            Matrix44.scale(1, -1, 1),
            Matrix44.z_rotate(ang),
            Matrix44.translate(*a),
        )
        targets = handles if delete_source else [c["handle"] for c in self.copy_entities(handles, (0, 0, 0))]
        return self._transform(targets, m)

    def offset_entity(self, handle, distance):
        e = self._entity(handle)
        kind = e.dxftype()
        attribs = {k: e.dxf.get(k) for k in ("layer", "color", "linetype") if e.dxf.hasattr(k)}
        if kind in ("CIRCLE", "ARC"):
            r = e.dxf.radius + distance
            if r <= 0:
                raise CadError("Offset would produce a non-positive radius.")
            c = e.copy()
            c.dxf.radius = r
            self.msp.add_entity(c)
            return [describe(c)]
        if kind == "LINE":
            s, t = Vec3(e.dxf.start), Vec3(e.dxf.end)
            n = (t - s).orthogonal().normalize(distance)
            return [describe(self.msp.add_line(s + n, t + n, dxfattribs=attribs))]
        if kind == "LWPOLYLINE":
            pts = [Vec3(x, y) for x, y in e.get_points("xy")]
            new = list(offset_vertices_2d(pts, distance, closed=e.closed))
            attribs["elevation"] = e.dxf.elevation
            return [describe(self.msp.add_lwpolyline(new, close=e.closed, dxfattribs=attribs))]
        raise CadError(f"Offset is supported for LINE, CIRCLE, ARC and LWPOLYLINE, not {kind}.")

    def set_entity_properties(self, handle, props):
        e = self._entity(handle)
        if props.get("layer"):
            if props["layer"] not in self.doc.layers:
                self.doc.layers.add(props["layer"])
            e.dxf.layer = props["layer"]
        if props.get("color") is not None:
            e.dxf.color = int(props["color"])
        if props.get("linetype"):
            self._require_linetype(props["linetype"])
            e.dxf.linetype = props["linetype"]
        if props.get("lineweight") is not None:
            e.dxf.lineweight = int(props["lineweight"])
        return describe(e)

    def extents(self):
        box = bbox.extents(self.msp, fast=True)
        if not box.has_data:
            return None
        return {"min": rpt(box.extmin), "max": rpt(box.extmax)}

    # ---- view / misc ---------------------------------------------------------
    def zoom_extents(self):
        ext = self.extents()
        if ext:
            self.doc.set_modelspace_vport(
                height=max(ext["max"][1] - ext["min"][1], 1) * 1.1,
                center=((ext["min"][0] + ext["max"][0]) / 2, (ext["min"][1] + ext["max"][1]) / 2),
            )

    def zoom_window(self, p1, p2):
        self.doc.set_modelspace_vport(
            height=max(abs(p2[1] - p1[1]), 1e-6), center=((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2)
        )

    def send_command(self, command):
        raise CadError(
            "Raw AutoCAD commands need the AutoCAD (COM) backend; the headless backend has no command line."
        )

    def export(self, path, fmt):
        fmt = fmt.lower()
        path = os.path.abspath(path)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        if fmt == "dxf":
            self.doc.saveas(path)
        elif fmt in RENDER_FORMATS:
            render_to_file(self.doc, path)
        else:
            raise CadError(
                f"Unsupported export format {fmt!r} for the headless backend (dxf, png, pdf, svg)."
            )
        return {"exported": True, "path": path, "format": fmt}


def _dim_override(props: dict[str, Any]) -> dict[str, Any] | None:
    height = props.get("text_height")
    return (
        {"dimtxt": height, "dimasz": height, "dimexe": height / 2, "dimgap": height / 4} if height else None
    )


def render_to_file(doc, path: str) -> None:
    try:
        import matplotlib

        matplotlib.use("Agg")
        from ezdxf.addons.drawing import matplotlib as dxf_mpl
    except ImportError as exc:
        raise CadError("Rendering needs matplotlib: pip install 'power-cad-mcp[render]'.") from exc
    dxf_mpl.qsave(doc.modelspace(), path, bg="#FFFFFF", dpi=150)


# ---------------------------------------------------------------------------
TYPE_ALIASES = {
    "POLYLINE": {"LWPOLYLINE", "POLYLINE"},
    "LWPOLYLINE": {"LWPOLYLINE"},
    "BLOCK": {"INSERT"},
    "BLOCKREF": {"INSERT"},
    "DIM": {"DIMENSION"},
}


def type_matches(dxftype: str, wanted: str) -> bool:
    w = wanted.strip().upper()
    if w.startswith("ACDB"):
        w = w[4:]
    return dxftype in TYPE_ALIASES.get(w, {w})


def describe(e) -> Entity:
    """Convert an ezdxf entity to the backend-neutral entity dict."""
    kind = e.dxftype()
    d: Entity = {
        "handle": e.dxf.handle,
        "type": kind,
        "layer": e.dxf.get("layer", "0"),
        "color": e.dxf.get("color", BYLAYER),
        "linetype": e.dxf.get("linetype", "ByLayer"),
    }
    dx = e.dxf
    if kind == "LINE":
        d.update(start=rpt(dx.start), end=rpt(dx.end), length=rnd(Vec3(dx.start).distance(dx.end)))
    elif kind == "CIRCLE":
        d.update(center=rpt(dx.center), radius=rnd(dx.radius))
    elif kind == "ARC":
        d.update(
            center=rpt(dx.center),
            radius=rnd(dx.radius),
            start_angle=rnd(dx.start_angle),
            end_angle=rnd(dx.end_angle),
        )
    elif kind == "LWPOLYLINE":
        z = dx.get("elevation", 0.0)
        z = z[2] if isinstance(z, tuple | Vec3) else z
        d.update(points=[rpt((x, y, z)) for x, y in e.get_points("xy")], closed=bool(e.closed))
    elif kind == "POLYLINE":
        d.update(points=[rpt(v.dxf.location) for v in e.vertices], closed=bool(e.is_closed))
    elif kind == "ELLIPSE":
        d.update(center=rpt(dx.center), major_axis=rpt(dx.major_axis), ratio=rnd(dx.ratio))
    elif kind == "POINT":
        d.update(location=rpt(dx.location))
    elif kind == "TEXT":
        d.update(
            text=dx.text, insert=rpt(dx.insert), height=rnd(dx.height), rotation=rnd(dx.get("rotation", 0))
        )
    elif kind == "MTEXT":
        d.update(
            text=e.text,
            insert=rpt(dx.insert),
            height=rnd(dx.get("char_height", 0)),
            width=rnd(dx.get("width", 0)),
        )
    elif kind == "INSERT":
        d.update(
            name=dx.name,
            insert=rpt(dx.insert),
            scale=rnd(dx.get("xscale", 1)),
            rotation=rnd(dx.get("rotation", 0)),
        )
    elif kind == "DIMENSION":
        try:
            measurement = e.get_measurement()
            measurement = measurement if isinstance(measurement, float | int) else Vec3(measurement).magnitude
        except Exception:  # pragma: no cover - depends on dimension subtype
            measurement = None
        d.update(measurement=rnd(measurement) if measurement is not None else None, text=dx.get("text", "<>"))
    elif kind == "HATCH":
        d.update(pattern=dx.pattern_name, solid=bool(dx.solid_fill))
    return d
