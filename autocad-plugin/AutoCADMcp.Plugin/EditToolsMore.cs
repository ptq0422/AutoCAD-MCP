using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using static AutoCADMcp.Plugin.ToolContext;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 编辑工具（续）：explode / join_entity /
    /// array / array_rect / array_polar。
    /// </summary>
    public static class EditToolsMore
    {
        public static void Register()
        {
            ToolRegistry.Register("explode", ExplodeTool);
            ToolRegistry.Register("join_entity", JoinEntityTool);
            ToolRegistry.Register("array", ArrayTool);
            ToolRegistry.Register("array_rect", ArrayRectTool);
            ToolRegistry.Register("array_polar", ArrayPolarTool);
        }

        // ---------------------------------------------------------------
        // explode
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ExplodeTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var created = new List<string>();
            var errors = new List<string>();

            foreach (ObjectId id in ids)
            {
                var source = (Entity)tr.GetObject(
                    id, OpenMode.ForWrite);

                List<Entity> pieces;

                try
                {
                    pieces = ExplodeEntity(source);
                }
                catch (System.Exception ex)
                {
                    errors.Add($"{source.Handle}: {ex.Message}");
                    continue;
                }

                foreach (Entity piece in pieces)
                {
                    piece.SetPropertiesFrom(source);
                    ObjectId newId = AddEntity(tr, btr, piece);
                    created.Add(piece.Handle.ToString());
                }

                source.Erase();
            }

            tr.Commit();

            return Result(
                "created", created,
                "count", created.Count,
                "errors", errors);
        }

        private static List<Entity> ExplodeEntity(Entity source)
        {
            var pieces = new List<Entity>();

            switch (source)
            {
                case BlockReference br:
                    {
                        var objs = new DBObjectCollection();
                        br.Explode(objs);

                        pieces.AddRange(objs.Cast<Entity>());
                        break;
                    }

                case Polyline pl:
                    {
                        int n = pl.NumberOfVertices;

                        for (int i = 0; i < n - 1; i++)
                            pieces.Add(SegmentEntity(
                                pl.GetPoint2dAt(i),
                                pl.GetPoint2dAt(i + 1),
                                pl.GetBulgeAt(i)));

                        if (pl.Closed)
                        {
                            pieces.Add(SegmentEntity(
                                pl.GetPoint2dAt(n - 1),
                                pl.GetPoint2dAt(0),
                                pl.GetBulgeAt(n - 1)));
                        }

                        break;
                    }

                default:
                    throw new McpException(
                        $"该类型无法分解：{DxfName(source)}");
            }

            return pieces;
        }

        /// <summary>把折线段（含凸度）转换为直线或圆弧。</summary>
        private static Entity SegmentEntity(
            Point2d p1, Point2d p2, double bulge)
        {
            if (Math.Abs(bulge) < 1e-6)
            {
                return new Line(
                    new Point3d(p1.X, p1.Y, 0),
                    new Point3d(p2.X, p2.Y, 0));
            }

            double theta = 4 * Math.Atan(bulge);

            if (Math.Abs(theta) >= Math.PI * 2 - Tol)
                throw new McpException("凸度对应整圆，无法分解");

            Point3d a = new Point3d(p1.X, p1.Y, 0);
            Point3d b = new Point3d(p2.X, p2.Y, 0);

            Vector3d chord = b - a;
            double len = chord.Length;

            if (len < Tol)
                throw new McpException("折线段长度为零");

            double radius = len
                / (2 * Math.Abs(Math.Sin(theta / 2)));

            double d = Math.Sqrt(Math.Max(
                0, radius * radius - len * len / 4));

            Point3d mid = a + chord / 2;

            Vector3d nLeft = Vector3d.ZAxis
                .CrossProduct(chord).GetNormal();

            // CCW 弧：圆心在弦的左侧；CW 弧：右侧
            Point3d center = mid
                + nLeft * d * Math.Sign(theta);

            double a1 = Math.Atan2(
                a.Y - center.Y, a.X - center.X);

            double a2 = Math.Atan2(
                b.Y - center.Y, b.X - center.X);

            a1 = NormalizeTwoPi(a1);
            a2 = NormalizeTwoPi(a2);

            if (theta > 0)
                return new Arc(center, radius, a1, a1 + theta);

            return new Arc(center, radius, a2, a2 - theta);
        }

        private static double NormalizeTwoPi(double a)
        {
            while (a < 0)
                a += Math.PI * 2;

            while (a >= Math.PI * 2)
                a -= Math.PI * 2;

            return a;
        }

        // ---------------------------------------------------------------
        // join_entity：合并共线直线 / 同圆心圆弧
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> JoinEntityTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count < 2)
                throw new McpException("至少选择 2 个图元进行合并");

            var lines = new List<Line>();
            var arcs = new List<Arc>();

            foreach (ObjectId id in ids)
            {
                switch (tr.GetObject(id, OpenMode.ForWrite))
                {
                    case Line line:
                        lines.Add(line);
                        break;

                    case Arc arc:
                        arcs.Add(arc);
                        break;
                }
            }

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var created = new List<string>();
            var consumed = new List<string>();

            // 共线直线合并
            while (lines.Count > 0)
            {
                Line first = lines[0];
                lines.RemoveAt(0);

                Vector3d d = (first.EndPoint
                    - first.StartPoint).GetNormal();

                var group = new List<Line> { first };

                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    Line other = lines[i];

                    Vector3d od = (other.EndPoint
                        - other.StartPoint).GetNormal();

                    if (Math.Abs(Math.Abs(d.DotProduct(od)) - 1) > 1e-6)
                        continue;

                    // 共线检查：另一条线到基准直线的距离
                    double dist = DistanceToLine(
                        other.StartPoint, first.StartPoint, d);

                    if (dist > 1e-4)
                        continue;

                    group.Add(other);
                    lines.RemoveAt(i);
                }

                if (group.Count > 1)
                {
                    double min = double.MaxValue;
                    double max = double.MinValue;

                    foreach (Line line in group)
                    {
                        double t1 = (line.StartPoint
                            - first.StartPoint).DotProduct(d);

                        double t2 = (line.EndPoint
                            - first.StartPoint).DotProduct(d);

                        min = Math.Min(min, Math.Min(t1, t2));
                        max = Math.Max(max, Math.Max(t1, t2));
                    }

                    var merged = new Line(
                        first.StartPoint + d * min,
                        first.StartPoint + d * max);

                    merged.SetPropertiesFrom(first);

                    ObjectId newId = AddEntity(tr, btr, merged);
                    created.Add(merged.Handle.ToString());

                    foreach (Line line in group)
                    {
                        consumed.Add(line.Handle.ToString());
                        line.Erase();
                    }
                }
            }

            // 同圆心同半径圆弧合并
            while (arcs.Count > 0)
            {
                Arc first = arcs[0];
                arcs.RemoveAt(0);

                var group = new List<Arc> { first };

                for (int i = arcs.Count - 1; i >= 0; i--)
                {
                    Arc other = arcs[i];

                    if (other.Center.DistanceTo(first.Center) > 1e-4
                        || Math.Abs(other.Radius - first.Radius) > 1e-4)
                        continue;

                    group.Add(other);
                    arcs.RemoveAt(i);
                }

                if (group.Count > 1)
                {
                    var merged = MergeArcs(group);

                    if (merged != null)
                    {
                        merged.SetPropertiesFrom(first);

                        ObjectId newId = AddEntity(tr, btr, merged);
                        created.Add(merged.Handle.ToString());

                        foreach (Arc arc in group)
                        {
                            consumed.Add(arc.Handle.ToString());
                            arc.Erase();
                        }
                    }
                }
            }

            tr.Commit();

            return Result(
                "created", created,
                "consumed", consumed,
                "count", created.Count,
                "message", created.Count > 0
                    ? "合并完成"
                    : "未找到可合并的图元");
        }

        private static double DistanceToLine(
            Point3d p, Point3d anchor, Vector3d dir)
        {
            Vector3d v = p - anchor;
            Vector3d perp = v - dir * v.DotProduct(dir);
            return perp.Length;
        }

        /// <summary>合并同圆心同半径的相邻/重叠圆弧。</summary>
        private static Arc? MergeArcs(List<Arc> arcs)
        {
            var sorted = arcs
                .OrderBy(a => a.StartAngle)
                .ToList();

            double start = sorted[0].StartAngle;
            double end = sorted[0].EndAngle;

            for (int i = 1; i < sorted.Count; i++)
            {
                Arc next = sorted[i];

                if (next.StartAngle > end + Tol)
                    return null; // 有间隙，无法一次合并

                end = Math.Max(end, next.EndAngle);
            }

            if (end - start > Math.PI * 2)
                return null;

            return new Arc(
                sorted[0].Center,
                sorted[0].Radius,
                start, end);
        }

        // ---------------------------------------------------------------
        // array：通用阵列入口
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ArrayTool(
            Document doc, JsonElement args)
        {
            string mode = OptStr(args, "mode", "rect")
                .Trim().ToLowerInvariant();

            return mode == "polar"
                ? ArrayPolarTool(doc, args)
                : ArrayRectTool(doc, args);
        }

        // ---------------------------------------------------------------
        // array_rect：矩形阵列
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ArrayRectTool(
            Document doc, JsonElement args)
        {
            int rows = Math.Max(1, OptInt(args, "rows", 1));
            int cols = Math.Max(1, OptInt(args, "cols", 1));

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            Vector3d rowOffset = ParseOffset(args, "row_offset");
            Vector3d colOffset = ParseOffset(args, "col_offset");

            if (rowOffset.IsZeroLength() || colOffset.IsZeroLength())
            {
                // 默认偏移：包围盒大小
                Extents3d ext = new Extents3d();

                foreach (ObjectId id in ids)
                {
                    var ent = (Entity)tr.GetObject(
                        id, OpenMode.ForRead);

                    ext.AddExtents(BoundsOf(ent));
                }

                double w = ext.MaxPoint.X - ext.MinPoint.X;
                double h = ext.MaxPoint.Y - ext.MinPoint.Y;

                if (rowOffset.IsZeroLength())
                    rowOffset = new Vector3d(0, h, 0);

                if (colOffset.IsZeroLength())
                    colOffset = new Vector3d(w, 0, 0);
            }

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var created = new List<string>();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (r == 0 && c == 0)
                        continue;

                    Vector3d delta = colOffset * c
                        + rowOffset * r;

                    foreach (ObjectId id in ids)
                    {
                        var source = (Entity)tr.GetObject(
                            id, OpenMode.ForRead);

                        Entity copy = (Entity)source.Clone();
                        copy.TransformBy(
                            Matrix3d.Displacement(delta));

                        ObjectId newId = AddEntity(tr, btr, copy);
                        created.Add(copy.Handle.ToString());
                    }
                }
            }

            tr.Commit();

            return Result(
                "created", created,
                "count", created.Count,
                "rows", rows,
                "cols", cols);
        }

        // ---------------------------------------------------------------
        // array_polar：环形阵列
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ArrayPolarTool(
            Document doc, JsonElement args)
        {
            Point3d center = ReqPoint(args, "center");
            int count = Math.Max(2, OptInt(args, "count", 4));

            double totalAngle = OptDouble(args, "total_angle", 360);
            bool rotateItems = OptBool(args, "rotate_items", true);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            bool full = Math.Abs(totalAngle - 360) < Tol;

            double step = full
                ? 2 * Math.PI / count
                : Deg(totalAngle) / (count - 1);

            BlockTableRecord btr =
                GetCurrentSpace(tr, doc.Database, true);

            var created = new List<string>();

            for (int i = 1; i < count; i++)
            {
                foreach (ObjectId id in ids)
                {
                    var source = (Entity)tr.GetObject(
                        id, OpenMode.ForRead);

                    Entity copy = (Entity)source.Clone();

                    if (rotateItems)
                    {
                        copy.TransformBy(Matrix3d.Rotation(
                            step * i, Vector3d.ZAxis, center));
                    }
                    else
                    {
                        // 不旋转：基点沿圆周平移
                        Extents3d ext = BoundsOf(source);

                        Point3d basePt = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                            0);

                        Point3d newPos = center
                            + (basePt - center)
                                .RotateBy(step * i, Vector3d.ZAxis);

                        copy.TransformBy(Matrix3d.Displacement(
                            newPos - basePt));
                    }

                    ObjectId newId = AddEntity(tr, btr, copy);
                    created.Add(copy.Handle.ToString());
                }
            }

            tr.Commit();

            return Result(
                "created", created,
                "count", created.Count,
                "total", count);
        }

        private static Vector3d ParseOffset(
            JsonElement args, string name)
        {
            // 支持数组 [dx, dy] 和字符串 "dx,dy"
            if (Has(args, name))
            {
                Point3d v = OptPoint(args, name, default);
                if (v.X != 0 || v.Y != 0 || v.Z != 0)
                    return new Vector3d(v.X, v.Y, v.Z);

                string text = OptStr(args, name, "");
                if (text.Length > 0)
                {
                    Point3d p = ParsePoint3d(text);
                    return new Vector3d(p.X, p.Y, p.Z);
                }
            }

            return new Vector3d(0, 0, 0);
        }
    }
}
