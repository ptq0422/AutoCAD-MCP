using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;

using AcadColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AutoCADMcp.Plugin
{
    /// <summary>业务异常：消息会作为 error 返回给调用方。</summary>
    public sealed class McpException : System.Exception
    {
        public McpException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 公共工具：文档/事务访问、坐标解析、选择集解析、
    /// 图元 JSON 描述与几何计算。
    /// </summary>
    public static class ToolContext
    {
        public const double Tol = 1e-6;

        private const string TagApp = "MCPTAG";

        // ---------------------------------------------------------------
        // 文档与事务
        // ---------------------------------------------------------------

        public static Document Doc()
        {
            Document? doc =
                Application.DocumentManager.MdiActiveDocument;

            return doc ?? throw new McpException(
                "AutoCAD 当前没有打开的文档");
        }

        public static Database Db() => Doc().Database;

        public static Editor Ed() => Doc().Editor;

        public static BlockTable GetBlockTable(
            Transaction tr, Database db) =>
            (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        public static BlockTableRecord GetSpace(
            Transaction tr, Database db, ObjectId id,
            OpenMode mode = OpenMode.ForWrite) =>
            (BlockTableRecord)tr.GetObject(id, mode);

        public static BlockTableRecord GetCurrentSpace(
            Transaction tr, Database db, bool forWrite = true) =>
            GetSpace(tr, db, db.CurrentSpaceId,
                forWrite ? OpenMode.ForWrite : OpenMode.ForRead);

        public static ObjectId AddEntity(
            Transaction tr, BlockTableRecord btr, Entity ent)
        {
            btr.AppendEntity(ent);
            tr.AddNewlyCreatedDBObject(ent, true);
            return ent.ObjectId;
        }

        public static List<ObjectId> AddEntities(
            Transaction tr, BlockTableRecord btr,
            IEnumerable<Entity> entities)
        {
            var ids = new List<ObjectId>();

            foreach (Entity ent in entities)
                ids.Add(AddEntity(tr, btr, ent));

            return ids;
        }

        // ---------------------------------------------------------------
        // 结果构造
        // ---------------------------------------------------------------

        /// <summary>Result("k1", v1, "k2", v2, ...) 构造结果字典。</summary>
        public static Dictionary<string, object?> Result(
            params object?[] pairs)
        {
            var d = new Dictionary<string, object?>();

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                d[(string)pairs[i]!] = pairs[i + 1];

            return d;
        }

        public static double R(double v) => Math.Round(v, 6);

        public static object[] Pt(Point3d p) =>
            new object[] { R(p.X), R(p.Y), R(p.Z) };

        public static object[] Pt2(Point2d p) =>
            new object[] { R(p.X), R(p.Y) };

        public static List<object> PtList(IEnumerable<Point3d> pts) =>
            pts.Select(p => (object)Pt(p)).ToList();

        // ---------------------------------------------------------------
        // 参数解析
        // ---------------------------------------------------------------

        public static double ParseDouble(string text)
        {
            if (!double.TryParse(text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double v))
                throw new McpException($"无法解析数值：{text}");

            return v;
        }

        /// <summary>解析 "x,y" / "x,y,z" / "x y z" 字符串。</summary>
        public static Point3d ParsePoint3d(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new McpException("坐标不能为空");

            string[] parts = text.Split(
                new[] { ',', ' ' },
                StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
                throw new McpException($"无法解析坐标：{text}");

            double x = ParseDouble(parts[0]);
            double y = ParseDouble(parts[1]);
            double z = parts.Length > 2 ? ParseDouble(parts[2]) : 0;

            return new Point3d(x, y, z);
        }

        public static Point2d ParsePoint2d(string text)
        {
            Point3d p = ParsePoint3d(text);
            return new Point2d(p.X, p.Y);
        }

        public static Point3d ToPoint3d(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return ParsePoint3d(el.GetString()!);

                case JsonValueKind.Array:
                    {
                        double[] v = new double[3];

                        for (int i = 0; i < el.GetArrayLength() && i < 3; i++)
                            v[i] = el[i].GetDouble();

                        return new Point3d(v[0], v[1], v[2]);
                    }

                default:
                    throw new McpException($"无法解析坐标：{el}");
            }
        }

        public static string OptStr(
            JsonElement args, string name, string def = "") =>
            args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name, out JsonElement v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? def
                    : def;

        public static string ReqStr(JsonElement args, string name)
        {
            string s = OptStr(args, name);

            if (string.IsNullOrEmpty(s))
                throw new McpException($"缺少参数：{name}");

            return s;
        }

        public static double OptDouble(
            JsonElement args, string name, double def = 0)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v))
                return def;

            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String => ParseDouble(v.GetString()!),
                _ => def
            };
        }

        public static double ReqDouble(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out _))
                throw new McpException($"缺少参数：{name}");

            return OptDouble(args, name);
        }

        /// <summary>
        /// 判断参数是否显式提供（存在且非 null）。
        /// 多模式工具一律用它判定模式，不能拿坐标值是否为原点作哨兵，
        /// 否则 [0,0] 这类合法坐标会被误判为“未提供”。
        /// </summary>
        public static bool Has(JsonElement args, string name)
        {
            return args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name, out JsonElement v)
                && v.ValueKind != JsonValueKind.Null;
        }

        /// <summary>
        /// 判断参数是否存在且可解析为数值（用于多模式工具的模式判定，
        /// 避免显式传入的 null / 空值被误判为“已提供”）。
        /// </summary>
        public static bool IsNumber(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v))
                return false;

            if (v.ValueKind == JsonValueKind.Number)
                return true;

            return v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out _);
        }

        public static int OptInt(
            JsonElement args, string name, int def = 0)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v))
                return def;

            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt32(),
                JsonValueKind.String => (int)Math.Round(
                    ParseDouble(v.GetString()!)),
                _ => def
            };
        }

        public static bool OptBool(
            JsonElement args, string name, bool def = false)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v))
                return def;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => v.GetString()!
                    .Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => def
            };
        }

        public static Point3d OptPoint(
            JsonElement args, string name, Point3d def)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v)
                || v.ValueKind == JsonValueKind.Null)
                return def;

            return ToPoint3d(v);
        }

        public static Point3d ReqPoint(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out JsonElement v))
                throw new McpException($"缺少参数：{name}");

            return ToPoint3d(v);
        }

        /// <summary>
        /// 解析点列表：支持字符串数组、数组的数组，
        /// 以及 "x,y;x,y" 分号分隔的单个字符串。
        /// </summary>
        public static List<Point3d> PointList(JsonElement el)
        {
            var pts = new List<Point3d>();

            if (el.ValueKind == JsonValueKind.String)
            {
                foreach (string part in el.GetString()!
                    .Split(';', StringSplitOptions.RemoveEmptyEntries))
                    pts.Add(ParsePoint3d(part));

                return pts;
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in el.EnumerateArray())
                    pts.Add(ToPoint3d(item));

                return pts;
            }

            throw new McpException($"无法解析点列表：{el}");
        }

        public static List<Point3d> OptPoints(
            JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name, out JsonElement v)
                ? PointList(v)
                : new List<Point3d>();

        public static List<Point3d> ReqPoints(
            JsonElement args, string name)
        {
            List<Point3d> pts = OptPoints(args, name);

            if (pts.Count == 0)
                throw new McpException($"缺少参数：{name}");

            return pts;
        }

        public static double Deg(double degrees) =>
            degrees * Math.PI / 180.0;

        // ---------------------------------------------------------------
        // 颜色 / 图层 / 线型
        // ---------------------------------------------------------------

        public static AcadColor ParseColor(string text)
        {
            text = text.Trim();

            if (text.Length == 0)
                return Color.FromColorIndex(
                    ColorMethod.ByLayer, 256);

            if (text.Equals("bylayer",
                    StringComparison.OrdinalIgnoreCase))
                return Color.FromColorIndex(
                    ColorMethod.ByLayer, 256);

            if (text.Equals("byblock",
                    StringComparison.OrdinalIgnoreCase))
                return Color.FromColorIndex(
                    ColorMethod.ByBlock, 0);

            if (int.TryParse(text, out int idx)
                && idx >= 0 && idx <= 256)
                return AcadColor.FromColorIndex(
                    ColorMethod.ByAci, (short)idx);

            string[] rgb = text.Split(',');
            if (rgb.Length == 3
                && byte.TryParse(rgb[0], out byte r)
                && byte.TryParse(rgb[1], out byte g)
                && byte.TryParse(rgb[2], out byte b))
                return AcadColor.FromRgb(r, g, b);

            try
            {
                System.Drawing.Color named =
                    System.Drawing.Color.FromName(text);

                if (named.IsKnownColor)
                    return AcadColor.FromRgb(
                        named.R, named.G, named.B);
            }
            catch
            {
                // 忽略名称解析失败
            }

            throw new McpException($"无法解析颜色：{text}");
        }

        public static ObjectId EnsureLayer(
            Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(
                db.LayerTableId, OpenMode.ForRead);

            if (lt.Has(name))
                return lt[name];

            lt.UpgradeOpen();

            var ltr = new LayerTableRecord { Name = name };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);

            return ltr.ObjectId;
        }

        public static LayerTableRecord? GetLayer(
            Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(
                db.LayerTableId, OpenMode.ForRead);

            if (!lt.Has(name))
                return null;

            return (LayerTableRecord)tr.GetObject(
                lt[name], OpenMode.ForRead);
        }

        public static ObjectId EnsureLinetype(
            Transaction tr, Database db, string name)
        {
            if (string.IsNullOrEmpty(name)
                || name.Equals("bylayer",
                    StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;

            LinetypeTable ltt = (LinetypeTable)tr.GetObject(
                db.LinetypeTableId, OpenMode.ForRead);

            if (ltt.Has(name))
                return ltt[name];

            throw new McpException($"线型不存在：{name}");
        }

        // ---------------------------------------------------------------
        // 图元按 handle 获取
        // ---------------------------------------------------------------

        public static ObjectId IdByHandle(
            Database db, string handleText)
        {
            if (!long.TryParse(handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long value))
                throw new McpException($"无效的 handle：{handleText}");

            return db.GetObjectId(
                false, new Handle(value), 0);
        }

        /// <summary>
        /// Handle 解析的安全版本：格式非法或句柄不存在时返回
        /// ObjectId.Null（而不是抛出异常），供 ResolveSelection 中
        /// .Where(!IsNull) 过滤使用。避免 AutoCAD 的 eUnknownHandle
        /// 异常穿透导致整个选择集解析失败。
        /// </summary>
        public static ObjectId TryIdByHandle(
            Database db, string handleText)
        {
            if (string.IsNullOrWhiteSpace(handleText))
                return ObjectId.Null;

            if (!long.TryParse(handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long value))
                return ObjectId.Null;

            try
            {
                return db.GetObjectId(
                    false, new Handle(value), 0);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // eUnknownHandle / eInvalidInput 等
                return ObjectId.Null;
            }
            catch (System.Exception)
            {
                return ObjectId.Null;
            }
        }

        public static Entity GetEntityByHandle(
            Transaction tr, Database db, string handle,
            OpenMode mode = OpenMode.ForRead)
        {
            ObjectId id = IdByHandle(db, handle);

            if (id.IsNull || id.IsErased)
                throw new McpException($"图元不存在：{handle}");

            return (Entity)tr.GetObject(id, mode);
        }

        public static List<string> HandlesOf(
            Transaction tr, IEnumerable<ObjectId> ids)
        {
            var list = new List<string>();

            foreach (ObjectId id in ids)
            {
                if (id.IsNull || id.IsErased)
                    continue;

                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                list.Add(obj.Handle.ToString());
            }

            return list;
        }

        // ---------------------------------------------------------------
        // 选择集解析
        // ---------------------------------------------------------------

        private static readonly Dictionary<string, string> TypeAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["line"] = "LINE",
                ["circle"] = "CIRCLE",
                ["arc"] = "ARC",
                ["text"] = "TEXT",
                ["mtext"] = "MTEXT",
                ["polyline"] = "LWPOLYLINE",
                ["pline"] = "LWPOLYLINE",
                ["lwpolyline"] = "LWPOLYLINE",
                ["polyline3d"] = "POLYLINE",
                ["spline"] = "SPLINE",
                ["ellipse"] = "ELLIPSE",
                ["point"] = "POINT",
                ["block"] = "INSERT",
                ["insert"] = "INSERT",
                ["blockref"] = "INSERT",
                ["block_reference"] = "INSERT",
                ["hatch"] = "HATCH",
                ["dimension"] = "DIMENSION",
                ["leader"] = "MULTILEADER",
                ["mleader"] = "MULTILEADER",
                ["region"] = "REGION",
                ["table"] = "ACAD_TABLE",
                ["viewport"] = "VIEWPORT",
                ["solid"] = "SOLID",
                ["face"] = "3DFACE"
            };

        public static string DxfName(DBObject obj)
        {
            string name = obj.GetRXClass().DxfName
                .ToUpperInvariant();

            return name;
        }

        /// <summary>把类型别名规范化为 DXF 名称（原样保留未知名称）。</summary>
        public static string NormalizeType(string type)
        {
            string t = type.Trim().ToUpperInvariant();

            return TypeAliases.TryGetValue(t, out string? dxf)
                ? dxf
                : t;
        }

        /// <summary>判断图元是否匹配过滤条件。</summary>
        public static bool MatchesFilter(
            Entity ent, JsonElement filter)
        {
            if (filter.ValueKind != JsonValueKind.Object)
                return true;

            if (filter.TryGetProperty("type", out JsonElement tv)
                && tv.ValueKind == JsonValueKind.String)
            {
                if (!DxfName(ent).Equals(
                        NormalizeType(tv.GetString()!),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (filter.TryGetProperty("layer", out JsonElement lv)
                && lv.ValueKind == JsonValueKind.String)
            {
                if (!ent.Layer.Equals(
                        lv.GetString(),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (filter.TryGetProperty("color", out JsonElement cv))
            {
                int want = cv.ValueKind == JsonValueKind.Number
                    ? cv.GetInt32()
                    : int.TryParse(cv.GetString(), out int c)
                        ? c
                        : -1;

                if (want >= 0 && ent.ColorIndex != want)
                    return false;
            }

            if (filter.TryGetProperty("block", out JsonElement bv)
                && bv.ValueKind == JsonValueKind.String
                && ent is BlockReference br)
            {
                string name = BlockName(br);

                if (!name.Equals(bv.GetString(),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (filter.TryGetProperty("tag", out JsonElement gv)
                && gv.ValueKind == JsonValueKind.String)
            {
                string? tag = GetTag(ent);

                if (tag == null
                    || !tag.Equals(gv.GetString(),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        public static string BlockName(BlockReference br)
        {
            try
            {
                return br.BlockName;
            }
            catch
            {
                // 动态块等特殊情况下 BlockName 可能不可用
                using Transaction tr = br.Database
                    .TransactionManager.StartTransaction();

                var btr = (BlockTableRecord)tr.GetObject(
                    br.BlockTableRecord, OpenMode.ForRead);

                string name = btr.Name;
                tr.Commit();
                return name;
            }
        }

        /// <summary>在指定空间按过滤条件扫描图元。</summary>
        public static List<ObjectId> ScanSpace(
            Transaction tr, Database db, string space,
            JsonElement filter)
        {
            var ids = new List<ObjectId>();
            string sp = space.Trim().ToLowerInvariant();

            ObjectId modelId = SymbolUtilityServices
                .GetBlockModelSpaceId(db);

            ObjectId paperId = SymbolUtilityServices
                .GetBlockPaperSpaceId(db);

            IEnumerable<ObjectId> spaces = sp switch
            {
                "model" => new[] { modelId },
                "paper" => new[] { paperId },
                "all" => new[] { modelId, paperId },
                _ => new[] { db.CurrentSpaceId }
            };

            foreach (ObjectId spaceId in spaces)
            {
                if (spaceId.IsNull)
                    continue;

                var btr = (BlockTableRecord)tr.GetObject(
                    spaceId, OpenMode.ForRead);

                foreach (ObjectId id in btr)
                {
                    if (id.IsErased)
                        continue;

                    DBObject obj = tr.GetObject(
                        id, OpenMode.ForRead, false, true);

                    if (obj is not Entity ent)
                        continue;

                    if (MatchesFilter(ent, filter))
                        ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// 解析单个选择描述（数组=handle 列表，对象=选择描述）。
        /// </summary>
        public static List<ObjectId> ResolveSpec(
            JsonElement spec, Document doc, Transaction tr)
        {
            if (spec.ValueKind == JsonValueKind.Array)
            {
                return spec.EnumerateArray()
                    .Select(e => TryIdByHandle(
                        doc.Database, e.GetString()!))
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToList();
            }

            if (spec.ValueKind == JsonValueKind.Object)
                return BySpec(spec, doc, tr);

            return new List<ObjectId>();
        }

        /// <summary>
        /// 通用选择集解析：
        /// 优先 handles 数组；其次 entities/selection 描述对象；
        /// 若参数本身含 mode 字段则视参数为选择描述；
        /// 否则使用当前隐含选择集。
        /// </summary>
        public static List<ObjectId> ResolveSelection(
            JsonElement args, Document doc, Transaction tr)
        {
            if (args.ValueKind == JsonValueKind.Object)
            {
                if (args.TryGetProperty("handles", out JsonElement hv)
                    && hv.ValueKind == JsonValueKind.Array)
                {
                    return hv.EnumerateArray()
                        .Select(e => TryIdByHandle(
                            doc.Database, e.GetString()!))
                        .Where(id => !id.IsNull && !id.IsErased)
                        .ToList();
                }

                JsonElement spec = default;

                if (args.TryGetProperty("entities", out JsonElement ev))
                    spec = ev;
                else if (args.TryGetProperty("selection", out JsonElement sv))
                    spec = sv;
                else if (args.TryGetProperty("mode", out _))
                    spec = args;

                if (spec.ValueKind == JsonValueKind.Object)
                    return BySpec(spec, doc, tr);
            }

            // 隐含选择集
            PromptSelectionResult res = doc.Editor.SelectImplied();

            return res.Status == PromptStatus.OK
                ? res.Value.GetObjectIds()
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToList()
                : new List<ObjectId>();
        }

        private static List<ObjectId> BySpec(
            JsonElement spec, Document doc, Transaction tr)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;

            string mode = OptStr(spec, "mode", "filter")
                .Trim().ToLowerInvariant();

            string space = OptStr(spec, "space", "current");

            switch (mode)
            {
                case "handles":
                    {
                        if (spec.TryGetProperty("handles",
                                out JsonElement hv))
                        {
                            return hv.EnumerateArray()
                                .Select(e => TryIdByHandle(
                                    db, e.GetString()!))
                                .Where(id => !id.IsNull && !id.IsErased)
                                .ToList();
                        }

                        return new List<ObjectId>();
                    }

                case "all":
                    return ScanSpace(tr, db, space, spec);

                case "last":
                    {
                        PromptSelectionResult r = ed.SelectLast();

                        return r.Status == PromptStatus.OK
                            ? r.Value.GetObjectIds()
                                .Where(id => !id.IsNull && !id.IsErased)
                                .ToList()
                            : new List<ObjectId>();
                    }

                case "window":
                case "crossing":
                    {
                        Point3d p1 = OptPoint(spec, "p1", default);
                        Point3d p2 = OptPoint(spec, "p2", default);

                        if (!Has(spec, "p1") || !Has(spec, "p2"))
                            throw new McpException(
                                "window/crossing 需要 p1 和 p2");

                        PromptSelectionResult r =
                            mode == "window"
                                ? ed.SelectWindow(p1, p2)
                                : ed.SelectCrossingWindow(p1, p2);

                        return r.Status == PromptStatus.OK
                            ? r.Value.GetObjectIds()
                                .Where(id => !id.IsNull && !id.IsErased)
                                .ToList()
                            : new List<ObjectId>();
                    }

                case "fence":
                    {
                        List<Point3d> pts = OptPoints(spec, "points");

                        if (pts.Count < 2)
                            throw new McpException(
                                "fence 至少需要 2 个点");

                        PromptSelectionResult r =
                            ed.SelectFence(
                                new Point3dCollection(pts.ToArray()));

                        return r.Status == PromptStatus.OK
                            ? r.Value.GetObjectIds()
                                .Where(id => !id.IsNull && !id.IsErased)
                                .ToList()
                            : new List<ObjectId>();
                    }

                case "implied":
                    {
                        PromptSelectionResult r = ed.SelectImplied();

                        return r.Status == PromptStatus.OK
                            ? r.Value.GetObjectIds()
                                .Where(id => !id.IsNull && !id.IsErased)
                                .ToList()
                            : new List<ObjectId>();
                    }

                default:
                    // layer/type/color/block/tag 等过滤模式
                    return ScanSpace(tr, db, space, spec);
            }
        }

        // ---------------------------------------------------------------
        // 标签（XData）
        // ---------------------------------------------------------------

        public static void EnsureRegApp(
            Transaction tr, Database db)
        {
            RegAppTable rat = (RegAppTable)tr.GetObject(
                db.RegAppTableId, OpenMode.ForRead);

            if (rat.Has(TagApp))
                return;

            rat.UpgradeOpen();

            var record = new RegAppTableRecord { Name = TagApp };
            rat.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        public static string? GetTag(Entity ent)
        {
            if (ent.XData == null)
                return null;

            foreach (TypedValue tv in ent.XData)
            {
                if (tv.TypeCode
                    == (int)DxfCode.ExtendedDataAsciiString
                    && tv.Value is string s)
                    return s;
            }

            return null;
        }

        public static void SetTag(
            Transaction tr, Database db, Entity ent, string tag)
        {
            EnsureRegApp(tr, db);

            ent.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName, TagApp),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString, tag));
        }

        public static void RemoveTag(Entity ent)
        {
            ent.XData = null;
        }

        // ---------------------------------------------------------------
        // 几何计算
        // ---------------------------------------------------------------

        public static List<Point3d> Intersections(
            Entity a, Entity b)
        {
            var pts = new Point3dCollection();

            try
            {
                a.IntersectWith(
                    b, Intersect.OnBothOperands,
                    pts, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // 某些实体组合不支持求交
            }

            var list = new List<Point3d>();
            foreach (Point3d p in pts)
                list.Add(p);

            return list;
        }

        public static bool PointsEqual(
            Point3d a, Point3d b, double tol = 1e-4) =>
            a.DistanceTo(b) <= tol;

        public static bool PointInPolygon(
            Point2d p, IList<Point2d> poly)
        {
            bool inside = false;
            int n = poly.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2d pi = poly[i];
                Point2d pj = poly[j];

                if ((pi.Y > p.Y) != (pj.Y > p.Y)
                    && p.X < (pj.X - pi.X) * (p.Y - pi.Y)
                        / (pj.Y - pi.Y) + pi.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>判断点是否在闭合曲线内（折线/圆/椭圆）。</summary>
        public static bool ClosedCurveContains(
            Curve curve, Point3d point)
        {
            switch (curve)
            {
                case Polyline pl when pl.Closed:
                    {
                        var pts = new List<Point2d>();

                        for (int i = 0; i < pl.NumberOfVertices; i++)
                            pts.Add(pl.GetPoint2dAt(i));

                        return PointInPolygon(
                            new Point2d(point.X, point.Y), pts);
                    }

                case Polyline2d p2d when p2d.Closed:
                    {
                        var pts = new List<Point2d>();

                        foreach (ObjectId vid in p2d)
                        {
                            var v = (Vertex2d)vid.GetObject(
                                OpenMode.ForRead);

                            pts.Add(new Point2d(
                                v.Position.X, v.Position.Y));
                        }

                        return PointInPolygon(
                            new Point2d(point.X, point.Y), pts);
                    }

                case Circle c:
                    return c.Center.DistanceTo(point) <= c.Radius;

                case Ellipse el:
                    {
                        Vector3d d = (point - el.Center)
                            .TransformBy(el.Ecs.Inverse());

                        double r = d.X * d.X
                            / (el.MajorRadius * el.MajorRadius)
                            + d.Y * d.Y
                            / (el.MinorRadius * el.MinorRadius);

                        return r <= 1.0 + Tol;
                    }

                default:
                    return false;
            }
        }

        public static double CurveLength(Curve curve)
        {
            try
            {
                return curve.GetDistanceAtParameter(
                    curve.EndParam);
            }
            catch
            {
                return 0;
            }
        }

        public static bool EllipseClosed(Ellipse el) =>
            Math.Abs(el.EndAngle - el.StartAngle)
            >= Math.PI * 2 - Tol;

        public static bool SplineClosed(Spline sp) =>
            sp.StartPoint.DistanceTo(sp.EndPoint) < Tol;

        public static double CurveArea(Curve curve) => curve switch
        {
            Circle c => c.Area,
            Polyline pl when pl.Closed => pl.Area,
            Ellipse el when EllipseClosed(el) =>
                Math.PI * el.MajorRadius * el.MinorRadius,
            _ => 0
        };

        public static Extents3d BoundsOf(Entity ent)
        {
            try
            {
                return ent.GeometricExtents;
            }
            catch
            {
                return new Extents3d();
            }
        }

        public static Dictionary<string, object?> BBoxJson(
            Extents3d e) =>
            Result(
                "min", Pt(e.MinPoint),
                "max", Pt(e.MaxPoint),
                "center", Pt(new Point3d(
                    (e.MinPoint.X + e.MaxPoint.X) / 2,
                    (e.MinPoint.Y + e.MaxPoint.Y) / 2,
                    (e.MinPoint.Z + e.MaxPoint.Z) / 2)),
                "width", R(e.MaxPoint.X - e.MinPoint.X),
                "height", R(e.MaxPoint.Y - e.MinPoint.Y));

        public static string Md5Hex(string text)
        {
            using MD5 md5 = MD5.Create();

            byte[] hash = md5.ComputeHash(
                Encoding.UTF8.GetBytes(text));

            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string Fmt(double v) => v.ToString(
            "0.######", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------
        // 图元 JSON 描述
        // ---------------------------------------------------------------

        public static Dictionary<string, object?> CommonProps(
            Entity ent)
        {
            var d = new Dictionary<string, object?>
            {
                ["handle"] = ent.Handle.ToString(),
                ["type"] = DxfName(ent),
                ["layer"] = ent.Layer,
                ["color"] = ent.ColorIndex,
                ["linetype"] = ent.Linetype,
                ["linetype_scale"] = R(ent.LinetypeScale),
                ["tag"] = GetTag(ent)
            };

            Extents3d bounds = BoundsOf(ent);
            d["bounds"] = BBoxJson(bounds);

            return d;
        }

        /// <summary>描述图元：基础属性 + 类型特有几何信息。</summary>
        public static Dictionary<string, object?> DescribeEntity(
            Entity ent, bool detailed)
        {
            Dictionary<string, object?> d = CommonProps(ent);

            switch (ent)
            {
                case Line line:
                    {
                        Vector3d v = line.EndPoint - line.StartPoint;
                        d["start"] = Pt(line.StartPoint);
                        d["end"] = Pt(line.EndPoint);
                        d["length"] = R(line.Length);
                        d["angle"] = R(Math.Atan2(v.Y, v.X)
                            * 180.0 / Math.PI);
                        break;
                    }

                case Polyline pl:
                    {
                        d["closed"] = pl.Closed;
                        d["vertices"] = pl.NumberOfVertices;
                        d["length"] = R(pl.Length);
                        d["area"] = pl.Closed ? R(pl.Area) : 0;

                        if (detailed)
                        {
                            var pts = new List<object>();

                            for (int i = 0;
                                i < pl.NumberOfVertices; i++)
                            {
                                pts.Add(Result(
                                    "point", Pt2(pl.GetPoint2dAt(i)),
                                    "bulge", R(pl.GetBulgeAt(i))));
                            }

                            d["points"] = pts;
                        }

                        break;
                    }

                case Polyline3d pl3:
                    {
                        d["closed"] = pl3.Closed;
                        d["length"] = R(pl3.Length);
                        break;
                    }

                case Polyline2d pl2:
                    {
                        d["closed"] = pl2.Closed;
                        d["length"] = R(pl2.Length);
                        break;
                    }

                case Arc arc:
                    {
                        d["center"] = Pt(arc.Center);
                        d["radius"] = R(arc.Radius);
                        d["start_angle"] = R(arc.StartAngle
                            * 180.0 / Math.PI);
                        d["end_angle"] = R(arc.EndAngle
                            * 180.0 / Math.PI);
                        d["start"] = Pt(arc.StartPoint);
                        d["end"] = Pt(arc.EndPoint);
                        d["length"] = R(arc.Length);
                        break;
                    }

                case Circle c:
                    {
                        d["center"] = Pt(c.Center);
                        d["radius"] = R(c.Radius);
                        d["diameter"] = R(c.Radius * 2);
                        d["area"] = R(c.Area);
                        d["circumference"] = R(c.Circumference);
                        break;
                    }

                case Ellipse el:
                    {
                        d["center"] = Pt(el.Center);
                        d["major_radius"] = R(el.MajorRadius);
                        d["minor_radius"] = R(el.MinorRadius);
                        d["radius_ratio"] = R(el.RadiusRatio);
                        d["closed"] = EllipseClosed(el);
                        d["area"] = R(Math.PI * el.MajorRadius
                            * el.MinorRadius);
                        break;
                    }

                case Spline sp:
                    {
                        d["degree"] = sp.Degree;
                        d["num_fit_points"] = sp.NumFitPoints;
                        d["closed"] = SplineClosed(sp);
                        d["length"] = R(CurveLength(sp));

                        if (detailed && sp.NumFitPoints > 0)
                        {
                            var pts = new List<object>();

                            for (int i = 0; i < sp.NumFitPoints; i++)
                                pts.Add(Pt(sp.GetFitPointAt(i)));

                            d["fit_points"] = pts;
                        }

                        break;
                    }

                case DBPoint pt:
                    {
                        d["position"] = Pt(pt.Position);
                        break;
                    }

                case Table table:
                    {
                        d["rows"] = table.Rows.Count;
                        d["columns"] = table.Columns.Count;
                        d["position"] = Pt(table.Position);
                        break;
                    }

                case BlockReference br:
                    {
                        d["block_name"] = BlockName(br);
                        d["position"] = Pt(br.Position);
                        d["rotation"] = R(br.Rotation
                            * 180.0 / Math.PI);
                        d["scale"] = new object[]
                        {
                            R(br.ScaleFactors.X),
                            R(br.ScaleFactors.Y),
                            R(br.ScaleFactors.Z)
                        };
                        d["attribute_count"] =
                            br.AttributeCollection.Count;

                        if (detailed
                            && br.AttributeCollection.Count > 0)
                        {
                            var attrs = new Dictionary<string, object?>();

                            foreach (ObjectId aid
                                in br.AttributeCollection)
                            {
                                var att = (AttributeReference)
                                    aid.GetObject(OpenMode.ForRead);

                                attrs[att.Tag] = att.TextString;
                            }

                            d["attributes"] = attrs;
                        }

                        break;
                    }

                case AttributeDefinition ad:
                    {
                        d["tag"] = ad.Tag;
                        d["text"] = ad.TextString;
                        d["position"] = Pt(ad.Position);
                        d["height"] = R(ad.Height);
                        break;
                    }

                case AttributeReference ar:
                    {
                        d["tag"] = ar.Tag;
                        d["text"] = ar.TextString;
                        d["position"] = Pt(ar.Position);
                        d["height"] = R(ar.Height);
                        break;
                    }

                case DBText t:
                    {
                        d["text"] = t.TextString;
                        d["position"] = Pt(t.Position);
                        d["height"] = R(t.Height);
                        d["rotation"] = R(t.Rotation
                            * 180.0 / Math.PI);
                        break;
                    }

                case MText mt:
                    {
                        d["text"] = mt.Contents;
                        d["position"] = Pt(mt.Location);
                        d["height"] = R(mt.TextHeight);
                        d["rotation"] = R(mt.Rotation
                            * 180.0 / Math.PI);
                        break;
                    }

                case Dimension dim:
                    {
                        d["text"] = dim.DimensionText;
                        d["measurement"] = R(dim.Measurement);
                        break;
                    }

                case Hatch hatch:
                    {
                        d["pattern"] = hatch.PatternName;
                        d["area"] = R(hatch.Area);
                        d["associative"] = hatch.Associative;
                        d["pattern_scale"] = R(hatch.PatternScale);
                        d["pattern_angle"] = R(hatch.PatternAngle
                            * 180.0 / Math.PI);
                        break;
                    }

                case MLeader ml:
                    {
                        if (ml.ContentType == ContentType.MTextContent)
                            d["text"] = ml.MText.Contents;

                        d["leader_count"] = ml.LeaderCount;
                        break;
                    }

                case Region reg:
                    {
                        d["area"] = R(reg.Area);
                        d["perimeter"] = R(reg.Perimeter);
                        break;
                    }

                case Viewport vp:
                    {
                        d["number"] = vp.Number;
                        d["center"] = Pt(vp.CenterPoint);
                        d["width"] = R(vp.Width);
                        d["height"] = R(vp.Height);
                        d["on"] = vp.On;
                        break;
                    }
            }

            if (ent is Curve curve)
            {
                d.TryAdd("start_point", Pt(curve.StartPoint));
                d.TryAdd("end_point", Pt(curve.EndPoint));
            }

            return d;
        }

        public static Dictionary<string, object?> DescribeById(
            Transaction tr, Database db, ObjectId id,
            bool detailed = false)
        {
            var ent = (Entity)tr.GetObject(
                id, OpenMode.ForRead, false, true);

            return DescribeEntity(ent, detailed);
        }
    }
}
