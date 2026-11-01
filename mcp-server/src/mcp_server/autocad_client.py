"""AutoCAD C# 桥接客户端。

与 AutoCAD 内运行的 C# 插件（BridgeServer）通信：
- GET  /health   健康检查
- GET  /tools    列出 C# 端已注册的工具
- POST /execute  执行工具：{"tool": "...", "params": {...}}

C# 端最长等待 120 秒（导出等异步任务可达 120+ 秒），
因此这里的 HTTP 超时略大于该值。
"""

from __future__ import annotations

import json
import os
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

BRIDGE_URL = os.environ.get(
    "AUTOCAD_MCP_BRIDGE_URL", "http://127.0.0.1:8765"
)

# C# 桥单次请求最长等待 120 秒，默认留 10 秒余量；
# Dify 侧的“请求超时/SSE 读取超时”需大于该值。
HTTP_TIMEOUT = float(os.environ.get("AUTOCAD_MCP_BRIDGE_TIMEOUT", "130"))


def _post(path: str, payload: dict) -> dict:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    request = Request(
        f"{BRIDGE_URL}{path}",
        data=data,
        method="POST",
        headers={"Content-Type": "application/json"},
    )

    try:
        with urlopen(request, timeout=HTTP_TIMEOUT) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        try:
            detail = exc.read().decode("utf-8")
        except Exception:
            detail = str(exc)

        return {
            "success": False,
            "error": f"HTTP {exc.code}: {detail}",
        }
    except (URLError, TimeoutError, OSError) as exc:
        return {
            "success": False,
            "error": (
                "无法连接 AutoCAD Bridge。请确认："
                "1) AutoCAD 正在运行；"
                "2) 插件已加载（NETLOAD）；"
                "3) 已执行命令 MCPBRIDGESTART。"
                f"详情：{exc}"
            ),
        }
    except Exception as exc:  # pragma: no cover - 兜底
        return {
            "success": False,
            "error": f"请求处理失败：{exc}",
        }


def _clean(value):
    """递归移除 None，避免把 null 传给 C# 端参数解析。"""
    if isinstance(value, dict):
        return {
            k: _clean(v)
            for k, v in value.items()
            if v is not None
        }

    if isinstance(value, list):
        return [_clean(v) for v in value if v is not None]

    return value


def execute(tool: str, params: dict | None = None) -> dict:
    """调用 C# 桥接执行 CAD 工具。

    C# 端响应统一为 {"success": bool, ...}，
    成功时附加工具返回的 payload 字段。
    """
    return _post(
        "/execute",
        {"tool": tool, "params": _clean(params or {})},
    )


def health() -> dict:
    """检查 C# 桥是否正在运行。"""
    request = Request(f"{BRIDGE_URL}/health", method="GET")

    try:
        with urlopen(request, timeout=5) as response:
            return json.loads(response.read().decode("utf-8"))
    except Exception as exc:
        return {
            "success": False,
            "error": (
                "无法连接 AutoCAD Bridge。请确认："
                "1) AutoCAD 正在运行；"
                "2) 插件已加载（NETLOAD）；"
                "3) 已执行命令 MCPBRIDGESTART。"
                f"详情：{exc}"
            ),
        }


def list_bridge_tools() -> dict:
    """列出 C# 端已注册的工具名。"""
    request = Request(f"{BRIDGE_URL}/tools", method="GET")

    try:
        with urlopen(request, timeout=5) as response:
            return json.loads(response.read().decode("utf-8"))
    except Exception as exc:
        return {
            "success": False,
            "error": f"无法获取工具列表：{exc}",
        }
