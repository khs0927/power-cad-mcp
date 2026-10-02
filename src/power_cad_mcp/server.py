"""MCP tool surface for Power CAD."""

from __future__ import annotations

import os
import tempfile
from collections.abc import Callable
from functools import partial
from typing import Annotated, Any, Literal

from mcp.server.mcpserver import Image, MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from . import __version__, ontology
from .backends import CadBackend, create_backend
from .colors import parse_color
from .config import Settings
from .errors import CadError
from .geometry import require_positive, to_point, to_points
from .safety import check_command

INSTRUCTIONS = """\
Power CAD drives AutoCAD (live, over COM on Windows) or a headless DXF drawing.
- Call cad_status first to see which backend is active and what drawing is open.
- Coordinates are [x, y] or [x, y, z] in drawing units; angles are in degrees, counter-clockwise from +X.
- Every created entity is returned with its handle; use handles to move/copy/rotate/delete/restyle it.
- Prefer draw_batch for many primitives: it is one round trip and returns every handle.
- Organise geometry on layers (create_layer + set_current_layer, or pass `layer` per entity).
- Finish with zoom_extents and save_drawing / export_drawing when the user wants a file.
- Before an automation, call ontology_auto_context(task) to pull every relevant door/window/wall/space,
  detail sheet and block from the Ontology building-data store instead of asking the user to list them.
  The ontology_* tools are read-only; their results are hints, so verify against the live drawing.
- Ontology handles are only unique per drawing: before passing an element's handle to get_entity /
  move_entities / delete_entities / set_entity_properties, call ontology_locate(element_ids) and use only
  results with status "matched" (same source file as the open drawing, handle exists, entity plausible).
- To place a block the Ontology knows, call ontology_block_candidates(name_or_task) and pass an
  `insertable` entry's insert_name to insert_block; blocks only in other files are not imported.
"""

PointArg = Annotated[list[float], Field(min_length=2, max_length=3, description="[x, y] or [x, y, z]")]
LayerArg = Annotated[
    str | None, Field(description="Target layer (created if missing). Default: current layer.")
]
ColorArg = Annotated[
    int | str | None,
    Field(description="ACI 0-256 or name (red, yellow, green, cyan, blue, magenta, white, bylayer)"),
]
LinetypeArg = Annotated[
    str | None, Field(description="Linetype name, e.g. CONTINUOUS, DASHED, CENTER, HIDDEN")
]
HandlesArg = Annotated[list[str], Field(min_length=1, description="Entity handles (hex strings)")]
ProjectArg = Annotated[str | None, Field(description="Ontology project_id filter")]
CursorArg = Annotated[str | None, Field(description="next_cursor from a previous page")]

READ = ToolAnnotations(
    read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False
)
WRITE = ToolAnnotations(
    read_only_hint=False, destructive_hint=False, idempotent_hint=False, open_world_hint=False
)
DESTRUCTIVE = ToolAnnotations(
    read_only_hint=False, destructive_hint=True, idempotent_hint=False, open_world_hint=False
)
ONTOLOGY = ToolAnnotations(
    read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=True
)


def _props(
    layer: str | None = None, color: int | str | None = None, linetype: str | None = None
) -> dict[str, Any]:
    return {"layer": layer or None, "color": parse_color(color), "linetype": linetype or None}


class PowerCad:
    """Holds the backend and implements each tool as a plain method (easy to call from draw_batch/tests)."""

    def __init__(self, backend: CadBackend, settings: Settings):
        self.backend = backend
        self.settings = settings
        self._ontology: ontology.OntologyClient | None = None

    @property
    def ontology(self) -> ontology.OntologyClient:
        """Created on first use so a bad POWERCAD_ONTOLOGY_URL only affects the ontology_* tools."""
        if self._ontology is None:
            s = self.settings
            self._ontology = ontology.OntologyClient(s.ontology_url, s.ontology_timeout, s.ontology_token)
        return self._ontology

    def open_drawing(self) -> dict[str, Any] | None:
        """The backend's drawing_info, or None when no drawing is reachable (e.g. AutoCAD not running)."""
        try:
            info = self.backend.drawing_info()
        except Exception:  # noqa: BLE001 - any backend failure just means "no open drawing"
            return None
        return info if isinstance(info, dict) and (info.get("name") or info.get("path")) else None

    def lookup(self, handle: str) -> dict[str, Any] | None:
        """Read one entity by handle; None when it does not exist. Read-only."""
        try:
            return self.backend.get_entity(handle)
        except Exception:  # noqa: BLE001 - a missing/invalid handle is a result, not an error
            return None

    def auto_context(
        self,
        task: str,
        drawing: str | None = None,
        limit: int = 20,
        *,
        k: int = 10,
        project_id: str | None = None,
    ) -> dict[str, Any]:
        return ontology.auto_context(
            self.ontology,
            task,
            drawing,
            limit=limit,
            k=k,
            project_id=project_id,
            open_drawing=self.open_drawing(),
        )

    def locate(self, element_ids: list[str]) -> dict[str, Any]:
        return ontology.locate(self.ontology, element_ids, self.open_drawing(), self.lookup)

    def drawing_blocks(self) -> list[dict[str, Any]] | None:
        """The open drawing's block definitions, or None when they cannot be read. Read-only."""
        try:
            blocks = self.backend.list_blocks()
        except Exception:  # noqa: BLE001 - no drawing / AutoCAD gone: report "unknown", not an error
            return None
        return blocks if isinstance(blocks, list) else None

    def block_candidates(
        self, name_or_task: str, *, project_id: str | None = None, limit: int = 50
    ) -> dict[str, Any]:
        return ontology.block_candidates(
            self.ontology,
            name_or_task,
            self.drawing_blocks(),
            self.open_drawing(),
            project_id=project_id,
            limit=limit,
        )

    def path(self, value: str, *, ext: str | None = None) -> str:
        value = os.path.expandvars(os.path.expanduser(value.strip().strip('"')))
        if not os.path.isabs(value):
            value = os.path.join(self.settings.workspace or os.getcwd(), value)
        if ext and not os.path.splitext(value)[1]:
            value += ext
        return os.path.abspath(value)

    # -- primitive dispatch shared by the individual tools and draw_batch -------
    def draw(self, op: str, args: dict[str, Any]) -> dict[str, Any]:
        b = self.backend
        a = dict(args)
        props = _props(a.pop("layer", None), a.pop("color", None), a.pop("linetype", None))

        def take(name: str, default: Any = ...) -> Any:
            if name in a:
                return a.pop(name)
            if default is ...:
                raise CadError(f"'{op}' needs '{name}'.")
            return default

        if op == "line":
            result = partial(
                b.add_line, to_point(take("start"), "start"), to_point(take("end"), "end"), props
            )
        elif op == "polyline":
            result = partial(b.add_polyline, to_points(take("points")), bool(take("closed", False)), props)
        elif op == "rectangle":
            result = partial(
                b.add_rectangle,
                to_point(take("corner1"), "corner1"),
                to_point(take("corner2"), "corner2"),
                props,
            )
        elif op == "polygon":
            sides = int(take("sides"))
            result = partial(
                b.add_polygon,
                to_point(take("center"), "center"),
                require_positive(take("radius"), "radius"),
                sides,
                float(take("rotation", 90.0 if sides % 2 else 0.0)),
                props,
            )
        elif op == "circle":
            result = partial(
                b.add_circle,
                to_point(take("center"), "center"),
                require_positive(take("radius"), "radius"),
                props,
            )
        elif op == "arc":
            result = partial(
                b.add_arc,
                to_point(take("center"), "center"),
                require_positive(take("radius"), "radius"),
                float(take("start_angle")),
                float(take("end_angle")),
                props,
            )
        elif op == "ellipse":
            ratio = float(take("ratio"))
            if not 0 < ratio <= 1:
                raise CadError("ratio (minor/major) must be in (0, 1].")
            major = to_point(take("major_axis"), "major_axis")
            if major == (0.0, 0.0, 0.0):
                raise CadError("major_axis must be a non-zero vector.")
            result = partial(b.add_ellipse, to_point(take("center"), "center"), major, ratio, props)
        elif op == "point":
            result = partial(b.add_point, to_point(take("location"), "location"), props)
        elif op == "text":
            text = str(take("text"))
            if not text:
                raise CadError("text must not be empty.")
            result = partial(
                b.add_text,
                text,
                to_point(take("insert"), "insert"),
                require_positive(take("height", 2.5), "height"),
                float(take("rotation", 0.0)),
                props,
            )
        elif op == "mtext":
            text = str(take("text"))
            if not text:
                raise CadError("text must not be empty.")
            width = float(take("width", 0.0))
            if width < 0:
                raise CadError("width must be >= 0.")
            result = partial(
                b.add_mtext,
                text,
                to_point(take("insert"), "insert"),
                width,
                require_positive(take("height", 2.5), "height"),
                props,
            )
        elif op == "dimension":
            kind = str(take("kind", "aligned")).lower()
            p1, p2 = to_point(take("p1"), "p1"), to_point(take("p2"), "p2")
            if p1 == p2:
                raise CadError("Dimension points must differ.")
            loc = to_point(take("location"), "location")
            text_height = take("text_height", None)
            if text_height is not None:
                props["text_height"] = require_positive(text_height, "text_height")
            if kind == "aligned":
                result = partial(b.add_aligned_dimension, p1, p2, loc, props)
            elif kind in ("linear", "rotated", "horizontal", "vertical"):
                rotation = {"horizontal": 0.0, "vertical": 90.0}.get(kind, float(take("rotation", 0.0)))
                result = partial(b.add_linear_dimension, p1, p2, loc, rotation, props)
            else:
                raise CadError("kind must be aligned, linear, horizontal or vertical.")
        elif op == "hatch":
            result = partial(
                b.add_hatch,
                str(take("boundary")),
                str(take("pattern", "ANSI31")),
                require_positive(take("scale", 1.0), "scale"),
                float(take("angle", 0.0)),
                props,
            )
        elif op == "block":
            result = partial(
                b.insert_block,
                str(take("name")),
                to_point(take("insert"), "insert"),
                require_positive(take("scale", 1.0), "scale"),
                float(take("rotation", 0.0)),
                props,
            )
        else:
            raise CadError(
                f"Unknown op {op!r}. Use line, polyline, rectangle, polygon, circle, arc, ellipse, point, "
                "text, mtext, dimension, hatch or block."
            )
        # Every argument is parsed and checked before anything is created, so a rejected item
        # never leaves geometry behind.
        if a:
            raise CadError(f"'{op}' got unexpected argument(s): {', '.join(sorted(a))}.")
        return result()


def create_server(backend: CadBackend | None = None, settings: Settings | None = None) -> MCPServer:
    settings = settings or Settings.from_env()
    backend = backend or create_backend(settings)
    cad = PowerCad(backend, settings)
    mcp = MCPServer(
        name="power-cad-mcp", title="Power CAD MCP", version=__version__, instructions=INSTRUCTIONS
    )
    mcp.cad = cad  # type: ignore[attr-defined]  # handy for tests and embedding

    def tool(annotations: ToolAnnotations, **kw: Any) -> Callable[[Callable], Callable]:
        """Register a tool whose CadError / ValueError become clean MCP tool errors."""

        def decorate(fn: Callable) -> Callable:
            import functools

            @functools.wraps(fn)
            def wrapped(*args: Any, **kwargs: Any) -> Any:
                try:
                    return fn(*args, **kwargs)
                except (CadError, ValueError, TypeError) as exc:
                    raise ToolError(str(exc)) from exc

            mcp.tool(annotations=annotations, **kw)(wrapped)
            return fn

        return decorate

    b = backend

    # ------------------------------------------------------------------ session
    @tool(READ)
    def cad_status() -> dict[str, Any]:
        """Report the active backend (AutoCAD over COM, or headless DXF), connection and open drawing."""
        return b.status()

    @tool(WRITE)
    def new_drawing(
        template: Annotated[
            str | None, Field(description="Optional .dwt template (AutoCAD) or .dxf (headless)")
        ] = None,
    ) -> dict[str, Any]:
        """Create a new, empty drawing and make it active."""
        return b.new_drawing(cad.path(template) if template else None)

    @tool(WRITE)
    def open_drawing(path: Annotated[str, Field(description="Path to a .dwg/.dxf file")]) -> dict[str, Any]:
        """Open an existing drawing and make it active."""
        return b.open_drawing(cad.path(path))

    @tool(WRITE)
    def save_drawing(
        path: Annotated[
            str | None, Field(description="Save-as path (.dwg or .dxf). Omit to save in place.")
        ] = None,
    ) -> dict[str, Any]:
        """Save the active drawing (optionally under a new name)."""
        default_ext = ".dxf" if b.name == "dxf" else ".dwg"
        return b.save_drawing(cad.path(path, ext=default_ext) if path else None)

    @tool(READ)
    def get_drawing_info() -> dict[str, Any]:
        """Name, path, units, current layer, entity count and extents of the active drawing."""
        return b.drawing_info()

    @tool(READ)
    def drawing_census(
        path: Annotated[str, Field(description="Drawing to inventory (.dxf; .dwg needs ODA File Converter)")],
        out_dir: Annotated[
            str | None, Field(description="Where census.md / census.json go (default: <drawing>_census)")
        ] = None,
        standard: Annotated[
            str | None, Field(description="ZIUM floor_plan_standard.json for sheet scale and layer mapping")
        ] = None,
    ) -> dict[str, Any]:
        """Inventory every entity of a drawing file and prove none was skipped.

        Buckets each entity into a title-block sheet, outside the sheets, a layout or a block definition,
        checks the total against the file, and reports unmapped layers, blocks, hatches, texts and
        entities the query tools cannot see (leaders, proxies, OLE). Reads the file; never changes it.
        """
        from pathlib import Path

        from .census import run

        src = Path(cad.path(path))
        out = Path(cad.path(out_dir)) if out_dir else src.with_name(src.stem + "_census")
        return run(src, Path(cad.path(standard)) if standard else None, out)

    # ------------------------------------------------------------------- layers
    @tool(READ)
    def list_layers() -> list[dict[str, Any]]:
        """List layers with color, linetype and on/frozen/locked/current state."""
        return b.list_layers()

    @tool(WRITE)
    def create_layer(
        name: Annotated[str, Field(min_length=1, max_length=255)],
        color: ColorArg = None,
        linetype: LinetypeArg = None,
        lineweight: Annotated[
            int | None, Field(description="Lineweight in 1/100 mm (e.g. 25 = 0.25 mm); -3 default")
        ] = None,
        make_current: bool = False,
    ) -> dict[str, Any]:
        """Create a layer (or update it if it exists) and optionally make it current."""
        layer = b.create_layer(name, parse_color(color), linetype, lineweight)
        if make_current:
            layer = b.set_current_layer(name)
        return layer

    @tool(WRITE)
    def update_layer(
        name: str,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
        on: bool | None = None,
        frozen: bool | None = None,
        locked: bool | None = None,
        new_name: str | None = None,
    ) -> dict[str, Any]:
        """Change a layer's color/linetype, turn it on/off, freeze/thaw, lock/unlock or rename it."""
        return b.update_layer(
            name,
            color=parse_color(color),
            linetype=linetype,
            on=on,
            frozen=frozen,
            locked=locked,
            new_name=new_name,
        )

    @tool(WRITE)
    def set_current_layer(name: str) -> dict[str, Any]:
        """Make a layer current; new entities without an explicit layer go there."""
        return b.set_current_layer(name)

    @tool(DESTRUCTIVE)
    def delete_layer(name: str) -> dict[str, Any]:
        """Delete an unused layer."""
        b.delete_layer(name)
        return {"deleted": name}

    # ------------------------------------------------------------------ drawing
    @tool(WRITE)
    def draw_line(
        start: PointArg,
        end: PointArg,
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a straight line segment."""
        return cad.draw("line", dict(start=start, end=end, layer=layer, color=color, linetype=linetype))

    @tool(WRITE)
    def draw_polyline(
        points: Annotated[list[PointArg], Field(min_length=2)],
        closed: bool = False,
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a polyline through the points (2D lightweight polyline; 3D if Z varies)."""
        return cad.draw(
            "polyline", dict(points=points, closed=closed, layer=layer, color=color, linetype=linetype)
        )

    @tool(WRITE)
    def draw_rectangle(
        corner1: PointArg,
        corner2: PointArg,
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw an axis-aligned rectangle (closed polyline) from two opposite corners."""
        return cad.draw(
            "rectangle", dict(corner1=corner1, corner2=corner2, layer=layer, color=color, linetype=linetype)
        )

    @tool(WRITE)
    def draw_polygon(
        center: PointArg,
        radius: Annotated[float, Field(gt=0, description="Circumscribed radius (center to vertex)")],
        sides: Annotated[int, Field(ge=3, le=1024)],
        rotation: Annotated[float | None, Field(description="Angle of the first vertex in degrees")] = None,
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a regular polygon as a closed polyline."""
        args: dict[str, Any] = dict(
            center=center, radius=radius, sides=sides, layer=layer, color=color, linetype=linetype
        )
        if rotation is not None:
            args["rotation"] = rotation
        return cad.draw("polygon", args)

    @tool(WRITE)
    def draw_circle(
        center: PointArg,
        radius: Annotated[float, Field(gt=0)],
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a circle."""
        return cad.draw(
            "circle", dict(center=center, radius=radius, layer=layer, color=color, linetype=linetype)
        )

    @tool(WRITE)
    def draw_arc(
        center: PointArg,
        radius: Annotated[float, Field(gt=0)],
        start_angle: Annotated[float, Field(description="Degrees, CCW from +X")],
        end_angle: Annotated[float, Field(description="Degrees, CCW from +X")],
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a circular arc counter-clockwise from start_angle to end_angle."""
        return cad.draw(
            "arc",
            dict(
                center=center,
                radius=radius,
                start_angle=start_angle,
                end_angle=end_angle,
                layer=layer,
                color=color,
                linetype=linetype,
            ),
        )

    @tool(WRITE)
    def draw_ellipse(
        center: PointArg,
        major_axis: Annotated[PointArg, Field(description="Vector from center to the end of the major axis")],
        ratio: Annotated[float, Field(gt=0, le=1, description="Minor/major axis ratio")],
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
    ) -> dict[str, Any]:
        """Draw a full ellipse."""
        return cad.draw(
            "ellipse",
            dict(
                center=center, major_axis=major_axis, ratio=ratio, layer=layer, color=color, linetype=linetype
            ),
        )

    @tool(WRITE)
    def draw_point(location: PointArg, layer: LayerArg = None, color: ColorArg = None) -> dict[str, Any]:
        """Place a POINT entity."""
        return cad.draw("point", dict(location=location, layer=layer, color=color))

    @tool(WRITE)
    def add_text(
        text: Annotated[str, Field(min_length=1)],
        insert: PointArg,
        height: Annotated[float, Field(gt=0)] = 2.5,
        rotation: float = 0.0,
        layer: LayerArg = None,
        color: ColorArg = None,
    ) -> dict[str, Any]:
        """Add single-line text at the insertion point (left baseline)."""
        return cad.draw(
            "text", dict(text=text, insert=insert, height=height, rotation=rotation, layer=layer, color=color)
        )

    @tool(WRITE)
    def add_mtext(
        text: Annotated[str, Field(min_length=1, description="Paragraph text; use \\P for new lines")],
        insert: PointArg,
        width: Annotated[float, Field(ge=0, description="Wrap width; 0 = no wrapping")] = 0.0,
        height: Annotated[float, Field(gt=0)] = 2.5,
        layer: LayerArg = None,
        color: ColorArg = None,
    ) -> dict[str, Any]:
        """Add multiline text (MTEXT) with its top-left corner at the insertion point."""
        return cad.draw(
            "mtext", dict(text=text, insert=insert, width=width, height=height, layer=layer, color=color)
        )

    @tool(WRITE)
    def add_dimension(
        p1: PointArg,
        p2: PointArg,
        location: Annotated[PointArg, Field(description="A point the dimension line passes through")],
        kind: Literal["aligned", "linear", "horizontal", "vertical"] = "aligned",
        rotation: Annotated[float, Field(description="Dimension-line angle for kind=linear (degrees)")] = 0.0,
        text_height: Annotated[
            float | None,
            Field(
                gt=0, description="Dimension text/arrow size in drawing units (e.g. 250 for a mm floor plan)"
            ),
        ] = None,
        layer: LayerArg = None,
        color: ColorArg = None,
    ) -> dict[str, Any]:
        """Dimension the distance between p1 and p2 (aligned, or linear/horizontal/vertical)."""
        args: dict[str, Any] = dict(kind=kind, p1=p1, p2=p2, location=location, layer=layer, color=color)
        if kind == "linear":
            args["rotation"] = rotation
        if text_height is not None:
            args["text_height"] = text_height
        return cad.draw("dimension", args)

    @tool(WRITE)
    def add_hatch(
        boundary: Annotated[str, Field(description="Handle of a closed polyline or circle")],
        pattern: Annotated[str, Field(description="SOLID, ANSI31, ANSI37, AR-CONC, NET, ...")] = "ANSI31",
        scale: Annotated[float, Field(gt=0)] = 1.0,
        angle: float = 0.0,
        layer: LayerArg = None,
        color: ColorArg = None,
    ) -> dict[str, Any]:
        """Fill a closed boundary with a hatch pattern or solid fill."""
        return cad.draw(
            "hatch",
            dict(boundary=boundary, pattern=pattern, scale=scale, angle=angle, layer=layer, color=color),
        )

    @tool(WRITE)
    def draw_batch(
        operations: Annotated[
            list[dict[str, Any]],
            Field(
                min_length=1,
                max_length=500,
                description=(
                    'Each item: {"op": <line|polyline|rectangle|polygon|circle|arc|ellipse|point|text|mtext|'
                    "dimension|hatch|block>, ...same arguments as the single tools}. For block use "
                    '{"op":"block","name":...,"insert":[x,y]}.'
                ),
            ),
        ],
        stop_on_error: bool = False,
        task: Annotated[
            str | None,
            Field(description="Free-text description of the automation (KO/EN), used for auto_context"),
        ] = None,
        auto_context: Annotated[
            bool | None,
            Field(
                description="Run ontology_auto_context(task) first and attach it as `ontology_context`. "
                "Default: POWERCAD_ONTOLOGY_AUTO_CONTEXT"
            ),
        ] = None,
    ) -> dict[str, Any]:
        """Create many entities in one call. Returns the created entity (or error) for every operation."""
        context: dict[str, Any] | None = None
        has_task = bool((task or "").strip())
        if auto_context and not has_task:
            raise CadError("auto_context needs a `task` describing the automation.")
        if auto_context is None:  # env default: only when the caller described the task
            auto_context = settings.ontology_auto_context and has_task
        targets: dict[str, Any] | None = None
        if auto_context:
            try:
                context = cad.auto_context(task or "")
            except ontology.OntologyError as exc:
                # The pre-step is advisory: drawing still proceeds when the Ontology service is down.
                context = {"available": False, "error": str(exc)}
            else:
                # Which of those elements can be edited by handle in the drawing that is open now.
                targets = ontology.targets_summary(context, cad.open_drawing(), cad.lookup)
        results: list[dict[str, Any]] = []
        created = 0
        for i, item in enumerate(operations):
            spec = dict(item)
            op = str(spec.pop("op", "")).lower()
            try:
                results.append({"index": i, "ok": True, "entity": cad.draw(op, spec)})
                created += 1
            except (CadError, ValueError, TypeError) as exc:
                results.append({"index": i, "ok": False, "op": op, "error": str(exc)})
                if stop_on_error:
                    break
        out: dict[str, Any] = {"created": created, "failed": len(results) - created, "results": results}
        if context is not None:
            out["ontology_context"] = context
        if targets is not None:
            out["ontology_targets"] = targets
        return out

    # ------------------------------------------------------------------- blocks
    @tool(READ)
    def list_blocks() -> list[dict[str, Any]]:
        """List block definitions available for insert_block."""
        return b.list_blocks()

    @tool(WRITE)
    def create_block(
        name: Annotated[str, Field(min_length=1)],
        base_point: PointArg,
        handles: HandlesArg,
        delete_source: Annotated[
            bool, Field(description="Remove the source entities after copying them")
        ] = False,
    ) -> dict[str, Any]:
        """Define a new block from existing model-space entities."""
        return b.create_block(name, to_point(base_point, "base_point"), handles, delete_source)

    @tool(WRITE)
    def insert_block(
        name: Annotated[
            str, Field(description="Block name, or a .dwg path (AutoCAD backend) to insert as a block")
        ],
        insert: PointArg,
        scale: Annotated[float, Field(gt=0)] = 1.0,
        rotation: float = 0.0,
        layer: LayerArg = None,
        color: ColorArg = None,
    ) -> dict[str, Any]:
        """Insert a block reference."""
        return cad.draw(
            "block", dict(name=name, insert=insert, scale=scale, rotation=rotation, layer=layer, color=color)
        )

    # -------------------------------------------------------------- query/edit
    @tool(READ)
    def list_entities(
        layer: Annotated[str | None, Field(description="Only this layer")] = None,
        entity_type: Annotated[
            str | None,
            Field(description="LINE, CIRCLE, ARC, POLYLINE, TEXT, MTEXT, INSERT, DIMENSION, HATCH..."),
        ] = None,
        limit: Annotated[int, Field(ge=1, le=5000)] = 200,
    ) -> dict[str, Any]:
        """List model-space entities with their geometry (filtered, up to `limit`)."""
        return b.list_entities(layer, entity_type, limit)

    @tool(READ)
    def get_entity(handle: str) -> dict[str, Any]:
        """Get one entity's type, layer, color and geometry by handle."""
        return b.get_entity(handle)

    @tool(DESTRUCTIVE)
    def delete_entities(handles: HandlesArg) -> dict[str, Any]:
        """Erase entities."""
        return {"deleted": b.delete_entities(handles)}

    @tool(WRITE)
    def move_entities(handles: HandlesArg, displacement: PointArg) -> list[dict[str, Any]]:
        """Move entities by a displacement vector [dx, dy(, dz)]."""
        return b.move_entities(handles, to_point(displacement, "displacement"))

    @tool(WRITE)
    def copy_entities(handles: HandlesArg, displacement: PointArg) -> list[dict[str, Any]]:
        """Copy entities, offsetting the copies by a displacement vector. Returns the new entities."""
        return b.copy_entities(handles, to_point(displacement, "displacement"))

    @tool(WRITE)
    def rotate_entities(
        handles: HandlesArg, base_point: PointArg, angle: Annotated[float, Field(description="Degrees, CCW")]
    ) -> list[dict[str, Any]]:
        """Rotate entities about a base point."""
        return b.rotate_entities(handles, to_point(base_point, "base_point"), angle)

    @tool(WRITE)
    def scale_entities(
        handles: HandlesArg, base_point: PointArg, factor: Annotated[float, Field(gt=0)]
    ) -> list[dict[str, Any]]:
        """Scale entities uniformly about a base point."""
        return b.scale_entities(
            handles, to_point(base_point, "base_point"), require_positive(factor, "factor")
        )

    @tool(WRITE)
    def mirror_entities(
        handles: HandlesArg, p1: PointArg, p2: PointArg, delete_source: bool = False
    ) -> list[dict[str, Any]]:
        """Mirror entities across the line p1-p2 (keeps the originals unless delete_source)."""
        return b.mirror_entities(handles, to_point(p1, "p1"), to_point(p2, "p2"), delete_source)

    @tool(WRITE)
    def offset_entity(
        handle: str, distance: Annotated[float, Field(description="Offset distance; the sign picks the side")]
    ) -> list[dict[str, Any]]:
        """Create a parallel copy (OFFSET) of a line, arc, circle or polyline."""
        if distance == 0:
            raise CadError("distance must be non-zero.")
        return b.offset_entity(handle, distance)

    @tool(WRITE)
    def set_entity_properties(
        handle: str,
        layer: LayerArg = None,
        color: ColorArg = None,
        linetype: LinetypeArg = None,
        lineweight: Annotated[int | None, Field(description="1/100 mm; -1 ByLayer")] = None,
    ) -> dict[str, Any]:
        """Change an entity's layer, color, linetype or lineweight."""
        props = _props(layer, color, linetype)
        props["lineweight"] = lineweight
        if all(v is None for v in props.values()):
            raise CadError("Pass at least one of layer, color, linetype, lineweight.")
        return b.set_entity_properties(handle, props)

    # ---------------------------------------------------------------- view/misc
    @tool(WRITE)
    def zoom_extents() -> dict[str, Any]:
        """Zoom the view to show the whole drawing."""
        b.zoom_extents()
        return {"ok": True, "extents": b.extents()}

    @tool(WRITE)
    def zoom_window(p1: PointArg, p2: PointArg) -> dict[str, Any]:
        """Zoom the view to the window spanned by two corners."""
        b.zoom_window(to_point(p1, "p1"), to_point(p2, "p2"))
        return {"ok": True}

    @tool(DESTRUCTIVE)
    def run_command(
        command: Annotated[
            str,
            Field(
                min_length=1,
                max_length=2000,
                description="AutoCAD command-line input, e.g. '_.FILLET R 5 ' or '-LAYER S Walls '. "
                "Newlines/spaces act as Enter.",
            ),
        ],
    ) -> dict[str, Any]:
        """Send raw input to the AutoCAD command line (AutoCAD backend only). Shell/script/loader commands are
        blocked; AutoLISP needs POWER_CAD_ALLOW_LISP=1."""
        return b.send_command(
            check_command(command, allow_commands=settings.allow_commands, allow_lisp=settings.allow_lisp)
        )

    @tool(WRITE)
    def export_drawing(
        path: str,
        format: Annotated[
            Literal["pdf", "dxf", "dwg", "png", "svg", "bmp", "wmf"] | None,
            Field(description="Defaults to the path's extension"),
        ] = None,
    ) -> dict[str, Any]:
        """Export the drawing: AutoCAD → pdf/dwg/dxf/png/bmp/wmf; headless → dxf/png/pdf/svg."""
        path_ext = os.path.splitext(path)[1].lstrip(".").lower()
        fmt = (format or path_ext).lower()
        if not fmt:
            raise CadError("Give a format or a path with an extension.")
        if path_ext and path_ext != fmt:
            raise CadError(f"The path ends in .{path_ext} but format is {fmt!r}; make them match.")
        return b.export(cad.path(path, ext="." + fmt), fmt)

    @tool(READ)
    def render_preview() -> Image:
        """Render the current drawing to a PNG image so you can check the result visually."""
        with tempfile.TemporaryDirectory() as tmp:
            target = os.path.join(tmp, "preview.png")
            b.export(target, "png")
            with open(target, "rb") as fh:
                return Image(data=fh.read(), format="png")

    # ----------------------------------------------------------------- ontology
    @tool(ONTOLOGY)
    def ontology_catalog(project_id: ProjectArg = None) -> dict[str, Any]:
        """Table of contents of the Ontology building-data store: element counts by kind (with Korean
        aliases), drawing categories, layers, blocks, storeys and projects. Use it to see what can be
        pulled up automatically."""
        data = cad.ontology.catalog(project_id)
        return data if isinstance(data, dict) else {"catalog": data}

    @tool(ONTOLOGY)
    def ontology_find_elements(
        kind: Annotated[
            str | None,
            Field(
                description="Door, Window, Wall, Space, Column, Beam, SteelSection ... "
                "or Korean (문, 창호, 벽); comma separated for several"
            ),
        ] = None,
        project_id: ProjectArg = None,
        storey: Annotated[str | None, Field(description="e.g. 2F, 2층, B1F (filtered client-side)")] = None,
        sheet: Annotated[
            str | None, Field(description="Sheet / layout / file name substring (filtered client-side)")
        ] = None,
        text: Annotated[
            str | None, Field(description="Substring of label or attribute values, e.g. AW-02")
        ] = None,
        drawing_category: Annotated[
            str | None, Field(description="평면도/상세도/... or plan/detail/section/elevation/structural")
        ] = None,
        layer: Annotated[str | None, Field(description="Layer name, wildcards allowed (A-WAL*)")] = None,
        block_name: Annotated[str | None, Field(description="Block name, wildcards allowed (DOOR*)")] = None,
        bbox: Annotated[
            str | None, Field(description="min_x,min_y,max_x,max_y in drawing coordinates")
        ] = None,
        include_properties: bool = True,
        limit: Annotated[int, Field(ge=1, le=500)] = 50,
        cursor: CursorArg = None,
    ) -> dict[str, Any]:
        """List building elements by kind (doors as doors, windows as windows, walls as walls...) with
        source file, sheet, layer, block name, handle, attributes and properties. Page with next_cursor."""
        page = cad.ontology.elements(
            kind,
            project_id=project_id,
            storey=storey,
            sheet=sheet,
            text=text,
            drawing_category=drawing_category,
            layer=layer,
            block_name=block_name,
            bbox=bbox,
            include_properties=include_properties,
            limit=limit,
            cursor=cursor,
        )
        return {"count": len(page["items"]), "elements": page["items"], "next_cursor": page["next_cursor"]}

    @tool(ONTOLOGY)
    def ontology_blocks(
        category: Annotated[
            str | None,
            Field(description="Kind the block's instances are classified as: Door, Window, 창호 ..."),
        ] = None,
        name_like: Annotated[
            str | None, Field(description="Block name substring or wildcard (DOOR*)")
        ] = None,
        project_id: ProjectArg = None,
        limit: Annotated[int, Field(ge=1, le=500)] = 50,
        cursor: CursorArg = None,
    ) -> dict[str, Any]:
        """List CAD block definitions organised by category, with instance counts, attribute tags,
        layers and example files."""
        page = cad.ontology.blocks(category, name_like, project_id=project_id, limit=limit, cursor=cursor)
        return {"count": len(page["items"]), "blocks": page["items"], "next_cursor": page["next_cursor"]}

    @tool(ONTOLOGY)
    def ontology_drawings(
        category: Annotated[
            str | None,
            Field(
                description="plan, detail, section, elevation, structural, schedule, or Korean "
                "(평면도, 상세도, 단면도, 입면도, 구조도, 창호도 ...)"
            ),
        ] = None,
        q: Annotated[str | None, Field(description="Filter by drawing number / title / file name")] = None,
        project_id: ProjectArg = None,
        limit: Annotated[int, Field(ge=1, le=500)] = 50,
        cursor: CursorArg = None,
    ) -> dict[str, Any]:
        """List sheets (drawing number, title, scale, category, file, layout), e.g. every detail drawing."""
        page = cad.ontology.drawings(category, q, project_id=project_id, limit=limit, cursor=cursor)
        return {"count": len(page["items"]), "drawings": page["items"], "next_cursor": page["next_cursor"]}

    @tool(ONTOLOGY)
    def ontology_element_context(
        element_id: Annotated[str, Field(min_length=1)],
        hops: Annotated[int, Field(ge=1, le=2)] = 1,
    ) -> dict[str, Any]:
        """One element plus its graph neighbours (storey, space, host wall, sheet, block definition ...)."""
        data = cad.ontology.element_context(element_id, hops)
        return data if isinstance(data, dict) else {"context": data}

    @tool(ONTOLOGY)
    def ontology_search(
        query: Annotated[str, Field(min_length=1, description="Korean or English question / keywords")],
        k: Annotated[int, Field(ge=1, le=100)] = 10,
        kind: Annotated[str | None, Field(description="Optional kind filter (Door, Window ...)")] = None,
        storey: str | None = None,
        project_id: ProjectArg = None,
        model: Annotated[
            str | None, Field(description="Embedding model hint; ignored by APIs that do not support it")
        ] = None,
    ) -> dict[str, Any]:
        """Hybrid lexical + vector + graph search over every parsed drawing."""
        hits = cad.ontology.search(query, k=k, model=model, kind=kind, storey=storey, project_id=project_id)
        return {"count": len(hits), "hits": hits}

    @tool(ONTOLOGY)
    def ontology_auto_context(
        task: Annotated[
            str,
            Field(
                min_length=1,
                description="What the automation will do, e.g. '2층 평면도 문 리스트 갱신' or "
                "'창호상세도에 AW-02 추가'",
            ),
        ],
        drawing: Annotated[
            str | None, Field(description="Optional sheet number / file name to focus on")
        ] = None,
        limit: Annotated[int, Field(ge=1, le=200)] = 20,
        project_id: ProjectArg = None,
        k: Annotated[int, Field(ge=1, le=100, description="Number of search hits to keep")] = 10,
    ) -> dict[str, Any]:
        """Pull up every relevant element for a task without the user listing them: infers element
        classes (Door/Window/Wall/Space/Column/Beam/SteelSection...), drawing categories (plan/detail/...),
        storey and marks from the text, then returns search hits, elements by class, sheets by category
        and blocks in one bundle. When a drawing is open, each hit/element carries `in_open_drawing`;
        pass the ids to ontology_locate before editing by handle."""
        return cad.auto_context(task, drawing, limit, k=k, project_id=project_id)

    @tool(ONTOLOGY)
    def ontology_locate(
        element_ids: Annotated[
            list[str], Field(min_length=1, max_length=200, description="Ontology element ids (id field)")
        ],
    ) -> dict[str, Any]:
        """Map Ontology elements to live CAD handles in the open drawing, safely and read-only.

        Handles are only unique per drawing, so for each id this checks that the element's source file is
        the open drawing (basename, case-insensitive, .dwg = .dxf), that the handle exists there and that
        the entity is plausible for the element (type, layer, block). Status per id: matched,
        mismatch, handle_missing, other_drawing or not_found. Only `matched` handles are safe to pass to
        get_entity / move_entities / delete_entities / set_entity_properties. Never modifies the drawing."""
        return cad.locate(element_ids)

    @tool(ONTOLOGY)
    def ontology_block_candidates(
        name_or_task: Annotated[
            str,
            Field(
                min_length=1,
                description="Block name or wildcard (DOOR*), or a task: '문 블록 배치' / 'place windows'",
            ),
        ],
        project_id: ProjectArg = None,
        limit: Annotated[int, Field(ge=1, le=500)] = 50,
    ) -> dict[str, Any]:
        """Which Ontology blocks can be inserted into the open drawing right now, read-only.

        Cross-references the Ontology block catalog (by name, or by the element classes a task names)
        with the open drawing's block definitions (list_blocks, case-insensitive, effective names too).
        `insertable` entries carry `insert_name`, the definition name to pass to insert_block;
        `other_files` exist only in other drawings (see example_files) and are not imported;
        `not_insertable` are xrefs / anonymous blocks. Never modifies the drawing."""
        return cad.block_candidates(name_or_task, project_id=project_id, limit=limit)

    return mcp
