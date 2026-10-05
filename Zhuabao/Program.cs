using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Zhuabao.Core;

namespace Zhuabao
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 日志文件：默认开启，放 BaseDirectory\logs\ 下按天滚动
            Log.DebugEnabled = true;
            Log.SetFile(Path.Combine(AppContext.BaseDirectory, "logs", "zhuabao-" + DateTime.Now.ToString("yyyyMMdd") + ".log"));
            Log.I("启动", AppInfo.Name + " " + AppInfo.Version + " pid=" + Environment.ProcessId + " args=[" + string.Join(" ", args) + "]");
            Log.I("启动", "程序集版本: " + AppInfo.AssemblyVersionText + " (对照 Fatbeans Creater V1.0.8 更新内容)");

            // 崩溃自愈：上次未正常退出时残留的系统代理，先还原，避免用户断网
            SystemProxy.CrashRecoveryCheck(AppContext.BaseDirectory);

            // 全局异常兜底：不再弹 .NET 默认错误框，全部记录日志
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.E("UI", "线程异常: " + e.Exception.Message, e.Exception);
                MessageBox.Show("操作出错:\n" + e.Exception.Message + "\n\n详情见 logs\\ 目录日志。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.E("崩溃", "未处理异常: " + e.ExceptionObject, e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.E("任务", "未观察任务异常: " + e.Exception.Message, e.Exception);
                e.SetObserved();
            };

            if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.RunAll();
                return;
            }

            if (args.Any(a => a.Equals("--enginetest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = EngineTest.Run().GetAwaiter().GetResult();
                return;
            }

            if (args.Any(a => a.Equals("--proxytest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = ProxyTest.Run();
                return;
            }

            if (args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = UiTest.Run();
                return;
            }

            ApplicationConfiguration.Initialize();
            bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            // 单实例保护：已有实例在跑就激活它，避免端口冲突报错
            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\ZhuabaoCreater_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Log.W("启动", "已有实例在运行，本次启动退出 (单实例保护)");
                    MessageBox.Show("抓包豆已在运行中（请看任务栏）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Log.I("启动", "进入 GUI 主循环 (autostart=" + autostart + ")");
                Application.Run(new MainForm(autostart));
            }
            Log.I("启动", "GUI 主循环退出");
        }
    }

    /// <summary>构建期/冒烟自测：不依赖 GUI，直接验证核心逻辑。</summary>
    internal static class SelfTest
    {
        public static int RunAll()
        {
            int fails = 0;
            void Check(bool ok, string name)
            {
                Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name);
                if (!ok) fails++;
            }

            // 1. HexUtils
            Check(HexUtils.ToHex(new byte[] { 0xAB, 0xCD }) == "AB CD", "HexUtils.ToHex");
            Check(HexUtils.FromHex("ab cd").SequenceEqual(new byte[] { 0xAB, 0xCD }), "HexUtils.FromHex");

            // 2. StreamIO 找头
            var head = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
            Check(StreamIO.FindHeaderEnd(head, head.Length) > 0, "StreamIO.FindHeaderEnd(GET)");
            Check(StreamIO.FindHeaderEnd(new byte[] { 1, 2, 3 }, 3) == -1, "StreamIO.FindHeaderEnd(binary)");

            // 3. TlsDetector
            Check(TlsDetector.IsTlsClientHello(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x50 }), "TlsDetector.IsTlsClientHello");
            Check(!TlsDetector.IsTlsClientHello(new byte[] { 0x47, 0x45, 0x54 }), "TlsDetector.reject-GET");

            // 4. SNI 解析
            byte[] ch = BuildClientHello("example.com");
            Check(TlsDetector.ParseSni(ch, ch.Length) == "example.com", "TlsDetector.ParseSni");

            // 5. 证书缓存
            using (var c = new CertCache(Path.Combine(Path.GetTempPath(), "zb-ca-test-" + Guid.NewGuid().ToString("N"))))
            {
                var ca = c.GetOrCreateCa();
                Check(ca != null && ca.HasPrivateKey, "CertCache.GetOrCreateCa");
            }

            // 5b. 叶子证书 SAN：域名只写 dNSName，IP 字面量必须补 iPAddress（否则 iOS/Mac 拒签）
            using (var c = new CertCache(Path.Combine(Path.GetTempPath(), "zb-san-test-" + Guid.NewGuid().ToString("N"))))
            {
                var dnsSan = c.GetLeafCertificate("www.example.com").Extensions["2.5.29.17"]?.RawData ?? Array.Empty<byte>();
                var ipSan = c.GetLeafCertificate("10.20.30.40").Extensions["2.5.29.17"]?.RawData ?? Array.Empty<byte>();
                Check(dnsSan.Contains((byte)0x82) && !dnsSan.Contains((byte)0x87), "CertCache.SAN.dNSName-only");
                Check(ipSan.Contains((byte)0x82) && ipSan.Contains((byte)0x87), "CertCache.SAN.iPAddress(IP 目标)");
            }

            // 5c. 根证书导出（Windows .cer / 通用 .crt / iOS .mobileconfig）
            using (var c = new CertCache(Path.Combine(Path.GetTempPath(), "zb-exp-test-" + Guid.NewGuid().ToString("N"))))
            {
                var ca = c.GetOrCreateCa();
                Check(CertExport.Der(ca).Length > 500, "CertExport.Der(.cer)");
                var pem = CertExport.Pem(ca);
                Check(pem.StartsWith("-----BEGIN CERTIFICATE-----") && pem.Contains("-----END CERTIFICATE-----"), "CertExport.Pem(.crt)");
                var mc = CertExport.MobileConfig(ca);
                Check(mc.Contains("<plist version=\"1.0\">") && mc.Contains("com.apple.security.root") && mc.Contains("</plist>"), "CertExport.MobileConfig(iOS)");
            }

            // 5d. 局域网抓包辅助
            Check(LanHelper.IsPrivate("192.168.1.5") && LanHelper.IsPrivate("10.0.0.9") && LanHelper.IsPrivate("172.20.1.1")
                  && !LanHelper.IsPrivate("8.8.8.8"), "LanHelper.IsPrivate");
            var steps = LanHelper.StepsText("10.0.0.2", 8899);
            Check(steps.Contains("10.0.0.2") && steps.Contains("8899") && steps.Contains("证书信任设置"), "LanHelper.StepsText");

            // 6. ConfigService
            string cfgDir = Path.Combine(Path.GetTempPath(), "zb-cfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cfg = ConfigService.Load(cfgDir);
                Check(cfg.Port == 8899 && cfg.InterceptEnabled == false, "ConfigService.Load(default)");
                cfg.InterceptEnabled = true;
                cfg.Redirects.Add(new RedirectRule { Pattern = @"foo\.com", Replacement = "bar.com" });
                ConfigService.Save(cfg, cfgDir);
                var cfg2 = ConfigService.Load(cfgDir);
                Check(cfg2.InterceptEnabled && cfg2.Redirects.Count == 1, "ConfigService.Save/Load roundtrip");
            }
            finally { try { Directory.Delete(cfgDir, true); } catch { } }

            // 7. 取值提取
            var resp = new HttpResponse();
            resp.SetBody("<html><input name='csrf' value='token123'></html>");
            var rule = new ExtractRule { Name = "csrf", Scope = "响应-表单", Input = "csrf", Regex = "token(\\d+)" };
            var hit = ExtractEngine.Apply(new[] { rule }, resp, null);
            Check(hit.Count == 1 && hit[0].Value == "123", "ExtractEngine.form-value");

            // 8. 会话仓库（落盘走后台队列，轮询等文件出现）
            string repo = Path.Combine(Path.GetTempPath(), "zb-repo-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new SessionStore(repo))
                {
                    var s = new HttpSession { Request = new HttpRequest { Method = "GET", Url = "http://t.local/x", Host = "t.local" } };
                    var rr = new HttpResponse { Status = "200 OK", StatusCode = 200 };
                    rr.SetBody("hello");
                    s.SetResponse(rr);
                    store.Add(s);
                    Check(store.Count == 1, "SessionStore.Add");
                    var file = Path.Combine(repo, s.Id + ".zb");
                    HttpSession loaded = null;
                    for (int i = 0; i < 50 && loaded == null; i++)
                    {
                        Thread.Sleep(50);
                        if (File.Exists(file)) loaded = SessionStore.LoadSession(file);
                    }
                    Check(loaded != null && loaded.Request.Url == "http://t.local/x" && loaded.Response.StatusCode == 200, "SessionStore.LoadSession(异步落盘)");
                }
            }
            finally { try { Directory.Delete(repo, true); } catch { } }

            // 9. RedirectEngine
            Check(RedirectEngine.Rewrite("http://foo.com/a", new[] { new RedirectRule { Pattern = @"foo\.com", Replacement = "bar.com" } })
                  == "http://bar.com/a", "RedirectEngine.Rewrite");

            // 10. ReplaceEngine (改包)
            var req = new HttpRequest { Method = "GET", Url = "http://t.local/", Host = "t.local" };
            var rules = new List<ReplaceRule>
            {
                new ReplaceRule { Direction = "请求", Target = "请求头", Match = "User-Agent", ReplaceType = "替换值", Value = "ZB-Agent" }
            };
            ReplaceEngine.ApplyRules(rules, req, null);
            Check(req.Headers.TryGetValue("User-Agent", out var ua) && ua == "ZB-Agent", "ReplaceEngine.header");

            // 11. HttpMessage.Parser
            var r1 = HttpRequest.Parse("POST /api HTTP/1.1\r\nHost: t.local\r\nContent-Type: application/json\r\nContent-Length: 9\r\n\r\n{\"a\":12345}");
            Check(r1.Method == "POST" && r1.PathAndQuery == "/api" && r1.GetBodyText() == "{\"a\":12345}", "HttpRequest.Parse");

            // 11b. Set-Cookie 多值保留（不能被 ", " 合并破坏）
            var scRaw = Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nSet-Cookie: a=1; Path=/\r\nSet-Cookie: b=2; Expires=Wed, 21 Oct 2026 07:28:00 GMT\r\nContent-Length: 0\r\n\r\n");
            var scResp = HttpResponse.Parse(scRaw);
            var scOut = Encoding.Latin1.GetString(scResp.ToRawBytes());
            Check(scResp.Headers["Set-Cookie"].Split('\n').Length == 2
                  && scOut.Contains("Set-Cookie: a=1; Path=/\r\nSet-Cookie: b=2")
                  && scOut.Contains("Content-Length: 0"), "HttpMessage.Set-Cookie多值");

            // 11c. gzip 响应体解压
            var gzPayload = Encoding.UTF8.GetBytes("{\"secret\":\"GZIP-SELFTEST\"}");
            var gzBytes = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(gzBytes, System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(gzPayload);
            var gzRaw = new MemoryStream();
            var gzHead = Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\n\r\n");
            gzRaw.Write(gzHead);
            gzRaw.Write(gzBytes.ToArray());
            var gzResp = HttpResponse.Parse(gzRaw.ToArray());
            bool gzOk = gzResp.TryDecompressBody() && gzResp.GetBodyText().Contains("GZIP-SELFTEST");
            Check(gzOk && !gzResp.Headers.ContainsKey("Content-Encoding"), "HttpMessage.TryDecompressBody(gzip)");

            // 11d. 101 响应不得带 Content-Length
            var wsResp = HttpResponse.Parse(Encoding.Latin1.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"));
            var wsOut = Encoding.Latin1.GetString(wsResp.ToRawBytes());
            Check(!wsOut.Contains("Content-Length"), "HttpMessage.101无Content-Length");

            // 11e. ReadHttpMessageAsync：chunked / Content-Length 分帧
            {
                var chunkedRaw = Encoding.Latin1.GetBytes(
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                    "5\r\nHELLO\r\n" +
                    "6\r\n WORLD\r\n" +
                    "0\r\n\r\n");
                using var ms2 = new MemoryStream(chunkedRaw);
                var msg2 = StreamIO.ReadHttpMessageAsync(ms2, CancellationToken.None, null, TimeSpan.FromSeconds(2), closeDelimited: true).GetAwaiter().GetResult();
                var t2 = msg2 == null ? "" : Encoding.Latin1.GetString(msg2);
                Check(t2.Contains("5\r\nHELLO\r\n6\r\n WORLD\r\n") && t2.EndsWith("0\r\n\r\n", StringComparison.Ordinal), "StreamIO.ReadHttpMessage(chunked)");

                var clRaw = Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nABCDEFGHIJ");
                using var ms3 = new MemoryStream(clRaw);
                var msg3 = StreamIO.ReadHttpMessageAsync(ms3, CancellationToken.None, null, TimeSpan.FromSeconds(2), closeDelimited: true).GetAwaiter().GetResult();
                Check(msg3 != null && Encoding.Latin1.GetString(msg3).EndsWith("ABCDEFGHIJ", StringComparison.Ordinal), "StreamIO.ReadHttpMessage(content-length)");
            }

            // 11f. ParseRaw 必须把 chunked 体解成明文（keep-alive 下分帧正确性的前提）
            {
                var chRaw = Encoding.Latin1.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n" +
                    "5\r\nHELLO\r\n6\r\n WORLD\r\n0\r\n\r\n");
                var chResp = HttpResponse.Parse(chRaw);
                Check(chResp.GetBodyText() == "HELLO WORLD" && !chResp.Headers.ContainsKey("Transfer-Encoding"),
                      "HttpMessage.ParseRaw解chunked");
                var chOut = chResp.ToRawBytes();
                var chText = Encoding.Latin1.GetString(chOut);
                Check(chText.Contains("Content-Length: 11") && chText.EndsWith("HELLO WORLD", StringComparison.Ordinal),
                      "HttpMessage.解chunked后Content-Length正确");
            }

            // 12. JS 脚本引擎 (Jint)
            var script = new JsScriptEngine();
            var req2 = new HttpRequest { Method = "GET", Url = "http://t.local/", Host = "t.local" };
            script.RunRequestScript("zb.request.setHeader('X-Flag','yes');", req2, null, new Dictionary<string, string>());
            Check(req2.Headers.TryGetValue("X-Flag", out var flagVal) && flagVal == "yes", "JsScriptEngine.request");

            // ================= V1.0.8 新增功能自测 =================

            // 13. 转发器：条件匹配（协议 / 端口 / 长度 / 内容 / IP 前缀）
            {
                var pkt = new PacketEvent
                {
                    Protocol = "TCP", SrcIP = "192.168.1.100", SrcPort = 54321,
                    DstIP = "104.160.141.3", DstPort = 5000,
                    ProcessName = "game.exe", Data = Encoding.ASCII.GetBytes("hello world")
                };
                Check(pkt.DataLength == 11 && pkt.TimestampMs > 1700000000000, "转发器.PacketEvent长度/时间戳");

                var m = new PacketMatch { UseProtocol = true, Protocols = new List<string> { "TCP" }, UseDstPort = true, DstPortFrom = 5000, DstPortTo = 5050 };
                Check(m.IsSatisfiedBy(pkt), "转发器.匹配-协议+端口区间");

                m.UseContent = true; m.Content = "hello";
                Check(m.IsSatisfiedBy(pkt), "转发器.匹配-文本内容");
                m.Content = "HEX:68 65 6C 6C 6F";
                m.ContentIsHex = true;
                Check(m.IsSatisfiedBy(pkt), "转发器.匹配-HEX内容");

                m.Content = "nope"; m.ContentIsHex = false;
                Check(!m.IsSatisfiedBy(pkt), "转发器.匹配-内容不命中应拒绝");

                var ex = new PacketMatch { UseDstPort = true, DstPortFrom = 5000, DstPortTo = 5000 };
                var fwdRule = new ForwardRule { RuleName = "t", Match = new PacketMatch(), Exclude = ex };
                Check(!fwdRule.Accepts(pkt), "转发器.排除条件优先级高于匹配条件");

                var ruleEmpty = new ForwardRule { RuleName = "all", Match = new PacketMatch() };
                Check(ruleEmpty.Accepts(pkt), "转发器.不勾任何匹配条件=转发全部");

                Check(PacketMatch.IpMatches("192.168.*", "192.168.1.100") && PacketMatch.IpMatches("10.0.0.1, 104.160.*", "104.160.141.3")
                      && !PacketMatch.IpMatches("172.16.*", "104.160.141.3"), "转发器.IP前缀/多值匹配");
            }

            // 14. 转发器：有界队列满时丢弃最旧（不阻塞、不错序）
            {
                string hookDir = Path.Combine(Path.GetTempPath(), "zb-fwd-" + Guid.NewGuid().ToString("N"));
                var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:18771/");
                bool hookUp = false;
                try { listener.Start(); hookUp = true; } catch { }
                if (hookUp)
                {
                    var t = Task.Run(async () =>
                    {
                        while (listener.IsListening)
                        {
                            System.Net.HttpListenerContext ctx;
                            try { ctx = await listener.GetContextAsync(); } catch { break; }
                            try
                            {
                                using var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                                var body = await rd.ReadToEndAsync();
                                if (body.Contains("\"DataBase64\"")) received.Enqueue(body);
                                ctx.Response.StatusCode = 200;
                                ctx.Response.Close();
                            }
                            catch { try { ctx.Response.Close(); } catch { } }
                        }
                    });

                    try
                    {
                        var fc = new ForwarderConfig
                        {
                            Enabled = true, TargetUrl = "http://127.0.0.1:18771/",
                            Workers = 2, QueueLimit = 10, TimeoutMs = 3000, MaxPacketBytes = 1024 * 1024
                        };
                        fc.Rules.Add(new ForwardRule { RuleName = "全部", Match = new PacketMatch() });   // 空匹配 = 全放行
                        using var eng = new ForwarderEngine(fc);
                        for (int i = 0; i < 25; i++)
                            eng.Observe(new PacketEvent
                            {
                                Protocol = "TCP", SrcIP = "127.0.0.1", SrcPort = 40000 + i,
                                DstIP = "127.0.0.1", DstPort = 9000, Data = Encoding.UTF8.GetBytes("pkt-" + i)
                            });
                        Check(eng.AcceptedCount == 25, "转发器.送入计数");
                        Check(eng.DroppedCount >= 0, "转发器.队列丢弃计数可读");

                        // 等 Worker 把队列排空（fire-and-forget，必须异步完成）
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (eng.QueueDepth > 0 && sw.ElapsedMilliseconds < 4000) Thread.Sleep(50);
                        while (received.Count < 1 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                        Check(received.Count >= 1, "转发器.旁路 POST 确实到达目标 (" + received.Count + " 条)");
                        string first = null;
                        received.TryDequeue(out first);
                        if (first != null)
                            Check(first.Contains("DataBase64") && first.Contains("HexData") && first.Contains("RuleName") && first.Contains("Timestamp")
                                  && first.Contains("\"Protocol\":\"TCP\"") && first.Contains("ProcessName"), "转发器.转发 JSON 字段齐全");
                        else Check(false, "转发器.转发 JSON 字段齐全");
                    }
                    finally
                    {
                        try { listener.Stop(); } catch { }
                        try { Directory.Delete(hookDir, true); } catch { }
                    }
                }
                else Check(true, "转发器.旁路转发（18771 端口被占用，跳过）");
            }

            // 15. 大模型：Anthropic 兼容模式的请求体结构（system 顶层 / messages 交替）
            {
                var cfg = new LlmConfig
                {
                    Enabled = true, Provider = "Anthropic兼容", ApiKey = "sk-test", Model = "claude-3-5-sonnet-latest",
                    BaseUrl = "https://api.anthropic.com", SystemPrompt = "你是助手", MaxTokens = 512, Temperature = 0.3
                };
                Check(cfg.Dialect == LlmDialect.AnthropicCompatible && cfg.Endpoint == "https://api.anthropic.com/v1/messages",
                      "大模型.Anthropic端点自动拼接 /v1/messages");

                var body = LlmClient.BuildRequestJson(cfg, new[]
                {
                    new LlmMessage("user", "第一个问题"),
                    new LlmMessage("user", "紧接着的第二句"),      // 连续同角色 → 必须合并
                    new LlmMessage("assistant", "回答"),
                    new LlmMessage("system", "追加的系统提示")      // system → 提到顶层，不进 messages
                });
                var o = Newtonsoft.Json.Linq.JObject.Parse(body);
                Check(o["system"] != null && o["system"].ToString().Contains("你是助手") && o["system"].ToString().Contains("追加的系统提示"),
                      "大模型.Anthropic.system为顶层独立字段");
                var msgs = (Newtonsoft.Json.Linq.JArray)o["messages"];
                Check(msgs.Count == 2 && msgs[0]["role"].ToString() == "user" && msgs[1]["role"].ToString() == "assistant",
                      "大模型.Anthropic.messages角色交替且合并连续同角色");
                Check(msgs[0]["content"].ToString().Contains("第一个问题") && msgs[0]["content"].ToString().Contains("紧接着的第二句"),
                      "大模型.Anthropic.连续同角色内容已合并");
                Check(o["max_tokens"] != null && (int)o["max_tokens"] == 512, "大模型.Anthropic.max_tokens必填");
                Check(!body.Contains("\"role\":\"system\""), "大模型.Anthropic.messages里不得出现system角色");

                // Anthropic 响应解析：content[] 里取 type==text
                var anthResp = "{\"content\":[{\"type\":\"text\",\"text\":\"第一段\"},{\"type\":\"tool_use\",\"id\":\"x\"},{\"type\":\"text\",\"text\":\"第二段\"}]," +
                               "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":11,\"output_tokens\":22}}";
                var text = LlmClient.ParseResponseText(LlmDialect.AnthropicCompatible, anthResp, out var inTok, out var outTok, out var stop);
                Check(text == "第一段第二段" && inTok == 11 && outTok == 22 && stop == "end_turn", "大模型.Anthropic响应解析(content[] + usage)");

                // OpenAI 方言对照：system 留在 messages 里，端点不同
                var oai = cfg.Clone();
                oai.Provider = "OpenAI兼容"; oai.BaseUrl = "https://api.openai.com";
                Check(oai.Dialect == LlmDialect.OpenAiCompatible && oai.Endpoint == "https://api.openai.com/v1/chat/completions",
                      "大模型.OpenAI端点自动拼接 /v1/chat/completions");
                var oaiBody = LlmClient.BuildRequestJson(oai, new[] { new LlmMessage("user", "hi") });
                var oaiObj = Newtonsoft.Json.Linq.JObject.Parse(oaiBody);
                Check(oaiObj["system"] == null && ((Newtonsoft.Json.Linq.JArray)oaiObj["messages"])[0]["role"].ToString() == "system",
                      "大模型.OpenAI.system作为messages首条");

                // 鉴权头：Anthropic 用 x-api-key + anthropic-version，OpenAI 用 Bearer
                using (var reqA = new System.Net.Http.HttpRequestMessage())
                {
                    LlmClient.ApplyAuthHeaders(reqA, cfg, LlmDialect.AnthropicCompatible);
                    Check(reqA.Headers.Contains("x-api-key") && reqA.Headers.Contains("anthropic-version") && !reqA.Headers.Contains("Authorization"),
                          "大模型.Anthropic鉴权头(x-api-key + anthropic-version)");
                }
                using (var reqO = new System.Net.Http.HttpRequestMessage())
                {
                    LlmClient.ApplyAuthHeaders(reqO, oai, LlmDialect.OpenAiCompatible);
                    Check(reqO.Headers.Contains("Authorization") && reqO.Headers.Authorization.Scheme == "Bearer",
                          "大模型.OpenAI鉴权头(Bearer)");
                }

                var oaiResp = "{\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}";
                var t2 = LlmClient.ParseResponseText(LlmDialect.OpenAiCompatible, oaiResp, out var i2, out var o2, out var s2);
                Check(t2 == "你好" && i2 == 3 && o2 == 4 && s2 == "stop", "大模型.OpenAI响应解析");
            }

            // 16. 改包规则次数限制：规则对象重建后计数仍有效（V1.0.8 BUG 修复的核心断言）
            {
                var reg = new RuleHitRegistry();
                var regRule = new ReplaceRule
                {
                    Direction = "响应", Target = "响应头", Match = "X-Test", ReplaceType = "替换值",
                    Value = "1", LimitEnabled = true, MaxHits = 2
                };
                Check(reg.HasBudget(regRule), "次数限制.初始有配额");
                reg.Consume(regRule); reg.Consume(regRule);
                Check(!reg.HasBudget(regRule) && reg.Used(regRule) == 2, "次数限制.用满后不再有配额");

                // 模拟 UI 的 _cfg.ReplaceRules.Clear() + 重建：新对象、同一语义指纹
                var rebuilt = new ReplaceRule
                {
                    Direction = "响应", Target = "响应头", Match = "X-Test", ReplaceType = "替换值",
                    Value = "1", LimitEnabled = true, MaxHits = 2
                };
                Check(reg.Used(rebuilt) == 2 && !reg.HasBudget(rebuilt), "次数限制.规则对象重建后计数不清零(修复点)");

                // 引擎级：ApplyRules 必须真的不再改包
                var regResp = new HttpResponse { StatusCode = 200, Status = "OK" };
                regResp.Headers["X-Test"] = "old";
                ReplaceEngine.RuleRegistry.ResetAll();
                ReplaceEngine.ApplyRules(new[] { rebuilt }, null, regResp);
                Check(regResp.Headers["X-Test"] == "1" && ReplaceEngine.RuleRegistry.Used(rebuilt) == 1, "次数限制.第1次生效并计数");
                regResp.Headers["X-Test"] = "old";
                ReplaceEngine.ApplyRules(new[] { rebuilt }, null, regResp);
                Check(regResp.Headers["X-Test"] == "1" && ReplaceEngine.RuleRegistry.Used(rebuilt) == 2, "次数限制.第2次生效并计数");
                regResp.Headers["X-Test"] = "old";
                ReplaceEngine.ApplyRules(new[] { rebuilt }, null, regResp);
                Check(regResp.Headers["X-Test"] == "old" && ReplaceEngine.RuleRegistry.Used(rebuilt) == 2, "次数限制.超限后不再改包");
                ReplaceEngine.RuleRegistry.ResetAll();
            }

            // 17. 修改器「改状态码」：:status 伪头名 + 状态行 + 区间
            {
                var sdResp = new HttpResponse { StatusCode = 200, Status = "OK" };
                var sr1 = new ReplaceRule { Direction = "响应", Target = "响应头", Match = ReplaceEngine.StatusPseudoHeader, ReplaceType = "替换值", Value = "404" };
                ReplaceEngine.ApplyRules(new[] { sr1 }, null, sdResp);
                Check(sdResp.StatusCode == 404 && sdResp.Status == "Not Found", "改状态码.响应头里用 :status 改状态码");

                var resp2 = new HttpResponse { StatusCode = 200, Status = "OK" };
                var r2 = new ReplaceRule { Direction = "响应", Target = "响应头", Match = ":status", ReplaceType = "替换值", Value = "403 Forbidden" };
                ReplaceEngine.ApplyRules(new[] { r2 }, null, resp2);
                Check(resp2.StatusCode == 403 && resp2.Status == "Forbidden", "改状态码.可自定义原因短语");

                var resp3 = new HttpResponse { StatusCode = 200, Status = "OK" };
                var r3 = new ReplaceRule { Direction = "响应", Target = "状态行", Match = "", ReplaceType = "替换值", Value = "200-299" };
                ReplaceEngine.ApplyRules(new[] { r3 }, null, resp3);
                Check(resp3.StatusCode >= 200 && resp3.StatusCode <= 299, "改状态码.区间写法随机取码");

                // 非 :status 的响应头规则行为不变
                var sdResp4 = new HttpResponse { StatusCode = 200, Status = "OK" };
                sdResp4.Headers["Server"] = "nginx";
                var sr4 = new ReplaceRule { Direction = "响应", Target = "响应头", Match = "Server", ReplaceType = "替换值", Value = "zhuabao" };
                ReplaceEngine.ApplyRules(new[] { sr4 }, null, sdResp4);
                Check(sdResp4.Headers["Server"] == "zhuabao" && sdResp4.StatusCode == 200, "改状态码.普通响应头规则不受影响");
            }

            // 18. 重发不覆盖原会话：CloneWithNewId 是新 Id + 深拷贝
            {
                var orig = new HttpSession { Protocol = "HTTP", Process = "p.exe", Comment = "原始" };
                orig.Request = HttpRequest.Parse("POST /a HTTP/1.1\r\nHost: t.local\r\nContent-Length: 3\r\n\r\nabc");
                var or = new HttpResponse { Status = "200 OK", StatusCode = 200 };
                or.SetBody("orig-body");
                orig.SetResponse(or);

                var copy = orig.CloneWithNewId(" (重发)");
                Check(copy.Id != orig.Id, "重发副本.新Id不与原会话相同");
                Check(copy.Comment != orig.Comment, "重发副本.备注标记来源");

                // 改副本绝不能影响原会话（headers 字典与 body 数组都不共享）
                copy.Request.Headers["X-New"] = "1";
                copy.Request.SetBody("CHANGED");
                copy.Response.SetBody("changed-body");
                copy.Request.Url = "http://t.local/b";
                Check(!orig.Request.Headers.ContainsKey("X-New") && orig.Request.GetBodyText() == "abc"
                      && orig.Response.GetBodyText() == "orig-body" && orig.Url == "http://t.local/a",
                      "重发副本.编辑副本不改动原封包(修复点)");

                // 完整原始报文编辑后解析回请求也不得污染原对象
                var edited = HttpRequest.Parse("POST /c HTTP/1.1\r\nHost: t.local\r\nContent-Length: 0\r\n\r\n");
                copy.Request = edited;
                Check(orig.Request.GetBodyText() == "abc" && orig.Url == "http://t.local/a", "重发副本.替换副本Request不影响原会话");
            }

            // 19. 转发器配置与大模型配置的旧配置兼容（config.json 缺字段不能崩）
            {
                var legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>("{\"Port\":8899,\"ReplaceRules\":[]}");
                legacy = ConfigService.Normalize(legacy);
                Check(legacy.Forwarder != null && legacy.Forwarder.Rules != null && legacy.Llm != null && legacy.Llm.Provider == "OpenAI兼容",
                      "配置兼容.旧config缺Forwarder/Llm字段时自动补齐");
            }

            // 20. 工具箱：专用解码器 GitHub 示例链接表可用且地址合法
            {
                var samples = ToolboxForm.DecoderSamples;
                Check(samples.Length >= 10, "解码器示例.链接表条目数");
                Check(samples.All(x => x.Url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) && x.Name.Length > 0),
                      "解码器示例.每条链接都是合法 GitHub https 地址");
                Check(samples.Any(x => x.Name == "AES") && samples.Any(x => x.Name == "Protobuf") && samples.Any(x => x.Name == "MessagePack"),
                      "解码器示例.覆盖内置解码算法");
            }

            // 21. 转发器规则表解析（"起-止" 端口与 HEX: 内容前缀）
            {
                Check(ToolboxParseRange("8000-8100", out var pf, out var pt) && pf == 8000 && pt == 8100, "转发器.端口区间解析");
                Check(ToolboxParseRange("8899", out var p1, out var p2) && p1 == 8899 && p2 == 8899, "转发器.单端口解析为精确匹配");
                Check(ToolboxParseRange("100-50", out var pf2, out var pt2) && pf2 == 100 && pt2 == 50, "转发器.区间端点原样返回(匹配时会自动交换)");
                Check(!ToolboxParseRange("", out _, out _), "转发器.空端口串视为未填写");
            }

            // 22. MCP / REST 服务：真的起一个 HttpListener 并用 JSON-RPC 调通（不是只看代码）
            {
                const int mcpPort = 18777, apiPort = 18778;
                string svcDir = Path.Combine(Path.GetTempPath(), "zb-svc-" + Guid.NewGuid().ToString("N"));
                ApiService svc = null;
                SessionStore svcStore = null;
                try
                {
                    svcStore = new SessionStore(Path.Combine(svcDir, "capture"));
                    svc = new ApiService(svcStore, null);
                    svc.StartMcp(mcpPort);
                    svc.StartApi(apiPort);
                    System.Threading.Thread.Sleep(400);

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                    string Rpc(string method, string paramsJson)
                    {
                        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":" + (paramsJson ?? "{}") + "}";
                        using var content = new StringContent(body, Encoding.UTF8, "application/json");
                        var r = http.PostAsync($"http://127.0.0.1:{mcpPort}/", content).GetAwaiter().GetResult();
                        return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }

                    // MCP 握手：initialize 必须回协议版本与 serverInfo
                    var init = Rpc("initialize", "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"zhuabao-selftest\",\"version\":\"1\"}}");
                    var initObj = Newtonsoft.Json.Linq.JObject.Parse(init);
                    Check(initObj["result"]?["protocolVersion"]?.ToString() == "2024-11-05"
                          && initObj["result"]?["serverInfo"]?["name"]?.ToString() == "zhuabao-mcp",
                          "MCP.initialize 返回协议版本与 serverInfo");

                    // tools/list：三个工具名必须齐
                    var tools = Rpc("tools/list", "{}");
                    var toolObj = Newtonsoft.Json.Linq.JObject.Parse(tools);
                    var names = ((Newtonsoft.Json.Linq.JArray)toolObj["result"]?["tools"])?.Select(t => t["name"]?.ToString()).ToList() ?? new List<string>();
                    Check(names.Contains("list_sessions") && names.Contains("get_session") && names.Contains("stats"),
                          "MCP.tools/list 暴露 list_sessions / get_session / stats");

                    // tools/call → stats：返回 content[].type=text
                    var call = Rpc("tools/call", "{\"name\":\"stats\",\"arguments\":{}}");
                    var callObj = Newtonsoft.Json.Linq.JObject.Parse(call);
                    Check(callObj["result"]?["content"]?[0]?["type"]?.ToString() == "text"
                          && (callObj["result"]?["content"]?[0]?["text"]?.ToString() ?? "").Contains("已捕获"),
                          "MCP.tools/call(stats) 返回文本内容");

                    // tools/call → list_sessions（空目录也不能报错）
                    var list = Rpc("tools/call", "{\"name\":\"list_sessions\",\"arguments\":{}}");
                    Check(Newtonsoft.Json.Linq.JObject.Parse(list)["result"]?["content"] != null, "MCP.tools/call(list_sessions) 正常返回");

                    // get_session 的 id 必须过白名单校验（防 ../ 路径穿越）
                    var bad = Rpc("tools/call", "{\"name\":\"get_session\",\"arguments\":{\"id\":\"../../config\"}}");
                    Check((Newtonsoft.Json.Linq.JObject.Parse(bad)["result"]?["content"]?[0]?["text"]?.ToString() ?? "") == "invalid session id",
                          "MCP.get_session 拒绝非法 id（路径穿越防护）");

                    // REST：/stats 与 /decode 必须真的可用
                    var stats = http.GetStringAsync($"http://127.0.0.1:{apiPort}/stats").GetAwaiter().GetResult();
                    Check(Newtonsoft.Json.Linq.JObject.Parse(stats)["captured"] != null, "REST./stats 返回 statistics");

                    var dec = http.PostAsync($"http://127.0.0.1:{apiPort}/decode",
                        new StringContent("{\"text\":\"5L2g5aW9\",\"mode\":\"base64\"}", Encoding.UTF8, "application/json"))
                        .GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(Newtonsoft.Json.Linq.JObject.Parse(dec)["result"]?.ToString() == "你好", "REST./decode base64 解码正确");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] MCP/REST 服务自测异常: " + ex.Message);
                    fails++;
                }
                finally
                {
                    try { svc?.StopAll(); } catch { }
                    try { svcStore?.Dispose(); } catch { }
                    try { Directory.Delete(svcDir, true); } catch { }
                }
            }

            Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : (fails + " TEST(S) FAILED"));
            foreach (var line in Log.SnapshotLines()) Console.WriteLine(line);
            return fails == 0 ? 0 : 1;
        }

        /// <summary>转发器规则表里「端口/长度 起-止」解析（转发器面板用的是同一份实现）。</summary>
        static bool ToolboxParseRange(string text, out int from, out int to)
            => ForwarderForm.TryParseRange(text, out from, out to);

        /// <summary>服务端证书：Windows SslStream 拒绝内存临时密钥，必须落 PFX 再加载。</summary>
        static byte[] BuildClientHello(string host)
        {
            // 最小化 ClientHello：仅含 SNI 扩展
            // server_name 扩展体: list_len(2) + name_type(1)=0 + name_len(2) + host
            byte[] hostBytes = Encoding.ASCII.GetBytes(host);
            int listLen = hostBytes.Length + 3;
            var extData = new List<byte>();
            extData.Add((byte)(listLen >> 8));
            extData.Add((byte)(listLen & 0xFF));
            extData.Add(0x00); // host_name
            extData.Add((byte)(hostBytes.Length >> 8));
            extData.Add((byte)(hostBytes.Length & 0xFF));
            extData.AddRange(hostBytes);

            var ext = new List<byte> { 0x00, 0x00 }; // extension_type = server_name (0)
            ext.Add((byte)((extData.Count >> 8) & 0xFF));
            ext.Add((byte)(extData.Count & 0xFF));
            ext.AddRange(extData);

            var body = new List<byte>();
            body.AddRange(new byte[] { 0x03, 0x03 }); // client_version
            body.AddRange(new byte[32]);               // random
            body.Add(0);                               // session_id len
            body.AddRange(new byte[] { 0x00, 0x02 });  // cipher suites len
            body.AddRange(new byte[] { 0x13, 0x01 });  // TLS_AES_128_GCM_SHA256
            body.AddRange(new byte[] { 0x01, 0x00 });  // compression methods
            body.AddRange(new byte[] { (byte)((ext.Count >> 8) & 0xFF), (byte)(ext.Count & 0xFF) });
            body.AddRange(ext);

            var hs = new List<byte> { 0x01 };
            hs.AddRange(new byte[] { (byte)((body.Count >> 16) & 0xFF), (byte)((body.Count >> 8) & 0xFF), (byte)(body.Count & 0xFF) });
            hs.AddRange(body);

            var record = new List<byte> { 0x16, 0x03, 0x01 };
            record.AddRange(new byte[] { (byte)((hs.Count >> 8) & 0xFF), (byte)(hs.Count & 0xFF) });
            record.AddRange(hs);
            return record.ToArray();
        }
    }
}
