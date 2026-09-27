"""Small geometry helpers shared by all backends."""

from __future__ import annotations

import math
from collections.abc import Sequence

from .errors import CadError

Point3 = tuple[float, float, float]


def to_point(value: Sequence[float] | None, name: str = "point") -> Point3:
    if value is None:
        raise CadError(f"{name} is required.")
    try:
        coords = [float(v) for v in value]
    except (TypeError, ValueError):
        raise CadError(f"{name} must be a list of 2 or 3 numbers, got {value!r}.") from None
    if len(coords) == 2:
        coords.append(0.0)
    if len(coords) != 3:
        raise CadError(f"{name} must have 2 or 3 coordinates, got {len(coords)}.")
    if not all(math.isfinite(c) for c in coords):
        raise CadError(f"{name} contains a non-finite coordinate.")
    return coords[0], coords[1], coords[2]


def to_points(values: Sequence[Sequence[float]], name: str = "points", minimum: int = 2) -> list[Point3]:
    pts = [to_point(v, f"{name}[{i}]") for i, v in enumerate(values or [])]
    if len(pts) < minimum:
        raise CadError(f"{name} needs at least {minimum} points, got {len(pts)}.")
    return pts


def rectangle_points(corner1: Point3, corner2: Point3) -> list[Point3]:
    (x1, y1, z), (x2, y2, _) = corner1, corner2
    if math.isclose(x1, x2) or math.isclose(y1, y2):
        raise CadError("Rectangle corners must differ in both X and Y.")
    return [(x1, y1, z), (x2, y1, z), (x2, y2, z), (x1, y2, z)]


def regular_polygon_points(
    center: Point3, radius: float, sides: int, rotation_deg: float = 0.0
) -> list[Point3]:
    if sides < 3:
        raise CadError("A polygon needs at least 3 sides.")
    if radius <= 0:
        raise CadError("Polygon radius must be positive.")
    cx, cy, cz = center
    start = math.radians(rotation_deg)
    step = 2 * math.pi / sides
    return [
        (cx + radius * math.cos(start + i * step), cy + radius * math.sin(start + i * step), cz)
        for i in range(sides)
    ]


def require_positive(value: float, name: str) -> float:
    value = float(value)
    if not math.isfinite(value) or value <= 0:
        raise CadError(f"{name} must be a positive number.")
    return value


def rnd(value: float, ndigits: int = 6) -> float:
    r = round(float(value), ndigits)
    return 0.0 if r == 0 else r


def rpt(p: Sequence[float]) -> list[float]:
    return [rnd(c) for c in p]
