"""Opt-in acceptance against a dedicated AutoCAD process and newly created scratch drawings.

Refuses to start if AutoCAD is already running. Does not open user drawings.
Requires Windows, pywin32 and the matching installed PowerCad bundle.
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import json
import subprocess
import time
from pathlib import Path

from mcp import Client
from mcp.client.stdio import StdioServerParameters


async def verify(executable: Path, server: Path, output: Path) -> None:
    import pythoncom
    import win32com.client
    import win32process

    running = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq acad.exe", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
        check=True,
    )
    if '"acad.exe"' in running.stdout.lower():
        raise RuntimeError("Close existing AutoCAD sessions before this dedicated acceptance test.")
    output.mkdir(parents=True, exist_ok=False)
    startup = subprocess.STARTUPINFO()
    startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    process = subprocess.Popen([str(executable), "/nologo"], startupinfo=startup)
    app = None
    scratch = []
    startup_names = set()
    checks: list[str] = []
    report: dict = {"checks": checks, "autocad_pid": process.pid, "passed": False}
    try:
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            try:
                candidate = win32com.client.GetActiveObject("AutoCAD.Application.26")
                if win32process.GetWindowThreadProcessId(candidate.HWND)[1] == process.pid:
                    app = candidate
                    startup_names = {doc.Name for doc in app.Documents}
                    break
            except pythoncom.com_error:
                pass
            await asyncio.sleep(1)
        if app is None:
            raise RuntimeError("The dedicated AutoCAD process did not become COM-ready in 150 seconds.")

        def point(x: float, y: float):
            return win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_R8, (x, y, 0.0))

        a = app.Documents.Add()
        scratch.append(a)
        a.ModelSpace.AddCircle(point(0, 0), 10)
        a.SaveAs(str(output / "drawing-a.dwg"))
        b = app.Documents.Add()
        scratch.append(b)
        b.ModelSpace.AddCircle(point(0, 0), 10)
        b.SaveAs(str(output / "drawing-b.dwg"))

        async with Client(StdioServerParameters(command=str(server))) as client:

            async def call(name: str, **args):
                result = await client.call_tool(name, args)
                if result.is_error:
                    raise AssertionError(f"{name}: {result.content[0].text}")
                return json.loads(next(content.text for content in result.content if content.type == "text"))

            async def error(name: str, expected: str, **args):
                result = await client.call_tool(name, args)
                assert result.is_error and expected in result.content[0].text, name

            report["tool_count"] = len((await client.list_tools()).tools)
            identity = await call("cad_get_document_identity")
            await call("cad_bind_document", document_id=identity["document_id"])
            circle = (await call("cad_query", types=["CIRCLE"]))["entities"][0]
            a.Activate()
            await error("cad_get", "DOCUMENT_CHANGED", handles=[circle["handle"]])
            b.Activate()
            await call("cad_get", handles=[circle["handle"]])
            checks.append("document switch rejected under native binding")

            snapshot = await call("cad_extract_snapshot")
            await call("cad_query_page", snapshot_id=snapshot["snapshot_id"])
            await call("cad_review_snapshot", snapshot_id=snapshot["snapshot_id"])
            checks.append("native snapshot, frozen page and bundled standard review")
            plan = await call(
                "cad_plan_create",
                snapshot_id=snapshot["snapshot_id"],
                reason="Dedicated acceptance circle",
                steps=[
                    {
                        "command": "create",
                        "params": {"entities": [{"type": "circle", "center": [100, 0], "radius": 20}]},
                    }
                ],
            )
            await call("cad_plan_execute", plan_id=plan["plan_id"])
            assert (await call("cad_query", types=["CIRCLE"]))["total"] == 1
            await call("cad_plan_execute", plan_id=plan["plan_id"], dry_run=False)
            await call("cad_plan_execute", plan_id=plan["plan_id"], dry_run=False)
            assert (await call("cad_query", types=["CIRCLE"]))["total"] == 2
            checks.append("preview rollback, apply and committed-plan deduplication")
            b.SendCommand("_.UNDO\n1\n")
            await asyncio.sleep(2)
            assert (await call("cad_query", types=["CIRCLE"]))["total"] == 1
            checks.append("one native UNDO restored one applied plan")

            await call(
                "cad_move",
                targets=[{"handle": circle["handle"], "expect_fingerprint": circle["fingerprint"]}],
                displacement=[1, 0],
            )
            await error(
                "cad_move",
                "STALE_TARGET",
                targets=[{"handle": circle["handle"], "expect_fingerprint": circle["fingerprint"]}],
                displacement=[1, 0],
            )
            checks.append("native stale fingerprint rejection")

            await call("cad_create", entities=[{"type": "line", "start": [0, 100], "end": [1000, 100]}])
            line = (await call("cad_query", types=["LINE"]))["entities"][0]
            await call(
                "cad_offset",
                targets=[{"handle": line["handle"], "expect_fingerprint": line["fingerprint"]}],
                distance=50,
                side="left",
            )
            await call("cad_measure", handles=[line["handle"]])
            await call(
                "cad_create",
                entities=[{"type": "dimension", "p1": [0, 100], "p2": [1000, 100], "offset": 100}],
            )
            await call(
                "cad_create",
                entities=[
                    {
                        "type": "hatch",
                        "points": [[0, 300], [100, 300], [100, 400], [0, 400]],
                        "pattern": "SOLID",
                    }
                ],
            )
            checks.append("native line, offset, measurement, dimension and solid hatch")

            image = await client.call_tool("cad_snapshot", {"extents": True, "width": 600})
            assert not image.is_error
            block = next(content for content in image.content if content.type == "image")
            (output / "snapshot.png").write_bytes(base64.b64decode(block.data))
            for format_name in ("dwg", "dxf"):
                path = output / f"result.{format_name}"
                await call("cad_save", path=str(path), format=format_name)
                assert path.stat().st_size > 0
            checks.append("native viewport PNG and DWG/DXF copy saves")
            report["passed"] = True
    except Exception as exc:
        report["error"] = str(exc)
        raise
    finally:
        (output / "acceptance.json").write_text(
            json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8"
        )
        for doc in reversed(scratch):
            try:
                doc.Close(False)
            except Exception:
                report["cleanup_pending"] = True
        if app is not None:
            try:
                remaining = list(app.Documents)
                if all(doc.Name in startup_names and doc.ModelSpace.Count == 0 for doc in remaining):
                    app.Quit()
            except Exception:
                report["cleanup_pending"] = True
        elif process.poll() is None:
            # Only terminate the process created by this harness before COM readiness.
            process.terminate()
            process.wait(timeout=30)
        (output / "acceptance.json").write_text(
            json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8"
        )
        print(json.dumps(report, ensure_ascii=False), flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--autocad", type=Path, required=True)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.autocad.resolve(), args.server.resolve(), args.out.resolve()))


if __name__ == "__main__":
    main()
