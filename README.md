# AutoCAD-MCP

通过 **MCP（Model Context Protocol）** 让 AI 助手直接操作 **AutoCAD 2026** 进行绘图、编辑、查询与文件操作。

项目由两部分组成：

| 组件 | 目录 | 语言/框架 | 作用 |
| --- | --- | --- | --- |
| **AutoCAD 插件（桥）** | `autocad-plugin/` | C# / .NET 8 + AutoCAD .NET API | 在 AutoCAD 进程内运行 HTTP 桥，真正执行绘图/编辑/查询 |
| **MCP 服务器** | `mcp-server/` | Python 3.12 + [Official MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk) | 向 AI 客户端暴露 MCP 工具，校验参数并转发到 C# 桥 |

## 架构

```
AI 助手 (MCP Client)
    │  MCP 协议 (stdio / sse / http)
    ▼
mcp-server (Python / Official MCP SDK)
    │  HTTP POST /execute  {"tool": ..., "params": ...}
    ▼
autocad-plugin (C# / BridgeServer, 127.0.0.1:8765)
    │  SendStringToExecute("MCPEXECUTE")
    ▼
AutoCAD 2026 (实际执行绘图/编辑/查询)
```

- **mcp-server** 定义 MCP 工具，文件类工具在 Python 本地实现，CAD 类工具转发到 C# 桥。
- **autocad-plugin** 在 AutoCAD 内开启 HTTP 监听，收到请求后切换到命令上下文执行。

## 环境要求

- **AutoCAD 2026**（x64，Windows）
- **.NET 8 SDK**（编译 C# 插件）
- **Python ≥ 3.12** 与 **[uv](https://docs.astral.sh/uv/)**（运行 MCP 服务器）
- MCP 服务器需与 AutoCAD **同一台机器**运行（桥默认监听 `127.0.0.1:8765`）

---

## 一、编译并使用 C# 插件

### 1. 确认 AutoCAD 引用路径

`autocad-plugin/AutoCADMcp.Plugin/AutoCADMcp.Plugin.csproj` 引用了 `AcMgd.dll`、`AcDbMgd.dll`、`AcCoreMgd.dll`，其 `HintPath` 指向本机 AutoCAD 2026 安装目录。**请把它改成你实际的安装路径**，例如：

```xml
<Reference Include="AcMgd">
  <HintPath>C:\Program Files\Autodesk\AutoCAD 2026\AcMgd.dll</HintPath>
  <Private>false</Private>
</Reference>
```

### 2. 编译

```powershell
# 在项目根目录执行
dotnet build autocad-plugin\AutoCADMcp.Plugin -c Debug
```

产物路径：

```
autocad-plugin\AutoCADMcp.Plugin\bin\Debug\net8.0-windows\AutoCADMcp.Plugin.dll
```

> 若编译报「文件被占用」，通常是因为 AutoCAD 正在运行并已加载旧的 DLL。先关闭 AutoCAD 或在其中执行 `MCPBRIDGESTOP` 再重新编译。

### 3. 在 AutoCAD 中加载插件

1. 启动 AutoCAD 2026。
2. 命令行输入 `NETLOAD`，选择上一步生成的 `AutoCADMcp.Plugin.dll`。
3. 命令行输入 `MCPBRIDGESTART` 启动 HTTP 桥（默认 `http://127.0.0.1:8765`）。

### 4. 插件命令

| 命令 | 说明 |
| --- | --- |
| `MCPBRIDGESTART` | 启动 HTTP 桥，开始监听 `127.0.0.1:8765` |
| `MCPBRIDGESTOP` | 停止 HTTP 桥 |
| `MCPEXECUTE` | 桥内部使用的命令上下文分发入口（一般无需手动调用） |

---

## 二、启动 MCP 服务器

依赖由 `uv` 管理，首次运行前在 `mcp-server/` 目录执行 `uv sync` 安装依赖。所有命令均在 `mcp-server/` 目录下运行。

### stdio 模式（供 MCP Client 接入）

```powershell
cd mcp-server
uv run mcp-server
```

### HTTP 模式（Streamable HTTP，可跨机访问 / 在线测试）

```powershell
cd mcp-server
uv run mcp-server --transport http                      # 默认 http://127.0.0.1:8000/mcp
uv run mcp-server --transport http --port 9000 --host 0.0.0.0
```

支持的传输：`stdio`（默认）/ `sse` / `http`（即 streamable-http）。

### 用 MCP Inspector 测试

```powershell
# 方式一：Inspector 以 stdio 直接拉起本服务
npx @modelcontextprotocol/inspector uv run mcp-server

# 方式二：先起 HTTP 端点，再在 Inspector 中选择 Streamable HTTP 连接
uv run mcp-server --transport http --host 127.0.0.1 --port 8000
# 端点：http://127.0.0.1:8000/mcp
```

> 官方 SDK 没有 FastMCP 的 `mcp dev` 集成命令，请使用上述 Inspector 客户端连接。

### 在 Dify 中接入（原生 MCP）

Dify 仅支持 **HTTP 传输**，按「URL + 名称 + 唯一标识符」添加：

1. 启动服务（与 AutoCAD 同机）：`uv run mcp-server --transport http --host 0.0.0.0 --port 8000`
2. Dify → 工具 → MCP → 添加：
   - **URL**：`http://<AutoCAD机器IP>:8000/mcp`（**末尾不要加斜杠**，否则 307 会导致 `Unsupported Content-Type: None`）
   - **名称**：`AutoCAD`；**标识符**：`autocad`（唯一，导出后需保持一致）
   - **动态客户端注册（DCR）**：关闭（本服务不提供 OAuth，匿名即可连接）
   - **自定义请求头**：仅在设置了 `AUTOCAD_MCP_AUTH_TOKEN` 时需要，填 `Authorization: Bearer <token>`
   - **超时**：请求超时 ≥ 130s，SSE 读取超时 ≥ 300s
3. 若客户端不支持会话或 SSE 流，可加 `--stateless` / `--json-response` 再试。

### 接入前检查

调用 MCP 工具 `cad_bridge_health`（或 `ping`）确认 C# 桥在线；返回失败时请检查插件是否已 `NETLOAD` 并执行了 `MCPBRIDGESTART`。

---

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

命令行参数 `--transport` / `--host` / `--port` / `--auth-token` / `--stateless` / `--json-response` 会覆盖对应环境变量。

---

## 工具总览（82 个）

| 分类 | 数量 | 工具 |
| --- | --- | --- |
| 绘图与标注 | 17 | `line` `circle` `arc` `rectangle` `polyline` `ellipse` `spline` `polygon` `point` `draw_text` `dimension` `leader` `hatch` `set_layer` `layer_manage` `dwg_op` |
| 编辑 | 18 | `transform` `delete` `offset` `trim` `extend` `fillet` `chamfer` `break` `stretch` `align` `change_properties` `erase_by_filter` `undo` `explode` `join_entity` `array` `array_rect` `array_polar` |
| 块与组 | 5 | `insert_block` `block_query` `block_attr` `block_replace` `group_op` |
| 查询 | 19 | `get_drawing_summary` `get_drawing_fingerprint` `query_entities` `get_entity` `entity_info` `entity_count` `find_text` `get_bounds` `sysvar` `query_relations` `trace_boundary` `verify_result` `compare_drawings` `list_styles` `get_selection` `get_dwg_info` `read_commandline` `layer_query` `list_all` |
| 几何与空间 | 10 | `geo_basic` `geo_query` `calc_area` `divide_curve` `connected` `inside` `aligned` `detect_clash` `find_vacant` `detect_islands` |
| 文件与数据（本地实现） | 11 | `write_file` `read_file` `append_file` `delete_file` `replace_in_file` `list_files` `list_outputs` `create_table` `export_csv` `save_library` `param_set` |
| 辅助 | 3 | `ping` `cad_bridge_health` `get_drawing_info` |

### 参数约定（CAD 工具）

- **点**：`[x, y]` 或 `[x, y, z]` 数组，或 `"x,y"` 字符串；角度单位为**度**。
- **选择集**：`handles` 数组，或顶层直接给 `mode`（`all`/`handles`/`last`/`window`/`crossing`/`filter`）与过滤条件（`type`/`layer`/`color`/`block`）。
- **空间**：`space` = `current` / `model` / `paper` / `all`。
- **响应**：统一 `{"success": bool, ...payload}`；失败时 `{"success": false, "error": "..."}`。

---

## 目录结构

```
AutoCAD-MCP/
├─ autocad-plugin/AutoCADMcp.Plugin/   # C# 插件：AutoCAD 内的 HTTP 桥与工具执行
│  ├─ BridgeServer.cs                  # 桥：监听 127.0.0.1:8765，POST /execute
│  ├─ ToolRegistry.cs                  # MCPEXECUTE 命令分发
│  ├─ Helpers.cs                       # 坐标/选择集/几何公共方法
│  ├─ DrawTools.cs / AnnotateTools.cs  # 绘图与标注
│  ├─ EditTools.cs / EditToolsMore.cs  # 编辑
│  ├─ BlockGroupTools.cs               # 块与组
│  ├─ QueryTools.cs                    # 查询
│  ├─ GeoSpatialTools.cs               # 几何与空间
│  ├─ LayerDwgTools.cs                 # 图层与图纸文件
│  └─ AutoCADMcp.Plugin.csproj         # net8.0-windows，引用 AutoCAD 2026
├─ mcp-server/                         # Python MCP 服务器（Official MCP SDK）
│  ├─ src/mcp_server/                  # 入口、registry、autocad_client 及各 tools_*.py
│  ├─ server.py                        # 供 Inspector 以 stdio 拉起的调试入口
│  ├─ README.md                        # 服务器详细说明
│  └─ pyproject.toml / uv.lock
└─ doc/                                # 接口文档与目录结构说明
```

## 快速上手（一次跑通）

```powershell
# 1) 编译插件（先按实际路径改好 csproj 中的 HintPath）
dotnet build autocad-plugin\AutoCADMcp.Plugin -c Debug

# 2) AutoCAD：NETLOAD 加载 DLL → 执行 MCPBRIDGESTART

# 3) 启动 MCP 服务器（HTTP 模式）
cd mcp-server; uv sync; uv run mcp-server --transport http

# 4) 用 MCP 客户端连接 http://127.0.0.1:8000/mcp，先调用 cad_bridge_health 验证桥在线
```

更多细节参见 [`mcp-server/README.md`](mcp-server/README.md) 与 [`doc/接口文档.md`](doc/接口文档.md)。
