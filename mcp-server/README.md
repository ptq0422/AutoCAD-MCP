# AutoCAD-MCP Server

基于 [Official MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)
（`mcp.server.Server` 低层 API）的 MCP（Model Context Protocol）服务器，
向 AI 助手暴露 AutoCAD 绘图能力。

## 架构

```
AI 助手 (MCP Client)
    │  MCP 协议 (stdio)
    ▼
mcp-server (Python / Official MCP SDK)
    │  HTTP POST /execute {"tool": ..., "params": ...}
    ▼
autocad-plugin (C# / BridgeServer, 127.0.0.1:8765)
    │  SendStringToExecute("MCPEXECUTE")
    ▼
AutoCAD 2026 (实际执行绘图/编辑/查询)
```

- **mcp-server**：定义 MCP 工具、校验参数并转发到 C# 桥；文件类工具在本地直接实现。
  工具通过 `registry.ToolRegistry` 注册（inputSchema 由函数签名自动推导），
  再挂载到官方 SDK 的 `Server` 上。
- **autocad-plugin**：在 AutoCAD 进程内运行 HTTP 桥，把请求切换到命令上下文执行。

## 安装与运行

### stdio 模式（MCP Client 接入）

```powershell
cd mcp-server
uv run mcp-server                  # 等待 MCP Client 连接
```

### HTTP 模式（浏览器可访问，支持在线 MCP Inspector 测试）

```powershell
cd mcp-server
uv run mcp-server --transport http           # 默认 http://127.0.0.1:8000
uv run mcp-server --transport http --port 9000 --host 0.0.0.0
```

启动后访问 `http://127.0.0.1:8000/mcp` 即为 MCP Streamable HTTP 端点。
浏览器测试方式：打开官方在线 [MCP Inspector](https://inspector.modelcontextprotocol.io)，
填入端点 `http://127.0.0.1:8000/mcp` 即可列出工具并调用；
或本地以 stdio 方式拉起 Inspector：

```powershell
npx @modelcontextprotocol/inspector uv run mcp-server
```

> 官方 SDK 没有 FastMCP 的 `mcp dev` 集成命令，请用上面的 Inspector 客户端连接。

支持的传输模式：`stdio`（默认）/ `sse` / `http`（即 streamable-http）。

### 在 Dify 中接入（原生 MCP）

Dify 仅支持 **HTTP 传输**的 MCP 服务器，按「URL + 名称 + 唯一标识符」添加：

1. 启动服务（必须与 AutoCAD 同机运行）：
   ```powershell
   cd mcp-server
   uv run mcp-server --transport http --host 0.0.0.0 --port 8000
   ```
2. Dify → 工具 → MCP → 添加 MCP 服务器：
   - **URL**：`http://<AutoCAD机器IP>:8000/mcp`（**末尾不要加斜杠**，否则 307 空 Content-Type 会报 `Unsupported Content-Type: None`）
   - **名称**：`AutoCAD`；**标识符**：`autocad`（唯一，应用通过标识符引用，导出后需保持一致）
   - **动态客户端注册（DCR）**：关闭。本服务不提供 OAuth，匿名即可连接；用静态令牌时也在关闭 DCR 后用自定义请求头传凭据。
   - **自定义请求头**：仅在设置了 `AUTOCAD_MCP_AUTH_TOKEN` 时需要，填 `Authorization: Bearer <token>`
   - **超时**：请求超时 ≥ 130s，SSE 读取超时 ≥ 300s（导出等长任务）
3. 若客户端不支持会话或 SSE 流，可加 `--stateless` / `--json-response` 启动再试。

在运行前，需要先在 AutoCAD 中加载插件并启动桥：

1. 编译插件：`dotnet build autocad-plugin\AutoCADMcp.Plugin`
2. AutoCAD 中执行 `NETLOAD` 加载 `AutoCADMcp.Plugin.dll`
3. 执行命令 `MCPBRIDGESTART` 启动 HTTP 桥（默认 `http://127.0.0.1:8765`）

桥的状态可通过 MCP 工具 `ping` / `cad_bridge_health` 检查。

## 环境变量

| 变量 | 说明 | 默认值 |
| --- | --- | --- |
| `AUTOCAD_MCP_OUTPUT_DIR` | 文件类工具的输出目录 | `~/Documents/AutoCAD-MCP` |
| `AUTOCAD_MCP_BRIDGE_URL` | C# 桥地址 | `http://127.0.0.1:8765` |
| `AUTOCAD_MCP_BRIDGE_TIMEOUT` | 等待 C# 桥响应的超时（秒） | `130` |
| `AUTOCAD_MCP_TRANSPORT` | 传输模式：`stdio` / `sse` / `http` | `stdio` |
| `AUTOCAD_MCP_HOST` | HTTP 监听地址 | `127.0.0.1` |
| `AUTOCAD_MCP_PORT` | HTTP 监听端口 | `8000` |
| `AUTOCAD_MCP_AUTH_TOKEN` | 静态 Bearer 令牌（配合 Dify 自定义请求头） | 空（匿名） |
| `AUTOCAD_MCP_STATELESS` | 设为 `1` 启用无会话模式 | 关闭 |
| `AUTOCAD_MCP_JSON_RESPONSE` | 设为 `1` 用 JSON 响应替代 SSE 流 | 关闭 |
| `AUTOCAD_MCP_DNS_PROTECTION` | 设为 `1` 仅允许本机 Host/Origin | 关闭 |

## 工具总览（82 个）

### 绘图与标注（17）
`line` `circle` `arc` `rectangle` `polyline` `ellipse` `spline` `polygon` `point`
`draw_text` `dimension` `leader` `hatch`
`set_layer` `layer_manage` `dwg_op`

### 编辑（18）
`transform` `delete` `offset` `trim` `extend` `fillet` `chamfer` `break`
`stretch` `align` `change_properties` `erase_by_filter` `undo` `explode`
`join_entity` `array` `array_rect` `array_polar`

### 块与组（5）
`insert_block` `block_query` `block_attr` `block_replace` `group_op`

### 查询（19）
`get_drawing_summary` `get_drawing_fingerprint` `query_entities` `get_entity`
`entity_info` `entity_count` `find_text` `get_bounds` `sysvar`
`query_relations` `trace_boundary` `verify_result` `compare_drawings`
`list_styles` `get_selection` `get_dwg_info` `read_commandline`
`layer_query` `list_all`

### 几何与空间感知（10）
`geo_basic` `geo_query` `calc_area` `divide_curve`
`connected` `inside` `aligned` `detect_clash` `find_vacant` `detect_islands`

### 文件与数据（11，本地实现）
`write_file` `read_file` `append_file` `delete_file` `replace_in_file`
`list_files` `list_outputs` `create_table` `export_csv` `save_library` `param_set`

### 辅助（3）
`ping` `cad_bridge_health` `get_drawing_info`

## 参数约定（CAD 工具）

- **点**：`[x, y]` 或 `[x, y, z]` 数组，或 `"x,y"` 字符串
- **选择集**：`handles` 数组，或顶层直接给 `mode`（all/handles/last/window/crossing/filter）
  与过滤条件（`type`/`layer`/`color`/`block`）
- **空间**：`space` = `current` / `model` / `paper` / `all`
- **响应**：统一 `{"success": bool, ...payload}`；失败时 `{"success": false, "error": "..."}`
