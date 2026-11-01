using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCADMcp.Plugin
{
    /// <summary>
    /// 本地 HTTP 桥：接收 Python MCP Server 的请求，
    /// 通过 AutoCAD 空闲事件与命令机制把操作切换到 AutoCAD
    /// 命令执行上下文运行，再把结果以 JSON 返回。
    /// </summary>
    public class BridgeServer
    {
        private const string Prefix = "http://127.0.0.1:8765/";
        private const int TimeoutSeconds = 120;
        private const int MaxBodyBytes = 8 * 1024 * 1024;

        private static HttpListener? _listener;
        private static Task? _listenerTask;

        private static readonly ConcurrentQueue<PendingRequest> Queue = new();

        private static readonly object CurrentLock = new();
        private static PendingRequest? _current;

        private static int _commandScheduled;

        // 异步等待：导出类工具通过命令队列执行，
        // 文件生成后由 OnIdle 完成 HTTP 响应。
        private static readonly object FileWaitLock = new();
        private static readonly List<FileWait> FileWaits = new();

        internal sealed class PendingRequest
        {
            public string Body = "";

            public TaskCompletionSource<object> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class FileWait
        {
            public string Path = "";
            public PendingRequest? Request;
            public DateTime Deadline;
        }

        /// <summary>当前正在命令上下文中执行的请求。</summary>
        internal static PendingRequest? CurrentPending
        {
            get
            {
                lock (CurrentLock)
                {
                    return _current;
                }
            }
        }

        /// <summary>注册“等待文件生成后完成请求”的异步任务。</summary>
        internal static void WaitForFile(string path, int timeoutSeconds = 90)
        {
            PendingRequest? pending = CurrentPending;
            if (pending == null || string.IsNullOrEmpty(path))
                return;

            lock (FileWaitLock)
            {
                FileWaits.Add(new FileWait
                {
                    Path = path,
                    Request = pending,
                    Deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds),
                });
            }
        }

        // 在 AutoCAD 命令行执行 MCPBRIDGESTART
        [CommandMethod("MCPBRIDGESTART")]
        public void Start()
        {
            ToolRegistry.Bootstrap();

            if (_listener is { IsListening: true })
            {
                AcadApplication.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage("\nMCP Bridge 已经启动。");
                return;
            }

            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(Prefix);
                listener.Start();

                _listener = listener;

                AcadApplication.Idle += OnIdle;

                _listenerTask = Task.Run(() => ListenLoopAsync(listener));

                AcadApplication.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage(
                        $"\nMCP Bridge 已启动：{Prefix}");
            }
            catch (System.Exception ex)
            {
                _listener?.Close();
                _listener = null;

                AcadApplication.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage(
                        "\nMCP Bridge 启动失败：" + ex.Message);
            }
        }

        // HTTP 监听循环：接收请求，不直接操作 CAD 数据库
        private static async Task ListenLoopAsync(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleRequestAsync(context));
            }
        }

        private static async Task HandleRequestAsync(
            HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "/";

                if (context.Request.HttpMethod == "GET"
                    && path == "/health")
                {
                    await WriteJsonAsync(context, new
                    {
                        success = true,
                        bridge = "running"
                    });
                    return;
                }

                if (context.Request.HttpMethod == "GET"
                    && path == "/tools")
                {
                    await WriteJsonAsync(context, new
                    {
                        success = true,
                        tools = ToolRegistry.ToolNames
                    });
                    return;
                }

                // 工具名：优先取请求体里的 tool 字段，
                // 兼容旧的 /get_drawing_info 端点。
                string body = "";
                string tool = "";

                if (path == "/get_drawing_info")
                {
                    tool = "get_dwg_info";
                    body = JsonSerializer.Serialize(new
                    {
                        tool,
                        @params = new { }
                    });
                }
                else if (path == "/execute")
                {
                    if (context.Request.HttpMethod != "POST")
                    {
                        context.Response.StatusCode = 405;
                        await WriteJsonAsync(context, new
                        {
                            success = false,
                            error = "Method not allowed"
                        });
                        return;
                    }

                    using (var reader = new StreamReader(
                        context.Request.InputStream,
                        Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    if (body.Length > MaxBodyBytes)
                    {
                        context.Response.StatusCode = 413;
                        await WriteJsonAsync(context, new
                        {
                            success = false,
                            error = "Request body too large"
                        });
                        return;
                    }

                    using (JsonDocument json =
                        JsonDocument.Parse(body))
                    {
                        tool = json.RootElement
                            .TryGetProperty("tool", out JsonElement t)
                            ? t.GetString() ?? ""
                            : "";
                    }

                    if (string.IsNullOrEmpty(tool))
                    {
                        context.Response.StatusCode = 400;
                        await WriteJsonAsync(context, new
                        {
                            success = false,
                            error = "Request missing 'tool' field"
                        });
                        return;
                    }
                }
                else
                {
                    context.Response.StatusCode = 404;

                    await WriteJsonAsync(context, new
                    {
                        success = false,
                        error = "Unknown endpoint"
                    });
                    return;
                }

                if (!listenerIsRunning())
                {
                    context.Response.StatusCode = 503;

                    await WriteJsonAsync(context, new
                    {
                        success = false,
                        error = "Bridge is not running"
                    });
                    return;
                }

                var pending = new PendingRequest { Body = body };
                Queue.Enqueue(pending);

                // 等待 AutoCAD 命令执行结果
                Task completed = await Task.WhenAny(
                    pending.Completion.Task,
                    Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));

                if (completed != pending.Completion.Task)
                {
                    context.Response.StatusCode = 504;

                    await WriteJsonAsync(context, new
                    {
                        success = false,
                        error = "Timed out waiting for AutoCAD. " +
                                "Check whether AutoCAD is busy or a modal dialog is open."
                    });
                    return;
                }

                object result = await pending.Completion.Task;
                await WriteJsonAsync(context, result);
            }
            catch (JsonException ex)
            {
                context.Response.StatusCode = 400;

                await WriteJsonAsync(context, new
                {
                    success = false,
                    error = "Invalid JSON body: " + ex.Message
                });
            }
            catch (System.Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;

                    await WriteJsonAsync(context, new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
                catch
                {
                    // 客户端可能已经关闭连接
                }
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static bool listenerIsRunning()
        {
            return _listener is { IsListening: true };
        }

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);

            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes);
        }

        // 将排队的请求切换到 AutoCAD 命令执行上下文
        private static void OnIdle(object? sender, EventArgs e)
        {
            ProcessFileWaits();

            if (!listenerIsRunning())
                return;

            if (Interlocked.CompareExchange(
                    ref _commandScheduled, 1, 0) != 0)
                return;

            if (!Queue.TryDequeue(out PendingRequest? pending))
            {
                Interlocked.Exchange(ref _commandScheduled, 0);
                return;
            }

            Document? doc =
                AcadApplication.DocumentManager.MdiActiveDocument;

            if (doc == null)
            {
                pending.Completion.TrySetResult(new
                {
                    success = false,
                    error = "No active AutoCAD drawing"
                });

                Interlocked.Exchange(ref _commandScheduled, 0);
                return;
            }

            lock (CurrentLock)
            {
                _current = pending;
            }

            try
            {
                doc.SendStringToExecute(
                    "MCPEXECUTE\n",
                    true,
                    false,
                    false);
            }
            catch (System.Exception ex)
            {
                lock (CurrentLock)
                {
                    _current = null;
                }

                pending.Completion.TrySetResult(new
                {
                    success = false,
                    error = "Unable to queue AutoCAD command: " + ex.Message
                });

                Interlocked.Exchange(ref _commandScheduled, 0);
            }
        }

        private static void ProcessFileWaits()
        {
            lock (FileWaitLock)
            {
                for (int i = FileWaits.Count - 1; i >= 0; i--)
                {
                    FileWait wait = FileWaits[i];

                    if (File.Exists(wait.Path))
                    {
                        FileWaits.RemoveAt(i);
                        wait.Request?.Completion.TrySetResult(new
                        {
                            success = true,
                            message = "文件已生成",
                            path = wait.Path
                        });
                    }
                    else if (DateTime.UtcNow > wait.Deadline)
                    {
                        FileWaits.RemoveAt(i);
                        wait.Request?.Completion.TrySetResult(new
                        {
                            success = false,
                            error = "等待文件生成超时：" + wait.Path
                        });
                    }
                }
            }
        }

        // 由 AutoCAD 自己执行；不要从 HTTP 线程直接调用此方法
        [CommandMethod("MCPEXECUTE")]
        public void ExecuteTool()
        {
            PendingRequest? pending;

            lock (CurrentLock)
            {
                pending = _current;
            }

            if (pending == null)
                return;

            try
            {
                using JsonDocument json =
                    JsonDocument.Parse(pending.Body);

                JsonElement root = json.RootElement;

                string tool = root.TryGetProperty(
                    "tool", out JsonElement t)
                    ? t.GetString() ?? ""
                    : "";

                JsonElement args = root.TryGetProperty(
                    "params", out JsonElement p)
                    ? p
                    : default;

                if (string.IsNullOrEmpty(tool))
                {
                    pending.Completion.TrySetResult(new
                    {
                        success = false,
                        error = "请求缺少 tool 字段"
                    });
                    return;
                }

                Dictionary<string, object?> payload =
                    ToolRegistry.Execute(tool, args);

                // 异步工具：等待文件生成，暂不完成请求
                if (payload.TryGetValue("_wait_file",
                        out object? waitPath)
                    && waitPath is string waitFile
                    && waitFile.Length > 0)
                {
                    int timeout = payload.TryGetValue(
                        "_wait_timeout", out object? to)
                        && to is int ti
                        ? ti
                        : 90;

                    WaitForFile(waitFile, timeout);
                    return;
                }

                var result = new Dictionary<string, object?>(payload)
                {
                    ["success"] = true
                };

                pending.Completion.TrySetResult(result);
            }
            catch (McpException ex)
            {
                pending.Completion.TrySetResult(new
                {
                    success = false,
                    error = ex.Message
                });
            }
            catch (System.Exception ex)
            {
                pending.Completion.TrySetResult(new
                {
                    success = false,
                    error = "工具执行失败：" + ex.Message
                });
            }
            finally
            {
                lock (CurrentLock)
                {
                    if (ReferenceEquals(_current, pending))
                        _current = null;
                }

                Interlocked.Exchange(ref _commandScheduled, 0);
            }
        }

        [CommandMethod("MCPBRIDGESTOP")]
        public void Stop()
        {
            AcadApplication.Idle -= OnIdle;

            HttpListener? listener = _listener;
            _listener = null;

            try
            {
                if (listener != null)
                {
                    if (listener.IsListening)
                        listener.Stop();

                    listener.Close();
                }
            }
            catch
            {
                // 忽略停止过程中的关闭异常
            }

            var error = new
            {
                success = false,
                error = "MCP Bridge was stopped"
            };

            lock (CurrentLock)
            {
                _current?.Completion.TrySetResult(error);
                _current = null;
            }

            while (Queue.TryDequeue(out PendingRequest? pending))
            {
                pending.Completion.TrySetResult(error);
            }

            Interlocked.Exchange(ref _commandScheduled, 0);

            AcadApplication.DocumentManager.MdiActiveDocument?
                .Editor.WriteMessage("\nMCP Bridge 已停止。");
        }
    }
}
