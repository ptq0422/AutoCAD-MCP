"""绘图与标注类 MCP 工具（转发到 AutoCAD C# 桥）。

包含 draw 类 9 个：line / circle / arc / rectangle / polyline /
ellipse / spline / polygon / point；
text/dimension/hatch 类 4 个：draw_text / dimension / leader / hatch。
"""

from __future__ import annotations

from typing import Annotated

from pydantic import Field

from . import autocad_client as bridge


def register(registry) -> None:
    """向 ToolRegistry（官方 SDK 工具注册中心）注册本模块全部工具。"""

    @registry.tool()
    def line(
        start: Annotated[
            list[float],
            Field(description="起点 [x,y]。[0,0] 是合法起点。"),
        ],
        end: Annotated[
            list[float],
            Field(description="终点 [x,y]。"),
        ],
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制直线。单一模式（两点）：

        - 必填 start（起点）+ end（终点）。
        - layer/color 可选，例如 color="red" 或 "1"。
        """
        return bridge.execute(
            "line",
            {
                "start": start,
                "end": end,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def circle(
        center: Annotated[
            list[float],
            Field(description="圆心 [x,y]。"),
        ],
        radius: Annotated[
            float,
            Field(description="半径（>0）。"),
        ],
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制圆。单一模式（圆心半径）：

        - 必填 center（圆心）+ radius（半径，>0）。
        - layer/color 可选。
        """
        return bridge.execute(
            "circle",
            {
                "center": center,
                "radius": radius,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def arc(
        center: Annotated[
            list[float],
            Field(description="圆心 [x,y]。"),
        ],
        radius: Annotated[
            float,
            Field(description="半径（>0）。"),
        ],
        start_angle: Annotated[
            float,
            Field(description="起始角（度，逆时针）。"),
        ],
        end_angle: Annotated[
            float,
            Field(description="终止角（度）。"),
        ],
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制圆弧。单一模式（圆心角度）：

        - 必填 center + radius + start_angle + end_angle。
        - 角度单位为度，逆时针；layer/color 可选。
        """
        return bridge.execute(
            "arc",
            {
                "center": center,
                "radius": radius,
                "start_angle": start_angle,
                "end_angle": end_angle,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def rectangle(
        p1: Annotated[
            list[float],
            Field(description="对角点 1 [x,y]。"),
        ],
        p2: Annotated[
            list[float],
            Field(description="对角点 2 [x,y]。"),
        ],
        rotation: float = 0,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制矩形（闭合多段线）。单一模式（对角点）：

        - 必填 p1 + p2（两个对角点）。
        - rotation 为绕中心旋转角（度，可选）；layer/color 可选。
        """
        return bridge.execute(
            "rectangle",
            {
                "p1": p1,
                "p2": p2,
                "rotation": rotation,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def polyline(
        points: list[list[float]],
        closed: bool = False,
        width: float = 0,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制多段线。单一模式，无互斥参数：

        - 必填 points：顶点列表 [[x,y],...] 或 [[x,y,z],...]，
          至少 2 个点。
        - 可选 closed（首尾闭合）、width（全局线宽）、layer、color。

        含非零 Z 坐标时自动创建三维多段线（width 对三维线不生效）。
        """
        return bridge.execute(
            "polyline",
            {
                "points": points,
                "closed": closed,
                "width": width,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def ellipse(
        center: Annotated[
            list[float], Field(description="椭圆中心 [x,y]。")
        ],
        major_radius: Annotated[
            float,
            Field(description="长半轴（>0）。"),
        ],
        minor_radius: Annotated[
            float,
            Field(description="短半轴（>0）。"),
        ],
        rotation: float = 0,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制椭圆。单一模式（半轴）：

        - 必填 center + major_radius（长半轴）+ minor_radius（短半轴）。
        - rotation 为旋转角（度，可选）；layer/color 可选。
        """
        return bridge.execute(
            "ellipse",
            {
                "center": center,
                "major_radius": major_radius,
                "minor_radius": minor_radius,
                "rotation": rotation,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def spline(
        points: list[list[float]],
        degree: int = 3,
        closed: bool = False,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制样条曲线。单一模式，无互斥参数：

        - 必填 points：拟合点列表，至少 2 个点。
        - 可选 degree（阶数 1~5，超出自动取 3）、closed（闭合）、
          layer、color。
        """
        return bridge.execute(
            "spline",
            {
                "points": points,
                "degree": degree,
                "closed": closed,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def polygon(
        center: list[float],
        radius: float,
        sides: int = 6,
        rotation: float = 0,
        inscribed: bool = True,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制正多边形（闭合多段线）。单一模式，无互斥参数：

        - 必填 center（中心）+ radius（>0）。
        - 可选 sides（边数 3~1024，默认 6）、rotation（旋转角，度）、
          inscribed（true 内接 / false 外切）、layer、color。
        """
        return bridge.execute(
            "polygon",
            {
                "center": center,
                "radius": radius,
                "sides": sides,
                "rotation": rotation,
                "inscribed": inscribed,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def point(
        position: list[float],
        style: int = -1,
        size: float = -1,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制点。单一模式，无互斥参数：

        - 必填 position（点位置 [x,y]）。
        - 可选 style（PDMODE 点样式，-1 不改动）、size（PDSIZE 点大小，
          -1 不改动）、layer、color。
        """
        return bridge.execute(
            "point",
            {
                "position": position,
                "style": style,
                "size": size,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def draw_text(
        position: list[float],
        text: str,
        height: float = 2.5,
        rotation: float = 0,
        kind: str = "text",
        align: str = "left",
        width: float = 0,
        width_factor: float = 1,
        style: str = "",
        layer: str = "",
        color: str = "",
    ) -> dict:
        """绘制文字。单一模式，无互斥参数：

        - 必填 position（插入点）+ text（内容）。
        - kind: "text" 单行文字（默认）/ "mtext" 多行文字。
        - 可选 height（字高，默认 2.5）、rotation（度）、
          align（对齐，见下）、width（mtext 宽度，>0 且 kind="mtext" 生效）、
          width_factor（宽度因子）、style（文字样式名）、layer、color。

        align 支持：left（默认）/ center / right / middle / top / bottom /
        topleft / topcenter / topright / middleleft / middleright /
        bottomleft / bottomright。
        """
        return bridge.execute(
            "draw_text",
            {
                "position": position,
                "text": text,
                "height": height,
                "rotation": rotation,
                "kind": kind,
                "align": align,
                "width": width,
                "width_factor": width_factor,
                "style": style,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def dimension(
        type: Annotated[
            str,
            Field(
                description="标注类型（必填）：linear / aligned / "
                "angular / radius / diameter。"
                "不同 type 的参数组合不同，见工具说明。"
            ),
        ],
        p1: Annotated[
            list[float] | None,
            Field(
                description="linear/aligned：第一条延伸线原点；"
                "angular 三点式：边 1 端点。各模式含义不同。"
            ),
        ] = None,
        p2: Annotated[
            list[float] | None,
            Field(
                description="linear/aligned：第二条延伸线原点；"
                "angular 三点式：边 2 端点。"
            ),
        ] = None,
        p3: Annotated[
            list[float] | None,
            Field(description="linear/aligned：尺寸线位置点。"),
        ] = None,
        rotation: float = 0,
        center: Annotated[
            list[float] | None,
            Field(description="radius/diameter：圆心。"),
        ] = None,
        chord_point: Annotated[
            list[float] | None,
            Field(description="radius/diameter：圆上点（决定半径）。"),
        ] = None,
        leader_length: float = 0,
        start1: Annotated[
            list[float] | None,
            Field(description="angular 四线式：边 1 起点。四点必须齐全。"),
        ] = None,
        end1: Annotated[
            list[float] | None,
            Field(description="angular 四线式：边 1 终点。"),
        ] = None,
        start2: Annotated[
            list[float] | None,
            Field(description="angular 四线式：边 2 起点。"),
        ] = None,
        end2: Annotated[
            list[float] | None,
            Field(description="angular 四线式：边 2 终点。"),
        ] = None,
        vertex: Annotated[
            list[float] | None,
            Field(description="angular 三点式：角顶点。"),
        ] = None,
        arc_point: Annotated[
            list[float] | None,
            Field(
                description="angular：弧标注位置（可选，缺省沿角平分线偏移）。"
            ),
        ] = None,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """创建尺寸标注。按 type 选择参数组合（互斥）：

        - type="linear"：必填 p1 + p2 + p3；rotation 可选（标注方向角）。
        - type="aligned"：必填 p1 + p2 + p3（p3 为尺寸线位置）。
        - type="angular"：二选一——
          四线式：start1 + end1 + start2 + end2（四点必须齐全）；
          三点式：vertex + p1 + p2（vertex 为角顶点）。
          arc_point 可选，缺省自动沿角平分线偏移。
        - type="radius"：必填 center + chord_point（圆上点）。
        - type="diameter"：必填 center + chord_point。
          leader_length 可选（引线长，<=0 自动取半径 30%）。

        type 必填且必须是上述 5 种之一；layer/color 可选。
        """
        return bridge.execute(
            "dimension",
            {
                "type": type,
                "p1": p1,
                "p2": p2,
                "p3": p3,
                "rotation": rotation,
                "center": center,
                "chord_point": chord_point,
                "leader_length": leader_length,
                "start1": start1,
                "end1": end1,
                "start2": start2,
                "end2": end2,
                "vertex": vertex,
                "arc_point": arc_point,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def leader(
        points: list[list[float]],
        text: str = "",
        height: float = 0,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """创建多重引线（MLeader）。单一模式，无互斥参数：

        - 必填 points：至少 2 个点（第一个点靠近文字侧）。
        - 可选 text（引线文字，默认空）、height（文字高度，>0 生效）、
          layer、color。
        """
        return bridge.execute(
            "leader",
            {
                "points": points,
                "text": text,
                "height": height,
                "layer": layer,
                "color": color,
            },
        )

    @registry.tool()
    def hatch(
        handles: Annotated[
            list[str] | None,
            Field(
                description="【模式A】闭合边界图元 handle 列表（非空）。"
                "与 point 二选一，不要同时提供。"
            ),
        ] = None,
        point: Annotated[
            list[float] | None,
            Field(
                description="【模式B】边界内部点 [x,y]，自动追踪边界。"
                "与 handles 二选一。"
            ),
        ] = None,
        pattern: str = "SOLID",
        scale: float = 1,
        angle: float = 0,
        associative: bool = True,
        layer: str = "",
        color: str = "",
    ) -> dict:
        """创建图案填充。两种互斥的边界指定方式，二选一：

        - 模式A（图元边界）：必填 handles（闭合图元 handle 列表）。
        - 模式B（内部点）：必填 point（内部点，自动追踪边界）。

        两者都未提供时返回错误；handles 非空时优先于 point。
        可选 pattern（默认 "SOLID"，如 "ANSI31"/"HONEY"）、
        scale（图案比例）、angle（图案角度，度）、
        associative（关联填充）、layer、color。
        """
        return bridge.execute(
            "hatch",
            {
                "handles": handles,
                "point": point,
                "pattern": pattern,
                "scale": scale,
                "angle": angle,
                "associative": associative,
                "layer": layer,
                "color": color,
            },
        )
