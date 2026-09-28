from __future__ import annotations

import json
import subprocess
import sys

import pytest

from power_cad_mcp.colors import color_name, parse_color
from power_cad_mcp.config import Settings
from power_cad_mcp.errors import CadError
from power_cad_mcp.geometry import rectangle_points, regular_polygon_points, to_point, to_points
from power_cad_mcp.safety import check_command


@pytest.mark.parametrize(
    ("value", "expected"),
    [(None, None), (1, 1), ("red", 1), ("ByLayer", 256), ("by_block", 0), ("  7 ", 7), ("light gray", 9)],
)
def test_parse_color(value, expected):
    assert parse_color(value) == expected


@pytest.mark.parametrize("value", [-1, 257, "chartreuse", True])
def test_parse_color_rejects(value):
    with pytest.raises(CadError):
        parse_color(value)


def test_color_name():
    assert color_name(1) == "red" and color_name(256) == "bylayer" and color_name(42) == "42"


def test_points():
    assert to_point([1, 2]) == (1.0, 2.0, 0.0)
    assert to_point((1, 2, 3)) == (1.0, 2.0, 3.0)
    for bad in ([1], [1, 2, 3, 4], ["a", 1], [float("nan"), 0]):
        with pytest.raises(CadError):
            to_point(bad)
    with pytest.raises(CadError, match="at least 2"):
        to_points([[0, 0]])
    with pytest.raises(CadError):
        rectangle_points((0, 0, 0), (0, 1, 0))
    square = regular_polygon_points((0, 0, 0), 1, 4, 45)
    assert len(square) == 4 and all(abs(abs(x) - 2**-0.5) < 1e-9 for x, _, _ in square)


@pytest.mark.parametrize(
    "cmd",
    ["LINE 0,0 10,10 ", "_.CIRCLE 0,0 5", "-LAYER _S Walls ", "_.ZOOM _E", "TEXT 0,0 2.5 0 Hello"],
)
def test_allowed_commands(cmd):
    assert check_command(cmd, allow_commands=True, allow_lisp=False) == cmd


@pytest.mark.parametrize(
    "cmd",
    [
        "SHELL",
        "_.SHELL dir",
        "'_sh calc",
        "LINE 0,0 1,1  START notepad",
        "NETLOAD",
        "_.APPLOAD",
        "!foo",
        "_.QUIT",
    ],
)
def test_blocked_commands(cmd):
    with pytest.raises(CadError):
        check_command(cmd, allow_commands=True, allow_lisp=True)


def test_lisp_rules():
    with pytest.raises(CadError, match="AutoLISP"):
        check_command("(setq a 1)", allow_commands=True, allow_lisp=False)
    assert check_command("(setq a 1)", allow_commands=True, allow_lisp=True)
    with pytest.raises(CadError, match="outside"):
        check_command('(startapp "cmd.exe")', allow_commands=True, allow_lisp=True)
    # The standard command trampoline must not reach blocked commands (Codex review finding).
    for trampoline in ('(command "_.SHELL" "calc")', '(command "sh")', '(eval (read "(startapp)"))'):
        with pytest.raises(CadError):
            check_command(trampoline, allow_commands=True, allow_lisp=True)
    assert check_command('(command "_.LINE" "0,0" "1,1" "")', allow_commands=True, allow_lisp=True)
    with pytest.raises(CadError, match="disabled"):
        check_command("LINE", allow_commands=False, allow_lisp=True)
    with pytest.raises(CadError, match="empty"):
        check_command("  \n", allow_commands=True, allow_lisp=False)


def test_settings_from_env(monkeypatch):
    monkeypatch.setenv("POWER_CAD_BACKEND", "DXF")
    monkeypatch.setenv("POWER_CAD_PROGID", "AutoCAD.Application.26, AutoCAD.Application")
    monkeypatch.setenv("POWER_CAD_ALLOW_COMMANDS", "0")
    monkeypatch.setenv("POWER_CAD_LAUNCH", "yes")
    s = Settings.from_env()
    assert s.backend == "dxf" and s.progids == ["AutoCAD.Application.26", "AutoCAD.Application"]
    assert s.allow_commands is False and s.launch is True and s.allow_lisp is False


def test_create_backend_selection(monkeypatch):
    from power_cad_mcp.backends import create_backend
    from power_cad_mcp.backends.dxf_backend import DxfBackend

    assert isinstance(create_backend(Settings(backend="dxf")), DxfBackend)
    if sys.platform != "win32":
        assert isinstance(create_backend(Settings(backend="auto")), DxfBackend)
    with pytest.raises(CadError):
        create_backend(Settings(backend="solidworks"))


def test_com_backend_off_windows_explains_itself():
    if sys.platform == "win32":
        pytest.skip("pywin32 is available on Windows")
    from power_cad_mcp.backends.com_backend import ComBackend

    backend = ComBackend()
    try:
        status = backend.status()
        assert status["connected"] is False and "pywin32" in status["error"]
    finally:
        backend.close()


def test_cli_check_and_version():
    out = subprocess.run(
        [sys.executable, "-m", "power_cad_mcp", "--backend", "dxf", "--check"], capture_output=True, text=True
    )
    assert out.returncode == 0, out.stderr
    assert json.loads(out.stdout)["backend"] == "dxf"
    ver = subprocess.run([sys.executable, "-m", "power_cad_mcp", "--version"], capture_output=True, text=True)
    assert ver.stdout.strip().startswith("power-cad-mcp 0.")


@pytest.mark.anyio
async def test_stdio_server_process(tmp_path):
    """Launch the real console entry point over stdio, exactly as Claude Desktop would."""
    from mcp import Client, StdioServerParameters

    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "power_cad_mcp", "--backend", "dxf", "--workspace", str(tmp_path)],
    )
    async with Client(params) as client:
        tools = (await client.list_tools()).tools
        assert len(tools) >= 40
        res = await client.call_tool("draw_circle", {"center": [0, 0], "radius": 3})
        assert not res.is_error
        res = await client.call_tool("save_drawing", {"path": "stdio.dxf"})
        assert not res.is_error
    assert (tmp_path / "stdio.dxf").exists()


@pytest.fixture
def anyio_backend():
    return "asyncio"
