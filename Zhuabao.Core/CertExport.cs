using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Zhuabao.Core
{
    /// <summary>
    /// 根证书导出：Windows(.cer DER) / 通用(.crt PEM，Mac·Android·iOS) / iOS·macOS 描述文件(.mobileconfig)。
    /// 只有根证书（公钥）会被导出，私钥始终留在本机 Config\certs\rootCA.pfx。
    /// </summary>
    public static class CertExport
    {
        /// <summary>Windows 双击即可安装的 DER 编码证书。</summary>
        public static byte[] Der(X509Certificate2 ca) => ca.Export(X509ContentType.Cert);

        /// <summary>PEM 文本（Base64 + BEGIN/END），Mac/Android/iOS 通用。</summary>
        public static string Pem(X509Certificate2 ca)
        {
            var b64 = Convert.ToBase64String(Der(ca));
            var sb = new StringBuilder();
            sb.AppendLine("-----BEGIN CERTIFICATE-----");
            for (int i = 0; i < b64.Length; i += 64)
                sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
            sb.AppendLine("-----END CERTIFICATE-----");
            return sb.ToString();
        }

        /// <summary>写 .crt（PEM，无 BOM —— 带 BOM 时部分安卓/iOS 客户端会解析失败）。</summary>
        public static void SavePem(X509Certificate2 ca, string path)
        {
            File.WriteAllText(path, Pem(ca), new UTF8Encoding(false));
        }

        /// <summary>
        /// Apple 描述文件（.mobileconfig）：内含根证书 payload。
        /// 装完必须去「设置 → 通用 → 关于本机 → 证书信任设置」打开完全信任，否则 HTTPS 仍报错。
        /// </summary>
        public static string MobileConfig(X509Certificate2 ca, string title = "抓包豆 HTTPS 抓包配置")
        {
            var data = Wrap(Convert.ToBase64String(Der(ca)), 68);
            string uuidCert = Guid.NewGuid().ToString().ToUpperInvariant();
            string uuidTop = Guid.NewGuid().ToString().ToUpperInvariant();

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
            sb.AppendLine("<plist version=\"1.0\">");
            sb.AppendLine("<dict>");
            sb.AppendLine("  <key>PayloadContent</key>");
            sb.AppendLine("  <array>");
            sb.AppendLine("    <dict>");
            sb.AppendLine("      <key>PayloadCertificateFileName</key>");
            sb.AppendLine("      <string>ZhuabaoRootCA.cer</string>");
            sb.AppendLine("      <key>PayloadContent</key>");
            sb.AppendLine("      <data>");
            sb.AppendLine(data);
            sb.AppendLine("      </data>");
            sb.AppendLine("      <key>PayloadDescription</key>");
            sb.AppendLine("      <string>抓包豆 CA 根证书，用于解密 HTTPS 流量</string>");
            sb.AppendLine("      <key>PayloadDisplayName</key>");
            sb.AppendLine("      <string>抓包豆 CA 根证书</string>");
            sb.AppendLine("      <key>PayloadIdentifier</key>");
            sb.AppendLine("      <string>com.zhuabao.ca." + uuidCert + "</string>");
            sb.AppendLine("      <key>PayloadType</key>");
            sb.AppendLine("      <string>com.apple.security.root</string>");
            sb.AppendLine("      <key>PayloadUUID</key>");
            sb.AppendLine("      <string>" + uuidCert + "</string>");
            sb.AppendLine("      <key>PayloadVersion</key>");
            sb.AppendLine("      <integer>1</integer>");
            sb.AppendLine("    </dict>");
            sb.AppendLine("  </array>");
            sb.AppendLine("  <key>PayloadDescription</key>");
            sb.AppendLine("  <string>安装后在 设置 → 通用 → 关于本机 → 证书信任设置 里打开完全信任</string>");
            sb.AppendLine("  <key>PayloadDisplayName</key>");
            sb.AppendLine("  <string>" + title + "</string>");
            sb.AppendLine("  <key>PayloadIdentifier</key>");
            sb.AppendLine("  <string>com.zhuabao.profile." + uuidTop + "</string>");
            sb.AppendLine("  <key>PayloadRemovalDisallowed</key>");
            sb.AppendLine("  <false/>");
            sb.AppendLine("  <key>PayloadType</key>");
            sb.AppendLine("  <string>Configuration</string>");
            sb.AppendLine("  <key>PayloadUUID</key>");
            sb.AppendLine("  <string>" + uuidTop + "</string>");
            sb.AppendLine("  <key>PayloadVersion</key>");
            sb.AppendLine("  <integer>1</integer>");
            sb.AppendLine("</dict>");
            sb.AppendLine("</plist>");
            return sb.ToString();
        }

        private static string Wrap(string s, int width)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i += width)
                sb.AppendLine(s.Substring(i, Math.Min(width, s.Length - i)));
            return sb.ToString().TrimEnd();
        }
    }
}
