using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Zhuabao.Core
{
    /// <summary>
    /// 单机 CA + 动态叶子证书（仿 Fiddler 机制）。
    /// CA 根证书首次启动生成并落盘，浏览器/客户端信任后即可解密 HTTPS。
    /// </summary>
    public sealed class CertCache : IDisposable
    {
        private readonly string _dir;
        private readonly object _leafGate = new();
        private readonly Dictionary<string, Lazy<X509Certificate2>> _leafCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _leafOrder = new();   // LRU：队头最新，队尾最旧
        private const int LeafCap = 1000;                          // 叶子证书缓存上限，防止长期使用内存/密钥库无限增长
        private X509Certificate2 _ca;

        public CertCache(string dir)
        {
            _dir = dir;
            System.IO.Directory.CreateDirectory(dir);
        }

        public X509Certificate2 Ca
        {
            get
            {
                if (_ca == null) _ca = GetOrCreateCa();
                return _ca;
            }
        }

        /// <summary>CN → CA 表，用于按 TLS SNI 挑选已导入的第三方证书。</summary>
        public Dictionary<string, string> IssuerToCaLookup { get; } = new(StringComparer.OrdinalIgnoreCase);

        public X509Certificate2 GetOrCreateCa()
        {
            string path = System.IO.Path.Combine(_dir, "rootCA.pfx");
            const string pwd = "zhuabao";
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var existing = new X509Certificate2(path, pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                    if (existing.NotAfter > DateTime.Now.AddDays(-1)) { _ca = existing; return existing; }
                    existing.Dispose();
                }
                catch { /* 重建 */ }
            }

            var ca = MakeCa();
            System.IO.File.WriteAllBytes(path, ca.Export(X509ContentType.Pfx, pwd));
            _ca = ca;
            return ca;
        }

        private static X509Certificate2 MakeCa()
        {
            using RSA rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=抓包豆 CA, O=Zhuabao, C=CN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var now = DateTimeOffset.Now.AddDays(-1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", null), new Oid("1.3.6.1.5.5.7.3.2", null) }, false));

            return req.CreateSelfSigned(now, now.AddYears(10));
        }

        /// <summary>为目标主机签发一张仿冒叶子证书（LRU 缓存，上限 LeafCap）。</summary>
        public X509Certificate2 GetLeafCertificate(string host)
        {
            if (string.IsNullOrEmpty(host)) host = "localhost";
            host = host.Split(':')[0];

            Lazy<X509Certificate2> lazy;
            lock (_leafGate)
            {
                if (_leafCache.TryGetValue(host, out lazy))
                {
                    _leafOrder.Remove(host);
                    _leafOrder.AddFirst(host);
                    return lazy.Value;
                }
                lazy = new Lazy<X509Certificate2>(() => MakeLeaf(host));
                _leafCache[host] = lazy;
                _leafOrder.AddFirst(host);
                while (_leafOrder.Count > LeafCap)
                {
                    var oldest = _leafOrder.Last.Value;
                    _leafOrder.RemoveLast();
                    if (_leafCache.Remove(oldest, out var evicted) && evicted.IsValueCreated)
                    {
                        try { evicted.Value.Dispose(); } catch { }
                        Log.D("证书", $"叶子证书缓存已满，淘汰 {oldest}");
                    }
                }
            }
            return lazy.Value;
        }

        private X509Certificate2 MakeLeaf(string host)
        {
            // 先取 CA（首次访问会即时生成），再算叶子有效期：
            // 否则「先算 notBefore、后生成 CA」会让叶子 notBefore 早于颁发者，
            // 平台直接报 notBefore earlier than issuerCertificate.NotBefore。
            var ca = Ca;

            using RSA rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", null) }, false));
            var sanDns = host;
            req.CertificateExtensions.Add(new X509Extension(
                new AsnEncodedData(new Oid("2.5.29.17"), EncodeSan(sanDns)), false));

            // 直接用 CA 证书（含私钥）签发；不要把 CA 私钥导出 PEM 再导入——
            // CA 从 PFX 重新加载后私钥持久化在 CNG 仓库，明文导出 PKCS8 会报"不支持请求的操作"。
            var notBefore = DateTimeOffset.Now.AddDays(-1);
            var notAfter = DateTimeOffset.Now.AddMonths(12);
            var caStart = new DateTimeOffset(ca.NotBefore);
            if (notBefore < caStart) notBefore = caStart;   // 绝不能早于颁发者

            using var pub = req.Create(ca, notBefore, notAfter, SerialNumber());
            var withKey = pub.CopyWithPrivateKey(rsa);

            // Windows SslStream 服务端握手拒绝内存临时密钥（"platform does not
            // support ephemeral keys"），必须走 PFX 落盘持久化私钥后再加载。
            var tmp = Path.Combine(_dir, ".leaf-" + Guid.NewGuid().ToString("N") + ".pfx");
            try
            {
                File.WriteAllBytes(tmp, withKey.Export(X509ContentType.Pfx, "zhuabao"));
                var persisted = new X509Certificate2(tmp, "zhuabao",
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                withKey.Dispose();
                return persisted;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static byte[] SerialNumber()
        {
            var b = RandomNumberGenerator.GetBytes(16);
            b[0] = 0;
            return b;
        }

        /// <summary>
        /// 手工编码 SAN 扩展（GeneralNames）。主机名写 dNSName [2]；
        /// 目标是 IP 字面量时额外写 iPAddress [7]，否则 iOS/Safari 等严格客户端会拒绝。
        /// </summary>
        private static byte[] EncodeSan(string host)
        {
            var parts = new List<byte[]>
            {
                TaggedGeneralName(0x82, Encoding.ASCII.GetBytes(host))     // dNSName [2] IA5String
            };
            if (System.Net.IPAddress.TryParse(host, out var ip))
                parts.Add(TaggedGeneralName(0x87, ip.GetAddressBytes()));  // iPAddress [7] OCTET STRING

            int contentLen = 0;
            foreach (var p in parts) contentLen += p.Length;

            var buf = new List<byte> { 0x30 };                              // SEQUENCE
            AppendLength(buf, contentLen);
            foreach (var p in parts) buf.AddRange(p);
            return buf.ToArray();
        }

        private static byte[] TaggedGeneralName(byte tag, byte[] value)
        {
            var buf = new List<byte> { tag };
            AppendLength(buf, value.Length);
            buf.AddRange(value);
            return buf.ToArray();
        }

        /// <summary>DER 长度编码（含 0x81/0x82 长格式）。</summary>
        private static void AppendLength(List<byte> buf, int len)
        {
            if (len < 0x80) { buf.Add((byte)len); return; }
            if (len <= 0xFF) { buf.Add(0x81); buf.Add((byte)len); return; }
            buf.Add(0x82); buf.Add((byte)(len >> 8)); buf.Add((byte)(len & 0xFF));
        }

        public void Dispose()
        {
            _leafCache.Clear();
            _ca?.Dispose();
            _ca = null;
        }
    }

    public static class HexUtils
    {
        public static string ToHex(byte[] data, string sep = " ")
        {
            if (data == null || data.Length == 0) return string.Empty;
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static byte[] FromHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");
            var bytes = new byte[s.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
