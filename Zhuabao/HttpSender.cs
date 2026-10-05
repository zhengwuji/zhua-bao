using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>独立 HTTP 发送工具（给发送器/回复器用）。</summary>
    public static class HttpSender
    {
        public static async Task<HttpResponse> SendAsync(HttpRequest req, int timeoutMs = 15000)
        {
            try
            {
                var uri = new Uri(req.Url);
                bool https = uri.Scheme == "https";
                int port = uri.IsDefaultPort ? (https ? 443 : 80) : uri.Port;

                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(uri.Host, port);
                await connectTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

                Stream s = tcp.GetStream();
                if (https)
                {
                    var ssl = new SslStream(s, false, (a, b, c, d) => true);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (a, b, c, d) => true
                    });
                    s = ssl;
                }

                if (!req.Headers.ContainsKey("Host")) req.Host = uri.Authority;
                req.Headers["Connection"] = "close";
                await s.WriteAsync(req.ToRawBytes());
                await s.FlushAsync();

                // 读完整响应（简化：读到 EOF）
                var ms = new MemoryStream();
                var buf = new byte[65536];
                using var cts = new CancellationTokenSource(timeoutMs);
                while (true)
                {
                    int n;
                    try { n = await s.ReadAsync(buf, cts.Token); }
                    catch { break; }
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    if (!https && tcp.Available == 0 && ms.Length > 0)
                    {
                        // 可能读完了，稍等避免截断
                        await Task.Delay(30);
                        if (tcp.Available == 0) break;
                    }
                }
                return HttpResponse.Parse(ms.ToArray());
            }
            catch (Exception ex)
            {
                Log.E("发送器", "发送失败: " + ex.Message);
                return null;
            }
        }
    }
}
