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
    /// 文字与标注工具：draw_text / dimension / leader / hatch。
    /// </summary>
    public static class AnnotateTools
    {
        public static void Register()
        {
            ToolRegistry.Register("draw_text", DrawTextTool);
            ToolRegistry.Register("dimension", DimensionTool);
            ToolRegistry.Register("leader", LeaderTool);
            ToolRegistry.Register("hatch", HatchTool);
        }

        private static ObjectId GetTextStyleId(
            Transaction tr, Database db, string style)
        {
            if (string.IsNullOrEmpty(style))
                return db.Textstyle;

            TextStyleTable tst = (TextStyleTable)tr.GetObject(
                db.TextStyleTableId, OpenMode.ForRead);

            if (!tst.Has(style))
                throw new McpException($"文字样式不存在：{style}");

            return tst[style];
        }

        // ---------------------------------------------------------------
        // draw_text
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DrawTextTool(
            Document doc, JsonElement args)
        {
            Point3d position = ReqPoint(args, "position");
            string text = ReqStr(args, "text");
            double height = OptDouble(args, "height", 2.5);
            double rotation = Deg(OptDouble(args, "rotation", 0));
            string kind = OptStr(args, "kind", "text")
                .Trim().ToLowerInvariant();
            string align = OptStr(args, "align", "left")
                .Trim().ToLowerInvariant();

            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");
            string style = OptStr(args, "style", "");

            Database db = doc.Database;

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            ObjectId styleId = GetTextStyleId(tr, db, style);
            BlockTableRecord btr = GetCurrentSpace(tr, db, true);

            Entity ent;

            if (kind == "mtext")
            {
                var mt = new MText
                {
                    Contents = text,
                    Location = position,
                    TextHeight = height,
                    Rotation = rotation,
                    TextStyleId = styleId,
                    Width = OptDouble(args, "width", 0)
                };

                mt.Attachment = align switch
                {
                    "center" => AttachmentPoint.TopCenter,
                    "middle" => AttachmentPoint.MiddleCenter,
                    "right" => AttachmentPoint.TopRight,
                    "topleft" => AttachmentPoint.TopLeft,
                    "topcenter" => AttachmentPoint.TopCenter,
                    "topright" => AttachmentPoint.TopRight,
                    "middleleft" => AttachmentPoint.MiddleLeft,
                    "middleright" => AttachmentPoint.MiddleRight,
                    "bottomleft" => AttachmentPoint.BottomLeft,
                    "bottomcenter" => AttachmentPoint.BottomCenter,
                    "bottomright" => AttachmentPoint.BottomRight,
                    _ => AttachmentPoint.TopLeft
                };

                ent = mt;
            }
            else
            {
                var txt = new DBText
                {
                    Position = position,
                    TextString = text,
                    Height = height,
                    Rotation = rotation,
                    TextStyleId = styleId,
                    WidthFactor = OptDouble(
                        args, "width_factor", 1)
                };

                switch (align)
                {
                    case "center":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextCenter;
                        txt.AlignmentPoint = position;
                        break;

                    case "right":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextRight;
                        txt.AlignmentPoint = position;
                        break;

                    case "middle":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextMid;
                        txt.AlignmentPoint = position;
                        break;

                    case "top":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextCenter;
                        txt.VerticalMode =
                            TextVerticalMode.TextTop;
                        txt.AlignmentPoint = position;
                        break;

                    case "bottom":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextCenter;
                        txt.VerticalMode =
                            TextVerticalMode.TextBottom;
                        txt.AlignmentPoint = position;
                        break;

                    case "topleft":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextLeft;
                        txt.VerticalMode =
                            TextVerticalMode.TextTop;
                        txt.AlignmentPoint = position;
                        break;

                    case "topright":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextRight;
                        txt.VerticalMode =
                            TextVerticalMode.TextTop;
                        txt.AlignmentPoint = position;
                        break;

                    case "middleleft":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextLeft;
                        txt.VerticalMode =
                            TextVerticalMode.TextVerticalMid;
                        txt.AlignmentPoint = position;
                        break;

                    case "middleright":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextRight;
                        txt.VerticalMode =
                            TextVerticalMode.TextVerticalMid;
                        txt.AlignmentPoint = position;
                        break;

                    case "bottomleft":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextLeft;
                        txt.VerticalMode =
                            TextVerticalMode.TextBottom;
                        txt.AlignmentPoint = position;
                        break;

                    case "bottomright":
                        txt.HorizontalMode =
                            TextHorizontalMode.TextRight;
                        txt.VerticalMode =
                            TextVerticalMode.TextBottom;
                        txt.AlignmentPoint = position;
                        break;
                }

                ent = txt;
            }

            if (layer.Length > 0)
                ent.Layer = layer;

            if (color.Length > 0)
                ent.Color = ParseColor(color);

            ObjectId id = AddEntity(tr, btr, ent);
            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            Dictionary<string, object?> payload = DescribeEntity(
                (Entity)tr2.GetObject(id, OpenMode.ForRead), true);

            tr2.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // dimension
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> DimensionTool(
            Document doc, JsonElement args)
        {
            string type = ReqStr(args, "type")
                .Trim().ToLowerInvariant();

            Database db = doc.Database;
            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            BlockTableRecord btr = GetCurrentSpace(tr, db, true);

            Dimension? dim = type switch
            {
                "linear" =>
                    MakeLinear(args, db),

                "aligned" =>
                    MakeAligned(args, db),

                "angular" =>
                    MakeAngular(args, db),

                "radius" =>
                    MakeRadial(args, db, false),

                "diameter" =>
                    MakeRadial(args, db, true),

                _ => throw new McpException(
                    "dimension type 支持：" +
                    "linear / aligned / angular / radius / diameter")
            };

            if (layer.Length > 0)
                dim.Layer = layer;

            if (color.Length > 0)
                dim.Color = ParseColor(color);

            ObjectId id = AddEntity(tr, btr, dim);
            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            Dictionary<string, object?> payload = DescribeEntity(
                (Entity)tr2.GetObject(id, OpenMode.ForRead), true);

            tr2.Commit();
            return payload;
        }

        private static Dimension MakeLinear(
            JsonElement args, Database db)
        {
            var dim = new RotatedDimension
            {
                XLine1Point = ReqPoint(args, "p1"),
                XLine2Point = ReqPoint(args, "p2"),
                DimLinePoint = ReqPoint(args, "p3"),
                Rotation = Deg(OptDouble(args, "rotation", 0)),
                DimensionStyle = db.Dimstyle
            };

            return dim;
        }

        private static Dimension MakeAligned(
            JsonElement args, Database db)
        {
            return new AlignedDimension
            {
                XLine1Point = ReqPoint(args, "p1"),
                XLine2Point = ReqPoint(args, "p2"),
                DimLinePoint = ReqPoint(args, "p3"),
                DimensionStyle = db.Dimstyle
            };
        }

        private static Dimension MakeAngular(
            JsonElement args, Database db)
        {
            Point3d s1 = OptPoint(args, "start1", default);
            Point3d e1 = OptPoint(args, "end1", default);
            Point3d s2 = OptPoint(args, "start2", default);
            Point3d e2 = OptPoint(args, "end2", default);

            // 模式判定按参数是否提供，start1 可为 [0,0]。
            if (!Has(args, "start1"))
            {
                // 三点式：vertex + p1 + p2
                Point3d vertex = ReqPoint(args, "vertex");
                Point3d p1 = ReqPoint(args, "p1");
                Point3d p2 = ReqPoint(args, "p2");

                s1 = vertex;
                e1 = p1;
                s2 = vertex;
                e2 = p2;
            }

            Point3d arcPoint = OptPoint(args, "arc_point", default);

            // arc_point 为可选参数，显式给 [0,0] 时也要采用。
            if (!Has(args, "arc_point"))
            {
                // 默认圆弧点：从顶点沿角平分线方向偏移 10
                Vector3d v1 = (e1 - s1).GetNormal();
                Vector3d v2 = (e2 - s2).GetNormal();
                Vector3d bisector = (v1 + v2).GetNormal();

                arcPoint = s1 + bisector * 10;
            }

            return new LineAngularDimension2
            {
                XLine1Start = s1,
                XLine1End = e1,
                XLine2Start = s2,
                XLine2End = e2,
                ArcPoint = arcPoint,
                DimensionStyle = db.Dimstyle
            };
        }

        private static Dimension MakeRadial(
            JsonElement args, Database db, bool diameter)
        {
            Point3d center = ReqPoint(args, "center");

            Point3d chord = ReqPoint(args,
                diameter ? "chord_point" : "chord_point");

            double radius = center.DistanceTo(chord);
            double leader = OptDouble(args, "leader_length", 0);

            if (leader <= 0)
                leader = Math.Max(radius * 0.3, 1);

            Dimension dim;

            if (diameter)
            {
                dim = new DiametricDimension
                {
                    ChordPoint = chord,
                    FarChordPoint = new Point3d(
                        center.X * 2 - chord.X,
                        center.Y * 2 - chord.Y,
                        center.Z * 2 - chord.Z),
                    LeaderLength = leader,
                    DimensionStyle = db.Dimstyle
                };
            }
            else
            {
                dim = new RadialDimension
                {
                    Center = center,
                    ChordPoint = chord,
                    LeaderLength = leader,
                    DimensionStyle = db.Dimstyle
                };
            }

            return dim;
        }

        // ---------------------------------------------------------------
        // leader（多重引线）
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> LeaderTool(
            Document doc, JsonElement args)
        {
            List<Point3d> points = ReqPoints(args, "points");

            if (points.Count < 2)
                throw new McpException("leader 至少需要 2 个点");

            string text = OptStr(args, "text", "");

            Database db = doc.Database;
            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            BlockTableRecord btr = GetCurrentSpace(tr, db, true);

            var ml = new MLeader();
            ml.SetDatabaseDefaults();
            ml.MLeaderStyle = db.MLeaderstyle;

            ml.ContentType = ContentType.MTextContent;

            int leaderIndex = ml.AddLeader();
            ml.AddLeaderLine(leaderIndex);

            // 第一个顶点位于文字一侧，其余依次向外
            ml.AddFirstVertex(leaderIndex, points[0]);

            for (int i = 1; i < points.Count; i++)
                ml.AddLastVertex(leaderIndex, points[i]);

            double height = OptDouble(args, "height", 0);

            if (height > 0)
                ml.MText.TextHeight = height;

            ml.MText.Contents = text;

            if (layer.Length > 0)
                ml.Layer = layer;

            if (color.Length > 0)
                ml.Color = ParseColor(color);

            ObjectId id = AddEntity(tr, btr, ml);
            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            Dictionary<string, object?> payload = DescribeEntity(
                (Entity)tr2.GetObject(id, OpenMode.ForRead), true);

            tr2.Commit();
            return payload;
        }

        // ---------------------------------------------------------------
        // hatch
        // ---------------------------------------------------------------

        private static Dictionary<string, object?> HatchTool(
            Document doc, JsonElement args)
        {
            string pattern = OptStr(args, "pattern", "SOLID");
            double scale = OptDouble(args, "scale", 1);
            double angle = Deg(OptDouble(args, "angle", 0));
            bool associative = OptBool(args, "associative", true);

            Database db = doc.Database;
            string layer = OptStr(args, "layer", "");
            string color = OptStr(args, "color", "");

            using Transaction tr =
                db.TransactionManager.StartTransaction();

            if (layer.Length > 0)
                EnsureLayer(tr, db, layer);

            List<ObjectId> boundaryIds = new();

            if (args.TryGetProperty("handles", out JsonElement hv)
                && hv.ValueKind == JsonValueKind.Array)
            {
                boundaryIds = hv.EnumerateArray()
                    .Select(e => IdByHandle(
                        db, e.GetString()!))
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToList();
            }

            Point3d seed = OptPoint(args, "point", default);

            // 内部点可为 [0,0]，按参数是否提供判定是否自动追踪边界。
            if (boundaryIds.Count == 0 && Has(args, "point"))
            {
                // 通过内部点自动追踪边界：返回的临时曲线
                // 需加入数据库才能作为填充边界
                DBObjectCollection traced =
                    doc.Editor.TraceBoundary(seed, false);

                if (traced != null && traced.Count > 0)
                {
                    BlockTableRecord tracedBtr =
                        (BlockTableRecord)tr.GetObject(
                            db.CurrentSpaceId, OpenMode.ForWrite);

                    foreach (DBObject obj in traced)
                    {
                        if (obj is Curve tracedCurve)
                        {
                            tracedBtr.AppendEntity(tracedCurve);
                            tr.AddNewlyCreatedDBObject(
                                tracedCurve, true);

                            boundaryIds.Add(
                                tracedCurve.ObjectId);
                        }
                        else
                        {
                            obj.Dispose();
                        }
                    }
                }
            }

            if (boundaryIds.Count == 0)
                throw new McpException(
                    "hatch 需要边界 handles 或内部 point");

            var hatch = new Hatch();
            hatch.SetDatabaseDefaults();

            hatch.SetHatchPattern(
                HatchPatternType.PreDefined, pattern);

            hatch.PatternScale = scale;
            hatch.PatternAngle = angle;
            hatch.Associative = associative;

            BlockTableRecord btr = GetCurrentSpace(tr, db, true);
            AddEntity(tr, btr, hatch);

            var collection = new ObjectIdCollection();
            foreach (ObjectId id in boundaryIds)
                collection.Add(id);

            hatch.AppendLoop(
                HatchLoopTypes.External, collection);

            try
            {
                hatch.EvaluateHatch(true);
            }
            catch (System.Exception ex)
            {
                hatch.Erase();
                throw new McpException(
                    "填充边界无效：" + ex.Message);
            }

            if (layer.Length > 0)
                hatch.Layer = layer;

            if (color.Length > 0)
                hatch.Color = ParseColor(color);

            ObjectId resultId = hatch.ObjectId;
            tr.Commit();

            using Transaction tr2 =
                db.TransactionManager.StartTransaction();

            Dictionary<string, object?> payload = DescribeEntity(
                (Entity)tr2.GetObject(resultId, OpenMode.ForRead),
                true);

            payload["boundary_handles"] =
                HandlesOf(tr2, boundaryIds);

            tr2.Commit();
            return payload;
        }
    }
}
