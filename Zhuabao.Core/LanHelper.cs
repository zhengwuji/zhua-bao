using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Zhuabao.Core
{
    /// <summary>
    /// 局域网抓包辅助：本机可被外部访问的 IPv4、防火墙放行、各平台配置步骤。
    /// 手机 / Mac 抓包的三件事：① 代理监听 0.0.0.0（配置 LanCapture）② 防火墙放行端口 ③ 对端装根证书。
    /// </summary>
    public static class LanHelper
    {
        public const string FirewallRuleName = "Zhuabao Proxy";

        /// <summary>本机非回环、非 APIPA(169.254) 的 IPv4，内网地址优先。</summary>
        public static List<string> LocalIPv4()
        {
            var list = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;         // 自动专用地址，连不通
                        if (!list.Contains(ip)) list.Add(ip);
                    }
                }
            }
            catch (Exception ex) { Log.W("网络", "枚举本机局域网地址失败: " + ex.Message); }
            return list.OrderBy(ip => IsPrivate(ip) ? 0 : 1).ToList();
        }

        public static bool IsPrivate(string ip)
        {
            if (!IPAddress.TryParse(ip, out var a)) return false;
            var b = a.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        public static string PrimaryLanIp()
        {
            var l = LocalIPv4();
            return l.Count > 0 ? l[0] : "";
        }

        /// <summary>防火墙是否已有本程序的放行规则（查询不需要管理员）。</summary>
        public static bool FirewallRuleExists(int port)
        {
            try
            {
                var outp = Capture("netsh", "advfirewall firewall show rule name=\"" + FirewallRuleName + " " + port + "\"");
                return outp.Contains("LocalPort") || outp.Contains("本地端口");
            }
            catch (Exception ex)
            {
                Log.D("网络", "查询防火墙规则失败: " + ex.Message);
                return false;
            }
        }

        public static string AddFirewallArgs(int port) =>
            "advfirewall firewall add rule name=\"" + FirewallRuleName + " " + port +
            "\" dir=in action=allow protocol=TCP localport=" + port + " profile=private,domain";

        public static string RemoveFirewallArgs(int port) =>
            "advfirewall firewall delete rule name=\"" + FirewallRuleName + " " + port + "\"";

        /// <summary>提权放行入站端口（会弹 UAC）。用户取消或失败时 message 里给出手动命令。</summary>
        public static bool AddFirewallRule(int port, out string message)
        {
            bool ok = RunElevated("netsh", AddFirewallArgs(port), out message);
            if (ok) Log.I("网络", $"防火墙已放行入站 TCP {port}");
            else Log.W("网络", "防火墙放行未完成: " + message.Replace("\n", " "));
            return ok;
        }

        public static bool RemoveFirewallRule(int port, out string message)
        {
            bool ok = RunElevated("netsh", RemoveFirewallArgs(port), out message);
            if (ok) Log.I("网络", $"已删除防火墙规则 TCP {port}");
            else Log.W("网络", "删除防火墙规则未完成: " + message.Replace("\n", " "));
            return ok;
        }

        private static string Capture(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return outp;
        }

        private static bool RunElevated(string file, string args, out string message)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(psi);
                p.WaitForExit();
                if (p.ExitCode == 0) { message = "已完成。"; return true; }
                message = "命令返回码 " + p.ExitCode + "。可手动执行:\n" + file + " " + args;
                return false;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                message = "已取消管理员授权（防火墙未改动）。可手动以管理员身份执行:\n" + file + " " + args;
                return false;
            }
            catch (Exception ex)
            {
                message = "执行失败: " + ex.Message + "\n可手动以管理员身份执行:\n" + file + " " + args;
                return false;
            }
        }

        /// <summary>各平台配置步骤（给向导文本框用）。</summary>
        public static string StepsText(string lanIp, int port)
        {
            string host = string.IsNullOrEmpty(lanIp) ? "<本机局域网IP>" : lanIp;
            var sb = new StringBuilder();

            sb.AppendLine("本机局域网地址: " + host + "      代理端口: " + port);
            sb.AppendLine("前提: ① 勾选『允许局域网设备连接』② 防火墙放行端口 ③ 对端与本机在同一个局域网");
            sb.AppendLine();
            sb.AppendLine("──────────── Windows 电脑 ────────────");
            sb.AppendLine("· 抓本机: 点 ▶启动(F5) 自动接管系统代理，无需设置；根证书用");
            sb.AppendLine("  菜单 证书(C) → 查看/安装根证书状态 一键装好。");
            sb.AppendLine("· 抓别的 Windows: 目标机 设置 → 网络和 Internet → 代理 →");
            sb.AppendLine("  手动设置代理 → 地址 " + host + "  端口 " + port + " → 保存；");
            sb.AppendLine("  再把导出的 ZhuabaoRootCA.crt 双击 → 安装证书 → 本地计算机 →");
            sb.AppendLine("  『将所有的证书都放入下列存储』→ 受信任的根证书颁发机构。");
            sb.AppendLine();
            sb.AppendLine("──────────── macOS ────────────");
            sb.AppendLine("1. 系统设置 → 网络 → 当前连接(Wi-Fi/以太网) → 详细信息 → 代理 →");
            sb.AppendLine("   勾选『网页代理(HTTP)』和『安全网页代理(HTTPS)』，两者都填");
            sb.AppendLine("   服务器 " + host + "  端口 " + port + " → 好 → 应用。");
            sb.AppendLine("   (命令行等价: export https_proxy=http://" + host + ":" + port + ")");
            sb.AppendLine("2. 证书: 把 ZhuabaoRootCA.crt 拷到 Mac，双击 → 钥匙串访问 →");
            sb.AppendLine("   系统 → 找到『抓包豆 CA』→ 双击 → 信任 → 使用此证书时: 始终信任。");
            sb.AppendLine("3. Safari/Chrome 走系统信任库；Firefox 有独立信任库，需要");
            sb.AppendLine("   设置 → 隐私与安全 → 证书 → 查看证书 → 导入 → 勾选信任由此 CA 标识的网站。");
            sb.AppendLine();
            sb.AppendLine("──────────── iPhone / iPad ────────────");
            sb.AppendLine("1. 代理: 设置 → 无线局域网 → 点当前 Wi-Fi 的 ⓘ → 配置代理 → 手动 →");
            sb.AppendLine("   服务器 " + host + "  端口 " + port + " → 存储。");
            sb.AppendLine("2. 装证书: 用 Safari 打开证书地址或把 .mobileconfig 传到手机点击安装 →");
            sb.AppendLine("   设置 → 通用 → VPN与设备管理 → 安装该描述文件/描述文件 → 输入锁屏密码 → 安装。");
            sb.AppendLine("3. ★关键: 设置 → 通用 → 关于本机 → 证书信任设置 →");
            sb.AppendLine("   找到『抓包豆 CA 根证书』→ 打开完全信任。不开这一步 HTTPS 必定报错。");
            sb.AppendLine();
            sb.AppendLine("──────────── Android ────────────");
            sb.AppendLine("1. 代理: 设置 → WLAN → 长按当前网络 → 修改网络 → 高级选项 → 代理 →");
            sb.AppendLine("   手动 → 主机名 " + host + "  端口 " + port + " → 保存。");
            sb.AppendLine("2. 装证书: 把 ZhuabaoRootCA.crt 传到手机 → 设置 → 安全 →");
            sb.AppendLine("   加密与凭据 → 安装证书 → CA 证书 → 选择文件 → 确认安装。");
            sb.AppendLine("3. ★注意: Android 7 以上，应用默认不信任用户安装的证书，");
            sb.AppendLine("   只有浏览器(Safari/Chrome)这类走系统信任库的才生效；");
            sb.AppendLine("   自带信任策略或做了证书固定的 App 抓不到，需要在 App 层放行。");
            sb.AppendLine();
            sb.AppendLine("──────────── 通用注意事项 ────────────");
            sb.AppendLine("· 对端必须能 ping 通 " + host + "；连不上先查防火墙规则是否已放行 " + port + "。");
            sb.AppendLine("· 本代理没有账号密码，同网段任何人都能用，抓完请切回『仅本机』。");
            sb.AppendLine("· 本程序只对 HTTP/1.1 代理生效；HTTP/3(QUIC) 流量不走代理，抓不到属正常。");
            sb.AppendLine("· 根证书是唯一的：换电脑/删过 Config\\certs 后指纹会变，对端要重新装。");
            return sb.ToString();
        }
    }
}
