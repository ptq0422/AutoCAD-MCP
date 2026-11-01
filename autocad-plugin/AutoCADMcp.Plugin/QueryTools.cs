using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

using static AutoCADMcp.Plugin.ToolContext;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 查询工具：get_drawing_summary / get_drawing_fingerprint /
    /// query_entities / get_entity / entity_info / entity_count /
    /// find_text / get_bounds / sysvar / query_relations /
    /// trace_boundary / verify_result / compare_drawings /
    /// list_styles / get_selection / get_dwg_info /
    /// read_commandline / layer_query / list_all。
    /// </summary>
    public static class QueryTools
    {
        public static void Register()
        {
            ToolRegistry.Register("get_drawing_summary", GetDrawingSummaryTool);
            ToolRegistry.Register("get_drawing_fingerprint", GetDrawingFingerprintTool);
            ToolRegistry.Register("query_entities", QueryEntitiesTool);
            ToolRegistry.Register("get_entity", GetEntityTool);
            ToolRegistry.Register("entity_info", EntityInfoTool);
            ToolRegistry.Register("entity_count", EntityCountTool);
            ToolRegistry.Register("find_text", FindTextTool);
            ToolRegistry.Register("get_bounds", GetBoundsTool);
            ToolRegistry.Register("sysvar", SysvarTool);
            ToolRegistry.Register("query_relations", QueryRelationsTool);
            ToolRegistry.Register("trace_boundary", TraceBoundaryTool);
            ToolRegistry.Register("verify_result", VerifyResultTool);
            ToolRegistry.Register("compare_drawings", CompareDrawingsTool);
            ToolRegistry.Register("list_styles", ListStylesTool);
            ToolRegistry.Register("get_selection", GetSelectionTool);
            ToolRegistry.Register("get_dwg_info", GetDwgInfoTool);
            ToolRegistry.Register("read_commandline", ReadCommandlineTool);
            ToolRegistry.Register("layer_query", LayerQueryTool);
            ToolRegistry.Register("list_all", ListAllTool);
        }

        // ---------------------------------------------------------------
        // get_drawing_summary
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetDrawingSummaryTool(
            Document doc, JsonElement args)
        {
            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var modelCounts = new Dictionary<string, int>();
            var paperCounts = new Dictionary<string, int>();
            var layers = new List<object>();

            ObjectId modelId = SymbolUtilityServices
                .GetBlockModelSpaceId(db);

            ObjectId paperId = SymbolUtilityServices
                .GetBlockPaperSpaceId(db);

            void Count(ObjectId spaceId,
                Dictionary<string, int> target)
            {
                if (spaceId.IsNull)
                    return;

                var btr = (BlockTableRecord)tr.GetObject(
                    spaceId, OpenMode.ForRead);

                foreach (ObjectId oid in btr)
                {
                    if (tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is not Entity ent)
                        continue;

                    string type = DxfName(ent);

                    target.TryGetValue(type, out int n);
                    target[type] = n + 1;
                }
            }

            Count(modelId, modelCounts);
            Count(paperId, paperCounts);

            LayerTable lt = (LayerTable)tr.GetObject(
                db.LayerTableId, OpenMode.ForRead);

            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(
                    id, OpenMode.ForRead);

                layers.Add(Result(
                    "name", ltr.Name,
                    "color", ltr.Color.ColorIndex,
                    "off", ltr.IsOff,
                    "frozen", ltr.IsFrozen,
                    "locked", ltr.IsLocked,
                    "current", db.Clayer == id));
            }

            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            int blockCount = bt.Cast<ObjectId>().Count(
                id => !((BlockTableRecord)tr.GetObject(
                    id, OpenMode.ForRead)).IsLayout
                    && !((BlockTableRecord)tr.GetObject(
                        id, OpenMode.ForRead)).IsAnonymous);

            Extents3d ext = GetSpaceExtents(
                tr, db, db.CurrentSpaceId);

            tr.Commit();

            return Result(
                "drawing_name", doc.Name,
                "path", db.Filename,
                "units", db.Insunits.ToString(),
                "version", db.OriginalFileVersion,
                "model_space_entities", modelCounts.Values.Sum(),
                "paper_space_entities", paperCounts.Values.Sum(),
                "model_counts", modelCounts
                    .OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                "paper_counts", paperCounts
                    .OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                "layer_count", layers.Count,
                "layers", layers,
                "block_count", blockCount,
                "current_layer", CurrentLayerName(db),
                "extents", BBoxJson(ext));
        }

        private static string CurrentLayerName(Database db)
        {
            using Transaction tr =
                db.TransactionManager.StartTransaction();

            string name = ((LayerTableRecord)tr.GetObject(
                db.Clayer, OpenMode.ForRead)).Name;

            tr.Commit();
            return name;
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

        // ---------------------------------------------------------------
        // get_drawing_fingerprint
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetDrawingFingerprintTool(
            Document doc, JsonElement args)
        {
            Database db = doc.Database;
            string space = OptStr(args, "space", "current");

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var lines = new List<string>();

            foreach (ObjectId id in ScanSpace(
                tr, db, space, default))
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                Extents3d b = BoundsOf(ent);

                lines.Add(
                    $"{ent.Handle.ToString()}|{DxfName(ent)}|" +
                    $"{ent.Layer}|" +
                    $"{Fmt(b.MinPoint.X)},{Fmt(b.MinPoint.Y)}," +
                    $"{Fmt(b.MaxPoint.X)},{Fmt(b.MaxPoint.Y)}");
            }

            lines.Sort(StringComparer.Ordinal);

            string fingerprint = Md5Hex(
                string.Join("\n", lines));

            tr.Commit();

            return Result(
                "fingerprint", fingerprint,
                "entity_count", lines.Count,
                "space", space);
        }

        // ---------------------------------------------------------------
        // query_entities
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> QueryEntitiesTool(
            Document doc, JsonElement args)
        {
            string space = OptStr(args, "space", "current");
            int limit = OptInt(args, "limit", 200);
            int offset = OptInt(args, "offset", 0);
            bool detailed = OptBool(args, "detailed", false);
            string sort = OptStr(args, "sort", "handle")
                .Trim().ToLowerInvariant();

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ScanSpace(
                tr, doc.Database, space, args);

            var items = new List<(long Handle, string Type,
                string Layer, double Length, double Area,
                Dictionary<string, object?> Desc)>();

            foreach (ObjectId id in ids)
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                double length = ent is Curve c
                    ? CurveLength(c) : 0;

                double area = ent is Curve curve
                    ? CurveArea(curve) : 0;

                items.Add((
                    long.Parse(ent.Handle.ToString(),
                        System.Globalization.NumberStyles.HexNumber),
                    DxfName(ent),
                    ent.Layer,
                    length,
                    area,
                    detailed ? DescribeEntity(ent, true) : null!));
            }

            IEnumerable<(long Handle, string Type, string Layer,
                double Length, double Area,
                Dictionary<string, object?> Desc)> sorted =
                sort switch
                {
                    "type" => items.OrderBy(i => i.Type)
                        .ThenBy(i => i.Handle),
                    "layer" => items.OrderBy(i => i.Layer)
                        .ThenBy(i => i.Handle),
                    "length" => items.OrderByDescending(i => i.Length),
                    "area" => items.OrderByDescending(i => i.Area),
                    _ => items.OrderBy(i => i.Handle)
                };

            var page = sorted
                .Skip(Math.Max(0, offset))
                .Take(Math.Max(0, limit))
                .ToList();

            var results = new List<object>();

            using Transaction tr2 =
                doc.Database.TransactionManager.StartTransaction();

            foreach (var item in page)
            {
                if (detailed)
                {
                    results.Add(item.Desc);
                }
                else
                {
                    var ent = (Entity)tr2.GetObject(
                        doc.Database.GetObjectId(false,
                            new Handle(item.Handle), 0),
                        OpenMode.ForRead, false, true);

                    results.Add(Result(
                        "handle", ent.Handle.ToString(),
                        "type", item.Type,
                        "layer", item.Layer,
                        "length", R(item.Length),
                        "area", R(item.Area)));
                }
            }

            tr2.Commit();
            tr.Commit();

            return Result(
                "total", ids.Count,
                "returned", results.Count,
                "offset", offset,
                "space", space,
                "entities", results);
        }

        // ---------------------------------------------------------------
        // get_entity / entity_info
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetEntityTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            Entity ent = GetEntityByHandle(
                tr, doc.Database, handle);

            Dictionary<string, object?> payload =
                DescribeEntity(ent, true);

            tr.Commit();
            return payload;
        }

        private static Dictionary<string, object?> EntityInfoTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            Entity ent = GetEntityByHandle(
                tr, doc.Database, handle);

            Dictionary<string, object?> payload =
                DescribeEntity(ent, true);

            // 组归属
            var gd = (DBDictionary)tr.GetObject(
                doc.Database.GroupDictionaryId, OpenMode.ForRead);

            var groups = new List<string>();

            foreach (DBDictionaryEntry entry in gd)
            {
                var group = (Group)tr.GetObject(
                    entry.Value, OpenMode.ForRead);

                if (group.GetAllEntityIds()
                    .Any(id => id == ent.ObjectId))
                    groups.Add(group.Name);
            }

            payload["groups"] = groups;
            payload["handle_value"] = ent.Handle.Value;

            tr.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // entity_count
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> EntityCountTool(
            Document doc, JsonElement args)
        {
            string space = OptStr(args, "space", "current");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ScanSpace(
                tr, doc.Database, space, args);

            var byType = new Dictionary<string, int>();

            foreach (ObjectId id in ids)
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                string type = DxfName(ent);

                byType.TryGetValue(type, out int n);
                byType[type] = n + 1;
            }

            tr.Commit();

            return Result(
                "count", ids.Count,
                "space", space,
                "by_type", byType
                    .OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        // ---------------------------------------------------------------
        // find_text
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> FindTextTool(
            Document doc, JsonElement args)
        {
            string text = ReqStr(args, "text");
            string mode = OptStr(args, "mode", "contains")
                .Trim().ToLowerInvariant();

            bool caseSensitive = OptBool(
                args, "case_sensitive", false);

            string space = OptStr(args, "space", "current");
            int limit = OptInt(args, "limit", 100);

            StringComparison comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            var matches = new List<object>();

            foreach (ObjectId id in ScanSpace(
                tr, doc.Database, space, default))
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                string? content = ent switch
                {
                    AttributeDefinition ad => ad.TextString,
                    AttributeReference ar => ar.TextString,
                    DBText t => t.TextString,
                    MText mt => mt.Contents,
                    Dimension dim => dim.DimensionText,
                    _ => null
                };

                if (content == null)
                    continue;

                bool hit = mode == "exact"
                    ? string.Equals(content, text, comparison)
                    : content.Contains(text, comparison);

                if (!hit)
                    continue;

                Point3d pos = ent switch
                {
                    AttributeDefinition ad => ad.Position,
                    AttributeReference ar => ar.Position,
                    DBText t => t.Position,
                    MText mt => mt.Location,
                    _ => default
                };

                matches.Add(Result(
                    "handle", ent.Handle.ToString(),
                    "type", DxfName(ent),
                    "layer", ent.Layer,
                    "position", pos == default ? null : Pt(pos),
                    "text", content));

                if (matches.Count >= limit)
                    break;
            }

            tr.Commit();

            return Result(
                "keyword", text,
                "matches", matches,
                "count", matches.Count);
        }

        // ---------------------------------------------------------------
        // get_bounds
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetBoundsTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ResolveSelection(args, doc, tr);

            if (ids.Count == 0)
            {
                // 未选择时取当前空间范围
                Extents3d all = GetSpaceExtents(
                    tr, doc.Database, doc.Database.CurrentSpaceId);

                tr.Commit();

                return Result(
                    "count", 0,
                    "bounds", BBoxJson(all));
            }

            Extents3d total = new Extents3d();
            bool first = true;

            var perEntity = new List<object>();

            foreach (ObjectId id in ids)
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                Extents3d e = BoundsOf(ent);

                perEntity.Add(Result(
                    "handle", ent.Handle.ToString(),
                    "type", DxfName(ent),
                    "bounds", BBoxJson(e)));

                if (first)
                {
                    total = e;
                    first = false;
                }
                else
                {
                    total.AddExtents(e);
                }
            }

            tr.Commit();

            return Result(
                "count", ids.Count,
                "bounds", BBoxJson(total),
                "entities", perEntity);
        }

        // ---------------------------------------------------------------
        // sysvar
        // ---------------------------------------------------------------

        private static readonly string[] CommonSysvars =
        {
            "CECOLOR", "CELTYPE", "CLAYER", "CMDECHO", "DIMSTYLE",
            "TEXTSTYLE", "LTSCALE", "INSUNITS", "LUPREC", "OSMODE",
            "PDMODE", "PDSIZE", "FILLETRAD", "CHAMFERA", "CHAMFERB",
            "OFFSETDIST", "ATTDIA", "ATTMODE", "MIRRTEXT", "ORTHOMODE",
            "SNAPMODE", "GRIDMODE", "ELEVATION", "THICKNESS",
            "VIEWCTR", "VIEWSIZE", "LIMCHECK", "UCSNAME", "WORLDUCS"
        };

        private static Dictionary<string, object?> SysvarTool(
            Document doc, JsonElement args)
        {
            string op = OptStr(args, "op", "get")
                .Trim().ToLowerInvariant();

            if (op == "list")
            {
                var values = new Dictionary<string, object?>();

                foreach (string varName in CommonSysvars)
                {
                    try
                    {
                        values[varName] = Application
                            .GetSystemVariable(varName)
                            ?.ToString();
                    }
                    catch
                    {
                        // 忽略读取失败的系统变量
                    }
                }

                return Result(
                    "count", values.Count,
                    "variables", values);
            }

            string name = ReqStr(args, "name");

            if (op == "set")
            {
                object value;

                if (args.TryGetProperty("value", out JsonElement vv))
                {
                    value = vv.ValueKind == JsonValueKind.Number
                        ? vv.GetDouble()
                        : vv.GetString() ?? "";
                }
                else
                {
                    value = OptStr(args, "value", "");
                }

                try
                {
                    Application.SetSystemVariable(name, value);
                }
                catch (System.Exception ex)
                {
                    throw new McpException(
                        $"设置系统变量 {name} 失败：{ex.Message}");
                }

                return Result(
                    "name", name,
                    "value", value,
                    "message", "系统变量已设置");
            }

            try
            {
                object? v = Application.GetSystemVariable(name);

                return Result(
                    "name", name,
                    "value", v?.ToString(),
                    "type", v?.GetType().Name);
            }
            catch (System.Exception ex)
            {
                throw new McpException(
                    $"读取系统变量 {name} 失败：{ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // query_relations
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> QueryRelationsTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");
            double tolerance = OptDouble(args, "tolerance", 1e-3);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            Entity target = GetEntityByHandle(
                tr, doc.Database, handle);

            var relations = new List<object>();

            foreach (ObjectId id in ScanSpace(
                tr, doc.Database, "all", default))
            {
                if (id == target.ObjectId)
                    continue;

                var other = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                string reason = RelationReason(
                    target, other, tolerance);

                if (reason == null)
                    continue;

                relations.Add(Result(
                    "handle", other.Handle.ToString(),
                    "type", DxfName(other),
                    "layer", other.Layer,
                    "relation", reason));
            }

            tr.Commit();

            return Result(
                "handle", handle,
                "type", DxfName(target),
                "relations", relations,
                "count", relations.Count);
        }

        private static string? RelationReason(
            Entity a, Entity b, double tolerance)
        {
            // 共享端点
            if (a is Curve ca && b is Curve cb)
            {
                Point3d[] ptsA = { ca.StartPoint, ca.EndPoint };
                Point3d[] ptsB = { cb.StartPoint, cb.EndPoint };

                foreach (Point3d pa in ptsA)
                {
                    foreach (Point3d pb in ptsB)
                    {
                        if (pa.DistanceTo(pb) <= tolerance)
                            return "shared_endpoint";
                    }
                }
            }

            // 相交
            if (Intersections(a, b).Count > 0)
                return "intersect";

            return null;
        }

        // ---------------------------------------------------------------
        // trace_boundary
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> TraceBoundaryTool(
            Document doc, JsonElement args)
        {
            Point3d point = ReqPoint(args, "point");
            bool detectIslands = OptBool(
                args, "detect_islands", false);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            DBObjectCollection? traced = null;

            try
            {
                traced = doc.Editor.TraceBoundary(
                    point, detectIslands);
            }
            catch
            {
                // 部分版本 TraceBoundary 可能不可用，返回空边界
            }

            var boundary = new List<object>();

            if (traced != null)
            {
                try
                {
                    foreach (DBObject obj in traced)
                    {
                        if (obj is not Curve curve)
                            continue;

                        boundary.Add(Result(
                            "type", DxfName(curve),
                            "closed", curve is Polyline pl
                                && pl.Closed
                                || curve is Circle
                                || curve is Ellipse el
                                && EllipseClosed(el)
                                || curve is Spline sp
                                && SplineClosed(sp),
                            "length", R(CurveLength(curve))));
                    }
                }
                finally
                {
                    // 边界为临时曲线，用后即弃
                    foreach (DBObject obj in traced)
                        obj.Dispose();
                }
            }

            tr.Commit();

            return Result(
                "point", Pt(point),
                "boundary", boundary,
                "count", boundary.Count);
        }

        // ---------------------------------------------------------------
        // verify_result
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> VerifyResultTool(
            Document doc, JsonElement args)
        {
            Dictionary<string, object?>? lastOp = ToolRegistry.LastOp;

            if (lastOp == null)
            {
                return Result(
                    "message", "没有可验证的操作记录",
                    "verified", false);
            }

            var existence = new Dictionary<string, object?>();

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            if (lastOp.TryGetValue("handle", out object? hv)
                && hv is string h)
            {
                existence[h] = Exists(tr, doc.Database, h);
            }

            foreach (string key in new[]
            {
                "created", "changed", "deleted", "consumed"
            })
            {
                if (lastOp.TryGetValue(key, out object? list)
                    && list is List<string> handles)
                {
                    foreach (string item in handles.Take(20))
                        existence[item] = Exists(
                            tr, doc.Database, item);
                }
            }

            tr.Commit();

            var payload = new Dictionary<string, object?>(lastOp)
            {
                ["entity_exists"] = existence
            };

            return payload;
        }

        private static bool Exists(
            Transaction tr, Database db, string handleText)
        {
            try
            {
                ObjectId id = IdByHandle(db, handleText);

                return !id.IsNull
                    && !id.IsErased
                    && tr.GetObject(id, OpenMode.ForRead,
                        false, true) != null;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // compare_drawings
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> CompareDrawingsTool(
            Document doc, JsonElement args)
        {
            string path = ReqStr(args, "path");

            if (!File.Exists(path))
                throw new McpException($"文件不存在：{path}");

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            Dictionary<string, int> currentCounts = CountByType(
                tr, doc.Database);

            Extents3d currentExtents = GetSpaceExtents(
                tr, doc.Database, doc.Database.CurrentSpaceId);

            tr.Commit();

            using (var other = new Database(false, true))
            {
                other.ReadDwgFile(
                    path,
                    FileOpenMode.OpenForReadAndAllShare,
                    false,
                    null);

                using Transaction trOther =
                    other.TransactionManager.StartTransaction();

                Dictionary<string, int> otherCounts =
                    CountByType(trOther, other);

                Extents3d otherExtents = GetSpaceExtents(
                    trOther, other, other.CurrentSpaceId);

                trOther.Commit();

                var allTypes = currentCounts.Keys
                    .Union(otherCounts.Keys)
                    .ToList();

                var diff = new List<object>();

                foreach (string type in allTypes)
                {
                    int a = currentCounts.TryGetValue(
                        type, out int ca) ? ca : 0;

                    int b = otherCounts.TryGetValue(
                        type, out int cb) ? cb : 0;

                    if (a != b)
                    {
                        diff.Add(Result(
                            "type", type,
                            "current", a,
                            "other", b,
                            "delta", b - a));
                    }
                }

                other.CloseInput(true);

                var fi = new FileInfo(path);

                return Result(
                    "other_path", path,
                    "other_size", fi.Length,
                    "current_total", currentCounts.Values.Sum(),
                    "other_total", otherCounts.Values.Sum(),
                    "current_extents", BBoxJson(currentExtents),
                    "other_extents", BBoxJson(otherExtents),
                    "differences", diff);
            }
        }

        private static Dictionary<string, int> CountByType(
            Transaction tr, Database db)
        {
            var counts = new Dictionary<string, int>();

            foreach (ObjectId id in ScanSpace(
                tr, db, "all", default))
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                string type = DxfName(ent);

                counts.TryGetValue(type, out int n);
                counts[type] = n + 1;
            }

            return counts;
        }

        // ---------------------------------------------------------------
        // list_styles
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ListStylesTool(
            Document doc, JsonElement args)
        {
            string category = OptStr(args, "category", "all")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var result = new Dictionary<string, object?>();

            if (category is "text" or "all")
            {
                var tst = (TextStyleTable)tr.GetObject(
                    db.TextStyleTableId, OpenMode.ForRead);

                result["text_styles"] = tst.Cast<ObjectId>()
                    .Select(id => ((TextStyleTableRecord)tr.GetObject(
                        id, OpenMode.ForRead)).Name)
                    .ToList();
            }

            if (category is "dim" or "all")
            {
                var dst = (DimStyleTable)tr.GetObject(
                    db.DimStyleTableId, OpenMode.ForRead);

                result["dim_styles"] = dst.Cast<ObjectId>()
                    .Select(id => ((DimStyleTableRecord)tr.GetObject(
                        id, OpenMode.ForRead)).Name)
                    .ToList();
            }

            if (category is "table" or "all")
            {
                var dict = (DBDictionary)tr.GetObject(
                    db.TableStyleDictionaryId, OpenMode.ForRead);

                result["table_styles"] = dict.Cast<DBDictionaryEntry>()
                    .Select(e => e.Key)
                    .ToList();
            }

            if (category is "mleader" or "all")
            {
                var dict = (DBDictionary)tr.GetObject(
                    db.MLeaderStyleDictionaryId, OpenMode.ForRead);

                result["mleader_styles"] = dict.Cast<DBDictionaryEntry>()
                    .Select(e => e.Key)
                    .ToList();
            }

            if (category is "layer" or "all")
            {
                var lt = (LayerTable)tr.GetObject(
                    db.LayerTableId, OpenMode.ForRead);

                result["layers"] = lt.Cast<ObjectId>()
                    .Select(id => ((LayerTableRecord)tr.GetObject(
                        id, OpenMode.ForRead)).Name)
                    .ToList();
            }

            if (category is "linetype" or "all")
            {
                var ltt = (LinetypeTable)tr.GetObject(
                    db.LinetypeTableId, OpenMode.ForRead);

                result["linetypes"] = ltt.Cast<ObjectId>()
                    .Select(id => ((LinetypeTableRecord)tr.GetObject(
                        id, OpenMode.ForRead)).Name)
                    .ToList();
            }

            tr.Commit();
            return result;
        }

        // ---------------------------------------------------------------
        // get_selection
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetSelectionTool(
            Document doc, JsonElement args)
        {
            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            PromptSelectionResult res =
                doc.Editor.SelectImplied();

            var entities = new List<object>();

            if (res.Status == PromptStatus.OK)
            {
                foreach (ObjectId id in res.Value.GetObjectIds())
                {
                    if (tr.GetObject(id, OpenMode.ForRead,
                            false, true) is Entity ent)
                    {
                        entities.Add(DescribeEntity(ent, false));
                    }
                }
            }

            tr.Commit();

            return Result(
                "count", entities.Count,
                "entities", entities);
        }

        // ---------------------------------------------------------------
        // get_dwg_info
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GetDwgInfoTool(
            Document doc, JsonElement args)
        {
            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var summary = db.SummaryInfo;

            var custom = new Dictionary<string, object?>();

            try
            {
                var dict = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);

                foreach (DBDictionaryEntry entry in dict)
                    custom[entry.Key] = entry.Value.ToString();
            }
            catch
            {
                // 忽略命名对象读取失败
            }

            Dictionary<string, object?> payload = Result(
                "drawing_name", doc.Name,
                "path", db.Filename,
                "version", db.OriginalFileVersion,
                "saved_by", summary.LastSavedBy,
                "units", db.Insunits.ToString(),
                "tilemode", db.TileMode,
                "title", summary.Title,
                "subject", summary.Subject,
                "author", summary.Author,
                "keywords", summary.Keywords,
                "comments", summary.Comments,
                "named_objects", custom);

            tr.Commit();

            try
            {
                if (db.Filename.Length > 0
                    && File.Exists(db.Filename))
                {
                    var fi = new FileInfo(db.Filename);
                    payload["file_size"] = fi.Length;
                    payload["last_modified"] = fi.LastWriteTime
                        .ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            catch
            {
                // 忽略文件信息读取失败
            }

            return payload;
        }

        // ---------------------------------------------------------------
        // read_commandline
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ReadCommandlineTool(
            Document doc, JsonElement args)
        {
            int limit = OptInt(args, "limit", 20);

            return Result(
                "operations", ToolRegistry.GetOpLog(limit),
                "note", "此记录为 MCP 插件执行的操作历史，" +
                        "AutoCAD 原生命令行历史无法直接读取");
        }

        // ---------------------------------------------------------------
        // layer_query
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> LayerQueryTool(
            Document doc, JsonElement args)
        {
            string name = OptStr(args, "name", "");

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (name.Length > 0)
            {
                LayerTableRecord? layer = GetLayer(tr, db, name);

                if (layer == null)
                    throw new McpException($"图层不存在：{name}");

                // 统计该图层图元数量
                using JsonDocument layerFilter =
                    JsonDocument.Parse(
                        $"{{\"layer\":\"{name}\"}}");

                int count = ScanSpace(tr, db, "all",
                    layerFilter.RootElement)
                    .Count;

                Dictionary<string, object?> payload = Result(
                    "name", layer.Name,
                    "color", layer.Color.ColorIndex,
                    "linetype", layer.LinetypeObjectId.IsNull
                        ? "Continuous"
                        : ((LinetypeTableRecord)tr.GetObject(
                            layer.LinetypeObjectId,
                            OpenMode.ForRead)).Name,
                    "off", layer.IsOff,
                    "frozen", layer.IsFrozen,
                    "locked", layer.IsLocked,
                    "plottable", layer.IsPlottable,
                    "current", db.Clayer == layer.ObjectId,
                    "entity_count", count);

                tr.Commit();
                return payload;
            }

            LayerTable lt = (LayerTable)tr.GetObject(
                db.LayerTableId, OpenMode.ForRead);

            var layers = new List<object>();

            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(
                    id, OpenMode.ForRead);

                layers.Add(Result(
                    "name", ltr.Name,
                    "color", ltr.Color.ColorIndex,
                    "off", ltr.IsOff,
                    "frozen", ltr.IsFrozen,
                    "locked", ltr.IsLocked,
                    "current", db.Clayer == id));
            }

            tr.Commit();

            return Result(
                "count", layers.Count,
                "layers", layers);
        }

        // ---------------------------------------------------------------
        // list_all
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> ListAllTool(
            Document doc, JsonElement args)
        {
            string space = OptStr(args, "space", "current");
            int limit = OptInt(args, "limit", 500);

            using Transaction tr =
                doc.Database.TransactionManager.StartTransaction();

            List<ObjectId> ids = ScanSpace(
                tr, doc.Database, space, default);

            var items = new List<object>();

            foreach (ObjectId id in ids.Take(Math.Max(0, limit)))
            {
                var ent = (Entity)tr.GetObject(
                    id, OpenMode.ForRead, false, true);

                items.Add(Result(
                    "handle", ent.Handle.ToString(),
                    "type", DxfName(ent),
                    "layer", ent.Layer,
                    "color", ent.ColorIndex));
            }

            tr.Commit();

            return Result(
                "total", ids.Count,
                "returned", items.Count,
                "space", space,
                "entities", items);
        }
    }
}
