"""查询类 MCP 工具（转发到 AutoCAD C# 桥）。

包含 19 个：get_drawing_summary / get_drawing_fingerprint /
query_entities / get_entity / entity_info / entity_count /
find_text / get_bounds / sysvar / query_relations /
trace_boundary / verify_result / compare_drawings / list_styles /
get_selection / get_dwg_info / read_commandline / layer_query /
list_all。
"""

from __future__ import annotations

from . import autocad_client as bridge


def register(registry) -> None:
    """向 ToolRegistry（官方 SDK 工具注册中心）注册本模块全部工具。"""

    @registry.tool()
    def get_drawing_summary() -> dict:
        """获取当前图纸摘要：名称、路径、单位、模型/图纸空间图元
        统计、图层列表、块数量、当前层与图形范围。"""
        return bridge.execute("get_drawing_summary", {})

    @registry.tool()
    def get_drawing_fingerprint(space: str = "current") -> dict:
        """计算图纸指纹（MD5）。

        基于每个图元的 handle、类型、图层、包围盒生成，
        可用于判断图纸内容是否变化。space: current | model | paper | all。
        """
        return bridge.execute(
            "get_drawing_fingerprint", {"space": space}
        )

    @registry.tool()
    def query_entities(
        space: str = "current",
        limit: int = 200,
        offset: int = 0,
        detailed: bool = False,
        sort: str = "handle",
        type: str | None = None,
        layer: str | None = None,
        color: int | None = None,
        block: str | None = None,
    ) -> dict:
        """查询图元列表（支持分页、排序、过滤）。

        space: current | model | paper | all；sort: handle |
        type | layer | length | area；可按 type/layer/color/block
        过滤；detailed=True 返回完整属性。
        """
        return bridge.execute(
            "query_entities",
            {
                "space": space,
                "limit": limit,
                "offset": offset,
                "detailed": detailed,
                "sort": sort,
                "type": type,
                "layer": layer,
                "color": color,
                "block": block,
            },
        )

    @registry.tool()
    def get_entity(handle: str) -> dict:
        """按 handle 获取图元完整属性（几何、图层、颜色等）。"""
        return bridge.execute("get_entity", {"handle": handle})

    @registry.tool()
    def entity_info(handle: str) -> dict:
        """获取图元详细信息（含所属组、handle 值）。"""
        return bridge.execute("entity_info", {"handle": handle})

    @registry.tool()
    def entity_count(
        space: str = "current",
        type: str | None = None,
        layer: str | None = None,
        color: int | None = None,
        block: str | None = None,
    ) -> dict:
        """统计图元数量（可按类型/图层等过滤，返回按类型分组计数）。"""
        return bridge.execute(
            "entity_count",
            {
                "space": space,
                "type": type,
                "layer": layer,
                "color": color,
                "block": block,
            },
        )

    @registry.tool()
    def find_text(
        text: str,
        mode: str = "contains",
        case_sensitive: bool = False,
        space: str = "current",
        limit: int = 100,
    ) -> dict:
        """查找文字内容。

        mode: contains（包含）| exact（精确）；搜索范围包括单行/
        多行文字、属性、标注文字。
        """
        return bridge.execute(
            "find_text",
            {
                "text": text,
                "mode": mode,
                "case_sensitive": case_sensitive,
                "space": space,
                "limit": limit,
            },
        )

    @registry.tool()
    def get_bounds(
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
    ) -> dict:
        """获取图元包围盒（min/max 角点）。

        不指定选择时返回当前空间整体范围。
        """
        return bridge.execute(
            "get_bounds",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def sysvar(
        name: str = "",
        op: str = "get",
        value: str | float | None = None,
    ) -> dict:
        """读取/设置 AutoCAD 系统变量。

        op="get" 读取 name；op="set" 设置 name=value；
        op="list" 列出常用系统变量。
        """
        return bridge.execute(
            "sysvar",
            {"name": name, "op": op, "value": value},
        )

    @registry.tool()
    def query_relations(
        handle: str, tolerance: float = 0.001
    ) -> dict:
        """查询图元与其他图元的空间关系。

        返回与该图元共享端点或相交的图元列表（shared_endpoint /
        intersect）。
        """
        return bridge.execute(
            "query_relations",
            {"handle": handle, "tolerance": tolerance},
        )

    @registry.tool()
    def trace_boundary(
        point: list[float], detect_islands: bool = False
    ) -> dict:
        """通过内部点追踪闭合边界。

        返回构成边界的图元列表；detect_islands=True 时同时
        检测内部孤岛。
        """
        return bridge.execute(
            "trace_boundary",
            {"point": point, "detect_islands": detect_islands},
        )

    @registry.tool()
    def verify_result() -> dict:
        """验证最近一次 MCP 操作的执行结果。

        检查上次操作涉及的新建/修改/删除图元当前是否真实存在。
        """
        return bridge.execute("verify_result", {})

    @registry.tool()
    def compare_drawings(path: str) -> dict:
        """将当前图纸与另一 DWG 文件比较。

        对比各类型图元数量与整体范围，返回差异列表。
        """
        return bridge.execute("compare_drawings", {"path": path})

    @registry.tool()
    def list_styles(category: str = "all") -> dict:
        """列出样式定义。

        category: text | dim | table | mleader | layer |
        linetype | all。
        """
        return bridge.execute(
            "list_styles", {"category": category}
        )

    @registry.tool()
    def get_selection() -> dict:
        """获取 AutoCAD 当前（隐含）选择集内容。"""
        return bridge.execute("get_selection", {})

    @registry.tool()
    def get_dwg_info() -> dict:
        """获取当前 DWG 文件信息（版本、作者、标题、单位、命名
        对象、文件大小与修改时间等）。"""
        return bridge.execute("get_dwg_info", {})

    @registry.tool()
    def read_commandline(limit: int = 20) -> dict:
        """读取 MCP 插件最近的操作历史。

        注意：AutoCAD 原生命令行历史无法直接读取，此记录为
        MCP 工具调用历史（最近 limit 条）。
        """
        return bridge.execute(
            "read_commandline", {"limit": limit}
        )

    @registry.tool()
    def layer_query(name: str = "") -> dict:
        """查询图层信息。

        指定 name 返回该图层详细属性与图元数量；
        缺省列出全部图层概要。
        """
        return bridge.execute("layer_query", {"name": name})

    @registry.tool()
    def list_all(
        space: str = "current", limit: int = 500
    ) -> dict:
        """列出图元的 handle / 类型 / 图层 / 颜色。

        轻量版 query_entities，适合快速浏览。
        """
        return bridge.execute(
            "list_all", {"space": space, "limit": limit}
        )
