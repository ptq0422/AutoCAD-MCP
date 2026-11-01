using System;
using System.Collections.Generic;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using static AutoCADMcp.Plugin.ToolContext;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 绘图工具：line / circle / arc / rectangle /
    /// polyline / ellipse / spline / polygon / point。
    /// </summary>
    public static class DrawTools
    {
        public static void Register()
        {
            ToolRegistry.Register("line", LineTool);
            ToolRegistry.Register("circle", CircleTool);
            ToolRegistry.Register("arc", ArcTool);
            ToolRegistry.Register("rectangle", RectangleTool);
            ToolRegistry.Register("polyline", PolylineTool);
            ToolRegistry.Register("ellipse", EllipseTool);
            ToolRegistry.Register("spline", SplineTool);
            ToolRegistry.Register("polygon", PolygonTool);
            ToolRegistry.Register("point", PointTool);
        }

        // ---------------------------------------------------------------
        // 通用：创建实体 + 应用图层/颜色 + 提交并描述
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> Create(
            Document doc, JsonElement args, Entity ent)
        {
            Database db = doc.Database;
            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            if (layer.Length > 0)
                ent.Layer = layer;

            if (color.Length > 0)
                ent.Color = ParseColor(color);

            BlockTableRecord btr =
                GetCurrentSpace(tr, db, true);

            ObjectId id = AddEntity(tr, btr, ent);
            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            var created = (Entity)tr2.GetObject(
                id, OpenMode.ForRead);

            Dictionary<string, object?> payload =
                DescribeEntity(created, true);

            tr2.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // line
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> LineTool(
            Document doc, JsonElement args)
        {
            Point3d start = ReqPoint(args, "start");
            Point3d end = ReqPoint(args, "end");

            return Create(doc, args, new Line(start, end));
        }

        // ---------------------------------------------------------------
        // circle
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> CircleTool(
            Document doc, JsonElement args)
        {
            Point3d center = ReqPoint(args, "center");

            double radius = ReqDouble(args, "radius");

            if (radius <= 0)
                throw new McpException("圆的半径必须大于 0");

            return Create(doc, args, new Circle(
                center, Vector3d.ZAxis, radius));
        }

        // ---------------------------------------------------------------
        // arc
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ArcTool(
            Document doc, JsonElement args)
        {
            // 单一模式：圆心 + 半径 + 起止角。
            Point3d center = ReqPoint(args, "center");
            double radius = ReqDouble(args, "radius");
            double startAngle = ReqDouble(args, "start_angle");
            double endAngle = ReqDouble(args, "end_angle");

            return Create(doc, args, new Arc(
                center, radius,
                Deg(startAngle), Deg(endAngle)));
        }

        // ---------------------------------------------------------------
        // rectangle
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> RectangleTool(
            Document doc, JsonElement args)
        {
            // 单一模式：两个对角点 p1 + p2。
            Point3d p1 = ReqPoint(args, "p1");
            Point3d p2 = ReqPoint(args, "p2");

            double rotation = Deg(OptDouble(
                args, "rotation", 0));

            Polyline rect = new Polyline(4);

            Point2d min = new Point2d(
                Math.Min(p1.X, p2.X),
                Math.Min(p1.Y, p2.Y));

            Point2d max = new Point2d(
                Math.Max(p1.X, p2.X),
                Math.Max(p1.Y, p2.Y));

            Point2d[] corners =
            {
                min,
                new Point2d(max.X, min.Y),
                max,
                new Point2d(min.X, max.Y)
            };

            if (rotation != 0)
            {
                Matrix2d m = Matrix2d.Rotation(
                    rotation, new Point2d(
                        (min.X + max.X) / 2,
                        (min.Y + max.Y) / 2));

                for (int i = 0; i < 4; i++)
                    corners[i] = corners[i].TransformBy(m);
            }

            for (int i = 0; i < 4; i++)
                rect.AddVertexAt(i, corners[i], 0, 0, 0);

            rect.Closed = true;

            return Create(doc, args, rect);
        }

        // ---------------------------------------------------------------
        // polyline
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> PolylineTool(
            Document doc, JsonElement args)
        {
            List<Point3d> points = ReqPoints(args, "points");

            if (points.Count < 2)
                throw new McpException("polyline 至少需要 2 个点");

            bool closed = OptBool(args, "closed", false);
            double width = OptDouble(args, "width", 0);

            bool hasZ = points.Any(p => Math.Abs(p.Z) > Tol);

            Entity ent;

            if (hasZ)
            {
                var p3 = new Polyline3d(
                    Poly3dType.SimplePoly,
                    new Point3dCollection(points.ToArray()),
                    closed);

                ent = p3;
            }
            else
            {
                var pl = new Polyline(points.Count);

                for (int i = 0; i < points.Count; i++)
                {
                    pl.AddVertexAt(i,
                        new Point2d(points[i].X, points[i].Y),
                        0, width, width);
                }

                pl.Closed = closed;
                ent = pl;
            }

            return Create(doc, args, ent);
        }

        // ---------------------------------------------------------------
        // ellipse
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> EllipseTool(
            Document doc, JsonElement args)
        {
            // 单一模式：中心 + 长/短半轴。
            Point3d center = ReqPoint(args, "center");

            double rx = ReqDouble(args, "major_radius");
            double ry = ReqDouble(args, "minor_radius");

            if (rx <= 0 || ry <= 0)
                throw new McpException("椭圆半轴必须大于 0");

            double rotation = Deg(OptDouble(args, "rotation", 0));

            Vector3d axis = new Vector3d(rx, 0, 0);

            if (rotation != 0)
                axis = axis.RotateBy(rotation, Vector3d.ZAxis);

            return Create(doc, args, new Ellipse(
                center, Vector3d.ZAxis, axis,
                ry / rx, 0, Math.PI * 2));
        }

        // ---------------------------------------------------------------
        // spline
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> SplineTool(
            Document doc, JsonElement args)
        {
            List<Point3d> points = ReqPoints(args, "points");

            if (points.Count < 2)
                throw new McpException("spline 至少需要 2 个拟合点");

            int degree = OptInt(args, "degree", 3);

            if (degree < 1 || degree > 5)
                degree = 3;

            var spline = new Spline(
                new Point3dCollection(points.ToArray()),
                degree + 1,
                0.0);

            bool closed = OptBool(args, "closed", false);

            if (closed)
            {
                // 闭合：首尾拟合点重合（AutoCAD 托管 API 无
                // 直接设置闭合的方法，通过补点实现视觉闭合）
                var closedPoints = new Point3dCollection();

                foreach (Point3d p in points)
                    closedPoints.Add(p);

                closedPoints.Add(points[0]);

                spline = new Spline(closedPoints, degree + 1, 0.0);
            }

            return Create(doc, args, spline);
        }

        // ---------------------------------------------------------------
        // polygon
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> PolygonTool(
            Document doc, JsonElement args)
        {
            Point3d center = ReqPoint(args, "center");
            int sides = OptInt(args, "sides", 6);

            if (sides < 3 || sides > 1024)
                throw new McpException("多边形边数应在 3 到 1024 之间");

            double radius = ReqDouble(args, "radius");

            if (radius <= 0)
                throw new McpException("多边形半径必须大于 0");

            double rotation = Deg(OptDouble(args, "rotation", 0));

            // inscribed：顶点在圆上（内接）；否则为外切
            bool inscribed = OptBool(args, "inscribed", true);

            Polyline pl = new Polyline(sides + 1);

            if (!inscribed)
            {
                // 外切多边形：顶点到中心的距离 = r / cos(π/n)
                radius = radius / Math.Cos(Math.PI / sides);
            }

            for (int i = 0; i < sides; i++)
            {
                double a = rotation + 2 * Math.PI * i / sides;

                pl.AddVertexAt(i, new Point2d(
                    center.X + radius * Math.Cos(a),
                    center.Y + radius * Math.Sin(a)), 0, 0, 0);
            }

            pl.Closed = true;

            return Create(doc, args, pl);
        }

        // ---------------------------------------------------------------
        // point
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> PointTool(
            Document doc, JsonElement args)
        {
            Point3d position = ReqPoint(args, "position");

            int style = OptInt(args, "style", -1);

            if (style >= 0)
            {
                try
                {
                    Application.SetSystemVariable("PDMODE", style);
                }
                catch
                {
                    // 某些版本 PDMODE 设置失败可忽略
                }
            }

            double size = OptDouble(args, "size", -1);

            if (size >= 0)
            {
                try
                {
                    Application.SetSystemVariable("PDSIZE", size);
                }
                catch
                {
                    // 忽略
                }
            }

            return Create(doc, args, new DBPoint(position));
        }
    }
}
