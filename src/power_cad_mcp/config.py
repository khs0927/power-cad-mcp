"""Runtime settings, read from environment variables (and optionally CLI flags)."""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _flag(name: str, default: bool) -> bool:
    value = os.environ.get(name)
    if value is None or value == "":
        return default
    return value.strip().lower() in ("1", "true", "yes", "on")


def _env(*names: str) -> str | None:
    for name in names:
        value = os.environ.get(name)
        if value is not None and value.strip():
            return value.strip()
    return None


def _seconds(value: str | None, default: float) -> float:
    try:
        seconds = float(value) if value else default
    except ValueError:
        return default
    return min(max(seconds, 1.0), 120.0)


DEFAULT_ONTOLOGY_URL = "http://127.0.0.1:8765"


@dataclass
class Settings:
    backend: str = "auto"  # auto | autocad | dxf
    progids: list[str] = field(default_factory=list)
    launch: bool = False
    dxf_path: str | None = None
    allow_commands: bool = True
    allow_lisp: bool = False
    workspace: str | None = None
    # Read-only Ontology (aec_intelligence) REST API used by the ontology_* tools.
    ontology_url: str = DEFAULT_ONTOLOGY_URL
    ontology_timeout: float = 10.0
    ontology_token: str | None = None
    ontology_auto_context: bool = False  # run ontology_auto_context before draw_batch when a task is given

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
            ontology_url=_env("POWERCAD_ONTOLOGY_URL", "POWER_CAD_ONTOLOGY_URL") or DEFAULT_ONTOLOGY_URL,
            ontology_timeout=_seconds(_env("POWERCAD_ONTOLOGY_TIMEOUT"), 10.0),
            ontology_token=_env("POWERCAD_ONTOLOGY_TOKEN"),
            ontology_auto_context=_flag("POWERCAD_ONTOLOGY_AUTO_CONTEXT", False),
        )
