"""图层/DWG、块与组、几何与空间感知类 MCP 工具。

包含：layer 2 个（set_layer / layer_manage）、dwg 1 个（dwg_op）、
block_group 5 个（insert_block / block_query / block_attr /
block_replace / group_op）、geo 4 个（geo_basic / geo_query /
calc_area / divide_curve）、spatial 6 个（connected / inside /
aligned / detect_clash / find_vacant / detect_islands）。
"""

from __future__ import annotations

from typing import Annotated

from pydantic import Field

from . import autocad_client as bridge


def register(registry) -> None:
    """向 ToolRegistry（官方 SDK 工具注册中心）注册本模块全部工具。"""

    # ---------------------------------------------------------------
    # layer 图层
    # ---------------------------------------------------------------

    @registry.tool()
    def set_layer(
        name: str,
        color: str = "",
        linetype: str = "",
        on: bool | None = None,
        freeze: bool | None = None,
        lock: bool | None = None,
        plottable: bool | None = None,
        current: bool = False,
    ) -> dict:
        """创建/设置图层属性，可选置为当前层。

        图层不存在时自动创建；on/freeze/lock/plottable 为
        None 表示不改动该属性。
        """
        return bridge.execute(
            "set_layer",
            {
                "name": name,
                "color": color,
                "linetype": linetype,
                "on": on,
                "freeze": freeze,
                "lock": lock,
                "plottable": plottable,
                "current": current,
            },
        )

    @registry.tool()
    def layer_manage(
        op: str,
        name: str = "",
        color: str = "",
        linetype: str = "",
        new_name: str = "",
    ) -> dict:
        """图层管理。

        op 支持：list（列表）/ create / delete / rename
        （new_name）/ purge（清理未使用）/ freeze / thaw / lock /
        unlock / on / off / isolate / unisolate / current。
        name 可逗号分隔多个图层。
        """
        return bridge.execute(
            "layer_manage",
            {
                "op": op,
                "name": name,
                "color": color,
                "linetype": linetype,
                "new_name": new_name,
            },
        )

    # ---------------------------------------------------------------
    # dwg 文件管理
    # ---------------------------------------------------------------

    @registry.tool()
    def dwg_op(
        op: str,
        path: str = "",
        template: str = "",
        read_only: bool = False,
        fix: bool = True,
    ) -> dict:
        """DWG 文件管理。

        op 支持：
        - new（可指定 template 模板名，如 "acad.dwt"）
        - open（path，read_only）
        - save / saveas（path）
        - close（关闭当前文档）
        - export_pdf（path，导出 PDF，异步等待文件生成）
        - export_dxf（path）
        - audit（检查修复，fix）
        - info（当前文件信息）
        """
        return bridge.execute(
            "dwg_op",
            {
                "op": op,
                "path": path,
                "template": template,
                "read_only": read_only,
                "fix": fix,
            },
        )

    # ---------------------------------------------------------------
    # block_group 块与组
    # ---------------------------------------------------------------

    @registry.tool()
    def insert_block(
        name: str,
        position: list[float],
        rotation: float = 0,
        scale: float = 1,
        layer: str = "",
        entities: list[str] | None = None,
        cut: bool = True,
        attrs: dict | None = None,
    ) -> dict:
        """插入块参照。

        若块定义不存在，可用 entities（图元 handle 列表）创建新块
        定义，cut=True 时原图元移入块（从当前空间删除）。
        attrs 为属性值字典 {标签: 值}。
        """
        return bridge.execute(
            "insert_block",
            {
                "name": name,
                "position": position,
                "rotation": rotation,
                "scale": scale,
                "layer": layer,
                "entities": entities,
                "cut": cut,
                "attrs": attrs,
            },
        )

    @registry.tool()
    def block_query(name: str = "") -> dict:
        """查询块定义。

        指定 name 返回该块定义详情与成员图元；
        缺省列出全部块定义（排除匿名块）。
        """
        return bridge.execute("block_query", {"name": name})

    @registry.tool()
    def block_attr(
        handle: str,
        op: str = "get",
        values: dict | None = None,
    ) -> dict:
        """读取/修改块参照的属性。

        op="get" 读取全部属性；op="set" 按 values 字典
        {标签: 新值} 修改。
        """
        return bridge.execute(
            "block_attr",
            {"handle": handle, "op": op, "values": values},
        )

    @registry.tool()
    def block_replace(
        src: str, dst: str, op: str = "replace"
    ) -> dict:
        """块替换/重定义。

        op="replace"：把所有引用 src 的块参照替换为 dst；
        op="redefine"：用 dst 的内容重定义 src 块定义。
        """
        return bridge.execute(
            "block_replace",
            {"src": src, "dst": dst, "op": op},
        )

    @registry.tool()
    def group_op(
        op: Annotated[
            str,
            Field(
                description="操作：list / create（name + 选择集）/ "
                "add（向组添加图元）/ remove（从组移除）/ "
                "delete（删除组，图元保留）。"
            ),
        ],
        name: Annotated[
            str, Field(description="组名；create/add/remove/delete 时使用。")
        ] = "",
        description: Annotated[
            str, Field(description="组描述，仅 create 时可选。")
        ] = "",
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。与 entities/selection 三选一。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection 三选一。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities，如 "
                '{"mode":"window",...}。与 handles/entities 三选一。'
            ),
        ] = None,
    ) -> dict:
        """组管理。

        create/add/remove 需先选定图元（handles / entities /
        selection 三选一）：
        - handles 为字符串数组，如 ["2DA","2DB"]，不要用逗号拼接的字符串。
        - entities / selection 为选择描述对象，如 {"mode":"all","layer":"0"}。

        op 支持：list / create（name + 选择集，description 可选）/
        add（向组添加图元）/ remove（从组移除）/ delete（删除组，
        图元保留）。
        """
        return bridge.execute(
            "group_op",
            {
                "op": op,
                "name": name,
                "description": description,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    # ---------------------------------------------------------------
    # geo 几何计算
    # ---------------------------------------------------------------

    @registry.tool()
    def geo_basic(
        op: str,
        p1: list[float] | None = None,
        p2: list[float] | None = None,
        base: list[float] | None = None,
        angle: float | None = None,
        distance: float | None = None,
        point: list[float] | None = None,
        center: list[float] | None = None,
        factor: float | None = None,
        handle: str | None = None,
        delta: list[float] | None = None,
    ) -> dict:
        """基础几何计算。

        op 支持：
        - distance：p1/p2 距离
        - midpoint：p1/p2 中点
        - angle：p1→p2 方向角（度）
        - polar：base + angle（度）+ distance 求极坐标点
        - rotate_point：point 绕 center 旋转 angle（度）
        - translate：point + delta 平移
        - scale_point：point 以 center 为基准缩放 factor
        - nearest：point 到曲线 handle 的最近点与距离
        """
        return bridge.execute(
            "geo_basic",
            {
                "op": op,
                "p1": p1,
                "p2": p2,
                "base": base,
                "angle": angle,
                "distance": distance,
                "point": point,
                "center": center,
                "factor": factor,
                "handle": handle,
                "delta": delta,
            },
        )

    @registry.tool()
    def geo_query(handle: str, op: str = "all") -> dict:
        """查询图元几何属性。

        op: all（全部）/ length / area / perimeter / centroid /
        start / end / mid / direction。
        """
        return bridge.execute(
            "geo_query", {"handle": handle, "op": op}
        )

    @registry.tool()
    def calc_area(
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。与 entities/selection/point 互斥。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection/point 互斥。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities。"
                "与 handles/entities/point 互斥。"
            ),
        ] = None,
        point: Annotated[
            list[float] | None,
            Field(
                description="【内部点模式】区域内部任一点 [x,y]，"
                "自动追踪边界求面积；给出后不要再传 handles/entities/selection。"
            ),
        ] = None,
    ) -> dict:
        """计算面积。

        二选一：
        - 选择图元（handles / entities / selection 三选一）：
          handles 为字符串数组，如 ["2DA","2DB"]，不要用逗号拼接的字符串；
          支持闭合曲线、面域、填充。
        - 内部点 point：给区域内部任一点 [x,y] 自动追踪边界。
        """
        return bridge.execute(
            "calc_area",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "point": point,
            },
        )

    @registry.tool()
    def divide_curve(
        handle: str,
        segments: int = 0,
        interval: float = 0,
        mark_only: bool = False,
        layer: str = "",
    ) -> dict:
        """等分曲线。

        segments 等分段数或 interval 等分间距（二选一）；
        mark_only=True 只返回等分点坐标，不创建点图元。
        """
        return bridge.execute(
            "divide_curve",
            {
                "handle": handle,
                "segments": segments,
                "interval": interval,
                "mark_only": mark_only,
                "layer": layer,
            },
        )

    # ---------------------------------------------------------------
    # spatial 空间感知
    # ---------------------------------------------------------------

    @registry.tool()
    def connected(
        op: Annotated[
            str,
            Field(
                description='操作："check" 检查所有选中图元是否互相'
                '连通；"component" 返回第一个选中图元的连通分量。'
            ),
        ] = "check",
        tolerance: Annotated[
            float, Field(description="端点重合判定容差，默认 0.001。")
        ] = 0.001,
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。与 entities/selection 三选一。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection 三选一。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities，如 "
                '{"mode":"window",...}。与 handles/entities 三选一。'
            ),
        ] = None,
    ) -> dict:
        """检查图元连通性。

        先选定图元（handles / entities / selection 三选一，均省略则为全部图元）：
        - handles 为字符串数组，如 ["2DA","2DB"]，不要用逗号拼接的字符串。
        - entities / selection 为选择描述对象，如 {"mode":"all","layer":"0"}。

        op="check"：所有选中图元是否互相连通（共享端点/相交）；
        op="component"：返回第一个选中图元的连通分量。
        """
        return bridge.execute(
            "connected",
            {
                "op": op,
                "tolerance": tolerance,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def inside(
        op: Annotated[
            str,
            Field(
                description='判断模式："point"（point+container）/ '
                '"entities"（container+选择集）/ '
                '"rectangle"（p1/p2 矩形+选择集）。'
            ),
        ],
        point: Annotated[
            list[float] | None,
            Field(description="op=point 时的待判点 [x,y]。"),
        ] = None,
        container: Annotated[
            str | None,
            Field(
                description="闭合曲线容器 handle（op=point / "
                "op=entities 时使用），单个字符串如 \"2DA\"。"
            ),
        ] = None,
        p1: Annotated[
            list[float] | None,
            Field(description="op=rectangle 时矩形对角点 1 [x,y]。"),
        ] = None,
        p2: Annotated[
            list[float] | None,
            Field(description="op=rectangle 时矩形对角点 2 [x,y]。"),
        ] = None,
        handles: Annotated[
            list[str] | None,
            Field(
                description="待判图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。与 entities/selection 三选一。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection 三选一。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities，如 "
                '{"mode":"window",...}。与 handles/entities 三选一。'
            ),
        ] = None,
    ) -> dict:
        """判断点在闭合曲线内 / 图元在容器内 / 图元在矩形内。

        op="point"：point + container（闭合曲线 handle）；
        op="entities"：container + 选择集；
        op="rectangle"：p1/p2 矩形 + 选择集。

        选择集为 handles / entities / selection 三选一：
        handles 为字符串数组，如 ["2DA","2DB"]，不要用逗号拼接的字符串；
        entities / selection 为选择描述对象，如 {"mode":"all","layer":"0"}。
        """
        return bridge.execute(
            "inside",
            {
                "op": op,
                "point": point,
                "container": container,
                "p1": p1,
                "p2": p2,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def aligned(
        op: Annotated[
            str,
            Field(
                description="对齐类型：horizontal（水平）/ vertical（垂直）/ "
                "center（中心）/ collinear（共线）。"
            ),
        ],
        tolerance: Annotated[
            float, Field(description="对齐判定容差，默认 0.001。")
        ] = 0.001,
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。与 entities/selection 三选一。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection 三选一。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities，如 "
                '{"mode":"window",...}。与 handles/entities 三选一。'
            ),
        ] = None,
    ) -> dict:
        """检查图元对齐关系（以第一个选中图元为基准）。

        先选定图元（handles / entities / selection 三选一）：
        handles 为字符串数组，如 ["2DA","2DB"]，不要用逗号拼接的字符串；
        entities / selection 为选择描述对象，如 {"mode":"all","layer":"0"}。

        op: horizontal（水平）/ vertical（垂直）/ center（中心）/
        collinear（共线）。
        """
        return bridge.execute(
            "aligned",
            {
                "op": op,
                "tolerance": tolerance,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def detect_clash(
        tolerance: Annotated[
            float, Field(description="碰撞判定容差，默认 0.0001。")
        ] = 0.0001,
        set_a: Annotated[
            list[str] | dict | None,
            Field(
                description="A 组图元：handle 字符串数组如 [\"2DA\",\"2DB\"]"
                "（勿用逗号拼接的字符串），或选择描述对象如 "
                '{"mode":"all","layer":"0"}。'
            ),
        ] = None,
        set_b: Annotated[
            list[str] | dict | None,
            Field(
                description="B 组图元，格式同 set_a；缺省时与 A 组相同做两两检测。"
            ),
        ] = None,
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 列表，必须是字符串数组，"
                '例如 ["2DA","2DB"]；切勿用逗号拼接的单个字符串 '
                '"2DA,2DB"。仅在不传 set_a/set_b 时用于两两检测。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 "
                '{"mode":"all","layer":"0"}。与 handles/selection 三选一。'
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities，如 "
                '{"mode":"window",...}。与 handles/entities 三选一。'
            ),
        ] = None,
    ) -> dict:
        """检测图元碰撞/相交。

        两种方式：
        - 指定 set_a / set_b 两组图元（每组为 handle 字符串数组，如
          ["2DA","2DB"]，或选择描述对象 {"mode":"all","layer":"0"}）；
        - 或不传 set_a/set_b，用 handles / entities / selection 三选一
          选定一组图元做两两检测（handles 勿用逗号拼接的字符串）。

        共享端点的接触不算冲突。
        """
        return bridge.execute(
            "detect_clash",
            {
                "tolerance": tolerance,
                "set_a": set_a,
                "set_b": set_b,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def find_vacant(
        width: float,
        height: float,
        p1: list[float] | None = None,
        p2: list[float] | None = None,
        step: float = 0,
        limit: int = 20,
    ) -> dict:
        """在指定区域查找可容纳 width×height 矩形的空位。

        区域由 p1/p2 指定，缺省为当前空间范围；step 为搜索步长
        （缺省取宽高较大值的 1/4）。
        """
        return bridge.execute(
            "find_vacant",
            {
                "width": width,
                "height": height,
                "p1": p1,
                "p2": p2,
                "step": step,
                "limit": limit,
            },
        )

    @registry.tool()
    def detect_islands(outer: str) -> dict:
        """检测闭合曲线（outer）内部的独立闭合区域（孤岛）。

        返回完全位于 outer 内部的闭合曲线列表，并标注是否
        嵌套了其他孤岛。
        """
        return bridge.execute(
            "detect_islands", {"outer": outer}
        )
