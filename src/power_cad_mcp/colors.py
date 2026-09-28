"""AutoCAD Color Index (ACI) helpers."""

from __future__ import annotations

from .errors import CadError

BYBLOCK = 0
BYLAYER = 256

COLOR_NAMES: dict[str, int] = {
    "byblock": BYBLOCK,
    "red": 1,
    "yellow": 2,
    "green": 3,
    "cyan": 4,
    "blue": 5,
    "magenta": 6,
    "white": 7,
    "black": 7,  # ACI 7 renders black on light backgrounds, white on dark ones
    "gray": 8,
    "grey": 8,
    "lightgray": 9,
    "lightgrey": 9,
    "bylayer": BYLAYER,
}


def parse_color(value: int | str | None, *, default: int | None = None) -> int | None:
    """Accept an ACI number (0-256), a numeric string or a common color name."""
    if value is None or value == "":
        return default
    if isinstance(value, bool):
        raise CadError(f"Invalid color: {value!r}")
    if isinstance(value, int):
        aci = value
    else:
        text = str(value).strip().lower().replace(" ", "").replace("_", "")
        if text in COLOR_NAMES:
            return COLOR_NAMES[text]
        try:
            aci = int(text)
        except ValueError:
            raise CadError(
                f"Unknown color {value!r}. Use an ACI number 0-256 or one of: {', '.join(COLOR_NAMES)}"
            ) from None
    if not 0 <= aci <= 256:
        raise CadError(f"Color index {aci} out of range (0-256).")
    return aci


def color_name(aci: int | None) -> str | None:
    if aci is None:
        return None
    for name, idx in COLOR_NAMES.items():
        if idx == aci and name not in ("black", "grey", "lightgrey"):
            return name
    return str(aci)
