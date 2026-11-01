using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Autodesk.AutoCAD.ApplicationServices;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 工具注册表：把工具名分发到对应的 C# 实现，
    /// 并记录最近一次操作供 verify_result 查询。
    /// </summary>
    public static class ToolRegistry
    {
        public delegate Dictionary<string, object?> Handler(
            Document doc, JsonElement args);

        private static readonly Dictionary<string, Handler> Handlers =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool _bootstrapped;

        private static readonly object LastLock = new();
        private static Dictionary<string, object?>? _lastOp;

        private static readonly List<Dictionary<string, object?>> _opLog = new();
        private const int MaxLogEntries = 200;

        public static void Register(string name, Handler handler)
        {
            Handlers[name] = handler;
        }

        /// <summary>注册全部内置工具（幂等）。</summary>
        public static void Bootstrap()
        {
            if (_bootstrapped)
                return;

            _bootstrapped = true;

            DrawTools.Register();
            AnnotateTools.Register();
            LayerDwgTools.Register();
            EditTools.Register();
            EditToolsMore.Register();
            BlockGroupTools.Register();
            QueryTools.Register();
            GeoSpatialTools.Register();
            ViewLayoutTools.Register();
            MiscLispTools.Register();
        }

        public static IReadOnlyList<string> ToolNames =>
            Handlers.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

        public static void RecordLastOp(
            string tool, Dictionary<string, object?> payload)
        {
            lock (LastLock)
            {
                var copy = new Dictionary<string, object?>(payload)
                {
                    ["tool"] = tool,
                    ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                _lastOp = copy;

                _opLog.Add(copy);

                if (_opLog.Count > MaxLogEntries)
                    _opLog.RemoveRange(
                        0, _opLog.Count - MaxLogEntries);
            }
        }

        /// <summary>最近的操作日志（read_commandline 使用）。</summary>
        public static List<Dictionary<string, object?>> GetOpLog(int limit)
        {
            lock (LastLock)
            {
                int count = Math.Min(
                    Math.Max(0, limit), _opLog.Count);

                if (count <= 0)
                    return new List<Dictionary<string, object?>>();

                return _opLog
                    .Skip(_opLog.Count - count)
                    .Reverse()
                    .ToList();
            }
        }

        public static Dictionary<string, object?>? LastOp
        {
            get
            {
                lock (LastLock)
                {
                    return _lastOp;
                }
            }
        }

        public static Dictionary<string, object?> Execute(
            string tool, JsonElement args)
        {
            if (!Handlers.TryGetValue(tool, out Handler? handler))
                throw new McpException($"未知工具：{tool}");

            Document? doc =
                Application.DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new McpException("AutoCAD 当前没有打开的文档");

            Dictionary<string, object?> payload = handler(doc, args);

            RecordLastOp(tool, payload);

            return payload;
        }
    }
}
