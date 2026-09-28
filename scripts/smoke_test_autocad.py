"""Quick manual check that Power CAD MCP can drive the AutoCAD instance running on this PC.

    python scripts/smoke_test_autocad.py

Draws a labelled test frame into a NEW drawing (your open drawings are untouched) and prints what it did.
"""

from __future__ import annotations

import json
import sys

from power_cad_mcp.backends.com_backend import ComBackend

P = {"layer": "PCMCP-SMOKE", "color": None, "linetype": None}


def main() -> int:
    backend = ComBackend()
    status = backend.status()
    print(json.dumps(status, indent=2, ensure_ascii=False))
    if not status.get("connected"):
        print("\n[FAIL] AutoCAD is not reachable. Start AutoCAD 2027 and run this again.")
        return 1
    try:
        backend.new_drawing()
        backend.create_layer("PCMCP-SMOKE", 3)
        frame = backend.add_rectangle((0, 0, 0), (420, 297, 0), P)
        backend.add_text("POWER CAD MCP - SMOKE TEST", (20, 260, 0), 10, 0, P)
        circle = backend.add_circle((210, 148.5, 0), 50, P)
        backend.add_hatch(circle["handle"], "ANSI31", 5, 0, P)
        backend.add_aligned_dimension((0, 0, 0), (420, 0, 0), (210, -20, 0), {**P, "text_height": 7})
        backend.zoom_extents()
        info = backend.drawing_info()
        print(json.dumps(info, indent=2, ensure_ascii=False))
        print(f"\n[OK] Drew {info['entity_count']} entities in {info['name']} (frame {frame['handle']}).")
        return 0
    finally:
        backend.close()


if __name__ == "__main__":
    sys.exit(main())
