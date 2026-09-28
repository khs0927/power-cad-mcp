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

from . import __version__
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

READ = ToolAnnotations(
    read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False
)
WRITE = ToolAnnotations(
    read_only_hint=False, destructive_hint=False, idempotent_hint=False, open_world_hint=False
)
DESTRUCTIVE = ToolAnnotations(
    read_only_hint=False, destructive_hint=True, idempotent_hint=False, open_world_hint=False
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
    ) -> dict[str, Any]:
        """Create many entities in one call. Returns the created entity (or error) for every operation."""
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
        return {"created": created, "failed": len(results) - created, "results": results}

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

    return mcp
