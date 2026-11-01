"""AutoCAD-MCP Server 入口（基于 Official MCP SDK 低层 Server API）。

注册全部 MCP 工具：
- CAD 工具（68 个）：转发到 C# 插件桥（autocad_client）
- 文件工具（11 个）：本地实现（tools_file）
- 辅助工具（3 个）：ping / cad_bridge_health / get_drawing_info

与 FastMCP 版本的差异：
- 使用 `mcp.server.Server` 的 list_tools / call_tool 处理器，
  工具定义与分发由 registry.ToolRegistry 承担；
- HTTP / SSE 传输自行组装（StreamableHTTPSessionManager /
  SseServerTransport + Starlette + uvicorn），不再依赖 FastMCP 设置对象。
"""

import contextlib
import os
import secrets
from collections.abc import AsyncIterator

from mcp import types
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager
from mcp.server.transport_security import TransportSecuritySettings
from starlette.applications import Starlette
from starlette.datastructures import Headers
from starlette.responses import Response
from starlette.routing import Mount, Route
from starlette.types import Receive, Scope, Send

from . import autocad_client as bridge
from . import (
    tools_draw,
    tools_edit,
    tools_file,
    tools_layer_block_geo,
    tools_query,
)
from .registry import ToolRegistry

# 服务器级说明：Dify / Inspector 导入工具时会一并读取，帮助模型正确选用工具。
INSTRUCTIONS = """AutoCAD MCP Server：通过 AutoCAD 内运行的 C# 插件执行绘图、编辑、查询与文件操作。
使用前先调用 cad_bridge_health 确认桥在线；坐标使用 [x, y]（或 [x, y, z]），角度单位为度。
绘图类工具均为单一模式，参数即所需字段：
- line：start + end（两点）；
- circle：center + radius（圆心半径）；
- arc：center + radius + start_angle + end_angle（圆心角度）；
- rectangle：p1 + p2（对角点）；
- ellipse：center + major_radius + minor_radius（半轴）。
详见各工具描述。
"""

registry = ToolRegistry()

# 注册 CAD 工具（转发到 C# 桥）与本地文件工具
tools_draw.register(registry)
tools_edit.register(registry)
tools_query.register(registry)
tools_layer_block_geo.register(registry)
tools_file.register(registry)


@registry.tool()
def ping() -> dict:
    """MCP Server 存活检查。"""
    return {"success": True, "message": "pong"}


@registry.tool()
def cad_bridge_health() -> dict:
    """检查 AutoCAD 内 C# 桥是否在线。"""
    return bridge.health()


@registry.tool()
def get_drawing_info() -> dict:
    """获取当前 AutoCAD 图纸信息（get_dwg_info 的快捷方式）。"""
    return bridge.execute("get_dwg_info", {})


server = Server("AutoCAD-MCP", instructions=INSTRUCTIONS)


@server.list_tools()
async def _handle_list_tools(
    req: types.ListToolsRequest,
) -> list[types.Tool]:
    return await registry.list_tools(req)


@server.call_tool(validate_input=False)
async def _handle_call_tool(
    name: str, arguments: dict
) -> list[types.ContentBlock]:
    return await registry.call_tool(name, arguments)


class _BearerAuthMiddleware:
    """ASGI 中间件：校验 Authorization: Bearer <token>。

    用于 Dify「自定义请求头」中的静态令牌鉴权，例如：
        Authorization: Bearer <AUTOCAD_MCP_AUTH_TOKEN>
    未设置令牌时不安装本中间件，保持匿名可访问。
    """

    def __init__(self, app, token: str) -> None:
        self.app = app
        self.expected = f"Bearer {token}"

    async def __call__(self, scope, receive, send) -> None:
        if scope["type"] == "http":
            auth = Headers(scope=scope).get("authorization", "")

            if not secrets.compare_digest(auth, self.expected):
                response = Response(
                    "Unauthorized",
                    status_code=401,
                    headers={"WWW-Authenticate": 'Bearer realm="autocad-mcp"'},
                )
                await response(scope, receive, send)
                return

        await self.app(scope, receive, send)


def _security_settings() -> TransportSecuritySettings | None:
    """HTTP/SSE 传输下客户端（Dify、Inspector、反向代理）可能携带
    非本机 Origin/Host，SDK 的 DNS 重绑定防护会分别返回：
      403 Invalid Origin header / 421 Invalid Host header
    本服务以监听地址作为访问边界，故默认关闭该防护；
    如需严格校验，设置环境变量 AUTOCAD_MCP_DNS_PROTECTION=1（仅放行本机）。
    """
    if os.environ.get("AUTOCAD_MCP_DNS_PROTECTION") == "1":
        return TransportSecuritySettings(
            enable_dns_rebinding_protection=True,
            allowed_hosts=["127.0.0.1:*", "localhost:*", "[::1]:*"],
            allowed_origins=[
                "http://127.0.0.1:*",
                "http://localhost:*",
                "http://[::1]:*",
            ],
        )

    return TransportSecuritySettings(enable_dns_rebinding_protection=False)


class _MCPASGIApp:
    """将 StreamableHTTP 会话管理器包装为 ASGI 应用。

    Starlette 只对类形式的 endpoint 放开全部 HTTP 方法
    （函数形式会被限制为 GET/POST，导致 DELETE 返回 405）。
    """

    def __init__(self, handle_request) -> None:
        self.handle_request = handle_request

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        await self.handle_request(scope, receive, send)


def build_streamable_app(stateless: bool, json_response: bool):
    """构建 Streamable HTTP 的 ASGI 应用（端点 /mcp）。"""
    session_manager = StreamableHTTPSessionManager(
        app=server,
        stateless=stateless,
        json_response=json_response,
        security_settings=_security_settings(),
    )

    @contextlib.asynccontextmanager
    async def lifespan(_: Starlette) -> AsyncIterator[None]:
        async with session_manager.run():
            yield

    return Starlette(
        lifespan=lifespan,
        routes=[Route("/mcp", endpoint=_MCPASGIApp(session_manager.handle_request))],
    )


def build_sse_app():
    """构建 SSE 传输的 ASGI 应用（端点 /sse + /messages/）。"""
    from mcp.server.sse import SseServerTransport

    sse = SseServerTransport(
        "/messages/",
        security_settings=_security_settings(),
    )

    async def handle_sse(scope: Scope, receive: Receive, send: Send) -> None:
        async with sse.connect_sse(scope, receive, send) as streams:
            await server.run(
                streams[0],
                streams[1],
                server.create_initialization_options(),
            )

    async def sse_endpoint(request) -> Response:
        await handle_sse(request.scope, request.receive, request._send)  # noqa: SLF001
        return Response()

    return Starlette(
        routes=[
            Route("/sse", endpoint=sse_endpoint, methods=["GET"]),
            Mount("/messages/", app=sse.handle_post_message),
        ],
    )


def _normalize_transport(value: str) -> str:
    """把 CLI / 环境变量的传输名归一化为内部标识。"""
    value = value.lower()

    if value in ("http", "streamable-http", "streamable_http"):
        return "streamable-http"
    if value in ("sse", "stdio"):
        return value

    raise SystemExit(f"未知传输模式：{value}（支持 stdio / sse / http）")


def main() -> None:
    """启动 MCP 服务器。

    传输方式（Dify 原生 MCP 仅支持 HTTP）：
        uv run mcp-server --transport http --host 0.0.0.0 --port 8000
        端点：http://<host>:8000/mcp（Dify 中填写，末尾不要加斜杠）

    兼容选项（Dify 导入/调用异常时尝试）：
        --stateless       无会话模式，每次请求独立处理
        --json-response   以 application/json 而非 SSE 流返回
        --auth-token     启用静态 Bearer 鉴权（配合 Dify 自定义请求头）

    对应环境变量：AUTOCAD_MCP_TRANSPORT / AUTOCAD_MCP_HOST /
    AUTOCAD_MCP_PORT / AUTOCAD_MCP_AUTH_TOKEN /
    AUTOCAD_MCP_STATELESS / AUTOCAD_MCP_JSON_RESPONSE。
    """
    import sys

    args = sys.argv[1:]
    transport = _normalize_transport(
        os.environ.get("AUTOCAD_MCP_TRANSPORT", "stdio")
    )
    host: str = os.environ.get("AUTOCAD_MCP_HOST") or "127.0.0.1"
    port: int = int(os.environ.get("AUTOCAD_MCP_PORT") or 8000)
    auth_token: str = os.environ.get("AUTOCAD_MCP_AUTH_TOKEN", "")
    stateless = os.environ.get("AUTOCAD_MCP_STATELESS") == "1"
    json_response = os.environ.get("AUTOCAD_MCP_JSON_RESPONSE") == "1"

    i = 0
    while i < len(args):
        arg = args[i]

        if arg == "--transport" and i + 1 < len(args):
            transport = _normalize_transport(args[i + 1])
            i += 2
        elif arg == "--host" and i + 1 < len(args):
            host = args[i + 1]
            i += 2
        elif arg == "--port" and i + 1 < len(args):
            port = int(args[i + 1])
            i += 2
        elif arg == "--auth-token" and i + 1 < len(args):
            auth_token = args[i + 1]
            i += 2
        elif arg == "--stateless":
            stateless = True
            i += 1
        elif arg == "--json-response":
            json_response = True
            i += 1
        else:
            i += 1

    if transport == "stdio":
        # stdio 模式下 stdout 专用作 JSON-RPC 通道，日志只能走 stderr
        print(
            f"[AutoCAD-MCP] stdio 模式启动中，已注册 {registry.count} 个工具",
            file=sys.stderr,
            flush=True,
        )
        import anyio

        anyio.run(run_stdio)
        return

    if transport == "streamable-http":
        app = build_streamable_app(stateless, json_response)
        path = "/mcp"
    else:
        app = build_sse_app()
        path = "/sse"

    if auth_token:
        app = _BearerAuthMiddleware(app, auth_token)

    print(
        "[AutoCAD-MCP] "
        f"{transport} 模式启动中，端点：http://{host}:{port}{path}",
        flush=True,
    )
    print(
        "[AutoCAD-MCP] "
        f"鉴权：{'Bearer Token' if auth_token else '无（匿名）'}；"
        f"stateless={stateless}；json_response={json_response}",
        flush=True,
    )

    import uvicorn

    uvicorn.run(app, host=host, port=port)


async def run_stdio() -> None:
    """stdio 传输：标准输入输出上的 JSON-RPC 消息循环。"""
    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream,
            write_stream,
            server.create_initialization_options(),
        )
