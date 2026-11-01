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
    /// 几何计算：geo_basic / geo_query / calc_area / divide_curve；
    /// 空间感知：connected / inside / aligned / detect_clash /
    /// find_vacant / detect_islands。
    /// </summary>
    public static class GeoSpatialTools
    {
        public static void Register()
        {
            ToolRegistry.Register("geo_basic", GeoBasicTool);
            ToolRegistry.Register("geo_query", GeoQueryTool);
            ToolRegistry.Register("calc_area", CalcAreaTool);
            ToolRegistry.Register("divide_curve", DivideCurveTool);
            ToolRegistry.Register("connected", ConnectedTool);
            ToolRegistry.Register("inside", InsideTool);
            ToolRegistry.Register("aligned", AlignedTool);
            ToolRegistry.Register("detect_clash", DetectClashTool);
            ToolRegistry.Register("find_vacant", FindVacantTool);
            ToolRegistry.Register("detect_islands", DetectIslandsTool);
        }

        // ---------------------------------------------------------------
        // geo_basic
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GeoBasicTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            switch (op)
            {
                case "distance":
                    {
                        Point3d p1 = ReqPoint(args, "p1");
                        Point3d p2 = ReqPoint(args, "p2");

                        return Result(
                            "distance", R(p1.DistanceTo(p2)),
                            "p1", Pt(p1),
                            "p2", Pt(p2));
                    }

                case "midpoint":
                    {
                        Point3d p1 = ReqPoint(args, "p1");
                        Point3d p2 = ReqPoint(args, "p2");

                        Point3d mid = new Point3d(
                            (p1.X + p2.X) / 2,
                            (p1.Y + p2.Y) / 2,
                            (p1.Z + p2.Z) / 2);

                        return Result("midpoint", Pt(mid));
                    }

                case "angle":
                    {
                        Point3d p1 = ReqPoint(args, "p1");
                        Point3d p2 = ReqPoint(args, "p2");

                        double angle = Math.Atan2(
                            p2.Y - p1.Y, p2.X - p1.X)
                            * 180.0 / Math.PI;

                        return Result(
                            "angle", R(angle),
                            "radians", R(Deg(angle)));
                    }

                case "polar":
                    {
                        Point3d basePoint = ReqPoint(args, "base");
                        double angle = ReqDouble(args, "angle");
                        double distance = ReqDouble(args, "distance");

                        Point3d p = new Point3d(
                            basePoint.X + distance
                                * Math.Cos(Deg(angle)),
                            basePoint.Y + distance
                                * Math.Sin(Deg(angle)),
                            basePoint.Z);

                        return Result("point", Pt(p));
                    }

                case "rotate_point":
                    {
                        Point3d p = ReqPoint(args, "point");
                        Point3d center = ReqPoint(args, "center");
                        double angle = ReqDouble(args, "angle");

                        Point3d r = center
                            + (p - center).RotateBy(
                                Deg(angle), Vector3d.ZAxis);

                        return Result("point", Pt(r));
                    }

                case "translate":
                    {
                        Point3d p = ReqPoint(args, "point");
                        Vector3d delta = ParseVector(args, "delta");

                        return Result("point", Pt(p + delta));
                    }

                case "scale_point":
                    {
                        Point3d p = ReqPoint(args, "point");
                        Point3d center = ReqPoint(args, "center");
                        double factor = ReqDouble(args, "factor");

                        Point3d s = center + (p - center) * factor;

                        return Result("point", Pt(s));
                    }

                case "nearest":
                    {
                        Point3d p = ReqPoint(args, "point");
                        string handle = ReqStr(args, "handle");

                        using Transaction tr =
                            doc.Database.TransactionManager
                                .StartTransaction();

                        var curve = GetEntityByHandle(
                            tr, doc.Database, handle) as Curve;

                        if (curve == null)
                            throw new McpException(
                                $"不是曲线：{handle}");

                        Point3d closest = curve.GetClosestPointTo(
                            p, false);

                        double dist = closest.DistanceTo(p);

                        tr.Commit();

                        return Result(
                            "point", Pt(closest),
                            "distance", R(dist));
                    }

                default:
                    throw new McpException(
                        "geo_basic op 支持：distance / midpoint / " +
                        "angle / polar / rotate_point / translate / " +
                        "scale_point / nearest");
            }
        }

        private static Vector3d ParseVector(
            JsonElement args, string name)
        {
            Point3d v = ReqPoint(args, name);
            return new Vector3d(v.X, v.Y, v.Z);
        }

        // ---------------------------------------------------------------
        // geo_query
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GeoQueryTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");
            string op = OptStr(args, "op", "all")
                .Trim().ToLowerInvariant();

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            Entity ent = GetEntityByHandle(
                tr, doc.Database, handle);

            Curve? curve = ent as Curve;

            double length = curve != null
                ? CurveLength(curve) : 0;

            double area = curve != null
                ? CurveArea(curve) : 0;

            switch (op)
            {
                case "length":
                    tr.Commit();
                    return Result("length", R(length));

                case "area":
                    tr.Commit();
                    return Result("area", R(area));

                case "perimeter":
                    tr.Commit();

                    return Result(
                        "perimeter", ent switch
                        {
                            Region r => R(r.Perimeter),
                            _ => R(length)
                        });

                case "centroid":
                    {
                        Point3d centroid = CentroidOf(ent);
                        tr.Commit();

                        return Result("centroid", Pt(centroid));
                    }

                case "start":
                    tr.Commit();

                    return Result(
                        "start", curve != null
                            ? Pt(curve.StartPoint) : null);

                case "end":
                    tr.Commit();

                    return Result(
                        "end", curve != null
                            ? Pt(curve.EndPoint) : null);

                case "mid":
                    {
                        Point3d mid = curve != null
                            ? curve.GetPointAtDist(length / 2)
                            : default;

                        tr.Commit();

                        return Result(
                            "mid", curve != null ? Pt(mid) : null);
                    }

                case "direction":
                    {
                        object? dir = null;

                        if (curve != null)
                        {
                            Vector3d dv = (curve.EndPoint
                                - curve.StartPoint).GetNormal();

                            dir = Pt(new Point3d(
                                dv.X, dv.Y, dv.Z));
                        }

                        tr.Commit();
                        return Result("direction", dir);
                    }

                default:
                    {
                        Point3d centroid = CentroidOf(ent);

                        Dictionary<string, object?> payload =
                            DescribeEntity(ent, true);

                        payload["length"] = R(length);
                        payload["area"] = R(area);
                        payload["centroid"] = Pt(centroid);

                        tr.Commit();
                        return payload;
                    }
            }
        }

        private static Point3d CentroidOf(Entity ent)
        {
            switch (ent)
            {
                case Region reg:
                    {
                        // 面域用面积中心近似（包围盒中心）
                        Extents3d e = BoundsOf(reg);

                        return new Point3d(
                            (e.MinPoint.X + e.MaxPoint.X) / 2,
                            (e.MinPoint.Y + e.MaxPoint.Y) / 2,
                            0);
                    }

                case Curve curve:
                    return curve.GetPointAtDist(
                        CurveLength(curve) / 2);

                default:
                    {
                        Extents3d e = BoundsOf(ent);

                        return new Point3d(
                            (e.MinPoint.X + e.MaxPoint.X) / 2,
                            (e.MinPoint.Y + e.MaxPoint.Y) / 2,
                            0);
                    }
            }
        }

        // ---------------------------------------------------------------
        // calc_area
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> CalcAreaTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var areas = new List<object>();
            double total = 0;

            if (args.TryGetProperty("point", out JsonElement pv))
            {
                // 通过内部点追踪边界并计算面积
                Point3d point = ToPoint3d(pv);

                DBObjectCollection boundaryObjs =
                    doc.Editor.TraceBoundary(point, false);

                if (boundaryObjs != null)
                {
                    try
                    {
                        foreach (DBObject obj in boundaryObjs)
                        {
                            if (obj is not Curve curve)
                                continue;

                            double a = Math.Abs(CurveArea(curve));

                            if (a > 0)
                            {
                                total += a;

                                areas.Add(Result(
                                    "type", DxfName(curve),
                                    "area", R(a)));
                            }
                        }
                    }
                    finally
                    {
                        // 边界为临时曲线，用后即弃
                        foreach (DBObject obj in boundaryObjs)
                            obj.Dispose();
                    }
                }
            }
            else
            {
                List<ObjectId> ids = ResolveSelection(
                    args, doc, tr);

                if (ids.Count == 0)
                    throw new McpException(
                        "未选中图元，也没有 point 参数");

                foreach (ObjectId id in ids)
                {
                    var ent = (Entity)tr.GetObject(
                        id, OpenMode.ForRead, false, true);

                    double a = ent switch
                    {
                        Region reg => reg.Area,
                        Curve curve => Math.Abs(CurveArea(curve)),
                        Hatch hatch => hatch.Area,
                        _ => 0
                    };

                    areas.Add(Result(
                        "handle", ent.Handle.ToString(),
                        "type", DxfName(ent),
                        "area", R(a)));

                    total += a;
                }
            }

            tr.Commit();

            return Result(
                "areas", areas,
                "count", areas.Count,
                "total_area", R(total));
        }

        // ---------------------------------------------------------------
        // divide_curve
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DivideCurveTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");
            int segments = OptInt(args, "segments", 0);
            double interval = OptDouble(args, "interval", 0);
            bool markOnly = OptBool(args, "mark_only", false);

            if (segments <= 0 && interval <= 0)
                throw new McpException(
                    "请提供 segments 或 interval");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var curve = GetEntityByHandle(
                tr, doc.Database, handle) as Curve;

            if (curve == null)
                throw new McpException($"不是曲线：{handle}");

            double length = CurveLength(curve);

            if (length <= 0)
                throw new McpException("曲线长度为零");

            double step = interval > 0
                ? interval
                : length / segments;

            int count = interval > 0
                ? Math.Max(0, (int)(length / interval) - 1)
                : segments - 1;

            var points = new List<object>();
            var created = new List<string>();

            BlockTableRecord btr = markOnly
                ? GetCurrentSpace(tr, doc.Database, false)
                : GetCurrentSpace(tr, doc.Database, true);

            string layer = OptStr(args, "layer", "");

            if (layer.Length > 0)
                EnsureLayer(tr, doc.Database, layer);

            for (int i = 1; i <= count; i++)
            {
                Point3d p = curve.GetPointAtDist(step * i);

                points.Add(Pt(p));

                if (!markOnly)
                {
                    var pt = new DBPoint(p);

                    if (layer.Length > 0)
                        pt.Layer = layer;

                    ObjectId id = AddEntity(tr, btr, pt);
                    created.Add(pt.Handle.ToString());
                }
            }

            tr.Commit();

            return Result(
                "points", points,
                "created", created,
                "count", points.Count,
                "length", R(length),
                "mark_only", markOnly);
        }

        // ---------------------------------------------------------------
        // connected：图元连通性检查 / 连通分量 / 路径
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ConnectedTool(
            Document doc, JsonElement args)
        {
            double tolerance = OptDouble(args, "tolerance", 1e-3);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var all = ScanSpace(tr, doc.Database, "current", default)
                .Select(id => (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true))
                .Where(e => e is Curve)
                .ToList();

            // 构建邻接表
            var adjacency = new Dictionary<string, List<string>>();

            foreach (Entity a in all)
                adjacency[a.Handle.ToString()] = new List<string>();

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (!Touching(all[i], all[j], tolerance))
                        continue;

                    string ha = all[i].Handle.ToString();
                    string hb = all[j].Handle.ToString();

                    adjacency[ha].Add(hb);
                    adjacency[hb].Add(ha);
                }
            }

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            string start = ((Entity)tr.GetObject(
                ids[0], OpenMode.ForRead)).Handle.ToString();

            // BFS 求连通分量
            var visited = new HashSet<string> { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();

                foreach (string next in adjacency[current])
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }

            string op = OptStr(args, "op", "check")
                .Trim().ToLowerInvariant();

            if (op == "component")
            {
                tr.Commit();

                return Result(
                    "start", start,
                    "component", visited.ToList(),
                    "component_size", visited.Count);
            }

            // check：所有选中图元是否互相连通
            var selectedHandles = ids
                .Select(id => ((Entity)tr.GetObject(
                    id, OpenMode.ForRead)).Handle.ToString())
                .ToList();

            bool allConnected = selectedHandles.All(
                h => visited.Contains(h));

            tr.Commit();

            return Result(
                "connected", allConnected,
                "selected", selectedHandles,
                "reachable_from_first", visited.Count,
                "total_curves", all.Count,
                "message", allConnected
                    ? "所有选中图元互相连通"
                    : "选中图元不全部连通");
        }

        private static bool Touching(
            Entity a, Entity b, double tolerance)
        {
            if (Intersections(a, b).Count > 0)
                return true;

            if (a is Curve ca && b is Curve cb)
            {
                Point3d[] ptsA = { ca.StartPoint, ca.EndPoint };
                Point3d[] ptsB = { cb.StartPoint, cb.EndPoint };

                foreach (Point3d pa in ptsA)
                {
                    foreach (Point3d pb in ptsB)
                    {
                        if (pa.DistanceTo(pb) <= tolerance)
                            return true;
                    }
                }
            }

            return false;
        }

        // ---------------------------------------------------------------
        // inside
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> InsideTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            switch (op)
            {
                case "point":
                    {
                        Point3d point = ReqPoint(args, "point");
                        string container = ReqStr(args, "container");

                        var curve = GetEntityByHandle(
                            tr, doc.Database, container) as Curve;

                        if (curve == null)
                            throw new McpException(
                                $"不是曲线：{container}");

                        bool inside = ClosedCurveContains(
                            curve, point);

                        tr.Commit();

                        return Result(
                            "point", Pt(point),
                            "container", container,
                            "inside", inside);
                    }

                case "entities":
                    {
                        string container = ReqStr(
                            args, "container");

                        var curve = GetEntityByHandle(
                            tr, doc.Database, container) as Curve;

                        if (curve == null)
                            throw new McpException(
                                $"不是曲线：{container}");

                        List<ObjectId> ids = ResolveSelection(
                            args, doc, tr);

                        var insideList = new List<string>();
                        var outsideList = new List<string>();

                        foreach (ObjectId id in ids)
                        {
                            var ent = (Entity)tr.GetObject(
                                id, OpenMode.ForRead, false, true);

                            Extents3d e = BoundsOf(ent);

                            Point3d center = new Point3d(
                                (e.MinPoint.X + e.MaxPoint.X) / 2,
                                (e.MinPoint.Y + e.MaxPoint.Y) / 2,
                                0);

                            bool in1 = ClosedCurveContains(
                                curve, e.MinPoint);

                            bool in2 = ClosedCurveContains(
                                curve, e.MaxPoint);

                            bool in3 = ClosedCurveContains(
                                curve, center);

                            if (in1 && in2 && in3)
                                insideList.Add(ent.Handle.ToString());
                            else if (!in1 && !in2 && !in3)
                                outsideList.Add(ent.Handle.ToString());
                            else
                                insideList.Add(ent.Handle.ToString());
                        }

                        tr.Commit();

                        return Result(
                            "inside", insideList,
                            "outside", outsideList,
                            "container", container);
                    }

                case "rectangle":
                    {
                        Point3d p1 = ReqPoint(args, "p1");
                        Point3d p2 = ReqPoint(args, "p2");

                        List<ObjectId> ids = ResolveSelection(
                            args, doc, tr);

                        var insideList = new List<string>();

                        foreach (ObjectId id in ids)
                        {
                            var ent = (Entity)tr.GetObject(
                                id, OpenMode.ForRead, false, true);

                            Extents3d e = BoundsOf(ent);

                            bool inRect =
                                e.MinPoint.X >= Math.Min(p1.X, p2.X)
                                && e.MaxPoint.X <= Math.Max(p1.X, p2.X)
                                && e.MinPoint.Y >= Math.Min(p1.Y, p2.Y)
                                && e.MaxPoint.Y <= Math.Max(p1.Y, p2.Y);

                            if (inRect)
                                insideList.Add(ent.Handle.ToString());
                        }

                        tr.Commit();

                        return Result(
                            "inside", insideList,
                            "count", insideList.Count);
                    }

                default:
                    throw new McpException(
                        "inside op 支持：point / entities / rectangle");
            }
        }

        // ---------------------------------------------------------------
        // aligned
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> AlignedTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            double tolerance = OptDouble(args, "tolerance", 1e-3);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
                throw new McpException("未选中任何图元");

            var entities = ids
                .Select(id => (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true))
                .ToList();

            var aligned = new List<string>();
            var notAligned = new List<string>();

            // 以第一个图元为基准
            Entity? reference = entities.FirstOrDefault();

            if (reference == null)
                throw new McpException("未选中任何图元");

            Extents3d refBounds = BoundsOf(reference);

            foreach (Entity ent in entities)
            {
                Extents3d e = BoundsOf(ent);

                bool isAligned = op switch
                {
                    "horizontal" =>
                        Math.Abs(e.MinPoint.Y - e.MaxPoint.Y) < tolerance
                        || Math.Abs(refBounds.MinPoint.Y
                            - e.MinPoint.Y) < tolerance,

                    "vertical" =>
                        Math.Abs(e.MinPoint.X - e.MaxPoint.X) < tolerance
                        || Math.Abs(refBounds.MinPoint.X
                            - e.MinPoint.X) < tolerance,

                    "center" =>
                        Math.Abs((refBounds.MinPoint.X
                                + refBounds.MaxPoint.X)
                            - (e.MinPoint.X + e.MaxPoint.X)) < tolerance
                        || Math.Abs((refBounds.MinPoint.Y
                                + refBounds.MaxPoint.Y)
                            - (e.MinPoint.Y + e.MaxPoint.Y)) < tolerance,

                    "collinear" =>
                        ent is Curve c1 && reference is Curve c2
                            && CurvesCollinear(c1, c2, tolerance),

                    _ => throw new McpException(
                        "aligned op 支持：" +
                        "horizontal / vertical / center / collinear")
                };

                if (isAligned)
                    aligned.Add(ent.Handle.ToString());
                else
                    notAligned.Add(ent.Handle.ToString());
            }

            tr.Commit();

            return Result(
                "op", op,
                "aligned", aligned,
                "not_aligned", notAligned,
                "all_aligned", notAligned.Count == 0);
        }

        private static bool CurvesCollinear(
            Curve a, Curve b, double tolerance)
        {
            Vector3d d1 = (a.EndPoint - a.StartPoint).GetNormal();
            Vector3d d2 = (b.EndPoint - b.StartPoint).GetNormal();

            if (Math.Abs(Math.Abs(d1.DotProduct(d2)) - 1) > 1e-6)
                return false;

            Vector3d v = b.StartPoint - a.StartPoint;
            Vector3d perp = v - d1 * v.DotProduct(d1);

            return perp.Length <= tolerance;
        }

        // ---------------------------------------------------------------
        // detect_clash
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DetectClashTool(
            Document doc, JsonElement args)
        {
            double tolerance = OptDouble(args, "tolerance", 1e-4);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> setA;
            List<ObjectId> setB;

            if (args.TryGetProperty("set_a", out JsonElement av))
                setA = ResolveSpec(av, doc, tr);
            else
                setA = ResolveSelection(args, doc, tr);

            if (args.TryGetProperty("set_b", out JsonElement bv))
                setB = ResolveSpec(bv, doc, tr);
            else
                setB = setA;

            var clashes = new List<object>();

            for (int i = 0; i < setA.Count; i++)
            {
                var ea = (Entity)tr.GetObject(
                    setA[i], OpenMode.ForRead, false, true);

                for (int j = 0; j < setB.Count; j++)
                {
                    if (setA == setB && i >= j)
                        continue;

                    var eb = (Entity)tr.GetObject(
                        setB[j], OpenMode.ForRead, false, true);

                    List<Point3d> points = Intersections(ea, eb);

                    if (points.Count == 0)
                        continue;

                    // 仅端点接触（共享端点）不算冲突
                    bool realClash = points.Any(p =>
                        !(ea is Curve ca
                            && (p.DistanceTo(ca.StartPoint)
                                < tolerance
                                || p.DistanceTo(ca.EndPoint)
                                < tolerance))
                        && !(eb is Curve cb
                            && (p.DistanceTo(cb.StartPoint)
                                < tolerance
                                || p.DistanceTo(cb.EndPoint)
                                < tolerance)));

                    if (!realClash)
                        continue;

                    clashes.Add(Result(
                        "entity_a", ea.Handle.ToString(),
                        "type_a", DxfName(ea),
                        "entity_b", eb.Handle.ToString(),
                        "type_b", DxfName(eb),
                        "points", PtList(points)));
                }
            }

            tr.Commit();

            return Result(
                "clashes", clashes,
                "count", clashes.Count,
                "set_a_size", setA.Count,
                "set_b_size", setB.Count);
        }

        // ---------------------------------------------------------------
        // find_vacant
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> FindVacantTool(
            Document doc, JsonElement args)
        {
            double width = ReqDouble(args, "width");
            double height = ReqDouble(args, "height");

            Point3d p1 = OptPoint(args, "p1", default);
            Point3d p2 = OptPoint(args, "p2", default);

            Extents3d region;

            using (Transaction tr =
                doc.Database.TransactionManager.StartTransaction())
            {
                // 区域角点可以是 [0,0]，按参数是否提供判定。
                region = Has(args, "p1") && Has(args, "p2")
                    ? new Extents3d(
                        new Point3d(
                            Math.Min(p1.X, p2.X),
                            Math.Min(p1.Y, p2.Y), 0),
                        new Point3d(
                            Math.Max(p1.X, p2.X),
                            Math.Max(p1.Y, p2.Y), 0))
                    : GetSpaceExtents(tr, doc.Database,
                        doc.Database.CurrentSpaceId);

                tr.Commit();
            }

            double step = OptDouble(args, "step", 0);

            if (step <= 0)
                step = Math.Max(width, height) / 4;

            using Transaction tr2 =
                doc.Database.TransactionManager.StartTransaction();

            var obstacles = ScanSpace(
                    tr2, doc.Database, "current", default)
                .Select(id => (Entity)tr2.GetObject(
                    id, OpenMode.ForRead, false, true))
                .Where(e => e is not DBPoint)
                .ToList();

            var vacant = new List<object>();

            for (double y = region.MinPoint.Y;
                y + height <= region.MaxPoint.Y + Tol;
                y += step)
            {
                for (double x = region.MinPoint.X;
                    x + width <= region.MaxPoint.X + Tol;
                    x += step)
                {
                    var rectMin = new Point3d(x, y, 0);
                    var rectMax = new Point3d(
                        x + width, y + height, 0);

                    bool occupied = obstacles.Any(ent =>
                    {
                        Extents3d e = BoundsOf(ent);

                        return !(e.MaxPoint.X < rectMin.X - Tol
                            || e.MinPoint.X > rectMax.X + Tol
                            || e.MaxPoint.Y < rectMin.Y - Tol
                            || e.MinPoint.Y > rectMax.Y + Tol);
                    });

                    if (!occupied)
                    {
                        vacant.Add(Result(
                            "p1", Pt(rectMin),
                            "p2", Pt(rectMax),
                            "center", Pt(new Point3d(
                                x + width / 2, y + height / 2, 0))));

                        if (vacant.Count >= OptInt(args, "limit", 20))
                            break;
                    }
                }

                if (vacant.Count >= OptInt(args, "limit", 20))
                    break;
            }

            tr2.Commit();

            return Result(
                "width", width,
                "height", height,
                "vacant", vacant,
                "count", vacant.Count);
        }

        // ---------------------------------------------------------------
        // detect_islands：闭合曲线内部的独立闭合区域
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DetectIslandsTool(
            Document doc, JsonElement args)
        {
            string outerHandle = ReqStr(args, "outer");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var outer = GetEntityByHandle(
                tr, doc.Database, outerHandle) as Curve;

            if (outer == null)
                throw new McpException($"不是曲线：{outerHandle}");

            var candidates = ScanSpace(
                    tr, doc.Database, "current", default)
                .Select(id => tr.GetObject(
                    id, OpenMode.ForRead, false, true))
                .OfType<Curve>()
                .Where(c => c.ObjectId != outer.ObjectId
                    && IsClosedCurve(c))
                .ToList();

            var islands = new List<object>();

            foreach (Curve curve in candidates)
            {
                bool fullyInside = SampleInside(outer, curve);

                if (!fullyInside)
                    continue;

                bool nested = candidates.Any(other =>
                    other.ObjectId != curve.ObjectId
                    && SampleInside(curve, other));

                islands.Add(Result(
                    "handle", curve.Handle.ToString(),
                    "type", DxfName(curve),
                    "layer", curve.Layer,
                    "area", R(CurveArea(curve)),
                    "length", R(CurveLength(curve)),
                    "contains_other_islands", nested));
            }

            tr.Commit();

            return Result(
                "outer", outerHandle,
                "islands", islands,
                "count", islands.Count);
        }

        private static Extents3d GetSpaceExtents(
            Transaction tr, Database db, ObjectId spaceId)
        {
            Extents3d ext = new Extents3d();

            if (spaceId.IsNull)
                return ext;

            var btr = (BlockTableRecord)tr.GetObject(
                spaceId, OpenMode.ForRead);

            bool first = true;

            foreach (ObjectId oid in btr)
            {
                if (tr.GetObject(oid, OpenMode.ForRead,
                        false, true) is not Entity ent)
                    continue;

                if (ent is Viewport)
                    continue;

                Extents3d e = BoundsOf(ent);

                if (first)
                {
                    ext = e;
                    first = false;
                }
                else
                {
                    ext.AddExtents(e);
                }
            }

            return ext;
        }

        private static bool IsClosedCurve(Curve curve) =>
            curve is Polyline pl && pl.Closed
            || curve is Circle
            || curve is Ellipse el && EllipseClosed(el)
            || curve is Spline sp && SplineClosed(sp);

        private static bool SampleInside(
            Curve container, Curve inner)
        {
            if (CurveArea(container) <= 0)
                return false;

            // 采样内部曲线的若干点（端点/中点/包围盒中心）
            var samples = new List<Point3d>
            {
                inner.StartPoint,
                inner.EndPoint
            };

            double length = CurveLength(inner);

            if (length > 0)
                samples.Add(inner.GetPointAtDist(length / 2));

            Extents3d e = BoundsOf(inner);

            samples.Add(new Point3d(
                (e.MinPoint.X + e.MaxPoint.X) / 2,
                (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0));

            return samples.All(p =>
                ClosedCurveContains(container, p));
        }
    }
}
