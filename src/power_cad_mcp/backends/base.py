"""Backend interface. Every backend speaks plain dicts/lists so the MCP layer stays backend-agnostic.

Conventions:
* Points are ``(x, y, z)`` tuples in drawing units.
* Angles at this interface are in **degrees** (backends convert as needed).
* Entities are identified by their DWG/DXF **handle** (hex string), stable within a drawing.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Sequence
from typing import Any

from ..geometry import Point3, rectangle_points, regular_polygon_points

Entity = dict[str, Any]


class CadBackend(ABC):
    name: str = "abstract"

    # ---- session / documents -------------------------------------------------
    @abstractmethod
    def status(self) -> dict[str, Any]: ...

    @abstractmethod
    def new_drawing(self, template: str | None = None) -> dict[str, Any]: ...

    @abstractmethod
    def open_drawing(self, path: str) -> dict[str, Any]: ...

    @abstractmethod
    def save_drawing(self, path: str | None = None) -> dict[str, Any]: ...

    @abstractmethod
    def drawing_info(self) -> dict[str, Any]: ...

    # ---- layers --------------------------------------------------------------
    @abstractmethod
    def list_layers(self) -> list[dict[str, Any]]: ...

    @abstractmethod
    def create_layer(
        self, name: str, color: int | None = None, linetype: str | None = None, lineweight: int | None = None
    ) -> dict[str, Any]: ...

    @abstractmethod
    def update_layer(
        self,
        name: str,
        *,
        color: int | None = None,
        linetype: str | None = None,
        on: bool | None = None,
        frozen: bool | None = None,
        locked: bool | None = None,
        new_name: str | None = None,
    ) -> dict[str, Any]: ...

    @abstractmethod
    def set_current_layer(self, name: str) -> dict[str, Any]: ...

    @abstractmethod
    def delete_layer(self, name: str) -> None: ...

    # ---- drawing primitives --------------------------------------------------
    @abstractmethod
    def add_line(self, start: Point3, end: Point3, props: dict[str, Any]) -> Entity: ...

    @abstractmethod
    def add_polyline(self, points: Sequence[Point3], closed: bool, props: dict[str, Any]) -> Entity: ...

    @abstractmethod
    def add_circle(self, center: Point3, radius: float, props: dict[str, Any]) -> Entity: ...

    @abstractmethod
    def add_arc(
        self, center: Point3, radius: float, start_angle: float, end_angle: float, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_ellipse(
        self, center: Point3, major_axis: Point3, ratio: float, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_point(self, location: Point3, props: dict[str, Any]) -> Entity: ...

    @abstractmethod
    def add_text(
        self, text: str, insert: Point3, height: float, rotation: float, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_mtext(
        self, text: str, insert: Point3, width: float, height: float, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_aligned_dimension(
        self, p1: Point3, p2: Point3, text_position: Point3, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_linear_dimension(
        self, p1: Point3, p2: Point3, dim_line_point: Point3, rotation: float, props: dict[str, Any]
    ) -> Entity: ...

    @abstractmethod
    def add_hatch(
        self, boundary_handle: str, pattern: str, scale: float, angle: float, props: dict[str, Any]
    ) -> Entity: ...

    def add_rectangle(self, corner1: Point3, corner2: Point3, props: dict[str, Any]) -> Entity:
        return self.add_polyline(rectangle_points(corner1, corner2), True, props)

    def add_polygon(
        self, center: Point3, radius: float, sides: int, rotation: float, props: dict[str, Any]
    ) -> Entity:
        return self.add_polyline(regular_polygon_points(center, radius, sides, rotation), True, props)

    # ---- blocks --------------------------------------------------------------
    @abstractmethod
    def list_blocks(self) -> list[dict[str, Any]]: ...

    @abstractmethod
    def create_block(
        self, name: str, base_point: Point3, handles: Sequence[str], delete_source: bool
    ) -> dict: ...

    @abstractmethod
    def insert_block(
        self, name: str, insert: Point3, scale: float, rotation: float, props: dict[str, Any]
    ) -> Entity: ...

    # ---- query / edit --------------------------------------------------------
    @abstractmethod
    def list_entities(
        self, layer: str | None = None, entity_type: str | None = None, limit: int = 200
    ) -> dict[str, Any]: ...

    @abstractmethod
    def get_entity(self, handle: str) -> Entity: ...

    def model_space_entity(self, handle: str) -> Entity | None:
        """Read-only lookup used to resolve Ontology handles: the model-space entity, else None.

        Never raises for a missing handle, a non-entity object (layer record, block definition) or an
        entity owned by another block / layout. Backends whose get_entity is not model-space-only
        override this."""
        try:
            return self.get_entity(handle)
        except Exception:  # noqa: BLE001 - a missing/invalid handle is a result, not an error
            return None

    @abstractmethod
    def delete_entities(self, handles: Sequence[str]) -> int: ...

    @abstractmethod
    def move_entities(self, handles: Sequence[str], displacement: Point3) -> list[Entity]: ...

    @abstractmethod
    def copy_entities(self, handles: Sequence[str], displacement: Point3) -> list[Entity]: ...

    @abstractmethod
    def rotate_entities(self, handles: Sequence[str], base: Point3, angle: float) -> list[Entity]: ...

    @abstractmethod
    def scale_entities(self, handles: Sequence[str], base: Point3, factor: float) -> list[Entity]: ...

    @abstractmethod
    def mirror_entities(
        self, handles: Sequence[str], p1: Point3, p2: Point3, delete_source: bool
    ) -> list[Entity]: ...

    @abstractmethod
    def offset_entity(self, handle: str, distance: float) -> list[Entity]: ...

    @abstractmethod
    def set_entity_properties(self, handle: str, props: dict[str, Any]) -> Entity: ...

    @abstractmethod
    def extents(self) -> dict[str, Any] | None: ...

    # ---- view / misc ---------------------------------------------------------
    @abstractmethod
    def zoom_extents(self) -> None: ...

    @abstractmethod
    def zoom_window(self, p1: Point3, p2: Point3) -> None: ...

    @abstractmethod
    def send_command(self, command: str) -> dict[str, Any]: ...

    @abstractmethod
    def export(self, path: str, fmt: str) -> dict[str, Any]: ...

    def close(self) -> None:  # noqa: B027 - optional hook
        """Release resources (threads, COM references)."""
