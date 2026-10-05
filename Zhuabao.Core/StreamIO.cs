using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zhuabao.Core
{
    /// <summary>一条 HTTP 报文的分帧方式（由头部解析得出）。</summary>
    public struct HttpFraming
    {
        public bool HasBody;          // 头之后还有没有体
        public bool Chunked;          // Transfer-Encoding: chunked
        public bool CloseDelimited;   // 无长度头，靠连接关闭定界
        public long ContentLength;    // Chunked=false && !CloseDelimited 时有效

        public static readonly HttpFraming None = new() { HasBody = false };
    }

    public static class StreamIO
    {
        /// <summary>头部大小上限（超出返回 null，调用方应回 431）。</summary>
        public const int HeadCap = 64 * 1024;
        /// <summary>单个报文体在内存里保留的最大字节数（超出继续读完但不再存，保证分帧正确）。</summary>
        public const long BodyCap = 64L * 1024 * 1024;

        /// <summary>
        /// 从流里完整读取一条 HTTP 报文（头 + 体）。
        ///  - Content-Length / chunked 精确按帧读；
        ///  - closeDelimited=true（响应模式）时无长度头读到 EOF，且 1xx/204/304 按无体处理；
        ///  - noBody=true（HEAD 请求）只读头；
        ///  - beforeBody 在头解析完、体未读前回调（用于 Expect: 100-continue 应答）；
        ///  - idleTimeout 单次 Read 的空闲超时，超时返回已收到的部分；
        ///  - 头超过 HeadCap 返回 null；流未发任何字节返回空数组。
        /// </summary>
        public static async Task<byte[]> ReadHttpMessageAsync(Stream s, CancellationToken ct, Action<string> beforeBody = null,
            TimeSpan? idleTimeout = null, bool closeDelimited = false, bool noBody = false)
        {
            var ms = new MemoryStream();
            var scratch = new byte[64 * 1024];
            int headEnd = -1;
            var framing = HttpFraming.None;
            long bodyReceived = 0;
            bool beforeBodyDone = false;
            bool capWarned = false;

            while (true)
            {
                int n = await ReadWithIdleAsync(s, scratch, ct, idleTimeout);
                if (n <= 0) break;
                if (ms.Length < BodyCap)
                {
                    int take = (int)Math.Min(n, BodyCap - ms.Length);
                    ms.Write(scratch, 0, take);
                }
                else if (!capWarned)
                {
                    capWarned = true;
                    Log.W("HTTP", "报文体超过 " + BodyCap / 1024 / 1024 + "MB 上限，只保留前段（分帧继续读完）");
                }

                if (headEnd < 0)
                {
                    var buf = ms.GetBuffer();
                    headEnd = FindHeaderEnd(buf, (int)ms.Length);
                    if (headEnd >= 0)
                    {
                        framing = ParseFraming(buf, headEnd, closeDelimited);
                        if (!beforeBodyDone) { beforeBodyDone = true; try { beforeBody?.Invoke(Encoding.Latin1.GetString(buf, 0, headEnd)); } catch { } }
                        if (noBody || !framing.HasBody) return ms.ToArray();
                        // 响应模式：1xx/204/304 无体
                        if (closeDelimited && IsBodylessStatus(buf, headEnd)) return ms.ToArray();
                        bodyReceived = ms.Length - (headEnd + 4);
                    }
                    else if (ms.Length >= HeadCap)
                    {
                        Log.W("HTTP", "请求头超过 " + HeadCap + " 字节上限，拒绝（431）");
                        return null;
                    }
                }
                else
                {
                    bodyReceived += n;
                }

                if (headEnd >= 0)
                {
                    if (framing.Chunked)
                    {
                        // 直接看已收字节的最后 5 个是不是终止块 "0\r\n\r\n"
                        var buf = ms.GetBuffer();
                        int len = (int)ms.Length;
                        if (len >= 5 && buf[len - 5] == '0' && buf[len - 4] == '\r' && buf[len - 3] == '\n'
                                     && buf[len - 2] == '\r' && buf[len - 1] == '\n')
                            return ms.ToArray();
                    }
                    else if (!framing.CloseDelimited && bodyReceived >= framing.ContentLength)
                        return ms.ToArray();
                }
            }
            return ms.Length == 0 ? Array.Empty<byte>() : ms.ToArray();
        }

        /// <summary>带空闲超时的单次读取；超时返回 0（视为本轮结束）。被丢弃的原读任务要挂上观察，避免未观察任务异常。</summary>
        private static async Task<int> ReadWithIdleAsync(Stream s, byte[] buf, CancellationToken ct, TimeSpan? idleTimeout)
        {
            if (idleTimeout == null) return await s.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            var readTask = s.ReadAsync(buf.AsMemory(0, buf.Length), ct).AsTask();
            try { return await readTask.WaitAsync(idleTimeout.Value); }
            catch (TimeoutException)
            {
                _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return 0;
            }
        }

        /// <summary>解析头部（buf[0..headEnd)）得出分帧方式。</summary>
        public static HttpFraming ParseFraming(byte[] buf, int headEnd, bool closeDelimitedWhenNoLength)
        {
            var head = Encoding.Latin1.GetString(buf, 0, headEnd);
            bool chunked = false;
            long cl = -1;
            foreach (var raw in head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                var name = raw.Substring(0, c).Trim();
                var val = raw.Substring(c + 1).Trim();
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    if (val.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
                }
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && cl < 0)
                {
                    long.TryParse(val, out cl);
                }
            }
            if (chunked) return new HttpFraming { HasBody = true, Chunked = true };
            if (cl >= 0) return new HttpFraming { HasBody = true, ContentLength = cl };
            return new HttpFraming { HasBody = closeDelimitedWhenNoLength, CloseDelimited = closeDelimitedWhenNoLength };
        }

        private static bool IsBodylessStatus(byte[] buf, int headEnd)
        {
            // "HTTP/1.1 204 No Content" / 1xx / 304 —— 这些状态码没有响应体
            var head = Encoding.Latin1.GetString(buf, 0, Math.Min(headEnd, 64));
            if (!head.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return false;
            var sp = head.IndexOf(' ');
            if (sp < 0 || sp + 4 > head.Length) return false;
            if (!int.TryParse(head.Substring(sp + 1, 3), out var code)) return false;
            return code < 200 || code == 204 || code == 304;
        }

        /// <summary>定位 \r\n\r\n 头结束位置，返回头部字节数（不含结束 CRLF），未找到返回 -1。</summary>
        public static int FindHeaderEnd(byte[] buf, int count)
        {
            for (int i = 0; i + 3 < count; i++)
            {
                if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        /// <summary>解 chunked 编码体。不能用 StreamReader（内部缓冲会与 src.Read 的直接定位互相错位）。</summary>
        public static byte[] Dechunk(byte[] body)
        {
            using var src = new MemoryStream(body);
            using var dst = new MemoryStream();
            while (true)
            {
                var sizeLine = ReadLineBytes(src);
                if (sizeLine == null) break;
                var line = Encoding.Latin1.GetString(sizeLine).Trim();
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi);
                if (!int.TryParse(line, System.Globalization.NumberStyles.HexNumber, null, out int len)) break;
                if (len == 0) break; // 终止块（trailers 忽略）
                var buf = new byte[len];
                int off = 0;
                while (off < len)
                {
                    int n = src.Read(buf, off, len - off);
                    if (n <= 0) throw new IOException("chunked stream truncated");
                    off += n;
                }
                dst.Write(buf, 0, len);
                src.ReadByte(); // \r
                src.ReadByte(); // \n
            }
            return dst.ToArray();
        }

        /// <summary>从流里读一行（到 \n），去掉 \r；流已尽返回 null。</summary>
        private static byte[] ReadLineBytes(MemoryStream src)
        {
            using var buf = new MemoryStream();
            int b;
            bool any = false;
            while ((b = src.ReadByte()) >= 0)
            {
                any = true;
                if (b == '\n') break;
                if (b != '\r') buf.WriteByte((byte)b);
            }
            return any ? buf.ToArray() : null;
        }

        public static byte[] ReadExactly(Stream s, int count)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
            return buf;
        }

        public static byte[] ReadAtLeast(Stream s, int count)
        {
            var buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            if (total == count) return buf;
            var ret = new byte[total];
            Buffer.BlockCopy(buf, 0, ret, 0, total);
            return ret;
        }
    }

    internal class Latin1Encoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (int i = 0; i < charCount; i++) bytes[byteIndex + i] = (byte)chars[charIndex + i];
            return charCount;
        }
        public override int GetCharCount(byte[] bytes, int index, int count) => count;
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (int i = 0; i < byteCount; i++) chars[charIndex + i] = (char)bytes[byteIndex + i];
            return byteCount;
        }
        public override int GetMaxByteCount(int charCount) => charCount;
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }

    /// <summary>TLS ClientHello 识别与 SNI 提取。</summary>
    public static class TlsDetector
    {
        public static bool IsTlsClientHello(byte[] buf, int len = -1)
        {
            if (buf == null) return false;
            if (len < 0) len = buf.Length;
            return len >= 5 && buf[0] == 0x16 && buf[1] == 0x03 && buf[2] >= 0x00 && buf[2] <= 0x04;
        }

        public static bool IsTlsServerHello(byte[] buf, int len = -1)
        {
            if (buf == null) return false;
            if (len < 0) len = buf.Length;
            return len >= 5 && buf[0] == 0x16 && buf[1] == 0x03 && buf[2] >= 0x00 && buf[2] <= 0x04 && buf[5] == 0x02;
        }

        /// <summary>从 ClientHello 提取 SNI 主机名；失败返回 null。</summary>
        public static string ParseSni(byte[] buf, int len = -1)
        {
            if (!IsTlsClientHello(buf, len)) return null;
            try
            {
                int pos = 5;                          // 跳过 record header
                pos += 4;                             // handshake header (type + 3 len)
                pos += 2;                             // client_version
                pos += 32;                            // random
                int sidLen = buf[pos]; pos += 1 + sidLen;
                if (pos + 2 > len) return null;
                int csLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos, 2)); pos += 2 + csLen;
                if (pos >= len) return null;
                int compLen = buf[pos]; pos += 1 + compLen;
                if (pos + 2 > len) return null;
                int extLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos, 2)); pos += 2;
                int extEnd = Math.Min(pos + extLen, len);

                while (pos + 4 <= extEnd)
                {
                    int type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos, 2));
                    int eLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos + 2, 2));
                    pos += 4;
                    if (type == 0x0000) // server_name
                    {
                        if (pos + 5 > extEnd) return null;
                        // list_len(2) + name_type(1) + name_len(2)
                        int nameLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos + 3, 2));
                        if (pos + 5 + nameLen > len) return null;
                        return Encoding.ASCII.GetString(buf, pos + 5, nameLen);
                    }
                    pos += eLen;
                }
            }
            catch { }
            return null;
        }
    }
}
