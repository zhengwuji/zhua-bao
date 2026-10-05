using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Zhuabao.Core
{
    /// <summary>
    /// 一条 HTTP 报文（请求或响应）的通用容器，兼容字符串和二进制体。
    /// 重复头用 ", " 合并；Set-Cookie 例外（用 \n 连接，写出时拆回多行，逗号会破坏 cookie 语义）。
    /// </summary>
    public abstract class HttpMessage
    {
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();

        public string GetBodyText()
        {
            if (Body == null || Body.Length == 0) return string.Empty;
            try { return Encoding.UTF8.GetString(Body); }
            catch { return BinaryMarker + HexUtils.ToHex(Body.Take(Math.Min(Body.Length, 256)).ToArray()); }
        }

        public const string BinaryMarker = "\0[二进制 ";

        public void SetBody(string text)
        {
            Body = string.IsNullOrEmpty(text) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
        }

        public bool IsBinary()
        {
            if (Body == null || Body.Length == 0) return false;
            for (int i = 0; i < Math.Min(Body.Length, 32); i++)
                if (Body[i] == 0) return true;
            return false;
        }

        private static void AppendHeader(StringBuilder sb, string key, string value)
        {
            // Set-Cookie 的多个值按 \n 连接过，写出时拆回独立行
            if (value != null && value.IndexOf('\n') >= 0 && key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var seg in value.Split('\n'))
                    if (seg.Length > 0) sb.Append(key).Append(": ").Append(seg).Append("\r\n");
            }
            else sb.Append(key).Append(": ").Append(value ?? string.Empty).Append("\r\n");
        }

        /// <summary>把报文序列化为原始字节（头 + 体），可直接写向网络流。Content-Length 与体长度自动校正。</summary>
        public byte[] ToRawBytes()
        {
            // 101 Switching Protocols 不得带 Content-Length
            bool is101 = this is HttpResponse h && h.StatusCode == 101;
            if (!is101) Headers["Content-Length"] = (Body?.Length ?? 0).ToString();
            var sb = new StringBuilder();
            sb.Append(StartLine).Append("\r\n");
            foreach (var kv in Headers) AppendHeader(sb, kv.Key, kv.Value);
            sb.Append("\r\n");
            var head = Encoding.Latin1.GetBytes(sb.ToString());
            if (Body == null || Body.Length == 0) return head;
            var all = new byte[head.Length + Body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(Body, 0, all, head.Length, Body.Length);
            return all;
        }

        public abstract string StartLine { get; }

        public string HeaderText()
        {
            var sb = new StringBuilder();
            sb.Append(StartLine).Append("\r\n");
            foreach (var kv in Headers) AppendHeader(sb, kv.Key, kv.Value);
            return sb.ToString();
        }

        /// <summary>按 Content-Encoding 解压响应体（gzip/deflate/brotli）。成功则更新体、Content-Length 并移除头。Config.DecodeGzip 控制。</summary>
        public bool TryDecompressBody()
        {
            if (Body == null || Body.Length == 0) return false;
            if (!Headers.TryGetValue("Content-Encoding", out var ce)) return false;
            string v = (ce ?? "").Split(',')[0].Trim().ToLowerInvariant();
            try
            {
                byte[] outb = v switch
                {
                    "gzip" => Decompress(new GZipStream(new MemoryStream(Body), CompressionMode.Decompress)),
                    "br" => Decompress(new BrotliStream(new MemoryStream(Body), CompressionMode.Decompress)),
                    "deflate" => DecompressDeflate(Body),
                    _ => null
                };
                if (outb == null) return false;
                Body = outb;
                Headers.Remove("Content-Encoding");
                Headers["Content-Length"] = outb.Length.ToString();
                return true;
            }
            catch { return false; }
        }

        private static byte[] Decompress(Stream gz)
        {
            using (gz)
            {
                var ms = new MemoryStream();
                gz.CopyTo(ms, 81920);
                return ms.ToArray();
            }
        }

        private static byte[] DecompressDeflate(byte[] body)
        {
            try { return Decompress(new DeflateStream(new MemoryStream(body), CompressionMode.Decompress)); }
            catch
            {
                // 不少服务端发的是 zlib 封装（2 字节头）却标 deflate
                if (body.Length > 2)
                    return Decompress(new DeflateStream(new MemoryStream(body, 2, body.Length - 2), CompressionMode.Decompress));
                throw;
            }
        }


        /// <summary>从原始报文字节解析（头 + 体，不含 chunked 再解码）。</summary>
        protected void ParseRaw(byte[] raw)
        {
            int headEnd = StreamIO.FindHeaderEnd(raw, raw.Length);
            if (headEnd < 0) { headEnd = raw.Length; }
            var headText = Encoding.Latin1.GetString(raw, 0, headEnd);
            var lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line.Substring(0, c).Trim();
                string v = line.Substring(c + 1).Trim();
                if (Headers.TryGetValue(k, out var old))
                {
                    // Set-Cookie 不能用 ", " 合并（Expires 里的逗号会破坏语义），用 \n 分隔
                    Headers[k] = k.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? old + "\n" + v : old + ", " + v;
                }
                else Headers[k] = v;
            }
            ParseStartLine(lines[0]); // 头已就位，起始行才能用 Host 还原绝对 URL
            int bodyLen = raw.Length - (headEnd + 4);
            if (bodyLen > 0)
            {
                Body = new byte[bodyLen];
                Buffer.BlockCopy(raw, headEnd + 4, Body, 0, bodyLen);
            }
            DecodeChunkedIfNeeded();
        }

        private void DecodeChunkedIfNeeded()
        {
            if (Headers.TryGetValue("Transfer-Encoding", out var te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { Body = StreamIO.Dechunk(Body); Headers.Remove("Transfer-Encoding"); }
                catch { }
            }
        }

        protected abstract void ParseStartLine(string line);
        public abstract string DisplayUrl { get; }
    }

    public sealed class HttpRequest : HttpMessage
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = string.Empty;
        public string HttpVersion { get; set; } = "HTTP/1.1";

        public override string StartLine => $"{Method} {PathAndQuery} {HttpVersion}";
        public override string DisplayUrl => Url;

        public string Host
        {
            get => Headers.TryGetValue("Host", out var h) ? h : UriOrDefault(Url)?.Host ?? string.Empty;
            set => Headers["Host"] = value;
        }

        private static Uri UriOrDefault(string s)
        {
            try { return new Uri(s); } catch { return null; }
        }

        public string PathAndQuery
        {
            get
            {
                try { var u = new Uri(Url); return u.PathAndQuery; }
                catch { return Url; }
            }
        }

        public string Scheme
        {
            get
            {
                try { return new Uri(Url).Scheme; }
                catch { return "http"; }
            }
        }

        public static HttpRequest Parse(string text)
        {
            var r = new HttpRequest();
            r.ParseRaw(Encoding.UTF8.GetBytes(text));
            return r;
        }

        public static HttpRequest Parse(byte[] raw)
        {
            var r = new HttpRequest();
            r.ParseRaw(raw);
            return r;
        }

        internal void SetStartLine(string line)
        {
            var p = line.Split(' ');
            if (p.Length >= 3)
            {
                Method = p[0]; HttpVersion = p[2];
                var target = p[1];
                Url = target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target : "http://" + (Headers.TryGetValue("Host", out var h) ? h : "unknown") + target;
            }
        }

        protected override void ParseStartLine(string line) => SetStartLine(line);
    }

    public sealed class HttpResponse : HttpMessage
    {
        public string Status { get; set; } = "200 OK";
        public int StatusCode { get; set; } = 200;
        public string HttpVersion { get; set; } = "HTTP/1.1";

        public override string StartLine => $"{HttpVersion} {StatusCode} {StatusTail()}";
        public override string DisplayUrl => string.Empty;

        private string StatusTail() => StatusCode == Status.Length && string.IsNullOrEmpty(Status) ? string.Empty : ExtractTail();

        private string ExtractTail()
        {
            // Status 存的是完整 reason phrase；如果为空则返回常见短语
            if (string.IsNullOrEmpty(Status)) return ReasonOf(StatusCode);
            return Status;
        }

        public static string ReasonOf(int code) => code switch
        {
            200 => "OK", 204 => "No Content", 301 => "Moved Permanently", 302 => "Found",
            304 => "Not Modified", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 500 => "Internal Server Error",
            502 => "Bad Gateway", 503 => "Service Unavailable", _ => ""
        };

        public static HttpResponse Parse(byte[] raw)
        {
            var r = new HttpResponse();
            r.ParseRaw(raw);
            return r;
        }

        protected override void ParseStartLine(string line)
        {
            var p = line.Split(' ', 3);
            if (p.Length >= 2)
            {
                HttpVersion = p[0];
                if (int.TryParse(p[1], out var code)) StatusCode = code;
                Status = p.Length > 2 ? p[2] : ReasonOf(StatusCode);
            }
        }
    }
}
