#!/usr/bin/env python3
"""Read-only acceptance check for a Power CAD source binding."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from power_cad_mcp.source_binding import (
    CandidateLocator,
    LiveDocumentIdentity,
    LiveEntityIdentity,
    SourceTicket,
    verify_source_bound,
)


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ticket", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--document", type=Path, required=True)
    parser.add_argument("--entity", type=Path, required=True)
    parser.add_argument("--now", required=True, help="Offset-aware ISO-8601 time")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    report = verify_source_bound(
        SourceTicket(**load(args.ticket)),
        CandidateLocator(**load(args.candidate)),
        LiveDocumentIdentity(**load(args.document)),
        LiveEntityIdentity(**load(args.entity)),
        now=args.now,
    )

    rendered = json.dumps(report, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0 if report["binding_state"] == "SOURCE_BOUND" else 2


if __name__ == "__main__":
    raise SystemExit(main())
