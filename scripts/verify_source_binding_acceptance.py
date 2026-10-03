#!/usr/bin/env python3
"""Check an Ontology executor handoff against fresh Power CAD observations."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from power_cad_mcp.source_binding_acceptance import (
    READY,
    evaluate_source_binding_acceptance,
)


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--handoff", type=Path, required=True)
    parser.add_argument("--document", type=Path, required=True)
    parser.add_argument("--target", type=Path, required=True)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    result = evaluate_source_binding_acceptance(
        load(args.handoff),
        load(args.document),
        load(args.target),
    )
    rendered = json.dumps(result, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0 if result["status"] == READY else 2


if __name__ == "__main__":
    raise SystemExit(main())
