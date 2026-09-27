"""Runtime settings, read from environment variables (and optionally CLI flags)."""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _flag(name: str, default: bool) -> bool:
    value = os.environ.get(name)
    if value is None or value == "":
        return default
    return value.strip().lower() in ("1", "true", "yes", "on")


@dataclass
class Settings:
    backend: str = "auto"  # auto | autocad | dxf
    progids: list[str] = field(default_factory=list)
    launch: bool = False
    dxf_path: str | None = None
    allow_commands: bool = True
    allow_lisp: bool = False
    workspace: str | None = None

    @classmethod
    def from_env(cls) -> Settings:
        progids = [p.strip() for p in os.environ.get("POWER_CAD_PROGID", "").split(",") if p.strip()]
        return cls(
            backend=os.environ.get("POWER_CAD_BACKEND", "auto").strip().lower() or "auto",
            progids=progids,
            launch=_flag("POWER_CAD_LAUNCH", False),
            dxf_path=os.environ.get("POWER_CAD_DXF_PATH") or None,
            allow_commands=_flag("POWER_CAD_ALLOW_COMMANDS", True),
            allow_lisp=_flag("POWER_CAD_ALLOW_LISP", False),
            workspace=os.environ.get("POWER_CAD_WORKSPACE") or None,
        )
