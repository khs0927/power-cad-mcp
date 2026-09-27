from __future__ import annotations

import json
import logging
from typing import Any

import pytest
from mcp import Client

from power_cad_mcp.backends.dxf_backend import DxfBackend
from power_cad_mcp.config import Settings
from power_cad_mcp.server import create_server

logging.getLogger("ezdxf").setLevel(logging.WARNING)


class ToolCaller:
    """Calls tools through a real in-memory MCP client/server session."""

    def __init__(self, client: Client):
        self.client = client

    async def raw(self, name: str, /, **args: Any):
        return await self.client.call_tool(name, args)

    async def __call__(self, name: str, /, **args: Any) -> Any:
        result = await self.raw(name, **args)
        if result.is_error:
            raise AssertionError(f"{name} failed: {result.content[0].text}")
        sc = result.structured_content
        if sc is not None:
            return sc.get("result", sc) if set(sc) == {"result"} else sc
        return json.loads(result.content[0].text)

    async def error(self, name: str, /, **args: Any) -> str:
        result = await self.raw(name, **args)
        assert result.is_error, f"{name} unexpectedly succeeded: {result}"
        return result.content[0].text


@pytest.fixture
def anyio_backend() -> str:
    return "asyncio"


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(backend="dxf", workspace=str(tmp_path))


@pytest.fixture
def backend() -> DxfBackend:
    return DxfBackend()


@pytest.fixture
async def call(backend, settings):
    server = create_server(backend, settings)
    async with Client(server) as client:
        yield ToolCaller(client)
