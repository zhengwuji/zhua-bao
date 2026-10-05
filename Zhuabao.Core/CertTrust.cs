using System;
using System.Security.Cryptography.X509Certificates;

namespace Zhuabao.Core
{
    /// <summary>
    /// 根证书信任管理：把本机 CA 装进「当前用户 → 受信任的根证书颁发机构」。
    /// CurrentUser Root 无需管理员权限，也不弹确认框，装一次即可解密 HTTPS。
    /// </summary>
    public static class CertTrust
    {
        /// <summary>该证书是否已在本机信任库（CurrentUser 或 LocalMachine 的 Root）。</summary>
        public static bool IsTrusted(X509Certificate2 ca)
        {
            if (ca == null) return false;
            return Exists(ca.Thumbprint, StoreLocation.CurrentUser) || Exists(ca.Thumbprint, StoreLocation.LocalMachine);
        }

        private static bool Exists(string thumbprint, StoreLocation location)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates)
                {
                    if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex) { Log.D("证书", $"读取 {location} 信任库失败: {ex.Message}"); }
            return false;
        }

        /// <summary>确保 CA 已受信任；未安装时自动装进 CurrentUser Root。</summary>
        public static bool EnsureTrusted(X509Certificate2 ca, bool autoInstall = true)
        {
            if (ca == null) return false;
            if (IsTrusted(ca))
            {
                Log.I("证书", $"根证书已在本机信任库 (指纹 {ca.Thumbprint})");
                return true;
            }
            if (!autoInstall)
            {
                Log.W("证书", $"根证书未安装 (指纹 {ca.Thumbprint})；抓 HTTPS 会报证书错误，请执行 菜单 证书(C) → 安装到系统");
                return false;
            }
            return InstallToCurrentUserRoot(ca);
        }

        /// <summary>安装到当前用户根信任库（失败则回退尝试机器级）。</summary>
        public static bool InstallToCurrentUserRoot(X509Certificate2 ca)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Add(ca);
                store.Close();
                Log.I("证书", $"根证书已安装到 当前用户\\受信任的根证书颁发机构 (指纹 {ca.Thumbprint})");
                return true;
            }
            catch (Exception ex)
            {
                Log.W("证书", "安装根证书到当前用户失败: " + ex.Message);
            }
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(ca);
                store.Close();
                Log.I("证书", $"根证书已安装到 本地计算机\\受信任的根证书颁发机构 (指纹 {ca.Thumbprint})");
                return true;
            }
            catch (Exception ex)
            {
                Log.E("证书", "安装根证书失败（可能需要管理员权限）: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>从当前用户信任库移除本程序签发的 CA（卸载用）。</summary>
        public static bool Uninstall(X509Certificate2 ca)
        {
            if (ca == null) return false;
            try
            {
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Remove(ca);
                store.Close();
                Log.I("证书", "已从当前用户信任库移除根证书 " + ca.Thumbprint);
                return true;
            }
            catch (Exception ex)
            {
                Log.W("证书", "移除根证书失败: " + ex.Message);
                return false;
            }
        }
    }
}
