using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using static AutoCADMcp.Plugin.ToolContext;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 图层工具：set_layer / layer_manage，以及 dwg_op 文件管理。
    /// </summary>
    public static class LayerDwgTools
    {
        public static void Register()
        {
            ToolRegistry.Register("set_layer", SetLayerTool);
            ToolRegistry.Register("layer_manage", LayerManageTool);
            ToolRegistry.Register("dwg_op", DwgOpTool);
        }

        private static bool LayerInUse(
            Transaction tr, Database db, string layerName)
        {
            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    btrId, OpenMode.ForRead);

                foreach (ObjectId oid in btr)
                {
                    if (tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is Entity ent
                        && ent.Layer == layerName)
                        return true;
                }
            }

            return false;
        }

        private static Dictionary<string, object?> LayerProps(
            Transaction tr, Database db, LayerTableRecord ltr,
            bool isCurrent)
        {
            return Result(
                "name", ltr.Name,
                "color", ltr.Color.ColorIndex,
                "color_name", ltr.Color.ToString(),
                "linetype", LinetypeName(tr, db, ltr.LinetypeObjectId),
                "lineweight", ltr.LineWeight.ToString(),
                "off", ltr.IsOff,
                "frozen", ltr.IsFrozen,
                "locked", ltr.IsLocked,
                "plottable", ltr.IsPlottable,
                "current", isCurrent);
        }

        private static string LinetypeName(
            Transaction tr, Database db, ObjectId id)
        {
            if (id.IsNull)
                return "Continuous";

            var ltr = (LinetypeTableRecord)tr.GetObject(
                id, OpenMode.ForRead);

            return ltr.Name;
        }

        private static LayerTableRecord OpenLayerForWrite(
            Transaction tr, Database db, string name)
        {
            LayerTableRecord? layer = GetLayer(tr, db, name);

            if (layer == null)
                throw new McpException($"图层不存在：{name}");

            layer.UpgradeOpen();
            return layer;
        }

        // ---------------------------------------------------------------
        // set_layer：设置/创建图层属性并可选置为当前层
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> SetLayerTool(
            Document doc, JsonElement args)
        {
            string name = ReqStr(args, "name");
            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            bool exists = GetLayer(tr, db, name) != null;
            ObjectId layerId = EnsureLayer(tr, db, name);

            var ltr = (LayerTableRecord)tr.GetObject(
                layerId, OpenMode.ForWrite);

            string color = OptStr(args, "color", "");
            string linetype = OptStr(args, "linetype", "");

            if (color.Length > 0)
                ltr.Color = ParseColor(color);

            if (linetype.Length > 0)
            {
                ObjectId ltId = EnsureLinetype(tr, db, linetype);
                ltr.LinetypeObjectId = ltId;
            }

            if (args.TryGetProperty("on", out _))
                ltr.IsOff = !OptBool(args, "on", true);

            if (args.TryGetProperty("freeze", out _))
                ltr.IsFrozen = OptBool(args, "freeze", false);

            if (args.TryGetProperty("lock", out _))
                ltr.IsLocked = OptBool(args, "lock", false);

            if (args.TryGetProperty("plottable", out _))
                ltr.IsPlottable = OptBool(args, "plottable", true);

            bool makeCurrent = OptBool(args, "current", false);

            if (makeCurrent)
                db.Clayer = layerId;

            Dictionary<string, object?> payload = LayerProps(
                tr, db, ltr, makeCurrent || db.Clayer == layerId);

            payload["created"] = !exists;

            tr.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // layer_manage：图层列表/创建/删除/重命名/清理/开关等
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> LayerManageTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            LayerTable lt = (LayerTable)tr.GetObject(
                db.LayerTableId, OpenMode.ForRead);

            string name = OptStr(args, "name", "");

            switch (op)
            {
                case "list":
                    {
                        var layers = new List<object>();

                        foreach (ObjectId id in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(
                                id, OpenMode.ForRead);

                            layers.Add(LayerProps(
                                tr, db, ltr, db.Clayer == id));
                        }

                        tr.Commit();

                        return Result(
                            "count", layers.Count,
                            "layers", layers);
                    }

                case "create":
                    {
                        EnsureLayer(tr, db, name);

                        var ltr = (LayerTableRecord)tr.GetObject(
                            lt[name], OpenMode.ForRead);

                        string color = OptStr(args, "color", "");
                        string linetype = OptStr(args, "linetype", "");

                        if (color.Length > 0 || linetype.Length > 0)
                        {
                            ltr.UpgradeOpen();

                            if (color.Length > 0)
                                ltr.Color = ParseColor(color);

                            if (linetype.Length > 0)
                                ltr.LinetypeObjectId =
                                    EnsureLinetype(tr, db, linetype);
                        }

                        tr.Commit();

                        return Result(
                            "created", name,
                            "message", $"图层 {name} 已创建");
                    }

                case "delete":
                    {
                        if (!lt.Has(name))
                            throw new McpException($"图层不存在：{name}");

                        if (name == "0"
                            || db.Clayer == lt[name])
                            throw new McpException(
                                "图层 0 或当前层不能删除");

                        ObjectId id = lt[name];
                        var ltr = (LayerTableRecord)tr.GetObject(
                            id, OpenMode.ForRead);

                        if (LayerInUse(tr, db, name))
                            throw new McpException(
                                $"图层 {name} 仍被图元引用，无法删除");

                        ltr.UpgradeOpen();
                        ltr.Erase();

                        tr.Commit();

                        return Result(
                            "deleted", name,
                            "message", $"图层 {name} 已删除");
                    }

                case "rename":
                    {
                        string newName = ReqStr(args, "new_name");

                        if (!lt.Has(name))
                            throw new McpException($"图层不存在：{name}");

                        if (lt.Has(newName))
                            throw new McpException(
                                $"目标图层名已存在：{newName}");

                        var ltr = (LayerTableRecord)tr.GetObject(
                            lt[name], OpenMode.ForWrite);

                        ltr.Name = newName;
                        tr.Commit();

                        return Result(
                            "renamed", $"{name} -> {newName}");
                    }

                case "purge":
                    {
                        var purged = new List<string>();

                        foreach (ObjectId id in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(
                                id, OpenMode.ForRead);

                            if (ltr.Name == "0"
                                || db.Clayer == id
                                || LayerInUse(tr, db, ltr.Name))
                                continue;

                            string purgedName = ltr.Name;

                            ltr.UpgradeOpen();
                            ltr.Erase();
                            purged.Add(purgedName);
                        }

                        tr.Commit();

                        return Result(
                            "purged", purged,
                            "count", purged.Count);
                    }

                case "freeze":
                case "thaw":
                    {
                        bool freeze = op == "freeze";
                        string[] names = name.Split(
                            ',', StringSplitOptions.RemoveEmptyEntries);

                        foreach (string n in names)
                            OpenLayerForWrite(tr, db, n.Trim())
                                .IsFrozen = freeze;

                        tr.Commit();

                        return Result(
                            "message", freeze
                                ? $"已冻结图层：{name}"
                                : $"已解冻图层：{name}");
                    }

                case "lock":
                case "unlock":
                    {
                        bool locked = op == "lock";
                        string[] names = name.Split(
                            ',', StringSplitOptions.RemoveEmptyEntries);

                        foreach (string n in names)
                            OpenLayerForWrite(tr, db, n.Trim())
                                .IsLocked = locked;

                        tr.Commit();

                        return Result(
                            "message", locked
                                ? $"已锁定图层：{name}"
                                : $"已解锁图层：{name}");
                    }

                case "on":
                case "off":
                    {
                        bool on = op == "on";
                        string[] names = name.Split(
                            ',', StringSplitOptions.RemoveEmptyEntries);

                        foreach (string n in names)
                            OpenLayerForWrite(tr, db, n.Trim())
                                .IsOff = !on;

                        tr.Commit();

                        return Result(
                            "message", on
                                ? $"已打开图层：{name}"
                                : $"已关闭图层：{name}");
                    }

                case "isolate":
                    {
                        // 仅保留指定图层可见
                        string[] names = name.Split(
                            ',', StringSplitOptions.RemoveEmptyEntries);

                        var keep = new HashSet<string>(
                            names.Select(n => n.Trim()),
                            StringComparer.OrdinalIgnoreCase);

                        keep.Add("0");

                        foreach (ObjectId id in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(
                                id, OpenMode.ForRead);

                            if (!keep.Contains(ltr.Name))
                            {
                                ltr.UpgradeOpen();
                                ltr.IsOff = true;
                            }
                        }

                        tr.Commit();

                        return Result(
                            "message", $"已隔离图层：{name}");
                    }

                case "unisolate":
                    {
                        foreach (ObjectId id in lt)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(
                                id, OpenMode.ForRead);

                            if (ltr.IsOff)
                            {
                                ltr.UpgradeOpen();
                                ltr.IsOff = false;
                            }
                        }

                        tr.Commit();

                        return Result("message", "已恢复全部图层显示");
                    }

                case "current":
                    {
                        if (!lt.Has(name))
                            throw new McpException($"图层不存在：{name}");

                        db.Clayer = lt[name];
                        tr.Commit();

                        return Result(
                            "current", name,
                            "message", $"当前图层已设为 {name}");
                    }

                default:
                    throw new McpException(
                        "layer_manage op 支持：" +
                        "list / create / delete / rename / purge / " +
                        "freeze / thaw / lock / unlock / on / off / " +
                        "isolate / unisolate / current");
            }
        }

        // ---------------------------------------------------------------
        // dwg_op：新建/打开/保存/另存/关闭/导出
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DwgOpTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;

            switch (op)
            {
                case "new":
                    {
                        string template = OptStr(
                            args, "template", "acad.dwt");

                        Document newDoc =
                            Application.DocumentManager.Add(template);

                        return Result(
                            "message", "已新建文档",
                            "drawing_name", newDoc.Name);
                    }

                case "open":
                    {
                        string path = ReqStr(args, "path");

                        if (!File.Exists(path))
                            throw new McpException($"文件不存在：{path}");

                        bool readOnly = OptBool(args, "read_only", false);

                        Document opened =
                            Application.DocumentManager.Open(
                                path, readOnly);

                        return Result(
                            "message", "已打开文件",
                            "drawing_name", opened.Name,
                            "path", path);
                    }

                case "save":
                    {
                        db.SaveAs(doc.Name, DwgVersion.Current);

                        return Result(
                            "message", "已保存",
                            "path", doc.Name);
                    }

                case "saveas":
                    {
                        string path = ReqStr(args, "path");

                        db.SaveAs(path, DwgVersion.Current);

                        return Result(
                            "message", "已另存为",
                            "path", path);
                    }

                case "close":
                    {
                        doc.SendStringToExecute("_.CLOSE\n",
                            true, false, false);

                        return Result(
                            "message",
                            "已提交关闭命令；若图纸未保存，" +
                            "AutoCAD 会弹出保存提示");
                    }

                case "export_pdf":
                    {
                        string path = ReqStr(args, "path");

                        doc.SendStringToExecute(
                            $"_.-EXPORT PDF \"{path}\"\n",
                            true, false, false);

                        var payload = Result(
                            "message", "PDF 导出命令已提交",
                            "path", path);

                        payload["_wait_file"] = path;
                        payload["_wait_timeout"] = 120;

                        return payload;
                    }

                case "export_dxf":
                    {
                        string path = ReqStr(args, "path");

                        db.DxfOut(path, 0, DwgVersion.Current);

                        return Result(
                            "message", "DXF 已导出",
                            "path", path);
                    }

                case "audit":
                    {
                        bool fix = OptBool(args, "fix", true);

                        db.Audit(fix, true);

                        return Result(
                            "message", "图纸检查完成" +
                                (fix ? "（已修复错误）" : ""));
                    }

                case "info":
                    {
                        using Transaction tr =
                            db.TransactionManager.StartTransaction();

                        var summary = db.SummaryInfo;

                        int dbmod = 0;

                        try
                        {
                            dbmod = Convert.ToInt32(
                                Application.GetSystemVariable("DBMOD"));
                        }
                        catch
                        {
                            // 某些版本可能读不到 DBMOD
                        }

                        bool modified = (dbmod & 1) != 0
                            || (dbmod & 2) != 0;

                        Dictionary<string, object?> payload = Result(
                            "drawing_name", doc.Name,
                            "path", db.Filename,
                            "modified", modified,
                            "version", db.OriginalFileVersion,
                            "saved_by", summary.LastSavedBy,
                            "units", db.Insunits.ToString(),
                            "tilemode", db.TileMode,
                            "title", summary.Title,
                            "subject", summary.Subject,
                            "author", summary.Author,
                            "keywords", summary.Keywords,
                            "comments", summary.Comments);

                        tr.Commit();
                        return payload;
                    }

                default:
                    throw new McpException(
                        "dwg_op 支持：" +
                        "new / open / save / saveas / close / " +
                        "export_pdf / export_dxf / audit / info");
            }
        }
    }
}
