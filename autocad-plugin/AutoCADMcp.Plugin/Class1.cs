
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AutoCADMcp.Plugin
{
    public class PluginCommands
    {
        [CommandMethod("HELLOCAD")]
        public void HelloCad()
        {
            // 获取当前 AutoCAD 文档
            Document doc =
                Application.DocumentManager.MdiActiveDocument;

            if (doc == null)
            {
                return;
            }

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                using (Transaction tr =
                    db.TransactionManager.StartTransaction())
                {
                    // 获取模型空间
                    BlockTable bt = (BlockTable)tr.GetObject(
                        db.BlockTableId,
                        OpenMode.ForRead);

                    BlockTableRecord modelSpace =
                        (BlockTableRecord)tr.GetObject(
                            bt[BlockTableRecord.ModelSpace],
                            OpenMode.ForWrite);

                    // 创建矩形的四个顶点
                    Polyline rectangle = new Polyline(4);

                    rectangle.AddVertexAt(
                        0, new Point2d(0, 0), 0, 0, 0);

                    rectangle.AddVertexAt(
                        1, new Point2d(100, 0), 0, 0, 0);

                    rectangle.AddVertexAt(
                        2, new Point2d(100, 60), 0, 0, 0);

                    rectangle.AddVertexAt(
                        3, new Point2d(0, 60), 0, 0, 0);

                    rectangle.Closed = true;

                    // 将实体加入模型空间
                    modelSpace.AppendEntity(rectangle);
                    tr.AddNewlyCreatedDBObject(rectangle, true);

                    // 提交事务
                    tr.Commit();
                }

                ed.WriteMessage(
                    "\nAutoCAD MCP 测试成功：已创建 100 x 60 矩形。");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage(
                    "\n执行失败：" + ex.Message);
            }
        }
    }
}
