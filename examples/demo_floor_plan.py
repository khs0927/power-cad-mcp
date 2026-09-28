"""Draw a small floor plan through the MCP tools and export it.

python examples/demo_floor_plan.py                 # headless: writes examples/out/floor_plan.{dxf,png}
python examples/demo_floor_plan.py --backend autocad   # draws live in the running AutoCAD (Windows)
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os

from mcp import Client

from power_cad_mcp.backends import create_backend
from power_cad_mcp.config import Settings
from power_cad_mcp.server import create_server

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")


async def main(backend_name: str) -> None:
    settings = Settings(backend=backend_name, workspace=OUT)
    server = create_server(create_backend(settings), settings)
    async with Client(server) as client:

        async def call(tool: str, /, **args):
            res = await client.call_tool(tool, args)
            if res.is_error:
                raise RuntimeError(f"{tool}: {res.content[0].text}")
            return res.structured_content

        print(json.dumps(await call("cad_status"), indent=2, ensure_ascii=False))
        await call("create_layer", name="A-WALL", color="white", lineweight=50)
        await call("create_layer", name="A-DOOR", color="yellow")
        await call("create_layer", name="A-ANNO", color="green")
        await call("create_layer", name="A-DIMS", color="cyan")
        await call("create_layer", name="A-HATCH", color=8)

        batch = await call(
            "draw_batch",
            operations=[
                {"op": "rectangle", "corner1": [0, 0], "corner2": [12000, 8000], "layer": "A-WALL"},
                {"op": "rectangle", "corner1": [200, 200], "corner2": [11800, 7800], "layer": "A-WALL"},
                {"op": "line", "start": [6000, 200], "end": [6000, 7800], "layer": "A-WALL"},
                {"op": "line", "start": [6000, 4000], "end": [11800, 4000], "layer": "A-WALL"},
                {
                    "op": "arc",
                    "center": [6000, 1000],
                    "radius": 900,
                    "start_angle": 0,
                    "end_angle": 90,
                    "layer": "A-DOOR",
                },
                {"op": "text", "text": "LIVING", "insert": [2200, 4000], "height": 400, "layer": "A-ANNO"},
                {"op": "text", "text": "BED 1", "insert": [8200, 5800], "height": 400, "layer": "A-ANNO"},
                {"op": "text", "text": "BED 2", "insert": [8200, 1800], "height": 400, "layer": "A-ANNO"},
                {
                    "op": "dimension",
                    "kind": "horizontal",
                    "p1": [0, 0],
                    "p2": [12000, 0],
                    "location": [6000, -1000],
                    "text_height": 250,
                    "layer": "A-DIMS",
                },
                {
                    "op": "dimension",
                    "kind": "vertical",
                    "p1": [12000, 0],
                    "p2": [12000, 8000],
                    "location": [13000, 4000],
                    "text_height": 250,
                    "layer": "A-DIMS",
                },
                {"op": "circle", "center": [3000, 6500], "radius": 500, "layer": "A-HATCH"},
            ],
        )
        column = batch["results"][-1]["entity"]["handle"]
        await call("add_hatch", boundary=column, pattern="SOLID", layer="A-HATCH")
        await call("zoom_extents")
        print(json.dumps(await call("get_drawing_info"), indent=2, ensure_ascii=False))
        if backend_name == "dxf":
            print(await call("export_drawing", path="floor_plan.dxf"))
            print(await call("export_drawing", path="floor_plan.png"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--backend", default="dxf", choices=["dxf", "autocad"])
    asyncio.run(main(parser.parse_args().backend))
