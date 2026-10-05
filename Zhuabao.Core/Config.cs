using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Zhuabao.Core
{
    public sealed class AppConfig
    {
        public int Port { get; set; } = 8899;
        public bool CaptureEnabled { get; set; } = true;
        public bool InterceptEnabled { get; set; } = false;   // 断点
        public bool Paused { get; set; } = false;             // 挂机（暂停采集）
        public bool DecodeGzip { get; set; } = true;
        public string CaptureCountLimit { get; set; } = "无限";
        public string HttpSniMode { get; set; } = "禁用";     // 禁用 | 自动 | 强制
        /// <summary>启动时接管系统代理（旧版配置缺此字段时按开启处理）。</summary>
        [Newtonsoft.Json.JsonProperty("TakeOverSystemProxy", NullValueHandling = Newtonsoft.Json.NullValueHandling.Include, DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Include)]
        public bool? TakeOverSystemProxy { get; set; } = true;

        [Newtonsoft.Json.JsonIgnore]
        public bool TakeOverSystemProxyEnabled => TakeOverSystemProxy != false;
        public bool RedirectEnabled { get; set; } = false;
        public bool ExtractEnabled { get; set; } = false;
        public bool ReplaceEnabled { get; set; } = false;
        public bool ApiEnabled { get; set; } = false;
        public int ApiPort { get; set; } = 8900;
        public bool WebHookEnabled { get; set; } = false;
        public string WebHookUrl { get; set; } = string.Empty;
        public bool McpEnabled { get; set; } = false;
        public int McpPort { get; set; } = 8901;
        public bool DebugLog { get; set; } = true;            // DEBUG 级日志开关
        public List<RedirectRule> Redirects { get; set; } = new();
        public List<ReplaceRule> ReplaceRules { get; set; } = new();
        public List<ExtractRule> Extracts { get; set; } = new();
        public List<string> ProcessFilters { get; set; } = new();
        public List<string> RecentFiles { get; set; } = new();
        public string Theme { get; set; } = "默认";
        public string NicIp { get; set; } = string.Empty;   // 指定网卡 IP：只抓从该网卡出去的流量（空=不限）
        public bool AutoInstallCert { get; set; } = true;   // 启动时自动把根证书装进当前用户信任库
        /// <summary>局域网抓包：监听 0.0.0.0，手机 / Mac 等外部设备也能连上本代理（关闭时只监听 127.0.0.1）。</summary>
        public bool LanCapture { get; set; } = false;

        // ===== V1.0.8 新增 =====
        /// <summary>V1.0.8 新功能①：转发器（TCP/UDP 条件匹配 → 旁路 POST 到自己的 API）。</summary>
        public ForwarderConfig Forwarder { get; set; } = new();
        /// <summary>V1.0.8 新功能②：自定义大模型接口（支持 OpenAI 兼容 / Anthropic 兼容两种模式）。</summary>
        public LlmConfig Llm { get; set; } = new();
    }

    public sealed class RedirectRule
    {
        public string Pattern { get; set; } = string.Empty;     // 正则
        public string Replacement { get; set; } = string.Empty; // 替换
        public bool Enabled { get; set; } = true;
        public string Comment { get; set; } = string.Empty;
    }

    public sealed class ReplaceRule
    {
        public string Direction { get; set; } = "请求";   // 请求 | 响应
        public string Target { get; set; } = "请求头";    // 请求头|请求体|响应头|响应体|状态行|URL
        public string Match { get; set; } = string.Empty;
        public string ReplaceType { get; set; } = "替换值"; // 替换值|追加|删除|正则替换
        public string Value { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string Comment { get; set; } = string.Empty;

        // ===== V1.0.8 修改器「次数限制」=====
        /// <summary>是否启用次数限制（关 = 无限次）。</summary>
        public bool LimitEnabled { get; set; } = false;
        /// <summary>最多生效多少次（LimitEnabled=true 时有效）。</summary>
        public int MaxHits { get; set; } = 1;

        /// <summary>
        /// 本规则已生效次数。V1.0.8 修复点：这个计数必须跨配置重载存活，
        /// 否则每请求/每保存都会从 0 开始，限制形同虚设 —— 具体实现见 ReplaceEngine 的
        /// 静态计数表 RuleRegistry（按规则指纹索引），此属性只作为会话内的镜像便于界面显示。
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public int HitsUsed { get; set; }

        /// <summary>规则指纹：按语义字段生成，配置重载后仍是同一条规则。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string Signature => string.Join("\u0001", Direction, Target, Match, ReplaceType, Value, Comment);
    }

    public sealed class ExtractRule
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "响应-表单"; // 响应-表单|响应-JSON|响应-头|响应-体|请求-头|请求-体|Cookie
        public string Input { get; set; } = string.Empty; // key / 头名
        public string Regex { get; set; } = string.Empty; // 带捕获组
        public bool Enabled { get; set; } = true;
    }

    public static class ConfigService
    {
        private static readonly object Gate = new();
        public static string Dir { get; private set; } = DefaultDir();
        private const string FileName = "config.json";

        private static string DefaultDir()
        {
            return Path.Combine(AppContext.BaseDirectory, "Config");
        }

        public static void UseDir(string dir)
        {
            Dir = string.IsNullOrEmpty(dir) ? DefaultDir() : dir;
        }

        public static AppConfig Load(string dir = null)
        {
            lock (Gate)
            {
                if (dir != null) Dir = dir;
                Directory.CreateDirectory(Dir);
                var p = Path.Combine(Dir, FileName);
                if (!File.Exists(p))
                {
                    Log.I("配置", "未找到配置文件，使用默认配置: " + p);
                    return new AppConfig();
                }
                try
                {
                    var cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(p, Encoding.UTF8));
                    cfg = Normalize(cfg);
                    Log.I("配置", "已加载配置: " + p + " (端口=" + cfg?.Port + ", 规则数: 重定向" + cfg?.Redirects.Count + "/改包" + cfg?.ReplaceRules.Count + "/取值" + cfg?.Extracts.Count + ")");
                    return cfg ?? new AppConfig();
                }
                catch (Exception ex)
                {
                    Log.E("配置", "配置文件解析失败，回退默认配置: " + p, ex);
                    return new AppConfig();
                }
            }
        }

        public static void Save(AppConfig cfg, string dir = null)
        {
            lock (Gate)
            {
                if (dir != null) Dir = dir;
                Directory.CreateDirectory(Dir);
                var p = Path.Combine(Dir, FileName);
                File.WriteAllText(p, JsonConvert.SerializeObject(cfg, Formatting.Indented), Encoding.UTF8);
                Log.I("配置", "已保存配置: " + p);
            }
        }

        /// <summary>
        /// V1.0.8 修复：把老配置补齐新增字段。
        /// 旧 config.json 里没有 Forwarder / Llm 节点，反序列化后是 null（Json.NET 不调构造器默认值），
        /// 会让转发器与大模型面板直接 NullReferenceException；这里统一兜底。
        /// 同时把「断点拦截」等既有开关的 null 集合补上空集合。
        /// </summary>
        public static AppConfig Normalize(AppConfig cfg)
        {
            if (cfg == null) return new AppConfig();
            cfg.Redirects ??= new List<RedirectRule>();
            cfg.ReplaceRules ??= new List<ReplaceRule>();
            cfg.Extracts ??= new List<ExtractRule>();
            cfg.ProcessFilters ??= new List<string>();
            cfg.RecentFiles ??= new List<string>();
            if (cfg.Forwarder == null) cfg.Forwarder = ForwarderConfig.CreateDefault();
            cfg.Forwarder.Rules ??= new List<ForwardRule>();
            if (cfg.Forwarder.Workers <= 0) cfg.Forwarder.Workers = 3;
            if (cfg.Forwarder.QueueLimit <= 0) cfg.Forwarder.QueueLimit = 2000;
            if (cfg.Forwarder.TimeoutMs <= 0) cfg.Forwarder.TimeoutMs = 5000;
            foreach (var r in cfg.Forwarder.Rules)
            {
                if (r == null) continue;
                if (string.IsNullOrEmpty(r.RuleID)) r.RuleID = Guid.NewGuid().ToString();
                r.Match ??= new PacketMatch();
                r.Exclude ??= new PacketMatch();
                r.Match.Protocols ??= new List<string> { "TCP" };
                r.Exclude.Protocols ??= new List<string> { "TCP" };
            }
            if (cfg.Llm == null) cfg.Llm = new LlmConfig();
            if (string.IsNullOrWhiteSpace(cfg.Llm.Provider)) cfg.Llm.Provider = "OpenAI兼容";
            return cfg;
        }
    }
}

namespace Zhuabao.Core
{
    public enum LogLevel : byte { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Module { get; set; }
        public string Message { get; set; }

        internal static string Tag(LogLevel l) => l switch
        {
            LogLevel.Debug => "DBUG", LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN", _ => "ERRO"
        };

        public string Line => Time.ToString("HH:mm:ss.fff") + " [" + Tag(Level) + "][" + Module + "] " + (Message ?? "");
    }

    /// <summary>
    /// 全局日志：内存环形缓冲（供界面查看器）+ 常驻 StreamWriter 按天落盘 + 实时事件。
    /// 用法: Log.D/I/W/E("模块", "内容"[, exception])
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new();
        private static readonly Queue<LogEntry> Ring = new();
        private const int RingCap = 5000;
        private static StreamWriter _writer;
        private static string _dir;
        private static string _baseName = "zhuabao";
        private static DateTime _writerDay = DateTime.MinValue;
        private static string _currentPath;

        /// <summary>DEBUG 级是否输出（配置里可关）。</summary>
        public static bool DebugEnabled { get; set; } = true;

        /// <summary>界面订阅：新日志到达（在后台线程触发，接收方自行调度 UI）。</summary>
        public static event Action<LogEntry> OnEntry;

        public static string FilePath => _currentPath;

        /// <summary>启用文件落盘。传入首日文件路径（如 logs\zhuabao-20260928.log），之后按天自动滚动。</summary>
        public static void SetFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (Gate)
            {
                try
                {
                    _dir = Path.GetDirectoryName(path);
                    var name = Path.GetFileNameWithoutExtension(path);
                    // 从 zhuabao-yyyyMMdd 里取出基础名，用于后续按天滚动
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.+)-\d{8}$");
                    _baseName = m.Success ? m.Groups[1].Value : name;
                    Directory.CreateDirectory(_dir);
                    OpenWriterLocked(DateTime.Now);
                }
                catch (Exception ex) { _writer = null; System.Diagnostics.Debug.WriteLine("Log.SetFile: " + ex.Message); }
            }
            I("日志", "日志文件: " + _currentPath);
        }

        private static void OpenWriterLocked(DateTime day)
        {
            _writer?.Dispose();
            _currentPath = Path.Combine(_dir ?? "", _baseName + "-" + day.ToString("yyyyMMdd") + ".log");
            _writer = new StreamWriter(_currentPath, append: true, Encoding.UTF8) { AutoFlush = false };
            _writerDay = day.Date;
        }

        public static void D(string module, string msg)
        {
            if (DebugEnabled) Write(LogLevel.Debug, module, msg, null);
        }

        public static void I(string module, string msg) => Write(LogLevel.Info, module, msg, null);
        public static void W(string module, string msg) => Write(LogLevel.Warn, module, msg, null);
        public static void E(string module, string msg, Exception ex = null) => Write(LogLevel.Error, module, msg, ex);

        private static void Write(LogLevel level, string module, string msg, Exception ex)
        {
            var e = new LogEntry { Time = DateTime.Now, Level = level, Module = module ?? "", Message = msg ?? "" };
            var line = e.Line;
            var fileLine = ex == null ? line : line + Environment.NewLine + ex;
            lock (Gate)
            {
                Ring.Enqueue(e);
                while (Ring.Count > RingCap) Ring.Dequeue();
                if (_writer != null)
                {
                    try
                    {
                        if (e.Time.Date != _writerDay) OpenWriterLocked(e.Time); // 按天滚动
                        _writer.WriteLine(fileLine);
                        _writer.Flush();
                    }
                    catch { }
                }
            }
            try { OnEntry?.Invoke(e); } catch { }
        }

        public static LogEntry[] Snapshot() { lock (Gate) return Ring.ToArray(); }
        public static string[] SnapshotLines()
        {
            lock (Gate)
            {
                var arr = new string[Ring.Count];
                int i = 0;
                foreach (var e in Ring) arr[i++] = e.Line;
                return arr;
            }
        }

        public static void ClearScreenBuffer()
        {
            lock (Gate) Ring.Clear();
        }
    }
}
