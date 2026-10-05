using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zhuabao.Core;

namespace Zhuabao.Core
{
    /// <summary>
    /// MITM 代理引擎：
    ///  - 普通 HTTP：连接上循环读请求 → 规则改写 → 转发（同目标复用上游连接）→ 读响应 → 改写 → 回写（keep-alive）
    ///  - HTTPS：CONNECT → 用 CA 签发叶子证书与客户端握手 → 解密后在同一条隧道里循环处理多个请求（keep-alive）
    ///  - WebSocket：检测到 101 后进入双向透传
    ///  - 请求体按 Content-Length/chunked 补全读取（大 POST 不截断）；响应体按需解压 gzip/deflate/br
    /// 每个环节都有 Log 打点（模块名=代理/HTTPS/重定向/改包/会话/WebHook）。
    /// </summary>
    public sealed class ProxyEngine
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private int _connSeq;

        // 空闲超时：keep-alive 连接上等下一个请求 / 等响应剩余字节的耐心
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);
        private const int HeadCap = StreamIO.HeadCap;

        public AppConfig Config { get; }
        public CertCache Certs { get; }
        public SessionStore Store { get; }
        public JsScriptEngine Scripts { get; } = new();

        /// <summary>V1.0.8 新增：旁路转发器。命中规则的 TCP 封包异步 POST 到用户配置的 API，不影响正常流量。</summary>
        public ForwarderEngine Forwarder { get; private set; }

        private int _captured, _intercepted, _modified, _sent, _redirected, _blocked;
        public int CapturedCount => Volatile.Read(ref _captured);
        public int InterceptedCount => Volatile.Read(ref _intercepted);
        public int ModifiedCount => Volatile.Read(ref _modified);
        public int SentCount => Volatile.Read(ref _sent);
        public int RedirectedCount => Volatile.Read(ref _redirected);
        public int BlockedCount => Volatile.Read(ref _blocked);

        public event Action<HttpSession> OnSession;
        public event Action<string> OnStatus;

        public ProxyEngine(AppConfig cfg, CertCache certs, SessionStore store)
        {
            Config = cfg;
            Certs = certs;
            Store = store;
            Forwarder = new ForwarderEngine(cfg?.Forwarder);
            Log.D("代理", $"引擎创建 (端口={cfg.Port}, 改包规则={cfg.ReplaceRules.Count}, 重定向={cfg.Redirects.Count}, 转发器={(Forwarder.Enabled ? "开" : "关")})");
        }

        /// <summary>
        /// V1.0.8 新增：把一条 TCP/UDP 封包喂给转发器（旁路，fire-and-forget，绝不阻塞、绝不抛异常）。
        /// 调用点在「抓包线程已经拿到完整字节」之后，只做条件匹配 + 入队，序列化与 POST 在后台 Worker。
        /// </summary>
        public void ForwardPacket(string protocol, string srcIp, int srcPort, string dstIp, int dstPort,
            string process, byte[] data, ForwardDirection direction)
        {
            try
            {
                if (Forwarder == null || !Forwarder.Enabled) return;
                Forwarder.Observe(new PacketEvent
                {
                    Protocol = protocol,
                    SrcIP = srcIp ?? string.Empty, SrcPort = srcPort,
                    DstIP = dstIp ?? string.Empty, DstPort = dstPort,
                    ProcessName = process ?? string.Empty,
                    Data = data ?? Array.Empty<byte>(),
                    Direction = direction
                });
            }
            catch (Exception ex) { Log.W("转发", "喂包异常（已忽略，不影响抓包）: " + ex.Message); }
        }

        /// <summary>把一条 HTTP 会话按「方向 → 封包」拆成转发事件（请求方向 + 响应方向各一个）。</summary>
        private void ForwardSession(HttpSession s)
        {
            try
            {
                if (Forwarder == null || !Forwarder.Enabled || s == null) return;
                string host = s.Host;
                int port = 0;
                if (!string.IsNullOrEmpty(host))
                {
                    try
                    {
                        var uri = new Uri(s.Url);
                        port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
                    }
                    catch { port = 0; }
                }
                // 抓包端视角：本机 → 目标 = 发送方向
                if (s.Request != null)
                {
                    var raw = s.Request.ToRawBytes();
                    ForwardPacket("TCP", "127.0.0.1", 0, host, port, s.Process, raw, ForwardDirection.Send);
                }
                if (s.Response != null)
                {
                    var raw = s.Response.ToRawBytes();
                    ForwardPacket("TCP", host, port, "127.0.0.1", 0, s.Process, raw, ForwardDirection.Receive);
                }
            }
            catch (Exception ex) { Log.W("转发", "会话拆包失败（已忽略）: " + ex.Message); }
        }

        /// <summary>界面把「启用转发器」开关拨动后，让引擎里的转发器立刻看到新配置。</summary>
        public void ApplyForwarderConfig()
        {
            try
            {
                Forwarder?.Dispose();
                Forwarder = new ForwarderEngine(Config?.Forwarder);
                Log.I("转发", "转发器配置已应用 (启用=" + (Forwarder.Enabled) + ", 规则数=" + (Config?.Forwarder?.Rules?.Count ?? 0) + ")");
            }
            catch (Exception ex) { Log.W("转发", "转发器配置应用失败: " + ex.Message); }
        }

        public bool Running { get; private set; }
        public int ListeningPort { get; private set; }

        public void Start(int port)
        {
            if (Running) return;
            try
            {
                ListeningPort = port;
                _cts = new CancellationTokenSource();
                var bindAddr = Config.LanCapture ? IPAddress.Any : IPAddress.Loopback;
                _listener = new TcpListener(bindAddr, port);
                _listener.Start();
                Running = true;
                Task.Run(() => AcceptLoopAsync(_cts.Token));
                if (Config.LanCapture)
                {
                    Log.I("代理", $"监听已启动 0.0.0.0:{port} (局域网模式：手机/Mac 可直连)");
                    foreach (var ip in LanHelper.LocalIPv4()) Log.I("网络", $"局域网设备可用代理地址: {ip}:{port}");
                    Log.W("代理", "局域网模式没有账号密码保护，同网段设备都能使用本代理，用完请切回『仅本机』");
                    OnStatus?.Invoke("代理服务: 已开启 (局域网 0.0.0.0:" + port + ")");
                }
                else
                {
                    Log.I("代理", $"监听已启动 127.0.0.1:{port} (等待客户端连接)");
                    OnStatus?.Invoke("代理服务: 已开启 (127.0.0.1:" + port + ")");
                }
            }
            catch (Exception ex)
            {
                Log.E("代理", $"监听 {port} 端口失败", ex);
                Running = false;
                OnStatus?.Invoke("代理服务: 启动失败 (" + ex.Message + ")");
                throw;
            }
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            try { _cts.Cancel(); _listener.Stop(); } catch { }
            Log.I("代理", "监听已停止");
            OnStatus?.Invoke("代理服务: 已关闭");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                int id = Interlocked.Increment(ref _connSeq);
                Log.D("代理", $"#{id} 新连接来自 {client.Client.RemoteEndPoint}");
                _ = Task.Run(() => HandleClientAsync(id, client, ct), ct);
            }
        }

        private async Task HandleClientAsync(int id, TcpClient client, CancellationToken ct)
        {
            string process = null;
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (client)
                {
                    var stream = client.GetStream();

                    // 客户端进程 = 本连接对端（客户端的临时端口归它所有）；一条连接只查一次
                    try { process = ProcessLocator.FindByLocalPort(((IPEndPoint)client.Client.RemoteEndPoint).Port); }
                    catch { }

                    var first = await StreamIO.ReadHttpMessageAsync(stream, ct, Send100Continue(stream), IdleTimeout);
                    if (first == null) { Log.D("代理", $"#{id} 请求头超限，连接关闭"); return; }
                    if (first.Length == 0) { Log.D("代理", $"#{id} 客户端未发送数据即断开"); return; }
                    var headText = Encoding.ASCII.GetString(first);

                    if (headText.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.D("代理", $"#{id} HTTPS CONNECT 隧道请求");
                        await HandleConnectAsync(id, client, stream, headText, process, ct);
                    }
                    else
                    {
                        Log.D("代理", $"#{id} 普通 HTTP 请求 (keep-alive 循环)");
                        await HandlePlainHttpLoopAsync(id, stream, first, process, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.W("代理", $"#{id} 连接处理异常: {ex.Message}");
            }
            finally
            {
                Log.D("代理", $"#{id} 连接结束");
            }
        }

        /// <summary>Expect: 100-continue 时先回 100，客户端才会继续发请求体。</summary>
        private static Action<string> Send100Continue(Stream clientStream)
        {
            return head =>
            {
                try
                {
                    if (head != null && head.IndexOf("expect:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        head.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var b = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        clientStream.Write(b, 0, b.Length);
                        Log.D("HTTP", "已应答 Expect: 100-continue");
                    }
                }
                catch { }
            };
        }

        // ============ 普通 HTTP（连接内 keep-alive 循环）============
        private async Task HandlePlainHttpLoopAsync(int id, NetworkStream clientStream, byte[] firstMsg, string process, CancellationToken ct)
        {
            UpstreamLink up = null;
            string upKey = null;
            try
            {
                byte[] msg = firstMsg;
                while (msg != null && msg.Length > 0)
                {
                    var req = HttpRequest.Parse(msg);
                    if (string.IsNullOrEmpty(req.Method))
                    {
                        Log.W("HTTP", $"#{id} 无法解析请求起始行，关闭连接");
                        return;
                    }
                    // 重定向（映射）
                    var originalUrl = req.Url;
                    req.Url = RedirectEngine.Rewrite(req.Url, Config.Redirects);
                    if (req.Url != originalUrl)
                    {
                        CapturedInc(ref _redirected);
                        Log.I("重定向", $"{originalUrl}  ->  {req.Url}");
                    }
                    var session = new HttpSession { Request = req, Process = process ?? "", Protocol = "HTTP" };

                    if (!req.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        req.Url = "http://" + req.Host + req.PathAndQuery;
                    Log.D("HTTP", $"#{id} → 请求: {req.Method} {req.Url}");

                    CapturedInc(ref _captured);
                    if (Config.InterceptEnabled)
                    {
                        session.InterceptStatus = "拦截";
                        CapturedInc(ref _intercepted);
                        Log.I("断点", $"#{id} 已拦截 (当前版本自动放行): {req.Url}");
                    }
                    OnSession?.Invoke(session);

                    try { Scripts.RunRequestScript(null, req, null, new Dictionary<string, string>()); }
                    catch (Exception ex) { Log.W("脚本", $"#{id} 请求脚本执行失败: {ex.Message}"); }
                    ReplaceEngine.ApplyRules(Config.ReplaceRules, req, null);
                    if (Config.DecodeGzip) req.Headers["Accept-Encoding"] = "identity";

                    // 上游：同目标复用连接，目标变了或连接失效才重连
                    var key = UpstreamLink.KeyOf(req.Url);
                    if (up == null || up.Dead || upKey != key)
                    {
                        up?.Dispose();
                        up = await UpstreamLink.ConnectAsync(key, Config.NicIp, ct);
                        upKey = key;
                        Log.D("转发", $"#{id} 新建上游连接 {key}");
                    }

                    HttpResponse resp = null;
                    if (up != null)
                    {
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            var respBytes = await up.SendAsync(req, ct);
                            sw.Stop();
                            session.DurationMs = (int)sw.ElapsedMilliseconds;
                            resp = respBytes == null || respBytes.Length == 0 ? null : HttpResponse.Parse(respBytes);
                        }
                        catch (Exception ex)
                        {
                            up.Dead = true;
                            Log.W("转发", $"#{id} 转发失败: {ex.Message}");
                        }
                        Log.D("HTTP", $"#{id} ← 上游响应: {(resp == null ? "无" : resp.StatusCode + " (" + session.DurationMs + "ms)")}");
                    }

                    if (resp != null)
                    {
                        if (Config.DecodeGzip && resp.TryDecompressBody()) Log.D("HTTP", $"#{id} 响应体已解压 ({session.ResponseBytes}B)");
                        ReplaceEngine.ApplyRules(Config.ReplaceRules, req, resp);
                        if (Config.ReplaceRules?.Count > 0) CapturedInc(ref _modified);
                        session.SetResponse(resp);
                        try
                        {
                            var respRaw = resp.ToRawBytes();
                            await clientStream.WriteAsync(respRaw, ct);
                            CapturedInc(ref _sent);
                            Log.D("HTTP", $"#{id} ← 已回写客户端 {respRaw.Length} 字节");
                        }
                        catch (Exception ex) { Log.W("HTTP", $"#{id} 回写客户端失败: {ex.Message}"); }
                    }
                    else
                    {
                        session.StatusCode = "超时";
                        Log.W("HTTP", $"#{id} 上游无响应，返回 502: {req.Url}");
                        try
                        {
                            var err = Encoding.Latin1.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await clientStream.WriteAsync(err, ct);
                        }
                        catch { }
                        Store.Add(session);
                        FireWebHook(session);
                        ForwardSession(session);
                        OnSession?.Invoke(session);
                        return; // 502 后关连接
                    }

                    // WebSocket 升级成功 → 双向透传
                    if (resp.StatusCode == 101)
                    {
                        session.Protocol = "WS";
                        Log.I("WS", $"#{id} WebSocket 握手完成，进入透传模式: {req.Url}");
                        Store.Add(session);
                        ForwardSession(session);
                        FireWebHook(session);
                        OnSession?.Invoke(session);
                        await PumpAsync(clientStream, up.Stream, ct, req.Host, UpstreamLink.PortOf(req.Url), session.Process);
                        return;
                    }

                    Store.Add(session);
                    Log.D("会话", $"#{id} 已存盘 {session.Id}.zb ({session.Method} {session.Url} -> {session.StatusCode})");
                    ForwardSession(session);   // V1.0.8：旁路转发（不阻塞、不影响原流程）
                    FireWebHook(session);
                    OnSession?.Invoke(session);

                    if (WantsClose(req, resp)) return;

                    // 读同一条连接上的下一个请求
                    byte[] next;
                    try { next = await StreamIO.ReadHttpMessageAsync(clientStream, ct, Send100Continue(clientStream), IdleTimeout); }
                    catch (Exception ex) { Log.D("HTTP", $"#{id} 读取下一个请求结束: {ex.Message}"); return; }
                    if (next == null || next.Length == 0) return;
                    msg = next;
                }
            }
            finally
            {
                up?.Dispose();
            }
        }

        // ============ HTTPS CONNECT（隧道内 keep-alive 循环）============
        private async Task HandleConnectAsync(int id, TcpClient client, NetworkStream clientStream, string headText, string process, CancellationToken ct)
        {
            var firstLine = headText.Split('\n')[0].Trim();
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return;
            var target = parts[1];
            // 目标可能是 host、host:port、[v6]、[v6]:port —— 端口必须真正采用，
            // 否则非 443 的 HTTPS 站点（如 https://host:8443）会连错上游。
            string host;
            int upPort = 443;
            if (target.StartsWith("["))
            {
                int close = target.IndexOf(']');
                if (close <= 0) { Log.W("HTTPS", $"#{id} CONNECT 目标无法解析: {target}"); return; }
                host = target.Substring(1, close - 1);
                var rest = target.Substring(close + 1);
                if (rest.StartsWith(":") && int.TryParse(rest.Substring(1), out var p6)) upPort = p6;
            }
            else
            {
                int colon = target.LastIndexOf(':');
                if (colon > 0)
                {
                    host = target.Substring(0, colon);
                    if (int.TryParse(target.Substring(colon + 1), out var p4)) upPort = p4;
                }
                else host = target;
            }
            if (upPort <= 0 || upPort > 65535) upPort = 443;
            string hostPort = upPort == 443 ? host : host + ":" + upPort;
            Log.D("HTTPS", $"#{id} CONNECT 目标 {target} → 上游 {host}:{upPort}");

            await clientStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
            Log.D("HTTPS", $"#{id} 已回复 200 Connection Established");

            SslStream sslClient = null;
            UpstreamLink up = null;
            try
            {
                // 与客户端 TLS 握手（仿冒证书）
                sslClient = new SslStream(clientStream, false, (s, c, h, e) => true);
                var leaf = Certs.GetLeafCertificate(host);
                Log.D("HTTPS", $"#{id} 使用仿冒证书与客户端握手: CN={host}");
                await sslClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = leaf,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct);
                Log.I("HTTPS", $"#{id} 客户端 TLS 握手成功 ({sslClient.SslProtocol}): {host}");

                while (true)
                {
                    byte[] msg;
                    try { msg = await StreamIO.ReadHttpMessageAsync(sslClient, ct, Send100Continue(sslClient), IdleTimeout); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.D("HTTPS", $"#{id} 隧道读取结束: {ex.Message}"); break; }
                    if (msg == null) { Log.W("HTTPS", $"#{id} 隧道内请求头超限，关闭"); break; }
                    if (msg.Length == 0) { Log.D("HTTPS", $"#{id} 客户端关闭隧道"); break; }

                    var req = HttpRequest.Parse(msg);
                    if (string.IsNullOrEmpty(req.Method)) break;
                    req.Url = "https://" + hostPort + req.PathAndQuery;
                    req.Host = host;
                    var session = new HttpSession { Protocol = "HTTPS", Process = process ?? "", Request = req };
                    CapturedInc(ref _captured);
                    Log.D("HTTPS", $"#{id} → 解密请求: {req.Method} {req.Url}");

                    // 与上游建立 TLS（回调只能放 options，不能同时放构造器）；同隧道内复用
                    if (up == null || up.Dead)
                    {
                        up?.Dispose();
                        var conn = await ConnectAsync(host, upPort, Config.NicIp, ct);
                        if (conn == null)
                        {
                            Log.W("HTTPS", $"#{id} 无法连接上游 {host}:{upPort}");
                            await WriteTunnelError(sslClient, "502 Bad Gateway");
                            session.StatusCode = "502";
                            Store.Add(session); OnSession?.Invoke(session);
                            break;
                        }
                        var upSsl = new SslStream(conn.GetStream(), false);
                        await upSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback = (s, c, h, e) => true
                        }, ct);
                        up = new UpstreamLink(conn, upSsl);
                        Log.D("HTTPS", $"#{id} 上游 TLS 握手成功: {host}:{upPort}");
                    }

                    try { Scripts.RunRequestScript(null, req, null, new Dictionary<string, string>()); }
                    catch (Exception ex) { Log.W("脚本", $"#{id} 请求脚本执行失败: {ex.Message}"); }
                    ReplaceEngine.ApplyRules(Config.ReplaceRules, req, null);
                    if (Config.DecodeGzip) req.Headers["Accept-Encoding"] = "identity";

                    var raw = req.ToRawBytes();
                    session.RequestBytes = raw.Length;
                    var sw = Stopwatch.StartNew();
                    byte[] respBytes;
                    try { respBytes = await up.SendAsync(req, ct); }
                    catch (Exception ex)
                    {
                        up.Dead = true;
                        Log.W("HTTPS", $"#{id} 上游请求失败: {ex.Message}");
                        break;
                    }
                    sw.Stop();
                    session.DurationMs = (int)sw.ElapsedMilliseconds;

                    if (respBytes == null || respBytes.Length == 0)
                    {
                        Log.W("HTTPS", $"#{id} 上游无响应: {req.Url}");
                        up.Dead = true;
                        session.StatusCode = "超时";
                        Store.Add(session); OnSession?.Invoke(session);
                        break;
                    }

                    var resp = HttpResponse.Parse(respBytes);
                    if (Config.DecodeGzip && resp.TryDecompressBody()) Log.D("HTTPS", $"#{id} 响应体已解压 ({resp.Body.Length}B)");
                    ReplaceEngine.ApplyRules(Config.ReplaceRules, req, resp);
                    if (Config.ReplaceRules?.Count > 0) CapturedInc(ref _modified);
                    if (resp.StatusCode == 101) session.Protocol = "WSS";
                    session.SetResponse(resp);

                    var respRaw = resp.ToRawBytes();
                    await sslClient.WriteAsync(respRaw, ct);
                    CapturedInc(ref _sent);
                    Log.D("HTTPS", $"#{id} ← 已回写客户端 {respRaw.Length} 字节 (耗时 {session.DurationMs}ms)");

                    Store.Add(session);
                    Log.D("会话", $"#{id} 已存盘 {session.Id}.zb (HTTPS {req.Url} -> {session.StatusCode})");
                    ForwardSession(session);   // V1.0.8：旁路转发（不阻塞、不影响原流程）
                    FireWebHook(session);
                    OnSession?.Invoke(session);

                    if (resp.StatusCode == 101)
                    {
                        Log.I("WS", $"#{id} WSS 握手完成，进入透传模式: {req.Url}");
                        await PumpAsync(sslClient, up.Stream, ct, req.Host, upPort, session.Process);
                        break;
                    }
                    if (WantsClose(req, resp)) { up.Dead = true; break; }
                }
            }
            catch (Exception ex)
            {
                Log.W("HTTPS", $"#{id} 隧道失败 ({host}): {ex.Message}");
                try
                {
                    var errSession = new HttpSession { Protocol = "HTTPS", Process = process ?? "", StatusCode = "ERR" };
                    Store.Add(errSession); OnSession?.Invoke(errSession);
                }
                catch { }
            }
            finally
            {
                try { up?.Dispose(); } catch { }
                try { sslClient?.Dispose(); } catch { }
            }
        }

        private static async Task WriteTunnelError(SslStream sslClient, string status)
        {
            try
            {
                var err = Encoding.Latin1.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await sslClient.WriteAsync(err);
            }
            catch { }
        }

        private static bool WantsClose(HttpRequest req, HttpResponse resp)
        {
            string rc = req.Headers.TryGetValue("Connection", out var a) ? a : null;
            string sc = resp.Headers.TryGetValue("Connection", out var b) ? b : null;
            var any = rc ?? sc ?? "";
            if (any.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (string.Equals(req.HttpVersion, "HTTP/1.0", StringComparison.Ordinal) &&
                any.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) < 0) return true;
            return false;
        }

        /// <summary>WebSocket 透传：两个方向同时拷贝，任一方向结束即整体结束。</summary>
        private async Task PumpAsync(Stream clientSide, Stream upstreamSide, CancellationToken ct,
            string host = null, int port = 0, string process = null)
        {
            using var done = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, done.Token);
            // V1.0.8：WS 升级后就是纯 TCP 字节流，两个方向的每段字节都按方向喂给转发器（旁路，不影响透传）
            var t1 = PumpOne(clientSide, upstreamSide, linked.Token, "客户端→上游", ForwardDirection.Send, host, port, process);
            var t2 = PumpOne(upstreamSide, clientSide, linked.Token, "上游→客户端", ForwardDirection.Receive, host, port, process);
            await Task.WhenAny(t1, t2);
            done.Cancel();
            try { await Task.WhenAll(t1, t2); } catch { }
        }

        private async Task PumpOne(Stream from, Stream to, CancellationToken ct, string label,
            ForwardDirection dir, string host, int port, string process)
        {
            var buf = new byte[16384];
            long total = 0;
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    await to.WriteAsync(buf.AsMemory(0, n), ct);
                    total += n;
                    if (Forwarder != null && Forwarder.Enabled)
                    {
                        var chunk = new byte[n];
                        Buffer.BlockCopy(buf, 0, chunk, 0, n);
                        // WS 帧是 TCP 流上的真实字节，直接作为 TCP 封包转发
                        ForwardPacket("TCP",
                            dir == ForwardDirection.Send ? "127.0.0.1" : (host ?? ""),
                            dir == ForwardDirection.Send ? 0 : port,
                            dir == ForwardDirection.Send ? (host ?? "") : "127.0.0.1",
                            dir == ForwardDirection.Send ? port : 0,
                            process, chunk, dir);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            Log.D("WS", $"{label} 透传结束，共 {total} 字节");
        }

        private static void CapturedInc(ref int field) => Interlocked.Increment(ref field);

        private static async Task<TcpClient> ConnectAsync(string host, int port, string bindIp, CancellationToken ct)
        {
            try
            {
                var tcp = new TcpClient();
                if (!string.IsNullOrWhiteSpace(bindIp) && IPAddress.TryParse(bindIp, out var local))
                {
                    if (ShouldSkipBind(host, local))
                    {
                        Log.D("网络", $"目标 {host}:{port} 是本机/回环，跳过网卡绑定");
                    }
                    else
                    {
                        tcp.Client.Bind(new IPEndPoint(local, 0));
                        Log.D("网络", $"出站绑定网卡 {bindIp} → {host}:{port}");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(bindIp))
                {
                    Log.W("网络", $"网卡IP无效，忽略绑定: {bindIp}");
                }
                await tcp.ConnectAsync(host, port, ct);
                return tcp;
            }
            catch (Exception ex)
            {
                Log.W("网络", $"连接 {host}:{port} 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>回环/本机自身地址不参与网卡绑定，避免本地服务（127.0.0.1 等）被切断。</summary>
        private static bool ShouldSkipBind(string host, IPAddress bindIp)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IPAddress.TryParse(host, out var target)) return false;
            if (IPAddress.IsLoopback(target)) return true;
            return target.Equals(bindIp);
        }

        // ============ 上游连接封装 ============
        /// <summary>一条可复用的上游连接（HTTP 明文或 TLS）。发请求 → 按 HTTP 分帧读响应。</summary>
        private sealed class UpstreamLink : IDisposable
        {
            public TcpClient Tcp;
            public Stream Stream;
            public bool Dead;

            internal UpstreamLink(TcpClient tcp, Stream stream) { Tcp = tcp; Stream = stream; }

            public static string KeyOf(string url)
            {
                try
                {
                    var uri = new Uri(url);
                    bool isHttps = uri.Scheme == "https";
                    int port = uri.IsDefaultPort ? (isHttps ? 443 : 80) : uri.Port;
                    return (isHttps ? "ssl://" : "tcp://") + uri.Host + ":" + port;
                }
                catch { return "tcp://invalid"; }
            }

            /// <summary>URL → 上游端口（默认 443/80）。转发器需要真实端口作为 DstPort。</summary>
            public static int PortOf(string url)
            {
                try
                {
                    var uri = new Uri(url);
                    if (uri.IsDefaultPort) return uri.Scheme == "https" ? 443 : 80;
                    return uri.Port;
                }
                catch { return 0; }
            }

            public static async Task<UpstreamLink> ConnectAsync(string key, string bindIp, CancellationToken ct)
            {
                bool isHttps = key.StartsWith("ssl://", StringComparison.Ordinal);
                var authority = key.Substring(isHttps ? 6 : 6);
                int c = authority.LastIndexOf(':');
                if (c <= 0) return null;
                string host = authority.Substring(0, c);
                if (!int.TryParse(authority.Substring(c + 1), out int port)) return null;
                var tcp = await ProxyEngine.ConnectAsync(host, port, bindIp, ct);
                if (tcp == null) return null;
                try
                {
                    Stream s = tcp.GetStream();
                    if (isHttps)
                    {
                        var ssl = new SslStream(s, false);
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback = (a, b, d, e) => true
                        }, ct);
                        s = ssl;
                    }
                    return new UpstreamLink(tcp, s);
                }
                catch (Exception ex)
                {
                    Log.W("网络", $"上游握手失败 {host}:{port}: {ex.Message}");
                    try { tcp.Dispose(); } catch { }
                    return null;
                }
            }

            /// <summary>发送请求并读完整响应（含体）。失败抛异常并标记连接失效。</summary>
            public async Task<byte[]> SendAsync(HttpRequest req, CancellationToken ct)
            {
                var raw = req.ToRawBytes();
                await Stream.WriteAsync(raw, ct);
                bool noBody = string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                var respBytes = await StreamIO.ReadHttpMessageAsync(Stream, ct, null, IdleTimeout, closeDelimited: true, noBody: noBody);
                if (respBytes == null || respBytes.Length == 0) { Dead = true; return respBytes; }
                // 服务端明确要关连接时不再复用
                try
                {
                    var head = Encoding.Latin1.GetString(respBytes, 0, Math.Min(respBytes.Length, StreamIO.HeadCap));
                    if (head.Contains("Connection: close", StringComparison.OrdinalIgnoreCase) ||
                        head.Contains("connection:close", StringComparison.OrdinalIgnoreCase))
                        Dead = true;
                }
                catch { }
                return respBytes;
            }

            public void Dispose()
            {
                try { Stream?.Dispose(); } catch { }
                try { Tcp?.Dispose(); } catch { }
                Dead = true;
            }
        }

        // ============ WebHook ============
        private static readonly HttpClient HookClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        private void FireWebHook(HttpSession session)
        {
            if (!Config.WebHookEnabled || string.IsNullOrWhiteSpace(Config.WebHookUrl)) return;
            string url = Config.WebHookUrl;
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        time = session.Time,
                        method = session.Method,
                        url = session.Url,
                        status = session.StatusCode,
                        request = session.Request?.GetBodyText(),
                        response = session.Response?.GetBodyText()
                    });
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    var r = await HookClient.PostAsync(url, content);
                    Log.I("WebHook", $"已推送 {session.Method} {session.Url} -> {session.StatusCode} (HTTP {(int)r.StatusCode})");
                }
                catch (Exception ex) { Log.W("WebHook", $"推送失败 {session.Url}: {ex.Message}"); }
            });
        }
    }
}
