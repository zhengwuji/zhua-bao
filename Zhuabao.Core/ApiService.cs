using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zhuabao.Core
{
    /// <summary>
    /// 三个内嵌 HTTP 服务：
    ///  - API   (端口 8900)：查询/重发会话
    ///  - MCP   (端口 8901)：兼容 MCP 协议的 JSON-RPC 入口
    ///  - 解码器 (独立端点)：base64 / hex / url 解码
    /// </summary>
    public sealed class ApiService
    {
        private readonly SessionStore _store;
        private readonly ProxyEngine _engine;
        private HttpListener _api;
        private HttpListener _mcp;

        public ApiService(SessionStore store, ProxyEngine engine)
        {
            _store = store;
            _engine = engine;
        }

        public bool ApiRunning { get; private set; }
        public bool McpRunning { get; private set; }

        public void StartApi(int port)
        {
            if (ApiRunning) return;
            _api = new HttpListener();
            _api.Prefixes.Add($"http://127.0.0.1:{port}/");
            _api.Start();
            ApiRunning = true;
            _ = Task.Run(() => Loop(_api, HandleApiAsync));
            Log.I("服务", "API 服务已启动 http://127.0.0.1:" + port + "/");
        }

        public void StartMcp(int port)
        {
            if (McpRunning) return;
            _mcp = new HttpListener();
            _mcp.Prefixes.Add($"http://127.0.0.1:{port}/");
            _mcp.Start();
            McpRunning = true;
            _ = Task.Run(() => Loop(_mcp, HandleMcpAsync));
            Log.I("服务", "MCP 服务已启动 http://127.0.0.1:" + port + "/");
        }

        public void StopAll()
        {
            try { _api?.Stop(); } catch { }
            try { _mcp?.Stop(); } catch { }
            ApiRunning = false;
            McpRunning = false;
        }

        /// <summary>会话 id 只允许字母数字和短横线，杜绝 ../ 路径穿越。</summary>
        private static bool SessionIdValid(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (var c in id)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            return true;
        }

        private async Task Loop(HttpListener l, Func<HttpListenerContext, Task> handler)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => handler(ctx));
            }
        }

        // ============ REST API ============
        private async Task HandleApiAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                string body;
                if (path.EndsWith("/sessions") && ctx.Request.HttpMethod == "GET")
                {
                    var list = Directory.GetFiles(_store.Dir, "*.zb")
                        .OrderByDescending(File.GetLastWriteTime)
                        .Take(200)
                        .Select(f => SessionStore.LoadSession(f))
                        .Where(s => s != null)
                        .Select(s => new { s.Id, time = s.Time, method = s.Method, url = s.Url, status = s.StatusCode, process = s.Process });
                    body = JsonConvert.SerializeObject(list, Formatting.Indented);
                }
                else if (path.Contains("/session/") && ctx.Request.HttpMethod == "GET")
                {
                    var id = path.Substring(path.LastIndexOf('/') + 1);
                    if (!SessionIdValid(id))
                    {
                        body = "{\"error\":\"invalid session id\"}";
                    }
                    else
                    {
                        var file = Path.Combine(_store.Dir, id + ".zb");
                        var s = File.Exists(file) ? SessionStore.LoadSession(file) : null;
                        body = s == null ? "{\"error\":\"not found\"}" : JsonConvert.SerializeObject(new
                        {
                            s.Id,
                            time = s.Time,
                            method = s.Method,
                            url = s.Url,
                            status = s.StatusCode,
                            requestHeaders = s.Request?.HeaderText(),
                            requestBody = s.Request?.GetBodyText(),
                            responseHeaders = s.Response?.HeaderText(),
                            responseBody = s.Response?.GetBodyText()
                        }, Formatting.Indented);
                    }
                }
                else if (path.EndsWith("/stats"))
                {
                    body = JsonConvert.SerializeObject(new
                    {
                        captured = _engine?.CapturedCount ?? 0,
                        intercepted = _engine?.InterceptedCount ?? 0,
                        modified = _engine?.ModifiedCount ?? 0,
                        sent = _engine?.SentCount ?? 0
                    });
                }
                else if (path.EndsWith("/decode"))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var payload = JObject.Parse(await reader.ReadToEndAsync());
                    string text = payload["text"]?.ToString() ?? string.Empty;
                    string mode = payload["mode"]?.ToString() ?? "base64";
                    body = JsonConvert.SerializeObject(new
                    {
                        result = mode switch
                        {
                            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(text)),
                            "hex" => Encoding.UTF8.GetString(HexUtils.FromHex(text)),
                            "url" => Uri.UnescapeDataString(text),
                            _ => text
                        }
                    });
                }
                else { body = "{\"service\":\"zhuabao-api\",\"version\":\"" + AppInfo.Version + "\"}"; }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                try
                {
                    var err = Encoding.UTF8.GetBytes("{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = err.Length;
                    ctx.Response.OutputStream.Write(err);
                }
                catch { }
            }
            finally { ctx.Response.Close(); }
        }

        // ============ MCP (JSON-RPC over HTTP) ============
        private async Task HandleMcpAsync(HttpListenerContext ctx)
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var req = JObject.Parse(await reader.ReadToEndAsync());
                var method = req["method"]?.ToString();
                object result;
                var id = req["id"];

                if (method == "initialize")
                {
                    result = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "zhuabao-mcp", version = AppInfo.Version }
                    };
                }
                else if (method == "tools/list")
                {
                    result = new
                    {
                        tools = new object[]
                        {
                            new { name = "list_sessions", description = "列出最近抓包会话", inputSchema = new { type = "object", properties = new { } } },
                            new { name = "get_session", description = "查看会话详情", inputSchema = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } } },
                            new { name = "stats", description = "查看统计", inputSchema = new { type = "object", properties = new { } } }
                        }
                    };
                }
                else if (method == "tools/call")
                {
                    var tool = req["params"]?["name"]?.ToString();
                    var args = req["params"]?["arguments"] ?? new JObject();
                    string text;
                    switch (tool)
                    {
                        case "get_session":
                            var sid = args["id"]?.ToString() ?? "";
                            if (!SessionIdValid(sid)) { text = "invalid session id"; break; }
                            var f = Path.Combine(_store.Dir, sid + ".zb");
                            var s = File.Exists(f) ? SessionStore.LoadSession(f) : null;
                            text = s == null ? "not found" : $"{s.Method} {s.Url} -> {s.StatusCode}\n\n请求:\n{s.Request?.HeaderText()}{s.Request?.GetBodyText()}\n\n响应:\n{s.Response?.HeaderText()}{s.Response?.GetBodyText()}";
                            break;
                        case "stats":
                            text = $"已捕获: {_engine?.CapturedCount ?? 0}\n已拦截: {_engine?.InterceptedCount ?? 0}\n已修改: {_engine?.ModifiedCount ?? 0}\n已发送: {_engine?.SentCount ?? 0}";
                            break;
                        default:
                            // 轻量化：只回摘要（id 时间 方法 URL 状态），不回传 base64 报文全文
                            var list = Directory.GetFiles(_store.Dir, "*.zb")
                                .OrderByDescending(File.GetLastWriteTime).Take(50)
                                .Select(p => SessionStore.LoadSession(p))
                                .Where(x => x != null)
                                .Select(x => $"{x.Id}  {x.Time:HH:mm:ss}  {x.Method} {x.Url} -> {x.StatusCode}");
                            text = string.Join("\n", list);
                            break;
                    }
                    result = new { content = new[] { new { type = "text", text } } };
                }
                else if (method == "notifications/initialized") { ctx.Response.Close(); return; }
                else result = new { error = "unknown method: " + method };

                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { jsonrpc = "2.0", result, id }));
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                Log.W("MCP", "请求处理错误: " + ex.Message);
            }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }
}
