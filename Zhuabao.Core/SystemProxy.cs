using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zhuabao.Core
{
    /// <summary>
    /// 接管 / 恢复 Windows 用户级系统代理（WinINET）。
    /// Edge / Chrome / IE 及大多数走系统代理的程序都会生效；
    /// 停止或退出程序时自动恢复接管前的原设置。
    /// </summary>
    public static class SystemProxy
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        // 内网直连；注意：不放 127.* 和 <local>，否则本地/回环站点抓不到包
        private const string DefaultBypass =
            "10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;" +
            "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*";

        private static bool _saved;
        private static int _prevEnable;
        private static string _prevServer;
        private static string _prevOverride;

        private const string CrashMarkFile = "sysproxy-crash.mark";

        /// <summary>
        /// 崩溃自愈检查：上次运行若未正常恢复（强杀/崩溃残留 ProxyEnable=1），
        /// 启动时自动还原为接管前的原始设置。须在程序启动最早处调用。
        /// </summary>
        public static void CrashRecoveryCheck(string baseDir)
        {
            try
            {
                var mark = Path.Combine(baseDir, CrashMarkFile);
                if (!File.Exists(mark)) return;
                var saved = File.ReadAllText(mark).Trim().Split('|');
                int enable = saved.Length > 0 && int.TryParse(saved[0], out var e) ? e : 0;
                string server = saved.Length > 1 && saved[1].Length > 0 ? saved[1] : null;
                string overrideList = saved.Length > 2 && saved[2].Length > 0 ? saved[2] : null;

                var cur = ReadCurrent();
                bool residue = cur.Enabled && cur.Server != null &&
                               cur.Server.StartsWith("127.0.0.1:") && cur.Server != server;
                using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    if (k == null) return;
                    k.SetValue("ProxyEnable", enable, RegistryValueKind.DWord);
                    if (server == null) k.DeleteValue("ProxyServer", false);
                    else k.SetValue("ProxyServer", server, RegistryValueKind.String);
                    if (overrideList == null) k.DeleteValue("ProxyOverride", false);
                    else k.SetValue("ProxyOverride", overrideList, RegistryValueKind.String);
                }
                RefreshWininet();
                File.Delete(mark);
                Log.I("系统代理", "检测到上次异常退出残留的系统代理，已自动恢复原设置 (Enable=" + enable + ")");
            }
            catch (Exception ex)
            {
                Log.W("系统代理", "崩溃自愈检查失败: " + ex.Message);
            }
        }

        private static void WriteCrashMark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                var cur = ReadCurrent();
                var prev = _saved
                    ? _prevEnable + "|" + (_prevServer ?? "") + "|" + (_prevOverride ?? "")
                    : cur.Enabled + "|" + (cur.Server ?? "") + "|" + "";
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, CrashMarkFile), prev);
            }
            catch (Exception ex) { Log.W("系统代理", "写入自愈标记失败: " + ex.Message); }
        }

        public static bool TakenOver { get; private set; }

        /// <summary>把系统代理指向 proxyServer（如 127.0.0.1:8899）。</summary>
        public static void TakeOver(string proxyServer, string bypass = DefaultBypass)
        {
            WriteCrashMark();
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                if (k == null) { Log.W("系统代理", "无法打开注册表项 Internet Settings"); return; }

                if (!_saved) // 只记录第一次接管前的原始值
                {
                    _prevEnable = k.GetValue("ProxyEnable") is int i ? i : 0;
                    _prevServer = k.GetValue("ProxyServer") as string;
                    _prevOverride = k.GetValue("ProxyOverride") as string;
                    _saved = true;
                    Log.D("系统代理", $"记录原设置: Enable={_prevEnable}, Server={_prevServer ?? "(无)"}");
                }

                k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                k.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
            }
            RefreshWininet();
            TakenOver = true;
            Log.I("系统代理", "已接管系统代理 → " + proxyServer + "（浏览器免配置，停止时自动恢复）");
        }

        /// <summary>恢复接管前的系统代理设置。</summary>
        public static void Restore()
        {
            if (!_saved) { Disable(); return; }
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
            {
                if (k == null) return;
                k.SetValue("ProxyEnable", _prevEnable, RegistryValueKind.DWord);
                if (_prevServer == null) k.DeleteValue("ProxyServer", false);
                else k.SetValue("ProxyServer", _prevServer, RegistryValueKind.String);
                if (_prevOverride == null) k.DeleteValue("ProxyOverride", false);
                else k.SetValue("ProxyOverride", _prevOverride, RegistryValueKind.String);
            }
            RefreshWininet();
            TakenOver = false;
            _saved = false;
            try { File.Delete(Path.Combine(AppContext.BaseDirectory, CrashMarkFile)); } catch { }
            Log.I("系统代理", "已恢复系统代理原设置 (Enable=" + _prevEnable + ")");
        }

        /// <summary>直接关闭系统代理（异常残留时的自救）。</summary>
        public static void Disable()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                k?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            RefreshWininet();
            TakenOver = false;
            Log.I("系统代理", "已关闭系统代理 (ProxyEnable=0)");
        }

        /// <summary>读取当前是否指向本代理端口（诊断用）。</summary>
        public static (bool Enabled, string Server) ReadCurrent()
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k == null) return (false, null);
            return (k.GetValue("ProxyEnable") is int i && i == 1, k.GetValue("ProxyServer") as string);
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int InternetOptionSettingsChanged = 39;
        private const int InternetOptionRefresh = 37;

        private static void RefreshWininet()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
            }
            catch (Exception ex) { Log.W("系统代理", "通知系统刷新失败: " + ex.Message); }
        }
    }
}
