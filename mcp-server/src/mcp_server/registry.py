"""工具注册中心（基于 Official MCP SDK 低层 Server API）。

低层 `mcp.server.Server` 只暴露 list_tools / call_tool 两个聚合处理器，
不像 FastMCP 提供 `@mcp.tool()` 装饰器。本模块补齐这部分能力：

- 从函数签名 + 类型注解 + pydantic Field 元数据自动推导 inputSchema；
- 用 pydantic 对入参做校验与类型转换（容忍客户端传字符串数字等）；
- 同步工具函数经 `anyio.to_thread` 放入工作线程执行，
  避免长耗时桥接请求（最长 130 秒）阻塞事件循环；
- 结果统一序列化为 JSON 文本内容返回。

各 tools_* 模块通过 `registry.tool()(func)` 注册工具，
__init__ 再把注册中心接入低层 Server 的两个处理器。
"""

from __future__ import annotations

import inspect
import json
import logging
import sys
import time
from collections.abc import Callable
from typing import Any, get_type_hints

import anyio
from mcp import types
from mcp.server.fastmcp.exceptions import ToolError
from pydantic import BaseModel, ConfigDict, ValidationError, create_model
from pydantic.fields import FieldInfo

# ---------------------------------------------------------------------------
# 日志配置：输出到 stderr，不干扰 stdio 模式的 JSON-RPC 通信
# ---------------------------------------------------------------------------
logger = logging.getLogger("mcp_server")
if not logger.handlers:
    _handler = logging.StreamHandler(sys.stderr)
    _handler.setFormatter(
        logging.Formatter("[%(asctime)s] %(levelname)s %(message)s", datefmt="%H:%M:%S")
    )
    logger.addHandler(_handler)
    logger.setLevel(logging.INFO)
    logger.propagate = False


def _truncate(text: str, limit: int = 200) -> str:
    """截断过长文本用于日志展示。"""
    if len(text) <= limit:
        return text
    return text[:limit] + f"...（共 {len(text)} 字符）"


def _module_globals(func: Callable) -> dict:
    """工具模块使用了字符串化注解，需按其所属模块的命名空间解析。"""
    module = inspect.getmodule(func)
    return getattr(module, "__dict__", {})


def _arg_model(name: str, func: Callable) -> type[BaseModel]:
    """由函数签名构建 pydantic 参数模型。"""
    # include_extras=True 保留 Annotated[...] 中的 Field 元数据（description 等）
    hints = get_type_hints(func, globalns=_module_globals(func), include_extras=True)
    sig = inspect.signature(func)
    fields: dict[str, Any] = {}

    for pname, param in sig.parameters.items():
        if param.kind in (param.VAR_POSITIONAL, param.VAR_KEYWORD):
            continue

        annotation = hints.get(pname, Any)

        # Annotated[...] 中的 Field(...) 已被 pydantic 从类型元数据中提取
        # description/约束等，只需把函数签名的默认值作为第二元素传入。
        metadata = getattr(annotation, "__metadata__", ())
        field_info = next(
            (m for m in metadata if isinstance(m, FieldInfo)), None
        )

        if field_info is not None:
            # Annotated + Field: description 已在 annotation 内，
            # 第二元素传纯默认值（或 ... 表示必填）
            if param.default is not inspect.Parameter.empty:
                fields[pname] = (annotation, param.default)
            else:
                fields[pname] = (annotation, ...)
        elif param.default is not inspect.Parameter.empty:
            fields[pname] = (annotation, param.default)
        else:
            fields[pname] = (annotation, ...)

    model = create_model(f"{name}Args", **fields)  # type: ignore[call-overload]
    model.model_config = ConfigDict(extra="forbid")
    return model


def _input_schema(model: type[BaseModel]) -> dict[str, Any]:
    schema = model.model_json_schema()
    schema.pop("$defs", None)
    return schema


class ToolRegistry:
    """工具集合，供低层 Server 的 list_tools / call_tool 使用。"""

    def __init__(self) -> None:
        self._tools: dict[str, types.Tool] = {}
        self._funcs: dict[str, Callable] = {}
        self._models: dict[str, type[BaseModel]] = {}

    def tool(self, *, name: str | None = None, description: str | None = None):
        """装饰器：注册被包装函数为一个 MCP 工具。

        工具名默认取函数名；描述默认取完整 docstring（折叠换行）；
        inputSchema 由函数签名自动推导。
        """

        def decorator(func: Callable) -> Callable:
            tool_name = name or func.__name__

            if tool_name in self._tools:
                raise ValueError(f"工具重复注册：{tool_name}")

            model = _arg_model(tool_name, func)
            text = description or inspect.getdoc(func) or tool_name
            # 保留完整 docstring（含互斥模式说明），折叠换行以满足 MCP 描述
            text = " ".join(text.split())

            self._tools[tool_name] = types.Tool(
                name=tool_name,
                description=text,
                inputSchema=_input_schema(model),
            )
            self._funcs[tool_name] = func
            self._models[tool_name] = model
            return func

        return decorator

    @property
    def count(self) -> int:
        """已注册工具数。"""
        return len(self._tools)

    async def list_tools(self, _: types.ListToolsRequest) -> list[types.Tool]:
        """ListToolsRequest 处理器：返回全部工具定义。"""
        return list(self._tools.values())

    async def call_tool(
        self, name: str, arguments: dict[str, Any]
    ) -> list[types.ContentBlock]:
        """CallToolRequest 处理器：校验参数并执行工具。"""
        func = self._funcs.get(name)
        if func is None:
            logger.error("未知工具：%s", name)
            raise ToolError(f"未知工具：{name}")

        model = self._models[name]

        # 记录调用：工具名 + 参数摘要
        args_str = json.dumps(arguments, ensure_ascii=False, default=str)
        logger.info("→ %s(%s)", name, _truncate(args_str))

        # 参数校验
        try:
            params = model.model_validate(arguments or {})
        except ValidationError as exc:
            first = exc.errors()[0]
            err_msg = f"参数校验失败：{first.get('loc')}: {first['msg']}"
            logger.warning("✗ %s 参数校验失败：%s", name, err_msg)
            raise ToolError(err_msg) from exc

        kwargs = params.model_dump()

        # 执行并计时
        t0 = time.perf_counter()
        try:
            result = await anyio.to_thread.run_sync(_bind(func, kwargs))
        except Exception as exc:
            elapsed = time.perf_counter() - t0
            logger.error("✗ %s 执行异常（%.2fs）：%s", name, elapsed, exc)
            raise

        elapsed = time.perf_counter() - t0

        # 判断桥端返回是否业务失败
        if isinstance(result, dict) and result.get("success") is False:
            err = result.get("error", "未知错误")
            logger.warning("✗ %s 桥返回失败（%.2fs）：%s", name, elapsed, _truncate(str(err)))
        else:
            result_str = json.dumps(result, ensure_ascii=False, default=str)
            logger.info("✓ %s 完成（%.2fs）→ %s", name, elapsed, _truncate(result_str, 120))

        text = json.dumps(result, ensure_ascii=False, indent=2, default=str)
        return [types.TextContent(type="text", text=text)]


def _bind(func: Callable, kwargs: dict[str, Any]) -> Callable[[], Any]:
    """把同步函数与关键字参数绑定，供工作线程内执行。"""

    def run() -> Any:
        return func(**kwargs)

    return run
