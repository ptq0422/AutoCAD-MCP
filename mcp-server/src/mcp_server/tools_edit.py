"""编辑类 MCP 工具（转发到 AutoCAD C# 桥）。

包含 18 个：transform / delete / offset / trim / extend /
fillet / chamfer / break / stretch / align / change_properties /
erase_by_filter / undo / explode / join_entity / array /
array_rect / array_polar。
"""

from __future__ import annotations

from typing import Annotated

from pydantic import Field

from . import autocad_client as bridge


def register(registry) -> None:
    """向 ToolRegistry（官方 SDK 工具注册中心）注册本模块全部工具。"""

    @registry.tool()
    def transform(
        mode: Annotated[
            str,
            Field(
                description="操作模式：move（移动）/ rotate（旋转）/ "
                "scale（缩放）/ mirror（镜像）/ copy（复制并位移）。"
            ),
        ],
        handles: Annotated[
            list[str] | None,
            Field(
                description="图元 handle 数组，如 [\"2DA\",\"2DB\"]，"
                "不能用逗号拼接的单个字符串。"
                "与 entities/selection 三选一。"
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 {\"mode\":\"all\",\"layer\":\"0\"}。"
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(
                description="选择描述对象，同 entities。"
            ),
        ] = None,
        base: Annotated[
            list[float] | None,
            Field(
                description="基点 [x,y]。rotate 和 scale 时必填。"
            ),
        ] = None,
        angle: Annotated[
            float | None,
            Field(
                description="旋转角度（度，逆时针），仅 mode=rotate 时必填。"
            ),
        ] = None,
        factor: Annotated[
            float | None,
            Field(
                description="缩放因子（>0），仅 mode=scale 时必填。"
            ),
        ] = None,
        p1: Annotated[
            list[float] | None,
            Field(
                description="镜像轴起点 [x,y]，仅 mode=mirror 时必填。"
            ),
        ] = None,
        p2: Annotated[
            list[float] | None,
            Field(
                description="镜像轴终点 [x,y]，仅 mode=mirror 时必填。"
            ),
        ] = None,
        delta: Annotated[
            list[float] | None,
            Field(
                description="位移向量 [dx,dy] 或 [dx,dy,dz]，"
                "仅 mode=move/copy 时使用；与 from/to 和 dx/dy/dz 三选一。"
            ),
        ] = None,
        from_: Annotated[
            list[float] | None,
            Field(
                description="位移源点 [x,y]，需与 to 配对；"
                "仅 mode=move/copy 时使用。"
            ),
        ] = None,
        to: Annotated[
            list[float] | None,
            Field(
                description="位移目标点 [x,y]，需与 from_ 配对。"
            ),
        ] = None,
        dx: Annotated[float, Field(description="X 方向位移，默认 0。")] = 0,
        dy: Annotated[float, Field(description="Y 方向位移，默认 0。")] = 0,
        dz: Annotated[float, Field(description="Z 方向位移，默认 0。")] = 0,
        count: Annotated[
            int,
            Field(description="复制份数（仅 mode=copy），默认 1。"),
        ] = 1,
    ) -> dict:
        """移动/旋转/缩放/镜像/复制图元。

        按 mode 所需参数：
        - move/copy：位移三选一 — delta=[dx,dy] 或 from_+to 点对 或 dx/dy/dz 数值；
          copy 可选 count 份数。
        - rotate：base（基点）+ angle（度，逆时针）。
        - scale：base（基点）+ factor（缩放因子 >0）。
        - mirror：p1 + p2（镜像轴两端点）。

        对象选择：handles 数组（如 ["2DA","2DB"]，不要用逗号拼接字符串）；
        或 entities/selection 对象（如 {"mode":"all","layer":"0"}）；
        缺省用当前选择集。
        """
        return bridge.execute(
            "transform",
            {
                "mode": mode,
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "base": base,
                "angle": angle,
                "factor": factor,
                "p1": p1,
                "p2": p2,
                "delta": delta,
                "from": from_,
                "to": to,
                "dx": dx,
                "dy": dy,
                "dz": dz,
                "count": count,
            },
        )

    @registry.tool()
    def delete(
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
        mode: str | None = None,
        type: str | None = None,
        layer: str | None = None,
        color: int | None = None,
        block: str | None = None,
        space: str = "current",
    ) -> dict:
        """删除图元。

        对象选择：handles 列表；或直接给出过滤条件（mode/type/layer/
        color/block 将视为选择描述）；缺省用当前选择集。
        """
        return bridge.execute(
            "delete",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "mode": mode,
                "type": type,
                "layer": layer,
                "color": color,
                "block": block,
                "space": space,
            },
        )

    @registry.tool()
    def offset(
        distance: float,
        side: str = "left",
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
        layer: str = "",
    ) -> dict:
        """偏移曲线。

        distance 为偏移距离（>0）；side 为 "left"（沿曲线方向左侧）
        或 "right"；偏移结果可选放到指定 layer。
        """
        return bridge.execute(
            "offset",
            {
                "distance": distance,
                "side": side,
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "layer": layer,
            },
        )

    @registry.tool()
    def trim(
        target: Annotated[
            str,
            Field(
                description="被修剪曲线的 handle（须为 Curve 类型，"
                "如 Line/Arc/Polyline/Circle）。"
            ),
        ],
        cutting: Annotated[
            list[str] | None,
            Field(
                description='裁剪边界图元 handle 数组，如 ["3AA","3AB"]。'
                "边界必须与 target 有实际交点，否则报「未找到交点」。"
            ),
        ] = None,
        handles: Annotated[
            list[str] | None,
            Field(
                description="备选：用 handles 指定裁剪边界（与 cutting 等效）。"
            ),
        ] = None,
        keep: Annotated[
            str,
            Field(
                description='保留段选择："point"（保留靠近 point 的一侧）、'
                '"start"（保留起始端段）、"end"（保留末端段）。'
            ),
        ] = "point",
        point: Annotated[
            list[float] | None,
            Field(
                description="保留侧参考点 [x,y]，仅 keep=\"point\" 时使用。"
            ),
        ] = None,
    ) -> dict:
        """修剪曲线：用边界图元裁剪 target 曲线。

        要求：cutting（或 handles）指定的边界必须与 target 有实际交点。
        若边界为直线，需确保与 target 确实相交（延伸后也算）。
        keep="point" 时需在 point 所在侧保留图形。
        """
        return bridge.execute(
            "trim",
            {
                "target": target,
                "cutting": cutting,
                "handles": handles,
                "keep": keep,
                "point": point,
            },
        )

    @registry.tool()
    def extend(
        target: str,
        boundary: list[str],
        which: str = "both",
    ) -> dict:
        """延伸曲线到边界。

        target 为被延伸曲线 handle（支持 Line/Arc/Polyline）；
        boundary 为边界图元 handle 列表；which 为 "start" /
        "end" / "both"。
        """
        return bridge.execute(
            "extend",
            {
                "target": target,
                "boundary": boundary,
                "which": which,
            },
        )

    @registry.tool()
    def fillet(
        radius: Annotated[
            float,
            Field(description="圆角半径（>0）。"),
        ],
        e1: Annotated[
            str,
            Field(
                description="第一条直线 handle（两线模式），"
                "或折线 Polyline 的 handle（顶点模式，此时 e2 也填同一值）。"
            ),
        ],
        e2: Annotated[
            str,
            Field(
                description="第二条直线 handle（两线模式，须与 e1 不同）；"
                "顶点倒角时 e2 填写与 e1 相同的 Polyline handle。"
            ),
        ] = "",
        vertex: Annotated[
            int,
            Field(
                description="折线顶点索引（从 0 开始），仅顶点模式"
                "（e1==e2 且为 Polyline）时必填。"
            ),
        ] = -1,
    ) -> dict:
        """倒圆角（两条直线之间，或折线指定顶点处）。

        两种用法（互斥）：
        1. 两线模式：e1 和 e2 为不同 LINE 的 handle，两线需有交点或可延伸相交。
        2. 顶点模式：e1 == e2（同一 Polyline handle），vertex ≥ 0 指定顶点索引。
        注意：两线模式下对象必须是 Line 类型，否则报错。
        """
        return bridge.execute(
            "fillet",
            {
                "radius": radius,
                "e1": e1,
                "e2": e2,
                "vertex": vertex,
            },
        )

    @registry.tool()
    def chamfer(
        e1: Annotated[
            str,
            Field(
                description="第一条直线 handle（两线模式），"
                "或折线 Polyline handle（顶点模式，此时 e2 填同一值）。"
            ),
        ],
        e2: Annotated[
            str,
            Field(
                description="第二条直线 handle（须与 e1 不同）；"
                "顶点模式时 e2 填写与 e1 相同的 Polyline handle。"
            ),
        ] = "",
        distance1: Annotated[
            float,
            Field(description="第一条线方向的倒角距离（>0）。"),
        ] = 0,
        distance2: Annotated[
            float,
            Field(description="第二条线方向的倒角距离；为 0 时等于 distance1。"),
        ] = 0,
        vertex: Annotated[
            int,
            Field(
                description="折线顶点索引（从 0 开始），仅顶点模式"
                "（e1==e2 且为 Polyline）时必填。"
            ),
        ] = -1,
    ) -> dict:
        """倒角（两条直线之间，或折线指定顶点处）。

        两种用法（互斥）：
        1. 两线模式：e1 和 e2 为不同 LINE handle，需指定 distance1（distance2 缺省同 distance1）。
        2. 顶点模式：e1 == e2（同一 Polyline handle），vertex ≥ 0 指定顶点索引。
        注意：两线模式下对象必须是 Line 类型。
        """
        return bridge.execute(
            "chamfer",
            {
                "e1": e1,
                "e2": e2,
                "distance1": distance1,
                "distance2": distance2,
                "vertex": vertex,
            },
        )

    @registry.tool(name="break")
    def break_(
        handle: Annotated[
            str,
            Field(
                description="目标曲线 handle（须为 Curve 类型，"
                "如 Line/Arc/Polyline/Circle）。"
            ),
        ],
        point1: Annotated[
            list[float],
            Field(
                description="第一个打断点 [x,y]，必须为数组，"
                "不能是字符串。系统会自动投影到曲线上最近点。"
            ),
        ],
        point2: Annotated[
            list[float] | None,
            Field(
                description="第二个打断点 [x,y]（可选）。"
                "提供两点时删除两点间的中间段，保留首尾两段；"
                "不提供则单点切一刀，保留两段。"
            ),
        ] = None,
    ) -> dict:
        """打断曲线：在指定点处将曲线一分为二（或挖除中间段）。

        handle 须为 Curve 类型图元。
        point1 为打断点坐标数组 [x,y]（不能是字符串），系统取曲线上最近点。
        可选 point2 做第二段点打断：两点之间段落被删除，仅保留首尾。
        """
        return bridge.execute(
            "break",
            {
                "handle": handle,
                "point1": point1,
                "point2": point2,
            },
        )

    @registry.tool()
    def stretch(
        p1: Annotated[
            list[float],
            Field(description="交叉窗口角点1 [x,y]。"),
        ],
        p2: Annotated[
            list[float],
            Field(description="交叉窗口角点2 [x,y]（与 p1 对角）。"),
        ],
        delta: Annotated[
            list[float] | None,
            Field(
                description="位移向量 [dx,dy] 或 [dx,dy,dz]；"
                "与 from_/to 和 dx/dy/dz 三选一。"
            ),
        ] = None,
        from_: Annotated[
            list[float] | None,
            Field(description="位移源点 [x,y]，需与 to 配对。"),
        ] = None,
        to: Annotated[
            list[float] | None,
            Field(description="位移目标点 [x,y]，需与 from_ 配对。"),
        ] = None,
        dx: Annotated[float, Field(description="X 方向位移，默认 0。")] = 0,
        dy: Annotated[float, Field(description="Y 方向位移，默认 0。")] = 0,
        dz: Annotated[float, Field(description="Z 方向位移，默认 0。")] = 0,
    ) -> dict:
        """拉伸（交叉窗口内图元按位移移动）。

        p1/p2 定义交叉窗口对角点；窗口内图元顶点被拉伸。
        位移三选一：delta=[dx,dy] 或 from_+to 点对 或 dx/dy/dz 数值。
        """
        return bridge.execute(
            "stretch",
            {
                "p1": p1,
                "p2": p2,
                "delta": delta,
                "from": from_,
                "to": to,
                "dx": dx,
                "dy": dy,
                "dz": dz,
            },
        )

    @registry.tool()
    def align(
        src: list[list[float]],
        dst: list[list[float]],
        scale: bool = False,
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
    ) -> dict:
        """对齐图元（移动 + 旋转，可选缩放）。

        src/dst 为 2~3 组对应点；scale=True 时按第一组点距离缩放。
        """
        return bridge.execute(
            "align",
            {
                "src": src,
                "dst": dst,
                "scale": scale,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def change_properties(
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
        layer: str = "",
        color: str = "",
        linetype: str = "",
        linetype_scale: float | None = None,
        visible: bool | None = None,
    ) -> dict:
        """修改图元属性（图层/颜色/线型/线型比例/可见性）。

        只修改显式给出的属性；其余保持不变。
        """
        return bridge.execute(
            "change_properties",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "layer": layer,
                "color": color,
                "linetype": linetype,
                "linetype_scale": linetype_scale,
                "visible": visible,
            },
        )

    @registry.tool()
    def erase_by_filter(
        space: Annotated[
            str,
            Field(
                description='扫描范围："current"（当前空间）/ '
                '"model"（模型空间）/ "paper"（图纸空间）/ '
                '"all"（全部）。'
            ),
        ] = "current",
        type: Annotated[
            str | None,
            Field(
                description="按图元类型过滤（大小写不敏感）。"
                "可用别名：line, circle, arc, text, mtext, "
                "polyline/pline, polyline3d, spline, ellipse, "
                "point, block/insert, hatch, dimension, "
                "leader/mleader, region, table, viewport, "
                "solid, face。"
                "也可直接传 DXF 名称：LINE, CIRCLE, ARC, TEXT, "
                "MTEXT, LWPOLYLINE, POLYLINE, SPLINE, ELLIPSE, "
                "POINT, INSERT, HATCH, DIMENSION, MULTILEADER, "
                "REGION, ACAD_TABLE, VIEWPORT, SOLID, 3DFACE, "
                "RAY, XLINE, MLINE, ATTDEF, OLE2FRAME, WIPEOUT。"
            ),
        ] = None,
        layer: Annotated[
            str | None,
            Field(description="按图层名过滤（大小写不敏感），如 \"0\"、\"WALL\"。"),
        ] = None,
        color: Annotated[
            int | None,
            Field(description="按颜色索引过滤（ACI 色号 1-255），如 1=红、3=绿、7=白/黑。"),
        ] = None,
        block: Annotated[
            str | None,
            Field(description="按块名过滤（大小写不敏感），仅对 INSERT 类型有效。"),
        ] = None,
    ) -> dict:
        """按过滤条件批量删除图元。

        可同时指定多个过滤条件（AND 关系）；不传则不过滤。
        space 控制扫描范围，type/layer/color/block 逐条匹配。
        """
        return bridge.execute(
            "erase_by_filter",
            {
                "space": space,
                "type": type,
                "layer": layer,
                "color": color,
                "block": block,
            },
        )

    @registry.tool()
    def undo() -> dict:
        """撤销最近一次操作（提交 AutoCAD UNDO 命令）。"""
        return bridge.execute("undo", {})

    @registry.tool()
    def explode(
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
    ) -> dict:
        """分解图元（块参照 / 多段线）。"""
        return bridge.execute(
            "explode",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def join_entity(
        handles: list[str] | None = None,
        entities: dict | None = None,
        selection: dict | None = None,
    ) -> dict:
        """合并图元（共线直线合并、同圆心同半径圆弧合并）。"""
        return bridge.execute(
            "join_entity",
            {
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def array(
        mode: Annotated[
            str,
            Field(
                description='阵列类型："rect"（矩形）或 "polar"（环形）。'
            ),
        ] = "rect",
        handles: Annotated[
            list[str] | None,
            Field(
                description='图元 handle 数组，如 ["2DA","2DB"]。'
                "与 entities/selection 三选一。"
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(description="选择描述对象。"),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(description="选择描述对象，同 entities。"),
        ] = None,
        rows: Annotated[
            int,
            Field(description="行数（仅 rect），≥1，默认 1。"),
        ] = 1,
        cols: Annotated[
            int,
            Field(description="列数（仅 rect），≥1，默认 1。"),
        ] = 1,
        row_offset: Annotated[
            list[float] | None,
            Field(
                description="行间距 [dx,dy]（仅 rect，推荐 [0,间距]），"
                "缺省按包围盒高度。"
            ),
        ] = None,
        col_offset: Annotated[
            list[float] | None,
            Field(
                description="列间距 [dx,dy]（仅 rect，推荐 [间距,0]），"
                "缺省按包围盒宽度。"
            ),
        ] = None,
        center: Annotated[
            list[float] | None,
            Field(description="阵列中心点 [x,y]（仅 polar 必填）。"),
        ] = None,
        count: Annotated[
            int,
            Field(description="副本总数（仅 polar），≥2，默认 4。"),
        ] = 4,
        total_angle: Annotated[
            float,
            Field(description="总分布角度（度，仅 polar），默认 360。"),
        ] = 360,
        rotate_items: Annotated[
            bool,
            Field(description="副本是否随角度旋转（仅 polar），默认 true。"),
        ] = True,
    ) -> dict:
        """通用阵列（矩形或环形，由 mode 切换）。

        mode="rect"：rows×cols 矩形复制，row_offset/col_offset 为
        行/列间距向量 [dx,dy]，缺省按选中图元包围盒尺寸。
        mode="polar"：围绕 center 均匀分布 count 个副本，
        total_angle 总角度，rotate_items 是否旋转。

        对象选择：handles 数组 / entities / selection 三选一。
        """
        return bridge.execute(
            "array",
            {
                "mode": mode,
                "handles": handles,
                "entities": entities,
                "selection": selection,
                "rows": rows,
                "cols": cols,
                "row_offset": row_offset,
                "col_offset": col_offset,
                "center": center,
                "count": count,
                "total_angle": total_angle,
                "rotate_items": rotate_items,
            },
        )

    @registry.tool()
    def array_rect(
        rows: Annotated[
            int,
            Field(description="行数（≥1），默认 1。行方向偏移为 Y 轴。"),
        ] = 1,
        cols: Annotated[
            int,
            Field(description="列数（≥1），默认 1。列方向偏移为 X 轴。"),
        ] = 1,
        row_offset: Annotated[
            list[float] | None,
            Field(
                description="行间距 [dx,dy]（推荐 [0,间距]），"
                "缺省时按图元包围盒高度自动计算。"
            ),
        ] = None,
        col_offset: Annotated[
            list[float] | None,
            Field(
                description="列间距 [dx,dy]（推荐 [间距,0]），"
                "缺省时按图元包围盒宽度自动计算。"
            ),
        ] = None,
        handles: Annotated[
            list[str] | None,
            Field(
                description="源图元 handle 数组，如 [\"2DA\",\"2DB\"]，"
                "不能用逗号拼接字符串。与 entities/selection 三选一。"
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(
                description="选择描述对象，如 {\"mode\":\"all\",\"layer\":\"0\"}。"
            ),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(description="选择描述对象，同 entities。"),
        ] = None,
    ) -> dict:
        """矩形阵列：按行列数复制图元。

        rows×cols 生成阵列（r=0,c=0 位置为原图元不复制）；
        row_offset/col_offset 指定行/列间距向量 [dx,dy]，
        缺省时按选中的图元包围盒宽高自动计算。

        对象选择：handles 数组 / entities / selection 三选一。
        """
        return bridge.execute(
            "array_rect",
            {
                "rows": rows,
                "cols": cols,
                "row_offset": row_offset,
                "col_offset": col_offset,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )

    @registry.tool()
    def array_polar(
        center: Annotated[
            list[float],
            Field(description="阵列中心点 [x,y]。"),
        ],
        count: Annotated[
            int,
            Field(description="副本总数（含原对象，≥2），默认 4。"),
        ] = 4,
        total_angle: Annotated[
            float,
            Field(
                description="总分布角度（度），默认 360（整圆均布）。"
                "设为 180 为半圆均布。"
            ),
        ] = 360,
        rotate_items: Annotated[
            bool,
            Field(
                description="每个副本是否随角度旋转，默认 true。"
                "为 false 时保持原方向。"
            ),
        ] = True,
        handles: Annotated[
            list[str] | None,
            Field(
                description='图元 handle 十六进制字符串数组，'
                '如 ["2DA","2DB"]；必须先用 query_entities 等'
                '工具获取当前图纸中存在的 handle，不存在则被忽略。'
                '与 entities/selection 三选一。'
            ),
        ] = None,
        entities: Annotated[
            dict | None,
            Field(description="选择描述对象。"),
        ] = None,
        selection: Annotated[
            dict | None,
            Field(description="选择描述对象，同 entities。"),
        ] = None,
    ) -> dict:
        """环形阵列：围绕中心点均匀复制图元。

        center 中心点 [x,y]；count 副本总数（含原对象）；
        total_angle 总分布角度（360=整圆均布）；
        rotate_items 控制每个副本是否随之旋转。

        对象选择：handles 数组 / entities / selection 三选一。
        """
        return bridge.execute(
            "array_polar",
            {
                "center": center,
                "count": count,
                "total_angle": total_angle,
                "rotate_items": rotate_items,
                "handles": handles,
                "entities": entities,
                "selection": selection,
            },
        )
