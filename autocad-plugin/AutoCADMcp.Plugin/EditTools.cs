using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

using static AutoCADMcp.Plugin.ToolContext;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 编辑工具：transform / delete / offset / trim / extend /
    /// fillet / chamfer / break / stretch / align /
    /// change_properties / erase_by_filter / undo。
    /// </summary>
    public static class EditTools
    {
        public static void Register()
        {
            ToolRegistry.Register("transform", TransformTool);
            ToolRegistry.Register("delete", DeleteTool);
            ToolRegistry.Register("offset", OffsetTool);
            ToolRegistry.Register("trim", TrimTool);
            ToolRegistry.Register("extend", ExtendTool);
            ToolRegistry.Register("fillet", FilletTool);
            ToolRegistry.Register("chamfer", ChamferTool);
            ToolRegistry.Register("break", BreakTool);
            ToolRegistry.Register("stretch", StretchTool);
            ToolRegistry.Register("align", AlignTool);
            ToolRegistry.Register("change_properties", ChangePropertiesTool);
            ToolRegistry.Register("erase_by_filter", EraseByFilterTool);
            ToolRegistry.Register("undo", UndoTool);
        }

        private static Entity OpenWrite(
            Transaction tr, ObjectId id) =>
            (Entity)tr.GetObject(id, OpenMode.ForWrite);

        private static bool InsideRect(
            Point3d p, Point3d a, Point3d b) =>
            p.X >= Math.Min(a.X, b.X) - Tol
                && p.X <= Math.Max(a.X, b.X) + Tol
                && p.Y >= Math.Min(a.Y, b.Y) - Tol
                && p.Y <= Math.Max(a.Y, b.Y) + Tol;

        // ---------------------------------------------------------------
        // transform：move / rotate / scale / mirror / copy
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> TransformTool(
            Document doc, JsonElement args)
        {
            string mode = ReqStr(args, "mode")
                .Trim().ToLowerInvariant();

            bool clone = mode == "copy";

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            Matrix3d m;

            switch (mode)
            {
                case "move":
                case "copy":
                    {
                        Vector3d delta = GetDelta(args);

                        if (delta.IsZeroLength())
                            throw new McpException(
                                "移动距离为 0，请提供 delta 或 from/to");

                        m = Matrix3d.Displacement(delta);
                        break;
                    }

                case "rotate":
                    {
                        Point3d basePoint = ReqPoint(args, "base");
                        double angle = ReqDouble(args, "angle");

                        m = Matrix3d.Rotation(
                            Deg(angle), Vector3d.ZAxis, basePoint);
                        break;
                    }

                case "scale":
                    {
                        Point3d basePoint = ReqPoint(args, "base");
                        double factor = ReqDouble(args, "factor");

                        if (factor <= 0)
                            throw new McpException("缩放因子必须大于 0");

                        m = Matrix3d.Scaling(factor, basePoint);
                        break;
                    }

                case "mirror":
                    {
                        Point3d p1 = ReqPoint(args, "p1");
                        Point3d p2 = ReqPoint(args, "p2");

                        m = Matrix3d.Mirroring(new Line3d(p1, p2));
                        break;
                    }

                default:
                    throw new McpException(
                        "transform mode 支持：" +
                        "move / rotate / scale / mirror / copy");
            }

            var changed = new List<string>();
            var created = new List<string>();

            if (clone)
            {
                int count = Math.Max(1, OptInt(args, "count", 1));

                BlockTableRecord btr =
                    GetCurrentSpace(tr, doc.Database, true);

                Matrix3d current = m;

                for (int i = 0; i < count; i++)
                {
                    foreach (ObjectId id in ids)
                    {
                        var source = (Entity)tr.GetObject(
                            id, OpenMode.ForRead);

                        Entity copy = (Entity)source.Clone();
                        copy.TransformBy(current);

                        ObjectId newId = AddEntity(tr, btr, copy);
                        created.Add(newId.ToString());
                    }

                    current = m * current;
                }
            }
            else
            {
                foreach (ObjectId id in ids)
                {
                    Entity ent = OpenWrite(tr, id);
                    ent.TransformBy(m);
                    changed.Add(ent.Handle.ToString());
                }
            }

            tr.Commit();

            return Result(
                "mode", mode,
                "changed", changed,
                "created", created,
                "count", clone ? created.Count : changed.Count);
        }

        private static Vector3d GetDelta(JsonElement args)
        {
            // delta 支持数组 [dx, dy] 或字符串 "dx,dy,dz"
            if (Has(args, "delta"))
            {
                Point3d dv = OptPoint(args, "delta", default);
                if (dv.X != 0 || dv.Y != 0 || dv.Z != 0)
                    return new Vector3d(dv.X, dv.Y, dv.Z);

                string delta = OptStr(args, "delta", "");
                if (delta.Length > 0)
                {
                    Point3d v = ParsePoint3d(delta);
                    return new Vector3d(v.X, v.Y, v.Z);
                }
            }

            Point3d from = OptPoint(args, "from", default);
            Point3d to = OptPoint(args, "to", default);

            // from/to 可以是 [0,0]，按参数是否提供判定。
            if (Has(args, "from") && Has(args, "to"))
                return to - from;

            return new Vector3d(
                OptDouble(args, "dx", 0),
                OptDouble(args, "dy", 0),
                OptDouble(args, "dz", 0));
        }

        // ---------------------------------------------------------------
        // delete
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DeleteTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            var handles = new List<string>();

            foreach (ObjectId id in ids)
            {
                Entity ent = OpenWrite(tr, id);
                handles.Add(ent.Handle.ToString());
                ent.Erase();
            }

            tr.Commit();

            return Result(
                "deleted", handles,
                "count", handles.Count);
        }

        // ---------------------------------------------------------------
        // offset
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> OffsetTool(
            Document doc, JsonElement args)
        {
            double distance = ReqDouble(args, "distance");

            if (Math.Abs(distance) < Tol)
                throw new McpException("偏移距离不能为 0");

            string side = OptStr(args, "side", "left")
                .Trim().ToLowerInvariant();

            // 正值：沿曲线方向左侧偏移；负值：右侧
            double signed = side == "right"
                ? -Math.Abs(distance)
                : Math.Abs(distance);

            string layer = OptStr(args, "layer", "");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何曲线");

            if (layer.Length > 0)
                EnsureLayer(tr, doc.Database, layer);

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var created = new List<string>();
            var errors = new List<string>();

            foreach (ObjectId id in ids)
            {
                var source = (Entity)tr.GetObject(
                    id, OpenMode.ForRead);

                if (source is not Curve curve)
                {
                    errors.Add($"{source.Handle}: 不是曲线");
                    continue;
                }

                try
                {
                    DBObjectCollection offsets =
                        curve.GetOffsetCurves(signed);

                    foreach (DBObject obj in offsets)
                    {
                        if (obj is not Entity ent)
                            continue;

                        if (layer.Length > 0)
                            ent.Layer = layer;
                        else
                            ent.SetPropertiesFrom(source);

                        ObjectId newId = AddEntity(tr, btr, ent);
                        created.Add(newId.ToString());
                    }
                }
                catch (System.Exception ex)
                {
                    errors.Add($"{source.Handle}: {ex.Message}");
                }
            }

            tr.Commit();

            return Result(
                "created", created,
                "count", created.Count,
                "errors", errors);
        }

        // ---------------------------------------------------------------
        // trim
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> TrimTool(
            Document doc, JsonElement args)
        {
            string targetHandle = ReqStr(args, "target");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var target = GetEntityByHandle(
                tr, doc.Database, targetHandle,
                OpenMode.ForWrite) as Curve;

            if (target == null)
                throw new McpException(
                    $"目标不是曲线：{targetHandle}");

            List<ObjectId> cutterIds;

            if (args.TryGetProperty("cutting", out JsonElement cv))
                cutterIds = ResolveSpec(cv, doc, tr);
            else
                cutterIds = ResolveSelection(args, doc, tr);

            if (cutterIds.Count == 0)
                throw new McpException("未指定裁剪边界");

            var points = new List<Point3d>();

            foreach (ObjectId id in cutterIds)
            {
                if (id == target.ObjectId)
                    continue;

                var cutter = (Entity)tr.GetObject(
                    id, OpenMode.ForRead);

                points.AddRange(Intersections(target, cutter));
            }

            // 投影到曲线上并去重、过滤端点
            var projected = new List<Point3d>();

            foreach (Point3d p in points)
            {
                Point3d on = target.GetClosestPointTo(p, false);

                if (on.DistanceTo(target.StartPoint) < Tol
                    || on.DistanceTo(target.EndPoint) < Tol)
                    continue;

                bool dup = projected.Any(
                    q => q.DistanceTo(on) < Tol);

                if (!dup)
                    projected.Add(on);
            }

            if (projected.Count == 0)
                throw new McpException("未找到交点");

            projected.Sort((a, b) =>
                target.GetDistAtPoint(a)
                    .CompareTo(target.GetDistAtPoint(b)));

            DBObjectCollection pieces = target.GetSplitCurves(
                new Point3dCollection(projected.ToArray()));

            if (pieces.Count == 0)
                throw new McpException("裁剪失败");

            string keep = OptStr(args, "keep", "point")
                .Trim().ToLowerInvariant();

            Point3d keepPoint = OptPoint(args, "point", default);

            Curve chosen = PickPiece(
                pieces, keep, keepPoint, target);

            chosen.SetPropertiesFrom(target);
            target.Erase();

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            ObjectId newId = AddEntity(tr, btr, chosen);
            tr.Commit();

            return Result(
                "handle", chosen.Handle.ToString(),
                "message", "裁剪完成",
                "intersections", projected.Count);
        }

        private static Curve PickPiece(
            DBObjectCollection pieces, string keep,
            Point3d keepPoint, Curve original)
        {
            var curves = pieces.Cast<Curve>().ToList();

            if (keep == "start")
            {
                return curves.OrderBy(c =>
                    c.StartPoint.DistanceTo(original.StartPoint))
                    .First();
            }

            if (keep == "end")
            {
                return curves.OrderBy(c =>
                    c.EndPoint.DistanceTo(original.EndPoint))
                    .First();
            }

            return curves.OrderBy(c =>
                c.GetClosestPointTo(keepPoint, false)
                    .DistanceTo(keepPoint))
                .First();
        }

        // ---------------------------------------------------------------
        // extend
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ExtendTool(
            Document doc, JsonElement args)
        {
            string targetHandle = ReqStr(args, "target");

            string which = OptStr(args, "which", "both")
                .Trim().ToLowerInvariant();

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var target = GetEntityByHandle(
                tr, doc.Database, targetHandle,
                OpenMode.ForWrite) as Curve;

            if (target == null)
                throw new McpException(
                    $"目标不是曲线：{targetHandle}");

            List<ObjectId> boundaryIds;

            if (args.TryGetProperty("boundary", out JsonElement bv))
                boundaryIds = ResolveSpec(bv, doc, tr);
            else
                throw new McpException("extend 需要 boundary 参数");

            if (boundaryIds.Count == 0)
                throw new McpException("未指定延伸边界");

            var boundaries = boundaryIds
                .Select(id => (Entity)tr.GetObject(
                    id, OpenMode.ForRead))
                .ToList();

            bool extendStart = which is "start" or "both";
            bool extendEnd = which is "end" or "both";

            switch (target)
            {
                case Line line:
                    {
                        Vector3d d = line.EndPoint
                            - line.StartPoint;

                        if (d.Length < Tol)
                            throw new McpException("直线长度为零");

                        d = d.GetNormal();

                        double len = line.Length;
                        double probe = len * 100 + 1000;

                        var probeLine = new Line(
                            line.StartPoint - d * probe,
                            line.EndPoint + d * probe);

                        if (extendStart)
                        {
                            Point3d? best = BestExtendPoint(
                                probeLine, boundaries,
                                line.StartPoint, d,
                                line.Length, true);

                            if (best != null)
                                line.StartPoint = best.Value;
                        }

                        if (extendEnd)
                        {
                            Point3d? best = BestExtendPoint(
                                probeLine, boundaries,
                                line.StartPoint, d,
                                line.Length, false);

                            if (best != null)
                                line.EndPoint = best.Value;
                        }

                        break;
                    }

                case Arc arc:
                    {
                        var probe = new Circle(
                            arc.Center, arc.Normal, arc.Radius);

                        var candidates = new List<Point3d>();

                        foreach (Entity b in boundaries)
                            candidates.AddRange(
                                Intersections(probe, b));

                        if (extendStart)
                        {
                            double? a = BestAngle(
                                candidates, arc.Center,
                                arc.StartAngle, arc.EndAngle, false);

                            if (a != null)
                                arc.StartAngle = a.Value;
                        }

                        if (extendEnd)
                        {
                            double? a = BestAngle(
                                candidates, arc.Center,
                                arc.StartAngle, arc.EndAngle, true);

                            if (a != null)
                                arc.EndAngle = a.Value;
                        }

                        break;
                    }

                case Polyline pl:
                    {
                        if (pl.NumberOfVertices < 2)
                            throw new McpException("折线顶点不足");

                        ExtendPolyline(
                            pl, boundaries, extendStart, extendEnd);

                        break;
                    }

                default:
                    throw new McpException(
                        "extend 目前支持 Line / Arc / Polyline");
            }

            tr.Commit();

            return Result(
                "handle", target.Handle.ToString(),
                "message", "延伸完成");
        }

        private static Point3d? BestExtendPoint(
            Line probe, List<Entity> boundaries,
            Point3d anchor, Vector3d dir,
            double limit, bool extendStart)
        {
            var candidates = new List<Point3d>();

            foreach (Entity b in boundaries)
                candidates.AddRange(Intersections(probe, b));

            Point3d? best = null;
            double bestT = extendStart
                ? double.MinValue
                : double.MaxValue;

            foreach (Point3d p in candidates)
            {
                double t = (p - anchor).DotProduct(dir);

                if (extendStart)
                {
                    // 起点延伸：交点须在起点外侧（t < 0）
                    if (t >= -Tol)
                        continue;

                    if (t > bestT)
                    {
                        bestT = t;
                        best = p;
                    }
                }
                else
                {
                    // 终点延伸：交点须在原终点外侧
                    if (t <= limit + Tol)
                        continue;

                    if (t < bestT)
                    {
                        bestT = t;
                        best = p;
                    }
                }
            }

            return best;
        }

        private static double? BestAngle(
            List<Point3d> candidates, Point3d center,
            double startAngle, double endAngle,
            bool extendToEnd)
        {
            double? best = null;
            double bestDiff = double.MaxValue;

            foreach (Point3d p in candidates)
            {
                double a = Math.Atan2(
                    p.Y - center.Y, p.X - center.X);

                double rel = NormalizeAngle(a - startAngle);
                double total = NormalizeAngle(
                    endAngle - startAngle);

                if (extendToEnd)
                {
                    // 终点延伸：候选点须在终点外侧（CCW 超过终点）
                    if (rel <= total + Tol)
                        continue;

                    double diff = rel - total;

                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = a;
                    }
                }
                else
                {
                    // 起点延伸：候选点须在起点外侧（沿 CW 方向）
                    if (rel <= total + Tol)
                        continue;

                    double diff = Math.PI * 2 - rel;

                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = a;
                    }
                }
            }

            return best;
        }

        private static double NormalizeAngle(double a)
        {
            while (a < 0)
                a += Math.PI * 2;

            while (a >= Math.PI * 2)
                a -= Math.PI * 2;

            return a;
        }

        private static void ExtendPolyline(
            Polyline pl, List<Entity> boundaries,
            bool extendStart, bool extendEnd)
        {
            if (extendStart)
            {
                Point3d p0 = pl.GetPoint3dAt(0);
                Point3d p1 = pl.GetPoint3dAt(1);
                Vector3d d = (p0 - p1).GetNormal();

                var probe = new Line(
                    p1 - d * 10000, p0 + d * 10000);

                Point3d? best = BestExtendPoint(
                    probe, boundaries, p0, d, 0, true);

                if (best != null)
                    pl.SetPointAt(0, new Point2d(
                        best.Value.X, best.Value.Y));
            }

            if (extendEnd)
            {
                int n = pl.NumberOfVertices;
                Point3d p0 = pl.GetPoint3dAt(n - 2);
                Point3d p1 = pl.GetPoint3dAt(n - 1);
                Vector3d d = (p1 - p0).GetNormal();

                var probe = new Line(
                    p0 - d * 10000, p1 + d * 10000);

                Point3d? best = BestExtendPoint(
                    probe, boundaries, p0, d,
                    p0.DistanceTo(p1), false);

                if (best != null)
                    pl.SetPointAt(n - 1, new Point2d(
                        best.Value.X, best.Value.Y));
            }
        }

        // ---------------------------------------------------------------
        // fillet（直线-直线 与 折线顶点）
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> FilletTool(
            Document doc, JsonElement args)
        {
            double radius = ReqDouble(args, "radius");

            if (radius <= 0)
                throw new McpException("圆角半径必须大于 0");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            string h1 = OptStr(args, "e1", "");
            string h2 = OptStr(args, "e2", "");

            if (h1.Length > 0 && h1 == h2)
            {
                // 折线顶点倒圆角
                var pl = GetEntityByHandle(
                    tr, doc.Database, h1,
                    OpenMode.ForWrite) as Polyline;

                if (pl == null)
                    throw new McpException("仅折线支持顶点倒圆角");

                int vertex = OptInt(args, "vertex", -1);

                if (vertex < 0)
                    throw new McpException("请指定 vertex 顶点索引");

                Point3d ft1, ft2, fc;
                double sweep;
                FilletPolylineVertex(pl, vertex, radius,
                    out ft1, out ft2, out fc, out sweep);

                tr.Commit();

                return Result(
                    "handle", pl.Handle.ToString(),
                    "vertex", vertex,
                    "tangent1", Pt(ft1),
                    "tangent2", Pt(ft2));
            }

            if (h1.Length == 0 || h2.Length == 0)
                throw new McpException("fillet 需要 e1 和 e2 两个直线 handle");

            var l1 = GetEntityByHandle(
                tr, doc.Database, h1,
                OpenMode.ForWrite) as Line;

            var l2 = GetEntityByHandle(
                tr, doc.Database, h2,
                OpenMode.ForWrite) as Line;

            if (l1 == null || l2 == null)
                throw new McpException("倒圆角目前仅支持两条直线");

            Point3d t1, t2, center;
            double sweepAngle;
            ComputeFillet(l1, l2, radius,
                out t1, out t2, out center, out sweepAngle);

            // 修剪直线端点
            TrimLineTo(l1, t1);
            TrimLineTo(l2, t2);

            Arc arc = sweepAngle >= 0
                ? new Arc(center, radius,
                    Math.Atan2(t1.Y - center.Y, t1.X - center.X),
                    Math.Atan2(t2.Y - center.Y, t2.X - center.X))
                : new Arc(center, radius,
                    Math.Atan2(t2.Y - center.Y, t2.X - center.X),
                    Math.Atan2(t1.Y - center.Y, t1.X - center.X));

            arc.SetPropertiesFrom(l1);

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            ObjectId arcId = AddEntity(tr, btr, arc);
            tr.Commit();

            return Result(
                "handle", arc.Handle.ToString(),
                "tangent1", Pt(t1),
                "tangent2", Pt(t2),
                "center", Pt(center));
        }

        private static void FilletPolylineVertex(
            Polyline pl, int vertex, double radius,
            out Point3d t1, out Point3d t2,
            out Point3d center, out double sweep)
        {
            int n = pl.NumberOfVertices;

            if (vertex <= 0 || vertex >= n - 1)
                throw new McpException(
                    $"顶点索引须在 1 到 {n - 2} 之间");

            Point3d p = pl.GetPoint3dAt(vertex);
            Point3d pPrev = pl.GetPoint3dAt(vertex - 1);
            Point3d pNext = pl.GetPoint3dAt(vertex + 1);

            var l1 = new Line(pPrev, p);
            var l2 = new Line(p, pNext);

            ComputeFillet(l1, l2, radius,
                out t1, out t2, out center, out sweep);

            pl.SetPointAt(vertex, new Point2d(t1.X, t1.Y));
            pl.AddVertexAt(vertex + 1,
                new Point2d(t2.X, t2.Y), 0, 0, 0);

            // 凸度把直段变成圆弧
            pl.SetBulgeAt(vertex, Math.Tan(sweep / 4));
        }

        /// <summary>计算两条直线之间的圆角（含切点与圆心）。</summary>
        private static void ComputeFillet(
            Line l1, Line l2, double radius,
            out Point3d t1, out Point3d t2,
            out Point3d center, out double sweep)
        {
            var g1 = new Line3d(l1.StartPoint, l1.EndPoint);
            var g2 = new Line3d(l2.StartPoint, l2.EndPoint);

            Point3d[] pts = g1.IntersectWith(g2);

            if (pts.Length == 0)
                throw new McpException("两条直线平行，无法倒圆角");

            Point3d p = pts[0];

            Vector3d u1 = FarDirection(l1, p);
            Vector3d u2 = FarDirection(l2, p);

            double cosA = Math.Max(-1,
                Math.Min(1, u1.DotProduct(u2)));

            double theta = Math.Acos(cosA);

            if (theta < Tol || Math.Abs(theta - Math.PI) < Tol)
                throw new McpException("两条直线平行，无法倒圆角");

            double dist = radius / Math.Tan(theta / 2);

            t1 = p + u1 * dist;
            t2 = p + u2 * dist;

            // 垂直方向：Z 轴（2D 平面）
            Vector3d n1 = Vector3d.ZAxis.CrossProduct(u1);

            center = t1 + n1 * radius;

            // 若圆心到第二条线距离不等于半径，则换另一侧
            double d2 = Math.Abs(
                (center - l2.StartPoint).DotProduct(
                    Vector3d.ZAxis.CrossProduct(u2)));

            if (Math.Abs(d2 - radius) > radius * 0.01 + 0.1)
                center = t1 - n1 * radius;

            double a1 = Math.Atan2(
                t1.Y - center.Y, t1.X - center.X);

            double a2 = Math.Atan2(
                t2.Y - center.Y, t2.X - center.X);

            sweep = a2 - a1;

            while (sweep > Math.PI)
                sweep -= Math.PI * 2;

            while (sweep < -Math.PI)
                sweep += Math.PI * 2;
        }

        private static Vector3d FarDirection(Line line, Point3d p)
        {
            Vector3d v = line.EndPoint - line.StartPoint;

            if (v.Length < Tol)
                throw new McpException("直线长度为零");

            double t = (p - line.StartPoint)
                .DotProduct(v) / v.LengthSqrd;

            Vector3d d = v.GetNormal();

            return t > 0.5 ? -d : d;
        }

        private static void TrimLineTo(Line line, Point3d t)
        {
            if (line.StartPoint.DistanceTo(t)
                < line.EndPoint.DistanceTo(t))
                line.StartPoint = t;
            else
                line.EndPoint = t;
        }

        // ---------------------------------------------------------------
        // chamfer
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ChamferTool(
            Document doc, JsonElement args)
        {
            double d1 = OptDouble(args, "distance1", 0);
            double d2 = OptDouble(args, "distance2", d1);

            if (d1 < 0 || d2 < 0)
                throw new McpException("倒角距离不能为负");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            string h1 = OptStr(args, "e1", "");
            string h2 = OptStr(args, "e2", "");

            if (h1.Length > 0 && h1 == h2)
            {
                var pl = GetEntityByHandle(
                    tr, doc.Database, h1,
                    OpenMode.ForWrite) as Polyline;

                if (pl == null)
                    throw new McpException("仅折线支持顶点倒角");

                int vertex = OptInt(args, "vertex", -1);

                if (vertex < 0)
                    throw new McpException("请指定 vertex 顶点索引");

                int n = pl.NumberOfVertices;

                if (vertex <= 0 || vertex >= n - 1)
                    throw new McpException(
                        $"顶点索引须在 1 到 {n - 2} 之间");

                Point3d p = pl.GetPoint3dAt(vertex);
                Point3d pPrev = pl.GetPoint3dAt(vertex - 1);
                Point3d pNext = pl.GetPoint3dAt(vertex + 1);

                var line1 = new Line(pPrev, p);
                var line2 = new Line(p, pNext);

                Point3d ct1, ct2;
                ComputeChamfer(line1, line2,
                    d1, d2, out ct1, out ct2);

                pl.SetPointAt(vertex, new Point2d(ct1.X, ct1.Y));
                pl.AddVertexAt(vertex + 1,
                    new Point2d(ct2.X, ct2.Y), 0, 0, 0);

                tr.Commit();

                return Result(
                    "handle", pl.Handle.ToString(),
                    "vertex", vertex,
                    "chamfer1", Pt(ct1),
                    "chamfer2", Pt(ct2));
            }

            if (h1.Length == 0 || h2.Length == 0)
                throw new McpException("chamfer 需要 e1 和 e2 两个直线 handle");

            var l1 = GetEntityByHandle(
                tr, doc.Database, h1,
                OpenMode.ForWrite) as Line;

            var l2 = GetEntityByHandle(
                tr, doc.Database, h2,
                OpenMode.ForWrite) as Line;

            if (l1 == null || l2 == null)
                throw new McpException("倒角目前仅支持两条直线");

            Point3d t1, t2;
            ComputeChamfer(l1, l2, d1, d2, out t1, out t2);

            TrimLineTo(l1, t1);
            TrimLineTo(l2, t2);

            var chamferLine = new Line(t1, t2);
            chamferLine.SetPropertiesFrom(l1);

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            ObjectId newId = AddEntity(tr, btr, chamferLine);
            tr.Commit();

            return Result(
                "handle", chamferLine.Handle.ToString(),
                "chamfer1", Pt(t1),
                "chamfer2", Pt(t2));
        }

        private static void ComputeChamfer(
            Line l1, Line l2, double d1, double d2,
            out Point3d t1, out Point3d t2)
        {
            var g1 = new Line3d(l1.StartPoint, l1.EndPoint);
            var g2 = new Line3d(l2.StartPoint, l2.EndPoint);

            Point3d[] pts = g1.IntersectWith(g2);

            if (pts.Length == 0)
                throw new McpException("两条直线平行，无法倒角");

            Point3d p = pts[0];

            Vector3d u1 = FarDirection(l1, p);
            Vector3d u2 = FarDirection(l2, p);

            t1 = p + u1 * d1;
            t2 = p + u2 * d2;
        }

        // ---------------------------------------------------------------
        // break：在 1~2 个点处打断曲线
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> BreakTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");
            Point3d p1 = ReqPoint(args, "point1");
            Point3d p2 = OptPoint(args, "point2", default);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var curve = GetEntityByHandle(
                tr, doc.Database, handle,
                OpenMode.ForWrite) as Curve;

            if (curve == null)
                throw new McpException($"不是曲线：{handle}");

            var breakPoints = new List<Point3d>
            {
                curve.GetClosestPointTo(p1, false)
            };

            // point2 可以是 [0,0]，按参数是否提供判定。
            if (Has(args, "point2"))
                breakPoints.Add(
                    curve.GetClosestPointTo(p2, false));

            var valid = breakPoints
                .Where(p => p.DistanceTo(curve.StartPoint) > Tol
                    && p.DistanceTo(curve.EndPoint) > Tol)
                .Distinct()
                .ToList();

            valid.Sort((a, b) =>
                curve.GetDistAtPoint(a)
                    .CompareTo(curve.GetDistAtPoint(b)));

            if (valid.Count == 0)
                throw new McpException("打断点位于曲线端点，无法打断");

            DBObjectCollection pieces = curve.GetSplitCurves(
                new Point3dCollection(valid.ToArray()));

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var kept = new List<string>();

            // 一个点：保留两段；两个点：删除中间段，保留首尾
            for (int i = 0; i < pieces.Count; i++)
            {
                if (valid.Count == 2 && i == 1)
                    continue;

                var piece = (Curve)pieces[i];
                piece.SetPropertiesFrom(curve);

                ObjectId id = AddEntity(tr, btr, piece);
                kept.Add(piece.Handle.ToString());
            }

            curve.Erase();
            tr.Commit();

            return Result(
                "created", kept,
                "removed", handle,
                "count", kept.Count);
        }

        // ---------------------------------------------------------------
        // stretch
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> StretchTool(
            Document doc, JsonElement args)
        {
            Point3d p1 = ReqPoint(args, "p1");
            Point3d p2 = ReqPoint(args, "p2");

            Vector3d delta = GetDelta(args);

            if (delta.IsZeroLength())
                throw new McpException(
                    "拉伸位移为 0，请提供 delta 或 from/to");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            PromptSelectionResult res =
                doc.Editor.SelectCrossingWindow(p1, p2);

            if (res.Status != PromptStatus.OK)
                throw new McpException("窗口内未选中任何图元");

            var changed = new List<string>();

            foreach (ObjectId id in res.Value.GetObjectIds())
            {
                if (id.IsNull || id.IsErased)
                    continue;

                Entity ent = OpenWrite(tr, id);
                bool moved = false;

                switch (ent)
                {
                    case Line line:
                        if (InsideRect(line.StartPoint, p1, p2))
                        {
                            line.StartPoint += delta;
                            moved = true;
                        }

                        if (InsideRect(line.EndPoint, p1, p2))
                        {
                            line.EndPoint += delta;
                            moved = true;
                        }

                        break;

                    case Polyline pl:
                        for (int i = 0;
                            i < pl.NumberOfVertices; i++)
                        {
                            Point3d v = pl.GetPoint3dAt(i);

                            if (InsideRect(v, p1, p2))
                            {
                                pl.SetPointAt(i, new Point2d(
                                    v.X + delta.X,
                                    v.Y + delta.Y));
                                moved = true;
                            }
                        }

                        break;

                    case Circle c:
                        if (InsideRect(c.Center, p1, p2))
                        {
                            c.Center += delta;
                            moved = true;
                        }

                        break;

                    case Arc arc:
                        {
                            bool centerMoved = false;

                            if (InsideRect(arc.Center, p1, p2))
                            {
                                arc.Center += delta;
                                centerMoved = true;
                            }

                            bool startMoved = false;
                            bool endMoved = false;

                            if (InsideRect(arc.StartPoint, p1, p2))
                            {
                                arc.StartAngle = Math.Atan2(
                                    arc.StartPoint.Y - arc.Center.Y,
                                    arc.StartPoint.X - arc.Center.X);
                                startMoved = true;
                            }

                            if (InsideRect(arc.EndPoint, p1, p2))
                            {
                                arc.EndAngle = Math.Atan2(
                                    arc.EndPoint.Y - arc.Center.Y,
                                    arc.EndPoint.X - arc.Center.X);
                                endMoved = true;
                            }

                            if (endMoved
                                && arc.EndAngle <= arc.StartAngle)
                                arc.EndAngle += Math.PI * 2;

                            moved = centerMoved
                                || startMoved || endMoved;

                            break;
                        }

                    case DBPoint dp:
                        if (InsideRect(dp.Position, p1, p2))
                        {
                            dp.Position += delta;
                            moved = true;
                        }

                        break;

                    case DBText txt:
                        if (InsideRect(txt.Position, p1, p2))
                        {
                            txt.Position += delta;
                            moved = true;
                        }

                        break;

                    case MText mt:
                        if (InsideRect(mt.Location, p1, p2))
                        {
                            mt.Location += delta;
                            moved = true;
                        }

                        break;

                    case BlockReference br:
                        if (InsideRect(br.Position, p1, p2))
                        {
                            br.Position += delta;
                            moved = true;
                        }

                        break;

                    case Ellipse el:
                        if (InsideRect(el.Center, p1, p2))
                        {
                            el.Center += delta;
                            moved = true;
                        }

                        break;
                }

                if (moved)
                    changed.Add(ent.Handle.ToString());
            }

            tr.Commit();

            return Result(
                "changed", changed,
                "count", changed.Count);
        }

        // ---------------------------------------------------------------
        // align：2~3 组对应点对齐（移动+旋转，可选缩放）
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> AlignTool(
            Document doc, JsonElement args)
        {
            List<Point3d> src = ReqPoints(args, "src");
            List<Point3d> dst = ReqPoints(args, "dst");

            if (src.Count < 2 || dst.Count < 2)
                throw new McpException("align 至少需要 2 组对应点");

            bool scale = OptBool(args, "scale", false);

            int n = Math.Min(src.Count, dst.Count);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            // 平移 + 在 XY 平面内旋转 src1->src2 对齐到 dst1->dst2
            Matrix3d m = Matrix3d.Displacement(dst[0] - src[0]);

            Vector3d v1 = src[1] - src[0];
            Vector3d w1 = dst[1] - dst[0];

            double ang = Math.Atan2(
                v1.X * w1.Y - v1.Y * w1.X,
                v1.X * w1.X + v1.Y * w1.Y);

            m = Matrix3d.Rotation(
                ang, Vector3d.ZAxis, dst[0]) * m;

            if (scale && v1.Length > Tol && w1.Length > Tol)
                m = Matrix3d.Scaling(
                    w1.Length / v1.Length, dst[0]) * m;

            if (n >= 3)
            {
                // 绕 dst1->dst2 轴旋转，使第三点共面
                Vector3d axis =
                    (dst[1] - dst[0]).GetNormal();

                Point3d s3 = src[2].TransformBy(m);

                Vector3d s3v = s3 - dst[0];
                Vector3d d3v = dst[2] - dst[0];

                Vector3d s3p = s3v
                    - axis * s3v.DotProduct(axis);

                Vector3d d3p = d3v
                    - axis * d3v.DotProduct(axis);

                if (s3p.Length > Tol && d3p.Length > Tol)
                {
                    double ang2 = Math.Atan2(
                        s3p.CrossProduct(d3p).DotProduct(axis),
                        s3p.DotProduct(d3p));

                    m = Matrix3d.Rotation(
                        ang2, axis, dst[0]) * m;
                }
            }

            var changed = new List<string>();

            foreach (ObjectId id in ids)
            {
                Entity ent = OpenWrite(tr, id);
                ent.TransformBy(m);
                changed.Add(ent.Handle.ToString());
            }

            tr.Commit();

            return Result(
                "changed", changed,
                "count", changed.Count,
                "message", "对齐完成");
        }

        // ---------------------------------------------------------------
        // change_properties
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ChangePropertiesTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");
            string linetype = OptStr(args, "linetype", "");

            bool hasScale = args.TryGetProperty(
                "linetype_scale", out JsonElement sv)
                && sv.ValueKind == JsonValueKind.Number;

            double scale = OptDouble(args, "linetype_scale", 1);

            bool hasVisible = args.TryGetProperty(
                "visible", out _);

            bool visible = OptBool(args, "visible", true);

            if (layer.Length > 0)
                EnsureLayer(tr, doc.Database, layer);

            ObjectId ltId = ObjectId.Null;

            if (linetype.Length > 0)
                ltId = EnsureLinetype(tr, doc.Database, linetype);

            var changed = new List<string>();

            foreach (ObjectId id in ids)
            {
                Entity ent = OpenWrite(tr, id);

                if (layer.Length > 0)
                    ent.Layer = layer;

                if (color.Length > 0)
                    ent.Color = ParseColor(color);

                if (linetype.Length > 0)
                    ent.LinetypeId = ltId;

                if (hasScale)
                    ent.LinetypeScale = scale;

                if (hasVisible)
                    ent.Visible = visible;

                changed.Add(ent.Handle.ToString());
            }

            tr.Commit();

            return Result(
                "changed", changed,
                "count", changed.Count);
        }

        // ---------------------------------------------------------------
        // erase_by_filter
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> EraseByFilterTool(
            Document doc, JsonElement args)
        {
            string space = OptStr(args, "space", "current");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ScanSpace(
                tr, doc.Database, space, args);

            var erased = new List<string>();

            foreach (ObjectId id in ids)
            {
                Entity ent = OpenWrite(tr, id);
                erased.Add(ent.Handle.ToString());
                ent.Erase();
            }

            tr.Commit();

            return Result(
                "erased", erased,
                "count", erased.Count);
        }

        // ---------------------------------------------------------------
        // undo
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> UndoTool(
            Document doc, JsonElement args)
        {
            doc.SendStringToExecute(
                "_.U\n", true, false, false);

            return Result(
                "message", "撤销命令已提交，将撤销最近一次操作");
        }
    }
}
