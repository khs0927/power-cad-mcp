"""Backend selection."""

from __future__ import annotations

import sys

from ..config import Settings
from ..errors import CadError
from .base import CadBackend

__all__ = ["CadBackend", "create_backend"]


def create_backend(settings: Settings) -> CadBackend:
    kind = settings.backend
    if kind == "auto":
        kind = "autocad" if sys.platform == "win32" else "dxf"
    if kind in ("autocad", "com"):
        from .com_backend import ComBackend

        return ComBackend(progids=settings.progids or None, launch=settings.launch)
    if kind in ("dxf", "ezdxf", "headless"):
        from .dxf_backend import DxfBackend

        return DxfBackend(settings.dxf_path)
    raise CadError(f"Unknown backend {settings.backend!r}; use auto, autocad or dxf.")
