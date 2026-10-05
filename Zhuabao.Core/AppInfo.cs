using System;
using System.Reflection;

namespace Zhuabao.Core
{
    /// <summary>
    /// 全局版本标识。
    ///
    /// 版本号规则（自 V1.0.8 起改为递增式简写）：
    ///   软件标题、关于窗口、启动日志、API/MCP 的 version 字段统一取这里的 <see cref="Version"/>，
    ///   每次发版只改这一处字符串即可：v1 → v2 → v3 → …
    ///
    /// 这一版对应「参照 Fatbeans Creater V1.0.8 更新内容」的落地版本，对外显示为 v1。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>对外显示的版本号（递增式：v1、v2、v3 …）。改版本只改这一个常量。</summary>
        public const string Version = "v1";

        /// <summary>程序名（不含版本）。</summary>
        public const string Name = "抓包豆 Creator";

        /// <summary>标题里的副标题/别名。</summary>
        public const string Alias = "配豆创器";

        /// <summary>标题栏文本：抓包豆 Creator (配豆创器) v1 [全能模式]</summary>
        public const string WindowTitle = Name + " (" + Alias + ") " + Version + " [全能模式]";

        /// <summary>关于窗口文本。</summary>
        public static string AboutText =>
            Name + " " + Version + "\n.NET 8 WinForms 复刻版\n\n" +
            "本次更新对应 Fatbeans Creater V1.0.8：\n" +
            "  · 新增转发器（条件匹配 TCP/UDP 旁路转发到自定义 API）\n" +
            "  · 自定义大模型接口新增 Anthropic 兼容模式\n" +
            "  · 修改响应头中可直接修改状态码（:status）\n" +
            "  · 修复右键重发会覆盖原封包会话\n" +
            "  · 修复修改器规则次数限制失效\n" +
            "  · 解码器面板新增专用解码器 GitHub 示例代码链接";

        /// <summary>程序集版本（供 API/MCP 的 version 字段做机器可读标识）。</summary>
        public static string AssemblyVersionText
        {
            get
            {
                try
                {
                    var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                    var v = asm?.GetName()?.Version;
                    if (v == null) return Version;
                    return Version + " (" + v.ToString(3) + ")";
                }
                catch { return Version; }
            }
        }
    }
}
