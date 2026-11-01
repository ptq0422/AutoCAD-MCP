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
    /// 块与组工具：insert_block / block_query /
    /// block_attr / block_replace / group_op。
    /// </summary>
    public static class BlockGroupTools
    {
        public static void Register()
        {
            ToolRegistry.Register("insert_block", InsertBlockTool);
            ToolRegistry.Register("block_query", BlockQueryTool);
            ToolRegistry.Register("block_attr", BlockAttrTool);
            ToolRegistry.Register("block_replace", BlockReplaceTool);
            ToolRegistry.Register("group_op", GroupOpTool);
        }

        private static ObjectId GetBlockId(
            Transaction tr, Database db, string name)
        {
            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            if (!bt.Has(name))
                throw new McpException($"块不存在：{name}");

            return bt[name];
        }

        private static Dictionary<string, object?> DescribeBlock(
            Transaction tr, Database db, string name)
        {
            ObjectId id = GetBlockId(tr, db, name);

            var btr = (BlockTableRecord)tr.GetObject(
                id, OpenMode.ForRead);

            int entityCount = btr.Cast<ObjectId>()
                .Count(oid => oid.IsValid
                    && !oid.IsErased
                    && tr.GetObject(oid, OpenMode.ForRead,
                        false, true) is Entity);

            // 统计插入引用数量
            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            int refs = 0;

            foreach (ObjectId spaceId in new[]
            {
                db.CurrentSpaceId,
                SymbolUtilityServices.GetBlockModelSpaceId(db)
            }.Distinct())
            {
                if (spaceId.IsNull)
                    continue;

                var space = (BlockTableRecord)tr.GetObject(
                    spaceId, OpenMode.ForRead);

                foreach (ObjectId oid in space)
                {
                    if (tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is BlockReference br
                        && br.BlockTableRecord == id)
                        refs++;
                }
            }

            return Result(
                "name", name,
                "is_layout", btr.IsLayout,
                "anonymous", btr.IsAnonymous,
                "entity_count", entityCount,
                "reference_count", refs,
                "origin", Pt(btr.Origin));
        }

        // ---------------------------------------------------------------
        // insert_block
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> InsertBlockTool(
            Document doc, JsonElement args)
        {
            string name = ReqStr(args, "name");
            Point3d position = ReqPoint(args, "position");
            double rotation = Deg(OptDouble(args, "rotation", 0));
            double scale = OptDouble(args, "scale", 1);

            string layer = OptStr(args, "layer", "");

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            ObjectId blockId;

            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            if (bt.Has(name))
            {
                blockId = bt[name];
            }
            else
            {
                // 块不存在：可从选中图元创建新块定义
                List<ObjectId> sourceIds = args
                    .TryGetProperty("entities", out JsonElement ev)
                        ? ResolveSpec(ev, doc, tr)
                        : new List<ObjectId>();

                if (sourceIds.Count == 0)
                    throw new McpException(
                        $"块 {name} 不存在，且未提供 entities 创建块定义");

                bt.UpgradeOpen();

                var btr = new BlockTableRecord { Name = name };
                bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);

                IdMapping mapping = new IdMapping();

                db.DeepCloneObjects(
                    new ObjectIdCollection(
                        sourceIds.ToArray()),
                    btr.ObjectId,
                    mapping,
                    false);

                if (OptBool(args, "cut", true))
                {
                    foreach (ObjectId sid in sourceIds)
                    {
                        var src = (Entity)tr.GetObject(
                            sid, OpenMode.ForWrite);

                        src.Erase();
                    }
                }

                blockId = btr.ObjectId;
            }

            var br = new BlockReference(
                position, blockId)
            {
                Rotation = rotation,
                ScaleFactors = new Scale3d(scale, scale, scale)
            };

            if (layer.Length > 0)
                br.Layer = layer;

            BlockTableRecord btrSpace =
                GetCurrentSpace(tr, db, true);

            ObjectId brId = AddEntity(tr, btrSpace, br);

            // 属性：块定义中的 AttributeDefinition 生成 AttributeReference
            if (args.TryGetProperty("attrs", out JsonElement av)
                && av.ValueKind == JsonValueKind.Object)
            {
                var blockDef = (BlockTableRecord)tr.GetObject(
                    blockId, OpenMode.ForRead);

                foreach (ObjectId oid in blockDef)
                {
                    if (tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is AttributeDefinition attDef
                        && !attDef.Constant)
                    {
                        var attRef = new AttributeReference();
                        attRef.SetAttributeFromBlock(
                            attDef, br.BlockTransform);

                        attRef.Position = attDef.Position.TransformBy(
                            br.BlockTransform);

                        string? value = av.TryGetProperty(
                            attDef.Tag, out JsonElement vv)
                            && vv.ValueKind == JsonValueKind.String
                                ? vv.GetString()
                                : attDef.TextString;

                        attRef.TextString = value ?? "";
                        attRef.Height = attDef.Height;

                        br.AttributeCollection.AppendAttribute(attRef);
                        tr.AddNewlyCreatedDBObject(attRef, true);
                    }
                }
            }

            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            Dictionary<string, object?> payload = DescribeEntity(
                (Entity)tr2.GetObject(brId, OpenMode.ForRead), true);

            tr2.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // block_query
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> BlockQueryTool(
            Document doc, JsonElement args)
        {
            string name = OptStr(args, "name", "");

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (name.Length > 0)
            {
                Dictionary<string, object?> payload =
                    DescribeBlock(tr, db, name);

                // 列出块定义内图元
                ObjectId id = GetBlockId(tr, db, name);

                var btr = (BlockTableRecord)tr.GetObject(
                    id, OpenMode.ForRead);

                var members = new List<object>();

                foreach (ObjectId oid in btr)
                {
                    if (tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is Entity member)
                    {
                        members.Add(Result(
                            "handle", member.Handle.ToString(),
                            "type", DxfName(member),
                            "layer", member.Layer));
                    }
                }

                payload["members"] = members;
                tr.Commit();
                return payload;
            }

            // 列出全部块定义
            BlockTable bt = (BlockTable)tr.GetObject(
                db.BlockTableId, OpenMode.ForRead);

            var blocks = new List<object>();

            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    id, OpenMode.ForRead);

                if (btr.IsAnonymous)
                    continue;

                blocks.Add(DescribeBlock(tr, db, btr.Name));
            }

            tr.Commit();

            return Result(
                "count", blocks.Count,
                "blocks", blocks);
        }

        // ---------------------------------------------------------------
        // block_attr：读取/修改块参照属性
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> BlockAttrTool(
            Document doc, JsonElement args)
        {
            string handle = ReqStr(args, "handle");
            string op = OptStr(args, "op", "get")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var br = GetEntityByHandle(
                tr, db, handle,
                OpenMode.ForWrite) as BlockReference;

            if (br == null)
                throw new McpException(
                    $"不是块参照：{handle}");

            var attrs = new Dictionary<string, object?>();

            foreach (ObjectId aid in br.AttributeCollection)
            {
                var att = (AttributeReference)tr.GetObject(
                    aid, OpenMode.ForWrite);

                if (op == "set")
                {
                    if (args.TryGetProperty("values",
                            out JsonElement vv)
                        && vv.ValueKind == JsonValueKind.Object
                        && vv.TryGetProperty(
                            att.Tag, out JsonElement nv)
                        && nv.ValueKind == JsonValueKind.String)
                    {
                        att.TextString = nv.GetString() ?? "";
                    }
                }

                attrs[att.Tag] = att.TextString;
            }

            tr.Commit();

            return Result(
                "handle", handle,
                "block_name", BlockName(br),
                "op", op,
                "attributes", attrs,
                "count", attrs.Count);
        }

        // ---------------------------------------------------------------
        // block_replace：替换块引用 / 重定义块
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> BlockReplaceTool(
            Document doc, JsonElement args)
        {
            string srcName = ReqStr(args, "src");
            string dstName = ReqStr(args, "dst");

            string op = OptStr(args, "op", "replace")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            ObjectId srcId = GetBlockId(tr, db, srcName);
            ObjectId dstId = GetBlockId(tr, db, dstName);

            int changed = 0;

            if (op == "redefine")
            {
                // 用 dst 的内容替换 src 块定义的内容
                var srcBtr = (BlockTableRecord)tr.GetObject(
                    srcId, OpenMode.ForWrite);

                var dstBtr = (BlockTableRecord)tr.GetObject(
                    dstId, OpenMode.ForRead);

                // 清空 src 内容
                foreach (ObjectId oid in srcBtr)
                {
                    if (oid.IsErased)
                        continue;

                    DBObject obj = tr.GetObject(
                        oid, OpenMode.ForWrite, false, true);

                    if (obj is Entity ent)
                        ent.Erase();
                }

                var ids = new ObjectIdCollection();

                foreach (ObjectId oid in dstBtr)
                {
                    if (!oid.IsErased
                        && tr.GetObject(oid, OpenMode.ForRead,
                            false, true) is Entity)
                        ids.Add(oid);
                }

                IdMapping mapping = new IdMapping();

                db.DeepCloneObjects(
                    ids, srcId, mapping, false);

                changed = ids.Count;
            }
            else
            {
                // 替换所有引用 src 的块参照为 dst
                foreach (ObjectId spaceId in new[]
                {
                    db.CurrentSpaceId,
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    SymbolUtilityServices.GetBlockPaperSpaceId(db)
                }.Distinct())
                {
                    if (spaceId.IsNull)
                        continue;

                    var space = (BlockTableRecord)tr.GetObject(
                        spaceId, OpenMode.ForRead);

                    foreach (ObjectId oid in space)
                    {
                        if (tr.GetObject(oid, OpenMode.ForWrite,
                                false, true) is BlockReference br
                            && br.BlockTableRecord == srcId)
                        {
                            br.BlockTableRecord = dstId;
                            changed++;
                        }
                    }
                }
            }

            tr.Commit();

            return Result(
                "op", op,
                "src", srcName,
                "dst", dstName,
                "changed", changed,
                "message", "块替换完成");
        }

        // ---------------------------------------------------------------
        // group_op：组管理
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> GroupOpTool(
            Document doc, JsonElement args)
        {
            string op = ReqStr(args, "op")
                .Trim().ToLowerInvariant();

            string name = OptStr(args, "name", "");

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            var gd = (DBDictionary)tr.GetObject(
                db.GroupDictionaryId, OpenMode.ForRead);

            switch (op)
            {
                case "list":
                    {
                        var groups = new List<object>();

                        foreach (DBDictionaryEntry entry in gd)
                        {
                            if (entry.Value.IsNull)
                                continue;

                            var group = (Group)tr.GetObject(
                                entry.Value, OpenMode.ForRead);

                            var members = group.GetAllEntityIds()
                                .Select(id => id.ToString())
                                .ToList();

                            groups.Add(Result(
                                "name", group.Name,
                                "description", group.Description,
                                "selectable", group.Selectable,
                                "count", members.Count,
                                "members", members));
                        }

                        tr.Commit();

                        return Result(
                            "count", groups.Count,
                            "groups", groups);
                    }

                case "create":
                    {
                        if (gd.Contains(name))
                            throw new McpException(
                                $"组已存在：{name}");

                        List<ObjectId> ids =
                            ResolveSelection(args, doc, tr);

                        if (ids.Count == 0)
                            throw new McpException("未选中任何图元");

                        string description = OptStr(
                            args, "description", "");

                        var group = new Group(
                            description, true);

                        foreach (ObjectId id in ids)
                            group.Append(id);

                        gd.UpgradeOpen();
                        gd.SetAt(name, group);
                        tr.AddNewlyCreatedDBObject(group, true);

                        tr.Commit();

                        return Result(
                            "created", name,
                            "count", ids.Count);
                    }

                case "add":
                case "remove":
                    {
                        if (!gd.Contains(name))
                            throw new McpException($"组不存在：{name}");

                        List<ObjectId> ids =
                            ResolveSelection(args, doc, tr);

                        var group = (Group)tr.GetObject(
                            gd.GetAt(name), OpenMode.ForWrite);

                        foreach (ObjectId id in ids)
                        {
                            if (op == "add")
                                group.Append(id);
                            else
                                group.Remove(id);
                        }

                        tr.Commit();

                        return Result(
                            "group", name,
                            "op", op,
                            "count", ids.Count);
                    }

                case "delete":
                    {
                        if (!gd.Contains(name))
                            throw new McpException($"组不存在：{name}");

                        var group = (Group)tr.GetObject(
                            gd.GetAt(name), OpenMode.ForWrite);

                        gd.UpgradeOpen();
                        gd.Remove(name);
                        group.Erase();

                        tr.Commit();

                        return Result(
                            "deleted", name,
                            "message", "组已删除（图元保留）");
                    }

                default:
                    throw new McpException(
                        "group_op 支持：list / create / add / " +
                        "remove / delete");
            }
        }
    }
}
