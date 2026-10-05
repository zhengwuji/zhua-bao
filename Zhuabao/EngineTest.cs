using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>
    /// 引擎级端到端诊断（--enginetest）：
    ///  ① 本地 HTTP 目标 + 本机代理，验证明文转发与落盘；
    ///  ② 局域网模式（0.0.0.0）+ 本地 HTTPS 目标 + 通过局域网 IP 走 CONNECT，
    ///     验证「手机/Mac 那种外部客户端」能连上并抓到解密后的 HTTPS 内容。
    /// </summary>
    internal static class EngineTest
    {
        private const int HttpTargetPort = 18080;
        private const int HttpsTargetPort = 18443;
        private const int LanProxyPort = 18900;

        public static async Task<int> Run()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Log.OnEntry += e => Console.WriteLine(e.Line);

            int fails = await PlainHttpTestAsync();
            fails += await Http1FeaturesTestAsync();
            fails += await LanHttpsTestAsync();
            fails += await ForwarderWiringTestAsync();

            Console.WriteLine(fails == 0 ? "ALL ENGINE TESTS PASSED" : fails + " ENGINE TEST(S) FAILED");
            return fails == 0 ? 0 : 1;
        }

        // ============ ④ V1.0.8 转发器接线端到端 ============
        private const int FwdTargetPort = 18082;
        private const int FwdHookPort = 18083;
        private const int FwdProxyPort = 18902;

        /// <summary>
        /// 验证「代理抓到的封包真的被旁路转发到用户配置的 API」这条接线：
        /// 本地目标站 + 本机代理 + 本地接收端，配置转发器规则（匹配目的端口），
        /// 走一次 HTTP 请求，断言接收端收到 JSON（字段齐全），并且**原请求仍然正常返回**
        /// （旁路不阻塞、不影响正常流量的硬性要求）。
        /// </summary>
        private static async Task<int> ForwarderWiringTestAsync()
        {
            int fails = 0;
            var target = new HttpListener();
            target.Prefixes.Add("http://127.0.0.1:" + FwdTargetPort + "/");
            var hook = new HttpListener();
            hook.Prefixes.Add("http://127.0.0.1:" + FwdHookPort + "/");
            try { target.Start(); } catch { Console.WriteLine("FWD-SKIP: 目标端口被占用"); return 0; }
            try { hook.Start(); } catch { target.Stop(); Console.WriteLine("FWD-SKIP: 接收端端口被占用"); return 0; }

            var received = new System.Collections.Concurrent.ConcurrentQueue<string>();

            _ = Task.Run(async () =>
            {
                while (target.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await target.GetContextAsync(); } catch { break; }
                    try
                    {
                        var body = Encoding.UTF8.GetBytes("{\"ok\":true,\"fwd\":\"target\"}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                }
            });

            _ = Task.Run(async () =>
            {
                while (hook.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await hook.GetContextAsync(); } catch { break; }
                    try
                    {
                        using var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        received.Enqueue(await rd.ReadToEndAsync());
                        ctx.Response.StatusCode = 200;
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                }
            });

            string tmp = Path.Combine(Path.GetTempPath(), "zb-fwd-engine");
            var cfg = new AppConfig { Port = FwdProxyPort };
            cfg.Forwarder = new ForwarderConfig
            {
                Enabled = true,
                TargetUrl = "http://127.0.0.1:" + FwdHookPort + "/",
                Workers = 2,
                QueueLimit = 500,
                TimeoutMs = 5000
            };
            // 只转发目的端口 = 目标站端口的 TCP 包（其余不转发），验证条件匹配在真实链路上生效
            cfg.Forwarder.Rules.Add(new ForwardRule
            {
                RuleName = "引擎接线用例",
                Match = new PacketMatch { UseProtocol = true, Protocols = new List<string> { "TCP" }, UseDstPort = true, DstPortFrom = FwdTargetPort, DstPortTo = FwdTargetPort }
            });

            using var certs = new CertCache(Path.Combine(tmp, "certs"));
            using var store = new SessionStore(Path.Combine(tmp, "capture"));
            var engine = new ProxyEngine(cfg, certs, store);
            engine.Start(FwdProxyPort);
            await Task.Delay(400);

            try
            {
                var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + FwdProxyPort), UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(8) };
                var resp = await http.GetAsync("http://127.0.0.1:" + FwdTargetPort + "/fwd-check?token=ZHUABAO-FWD");
                var text = await resp.Content.ReadAsStringAsync();
                bool normalOk = (int)resp.StatusCode == 200 && text.Contains("target");
                Console.WriteLine("FWD-NORMAL-TRAFFIC: " + (int)resp.StatusCode + " 正常流量未受影响=" + (normalOk ? "是" : "否"));
                if (!normalOk) { Console.WriteLine("FWD-FAIL: 开启转发器后原请求被影响"); fails++; }

                // 等背景 Worker 把旁路队列排空（fire-and-forget，必须异步等到）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (received.Count == 0 && sw.ElapsedMilliseconds < 8000) await Task.Delay(100);
                Console.WriteLine("FWD-RECEIVED: " + received.Count + " 条旁路转发已到达接收端");
                if (received.Count == 0) { Console.WriteLine("FWD-FAIL: 转发器没有把封包送到配置的 API"); fails++; }
                else
                {
                    received.TryDequeue(out var one);
                    bool fields = one.Contains("\"Protocol\"") && one.Contains("DataBase64") && one.Contains("HexData")
                                  && one.Contains("RuleName") && one.Contains("Timestamp") && one.Contains("DstPort");
                    bool carried = Encoding.UTF8.GetString(Convert.FromBase64String(ExtractJsonString(one, "DataBase64") ?? ""))
                                   .Contains("fwd-check");
                    Console.WriteLine("FWD-FIELDS: 字段齐全=" + (fields ? "是" : "否") + " 含原始请求字节=" + (carried ? "是" : "否"));
                    if (!fields) { Console.WriteLine("FWD-FAIL: 转发 JSON 字段不齐"); fails++; }
                    if (!carried) { Console.WriteLine("FWD-FAIL: 转发的封包字节里没有原始请求内容"); fails++; }
                }

                // 条件不匹配的封包不应被转发（换个端口再打一次）
                int before = received.Count;
                using (var c2 = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + FwdProxyPort), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(8) })
                    await c2.GetAsync("http://127.0.0.1:9/skip");
                await Task.Delay(1200);
                Console.WriteLine("FWD-FILTER: 目标端口不匹配的请求 转发数增加=" + Math.Max(0, received.Count - before));
                if (received.Count - before > 0) { Console.WriteLine("FWD-FAIL: 条件不匹配的封包被错误转发"); fails++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FWD-ERR: " + ex.Message);
                fails++;
            }
            finally
            {
                engine.Stop();
                try { target.Stop(); } catch { }
                try { hook.Stop(); } catch { }
                try { Directory.Delete(tmp, true); } catch { }
            }
            return fails;
        }

        /// <summary>从扁平 JSON 里取一个字符串字段的原始值（不引 JSON 依赖，够用即可）。</summary>
        private static string ExtractJsonString(string json, string field)
        {
            var key = "\"" + field + "\":\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = json.IndexOf('"', i);
            return j < 0 ? null : json.Substring(i, j - i);
        }

        // ============ ① 明文 HTTP 端到端 ============
        private static async Task<int> PlainHttpTestAsync()
        {
            var target = new HttpListener();
            target.Prefixes.Add("http://127.0.0.1:" + HttpTargetPort + "/");
            target.Start();
            var targetTask = Task.Run(async () =>
            {
                while (target.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await target.GetContextAsync(); }
                    catch { break; }
                    try
                    {
                        var body = Encoding.UTF8.GetBytes("{\"ok\":true,\"path\":\"" + ctx.Request.Url.PathAndQuery + "\"}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch { try { ctx.Response.Close(); } catch { } }
                }
            });

            string tmp = Path.Combine(Path.GetTempPath(), "zb-engine-test");
            var cfg = new AppConfig { Port = 18899 };
            var certs = new CertCache(Path.Combine(tmp, "certs"));
            var store = new SessionStore(Path.Combine(tmp, "capture"));
            var engine = new ProxyEngine(cfg, certs, store);
            engine.OnSession += s => Console.WriteLine("SESSION: " + s.Method + " " + s.Url + " -> " + s.StatusCode);
            engine.Start(18899);
            await Task.Delay(500);

            int fails = 0;
            try
            {
                var http = new HttpClient(new HttpClientHandler
                {
                    Proxy = new WebProxy("http://127.0.0.1:18899"),
                    UseProxy = true
                })
                { Timeout = TimeSpan.FromSeconds(8) };
                var resp = await http.GetAsync("http://127.0.0.1:" + HttpTargetPort + "/hello?from=engine-test");
                var text = await resp.Content.ReadAsStringAsync();
                Console.WriteLine("CLIENT-RESP: " + (int)resp.StatusCode + " " + text);
                if (!text.Contains("engine-test")) { Console.WriteLine("HTTP-FAIL: 响应内容不含请求标记"); fails++; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("HTTP-ERR: " + ex.Message);
                fails++;
            }

            await Task.Delay(500);
            var files = Directory.GetFiles(store.Dir, "*.zb");
            Console.WriteLine("CAPTURED: " + files.Length);
            if (files.Length == 0) { Console.WriteLine("HTTP-FAIL: 没有落盘任何会话"); fails++; }

            engine.Stop();
            target.Stop();
            return fails;
        }

        // ============ ② HTTP/1.1 细节端到端（大POST/解压/chunked/close定界/keep-alive/WS透传）============
    private const int RawTargetPort = 18081;

    private static async Task<int> Http1FeaturesTestAsync()
    {
        var target = await StartRawTargetAsync(RawTargetPort);
        string tmp = Path.Combine(Path.GetTempPath(), "zb-http1-test");
        var cfg = new AppConfig { Port = 18901 };
        using var certs = new CertCache(Path.Combine(tmp, "certs"));
        using var store = new SessionStore(Path.Combine(tmp, "capture"));
        var engine = new ProxyEngine(cfg, certs, store);
        engine.Start(18901);
        await Task.Delay(400);

        int fails = 0;
        var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:18901"), UseProxy = true })
        { Timeout = TimeSpan.FromSeconds(15) };

        // (a) 大 POST：200KB 体必须完整转发（修复前 ReadHeadAsync 找到头就停，体被截断）
        try
        {
            var payload = new byte[200 * 1024];
            new Random(7).NextBytes(payload);
            payload[0] = 0xAA; payload[^1] = 0x55;
            var resp = await http.PostAsync("http://127.0.0.1:" + RawTargetPort + "/echo", new ByteArrayContent(payload));
            var text = await resp.Content.ReadAsStringAsync();
            bool ok = text.Contains("\"echo_len\":" + payload.Length) && text.Contains("AA") && text.Contains("55");
            Console.WriteLine("HTTP1-LARGEPOST: " + (int)resp.StatusCode + " " + Trunc(text, 120) + " → " + (ok ? "OK" : "FAIL"));
            if (!ok) { Console.WriteLine("HTTP1-FAIL: 大请求体转发不完整"); fails++; }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-LARGEPOST-ERR: " + ex.Message); fails++; }

        // (b) gzip 响应：代理必须解压后回给客户端（修复前详情页全是乱码）
        try
        {
            var resp = await http.GetAsync("http://127.0.0.1:" + RawTargetPort + "/gz");
            var text = await resp.Content.ReadAsStringAsync();
            bool ok = text.Contains("GZIPPED-BY-TARGET");
            Console.WriteLine("HTTP1-GZIP: " + (int)resp.StatusCode + " 明文可见=" + (ok ? "是" : "否"));
            if (!ok) { Console.WriteLine("HTTP1-FAIL: gzip 响应未被解压"); fails++; }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-GZIP-ERR: " + ex.Message); fails++; }

        // (c) chunked 响应
        try
        {
            var resp = await http.GetAsync("http://127.0.0.1:" + RawTargetPort + "/chunked");
            var text = await resp.Content.ReadAsStringAsync();
            bool ok = text.Contains("CHUNKED-BY-TARGET");
            Console.WriteLine("HTTP1-CHUNKED: " + (int)resp.StatusCode + " 内容完整=" + (ok ? "是" : "否"));
            if (!ok) { Console.WriteLine("HTTP1-FAIL: chunked 响应不完整"); fails++; }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-CHUNKED-ERR: " + ex.Message); fails++; }

        // (d) close-delimited 响应（无 Content-Length，修复前第一块就提前返回被截断）
        try
        {
            var resp = await http.GetAsync("http://127.0.0.1:" + RawTargetPort + "/close");
            var text = await resp.Content.ReadAsStringAsync();
            bool ok = text.Length >= 40_000 && text.EndsWith("END-OF-CLOSE-BODY", StringComparison.Ordinal);
            Console.WriteLine("HTTP1-CLOSE-DELIM: " + (int)resp.StatusCode + " 长度=" + text.Length + " 完整=" + (ok ? "是" : "否"));
            if (!ok) { Console.WriteLine("HTTP1-FAIL: close-delimited 响应被截断"); fails++; }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-CLOSE-ERR: " + ex.Message); fails++; }

        // (e) keep-alive：同一条客户端连接连发两个请求都要有响应
        try
        {
            using var tcp = new TcpClient("127.0.0.1", 18901);
            var s = tcp.GetStream();
            var req1 = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + RawTargetPort + "/ka?a=1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            var req2 = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + RawTargetPort + "/ka?a=2 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            await s.WriteAsync(req1);
            var r1 = await StreamIO.ReadHttpMessageAsync(s, CancellationToken.None, null, TimeSpan.FromSeconds(10), closeDelimited: true);
            await s.WriteAsync(req2);
            var r2 = await StreamIO.ReadHttpMessageAsync(s, CancellationToken.None, null, TimeSpan.FromSeconds(10), closeDelimited: true);
            var t1 = r1 == null ? "" : Encoding.Latin1.GetString(r1);
            var t2 = r2 == null ? "" : Encoding.Latin1.GetString(r2);
            bool ok = t1.Contains("ka-count=1") && t2.Contains("ka-count=2");
            Console.WriteLine("HTTP1-KEEPALIVE: 响应1=" + t1.Split('\n')[0].Trim() + " 响应2=" + (t2.Split('\n')[0].Trim()) + " → " + (ok ? "OK" : "FAIL"));
            if (!ok) { Console.WriteLine("HTTP1-FAIL: keep-alive 第二个请求没有响应"); fails++; }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-KEEPALIVE-ERR: " + ex.Message); fails++; }

        // (f) WebSocket 透传：握手 101 后帧必须双向转发
        try
        {
            using var tcp = new TcpClient("127.0.0.1", 18901);
            var s = tcp.GetStream();
            var upgrade = Encoding.ASCII.GetBytes(
                "GET http://127.0.0.1:" + RawTargetPort + "/ws HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
            await s.WriteAsync(upgrade);
            var respBytes = await StreamIO.ReadHttpMessageAsync(s, CancellationToken.None, null, TimeSpan.FromSeconds(10));
            var respHead = respBytes == null ? "" : Encoding.Latin1.GetString(respBytes);
            bool upgraded = respHead.StartsWith("HTTP/1.1 101", StringComparison.Ordinal);
            Console.WriteLine("HTTP1-WS: 握手=" + (upgraded ? "101 OK" : "FAIL " + respHead.Split('\n')[0].Trim()));
            if (!upgraded) { fails++; }
            else
            {
                var msg = Encoding.UTF8.GetBytes("zb-ws-frame-12345");
                await s.WriteAsync(WsFrame(msg, mask: true));
                var echo = await ReadWsFrame(s, TimeSpan.FromSeconds(10));
                bool ok = echo != null && echo.AsSpan().SequenceEqual(msg);
                Console.WriteLine("HTTP1-WS: 帧回显=" + (ok ? "OK" : "FAIL"));
                if (!ok) { Console.WriteLine("HTTP1-FAIL: WebSocket 帧未透传"); fails++; }
            }
        }
        catch (Exception ex) { Console.WriteLine("HTTP1-WS-ERR: " + ex.Message); fails++; }

        // 落盘检查
        await Task.Delay(600);
        var files = Directory.GetFiles(store.Dir, "*.zb");
        Console.WriteLine("HTTP1-CAPTURED: " + files.Length + " 条会话已落盘");
        if (files.Length < 4) { Console.WriteLine("HTTP1-FAIL: 会话落盘数量异常"); fails++; }

        http.Dispose();
        engine.Stop();
        target.Stop();
        try { Directory.Delete(tmp, true); } catch { }
        return fails;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

    /// <summary>原始 HTTP 目标站：按路径返回不同分帧/编码的响应，支持 keep-alive 与 WebSocket 回显。</summary>
    private static Task<TcpListener> StartRawTargetAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            while (true)
                            {
                                var msg = await StreamIO.ReadHttpMessageAsync(s, CancellationToken.None, null, TimeSpan.FromSeconds(15));
                                if (msg == null || msg.Length == 0) break;
                                var head = Encoding.Latin1.GetString(msg);
                                int headEnd = StreamIO.FindHeaderEnd(msg, msg.Length);
                                var body = headEnd >= 0 && msg.Length > headEnd + 4 ? msg[(headEnd + 4)..] : Array.Empty<byte>();
                                var line = head.Split("\r\n")[0];
                                var parts = line.Split(' ');
                                var path = parts.Length > 1 ? parts[1] : "/";
                                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    path = new Uri(path).PathAndQuery;

                                switch (path)
                                {
                                    case "/echo":
                                    {
                                        var headHex = body.Length > 0 ? body[0].ToString("X2") : "";
                                        var tailHex = body.Length > 0 ? body[^1].ToString("X2") : "";
                                        await WriteSimpleAsync(s, "200 OK", "application/json",
                                            Encoding.UTF8.GetBytes("{\"echo_len\":" + body.Length + ",\"head\":\"" + headHex + "\",\"tail\":\"" + tailHex + "\"}"));
                                        break;
                                    }
                                    case "/gz":
                                    {
                                        var payload = Encoding.UTF8.GetBytes("{\"secret\":\"GZIPPED-BY-TARGET\"}");
                                        using var ms = new MemoryStream();
                                        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                                            gz.Write(payload);
                                        var gzBody = ms.ToArray();
                                        var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: " + gzBody.Length + "\r\n\r\n");
                                        await s.WriteAsync(h); await s.WriteAsync(gzBody);
                                        break;
                                    }
                                    case "/chunked":
                                    {
                                        var payload = Encoding.UTF8.GetBytes("{\"secret\":\"CHUNKED-BY-TARGET\"}");
                                        var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n");
                                        await s.WriteAsync(h);
                                        var part1 = payload[..10];
                                        var part2 = payload[10..];
                                        await s.WriteAsync(Encoding.ASCII.GetBytes(part1.Length.ToString("X") + "\r\n"));
                                        await s.WriteAsync(part1);
                                        await s.WriteAsync(Encoding.ASCII.GetBytes("\r\n" + part2.Length.ToString("X") + "\r\n"));
                                        await s.WriteAsync(part2);
                                        await s.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"));
                                        break;
                                    }
                                    case "/close":
                                    {
                                        var payload = Encoding.Latin1.GetBytes(new string('X', 40_000)) ;
                                        var tail = Encoding.Latin1.GetBytes("END-OF-CLOSE-BODY");
                                        var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n");
                                        await s.WriteAsync(h);
                                        await s.WriteAsync(payload);
                                        await s.WriteAsync(tail);
                                        return; // 直接关连接 = close-delimited
                                    }
                                    case "/ws":
                                    {
                                        if (head.Contains("Upgrade:", StringComparison.OrdinalIgnoreCase))
                                        {
                                            await s.WriteAsync(Encoding.ASCII.GetBytes(
                                                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                                "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n"));
                                            while (true)
                                            {
                                                var frame = await ReadWsFrame(s, TimeSpan.FromSeconds(30));
                                                if (frame == null) break;
                                                await s.WriteAsync(WsFrame(frame, mask: false));
                                            }
                                            return;
                                        }
                                        break;
                                    }
                                    default: // /ka 等
                                    {
                                        int count = 1;
                                        if (Uri.TryCreate("http://x" + path, UriKind.Absolute, out var u) && u.Query.Contains("a=2")) count = 2;
                                        await WriteSimpleAsync(s, "200 OK", "text/plain",
                                            Encoding.UTF8.GetBytes("ka-count=" + count + " path=" + u?.PathAndQuery));
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                });
            }
        });
        return Task.FromResult(listener);
    }

    private static async Task WriteSimpleAsync(Stream s, string status, string ctype, byte[] body)
    {
        var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + ctype +
                                          "\r\nContent-Length: " + body.Length + "\r\n\r\n");
        await s.WriteAsync(h);
        await s.WriteAsync(body);
    }

    private static byte[] WsFrame(byte[] payload, bool mask)
    {
        var buf = new MemoryStream();
        buf.WriteByte(0x81); // FIN + text
        if (mask) buf.WriteByte((byte)(0x80 | (payload.Length < 126 ? payload.Length : 126)));
        else buf.WriteByte(payload.Length < 126 ? (byte)payload.Length : (byte)126);
        if (payload.Length >= 126)
        {
            buf.WriteByte((byte)(payload.Length >> 8));
            buf.WriteByte((byte)(payload.Length & 0xFF));
        }
        var key = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        if (mask) buf.Write(key);
        for (int i = 0; i < payload.Length; i++) buf.WriteByte((byte)(payload[i] ^ (mask ? key[i % 4] : 0)));
        return buf.ToArray();
    }

    private static async Task<byte[]> ReadWsFrame(Stream s, TimeSpan timeout)
    {
        var head = new byte[2];
        if (!await ReadFullAsync(s, head, timeout)) return null;
        bool masked = (head[1] & 0x80) != 0;
        int len = head[1] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            if (!await ReadFullAsync(s, ext, timeout)) return null;
            len = (ext[0] << 8) | ext[1];
        }
        var maskKey = new byte[4];
        if (masked && !await ReadFullAsync(s, maskKey, timeout)) return null;
        var payload = new byte[len];
        if (!await ReadFullAsync(s, payload, timeout)) return null;
        if (masked) for (int i = 0; i < len; i++) payload[i] ^= maskKey[i % 4];
        return payload;
    }

    private static async Task<bool> ReadFullAsync(Stream s, byte[] buf, TimeSpan timeout)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off)).AsTask().WaitAsync(timeout);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    // ============ ③ 局域网 + HTTPS 解密端到端（含隧道内 keep-alive）============
    private static async Task<int> LanHttpsTestAsync()
        {
            var ips = LanHelper.LocalIPv4();
            Console.WriteLine("LAN-IP: " + (ips.Count == 0 ? "(none)" : string.Join(", ", ips)));
            if (ips.Count == 0) { Console.WriteLine("LAN-SKIP: 未检测到局域网地址，跳过局域网用例"); return 0; }

            int fails = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "zb-lan-test");
            var cfg = new AppConfig { Port = LanProxyPort, LanCapture = true };
            using var certs = new CertCache(Path.Combine(tmp, "certs"));
            var store = new SessionStore(Path.Combine(tmp, "capture"));
            var engine = new ProxyEngine(cfg, certs, store);
            engine.OnSession += s => Console.WriteLine("LAN-SESSION: " + s.Protocol + " " + s.Method + " " + s.Url + " -> " + s.StatusCode);

            // 上游：本地假 HTTPS 站点（自签证书即可，代理侧不校验证书）
            var upstream = await StartFakeHttpsAsync(HttpsTargetPort);
            engine.Start(LanProxyPort);
            await Task.Delay(400);

            string target = "127.0.0.1:" + HttpsTargetPort;
            foreach (var ip in ips)
            {
                try
                {
                    using var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(ip, LanProxyPort);
                    if (await Task.WhenAny(connect, Task.Delay(3000)) != connect || !tcp.Connected)
                    {
                        Console.WriteLine("LAN-FAIL: 无法连接 " + ip + ":" + LanProxyPort + "（监听没绑到 0.0.0.0？）");
                        fails++;
                        continue;
                    }

                    var s = tcp.GetStream();
                    await s.WriteAsync(Encoding.ASCII.GetBytes(
                        "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n\r\n"));

                    var headBuf = new byte[256];
                    int hn = await s.ReadAsync(headBuf);
                    var connectResp = Encoding.ASCII.GetString(headBuf, 0, hn);
                    if (!connectResp.Contains("200"))
                    {
                        Console.WriteLine("LAN-FAIL: " + ip + " CONNECT 响应异常: " + connectResp.Replace("\r\n", " "));
                        fails++;
                        continue;
                    }

                    X509Certificate2 seen = null;
                    using var ssl = new SslStream(s, false, (a, cert, chain, err) =>
                    {
                        try { seen = new X509Certificate2(cert); } catch { }
                        return true;
                    });
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "127.0.0.1",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    });

                    // (a) 叶子证书必须能用「装到信任库里的 CA」验通，并且带 IP SAN
                    bool chainOk = false;
                    if (seen != null)
                    {
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(certs.Ca);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chainOk = chain.Build(seen);
                    }
                    var san = seen?.Extensions["2.5.29.17"]?.RawData ?? Array.Empty<byte>();
                    bool ipSanOk = Contains(san, new byte[] { 0x87, 0x04, 0x7f, 0x00, 0x00, 0x01 });
                    Console.WriteLine("LAN-CERT: chain=" + (chainOk ? "OK" : "FAIL") + " ipSAN=" + (ipSanOk ? "OK" : "FAIL") +
                                      " subject=" + (seen?.Subject ?? "(null)"));
                    if (!chainOk) { Console.WriteLine("LAN-FAIL: 叶子证书无法用本机 CA 验通"); fails++; }
                    if (!ipSanOk) { Console.WriteLine("LAN-FAIL: 叶子证书缺 127.0.0.1 的 iPAddress SAN"); fails++; }

                    // (b) 同一条隧道里连发两个 HTTPS 请求（keep-alive + 上游连接复用）
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                        "GET /secure HTTP/1.1\r\nHost: " + target + "\r\nUser-Agent: zb-lan-test\r\n\r\n"));
                    var r1 = await StreamIO.ReadHttpMessageAsync(ssl, CancellationToken.None, null, TimeSpan.FromSeconds(10), closeDelimited: true);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                        "GET /secure2 HTTP/1.1\r\nHost: " + target + "\r\nUser-Agent: zb-lan-test\r\n\r\n"));
                    var r2 = await StreamIO.ReadHttpMessageAsync(ssl, CancellationToken.None, null, TimeSpan.FromSeconds(10), closeDelimited: true);
                    var respText = r1 == null ? "" : Encoding.UTF8.GetString(r1);
                    var respText2 = r2 == null ? "" : Encoding.UTF8.GetString(r2);
                    bool decrypted = respText.Contains("DECRYPTED-BY-ZHUABAO") && respText.Contains("/secure\"");
                    bool decrypted2 = respText2.Contains("DECRYPTED-BY-ZHUABAO") && respText2.Contains("/secure2\"");
                    Console.WriteLine("LAN-HTTPS: " + ip + ":" + LanProxyPort + " → 请求1完整=" + (decrypted ? "是" : "否") +
                                      " 请求2(同隧道)完整=" + (decrypted2 ? "是" : "否"));
                    if (!decrypted) { Console.WriteLine("LAN-FAIL: 未拿到解密后的响应体"); fails++; }
                    if (!decrypted2) { Console.WriteLine("LAN-FAIL: 同隧道第二个请求失败（keep-alive 未生效）"); fails++; }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("LAN-ERR: " + ip + " → " + ex.Message);
                    fails++;
                }
            }

            await Task.Delay(300);
            var httpsSessions = Directory.GetFiles(store.Dir, "*.zb");
            Console.WriteLine("LAN-CAPTURED: " + httpsSessions.Length + " 条会话已落盘");
            if (httpsSessions.Length == 0) { Console.WriteLine("LAN-FAIL: HTTPS 会话没有落盘"); fails++; }

            engine.Stop();
            upstream.Stop();
            try { Directory.Delete(tmp, true); } catch { }
            return fails;
        }

    /// <summary>本地假 HTTPS 站点：自签证书 + 循环处理请求（keep-alive），响应体回显请求路径，用于验证 MITM 解密链路。</summary>
    private static Task<TcpListener> StartFakeHttpsAsync(int port)
    {
        var cert = MakeServerCert("127.0.0.1");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            using var ssl = new SslStream(c.GetStream(), false);
                            await ssl.AuthenticateAsServerAsync(cert);
                            while (true)
                            {
                                var msg = await StreamIO.ReadHttpMessageAsync(ssl, CancellationToken.None, null, TimeSpan.FromSeconds(20));
                                if (msg == null || msg.Length == 0) break;
                                var firstLine = Encoding.Latin1.GetString(msg).Split('\n')[0].Trim();
                                var path = firstLine.Split(' ').Length > 1 ? firstLine.Split(' ')[1] : "/";
                                var body = Encoding.UTF8.GetBytes("{\"ok\":true,\"path\":\"" + path + "\",\"secret\":\"DECRYPTED-BY-ZHUABAO\"}");
                                var head = Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                                    body.Length + "\r\n\r\n");
                                await ssl.WriteAsync(head);
                                await ssl.WriteAsync(body);
                            }
                        }
                        catch { }
                    }
                });
            }
        });
        return Task.FromResult(listener);
    }

        /// <summary>服务端证书：Windows SslStream 拒绝内存临时密钥，必须落 PFX 再加载。</summary>
        private static X509Certificate2 MakeServerCert(string cn)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            var tmp = Path.Combine(Path.GetTempPath(), "zb-srv-" + Guid.NewGuid().ToString("N") + ".pfx");
            try
            {
                File.WriteAllBytes(tmp, created.Export(X509ContentType.Pfx, "zhuabao"));
                return new X509Certificate2(tmp, "zhuabao", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0) return false;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                if (hit) return true;
            }
            return false;
        }
    }
}
