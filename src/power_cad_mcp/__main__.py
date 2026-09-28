"""Command-line entry point: ``power-cad-mcp`` (stdio by default)."""

from __future__ import annotations

import argparse
import logging
import sys

from . import __version__
from .config import Settings


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(
        prog="power-cad-mcp", description="MCP server for AutoCAD / DXF drafting"
    )
    parser.add_argument("--backend", choices=["auto", "autocad", "dxf"], help="Overrides POWER_CAD_BACKEND")
    parser.add_argument("--dxf-path", help="Headless backend: DXF file to load/save by default")
    parser.add_argument("--workspace", help="Base directory for relative file paths")
    parser.add_argument("--launch", action="store_true", help="Start AutoCAD if it is not running")
    parser.add_argument("--transport", choices=["stdio", "streamable-http", "sse"], default="stdio")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--check", action="store_true", help="Print backend status as JSON and exit")
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(levelname)s %(name)s: %(message)s")
    logging.getLogger("ezdxf").setLevel(logging.WARNING)

    settings = Settings.from_env()
    if args.backend:
        settings.backend = args.backend
    if args.dxf_path:
        settings.dxf_path = args.dxf_path
    if args.workspace:
        settings.workspace = args.workspace
    if args.launch:
        settings.launch = True

    from .backends import create_backend
    from .server import create_server

    backend = create_backend(settings)
    if args.check:
        import json

        status = backend.status()
        print(json.dumps(status, indent=2, ensure_ascii=False))
        backend.close()
        sys.exit(0 if status.get("connected") else 1)

    server = create_server(backend, settings)
    try:
        if args.transport == "stdio":
            server.run("stdio")
        else:
            server.run(args.transport, host=args.host, port=args.port)
    finally:
        backend.close()


if __name__ == "__main__":
    main()
