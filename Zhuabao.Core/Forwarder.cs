using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zhuabao.Core
{
    /// <summary>
    /// V1.0.8 新增：转发器（旁路 fire-and-forget）。
   ///
    /// 参照 Fatbeans Creater V1.0.8 转发器语义实现：
    ///  · 命中规则的 TCP/UDP 封包以 HTTP POST 旁路转发到用户配置的 API 地址；
    ///  · 旁路 = 命中即丢入后台队列异步发送，**不等待响应、不阻塞、不影响原有通讯**，
    ///    也不影响拦截器 / 修改器 / WebHook 的正常工作；
    ///  · 只读不写：不修改、不拦截封包，只把封包数据复制一份发出去；
    ///  · 抓包线程只做「条件匹配 + 字节复制」，JSON 序列化与发送都在后台 Worker 完成；
    ///  · 队列满时丢弃最旧的封包（丢弃数在界面底部统计可见）。
    ///
    /// 转发 JSON 字段与 Fatbeans 对齐：
    ///   Protocol / SrcIP / SrcPort / DstIP / DstPort / PID / ProcessName /
    ///   DataLength / DataBase64 / HexData / Timestamp / RuleID / RuleName
    ///   （额外附带 Direction 字段区分「发送 / 接收」，接收端可忽略）
    /// </summary>
    public enum ForwardDirection { Send = 0, Receive = 1 }

    /// <summary>一个被抓到的 TCP/UDP 封包（转发器的最小单位）。</summary>
    public sealed class PacketEvent
    {
        public string Protocol { get; set; } = "TCP";       // TCP | UDP
        public string SrcIP { get; set; } = string.Empty;
        public int SrcPort { get; set; }
        public string DstIP { get; set; } = string.Empty;
        public int DstPort { get; set; }
        public int Pid { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public ForwardDirection Direction { get; set; } = ForwardDirection.Send;
        public DateTime Time { get; set; } = DateTime.Now;

        public int DataLength => Data?.Length ?? 0;

        /// <summary>13 位毫秒时间戳。</summary>
        public long TimestampMs => new DateTimeOffset(Time).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 一组匹配条件（匹配条件与排除条件共用同一模型，排除条件优先级更高）。
    /// 未勾选任何条件的匹配组 = 放行全部；未勾选任何条件的排除组 = 不排除任何封包。
    /// </summary>
    public sealed class PacketMatch
    {
        public bool UseProtocol { get; set; }
        public List<string> Protocols { get; set; } = new() { "TCP" };

        public bool UseSrcIP { get; set; }
        public string SrcIP { get; set; } = string.Empty;

        public bool UseDstIP { get; set; }
        public string DstIP { get; set; } = string.Empty;

        public bool UseSrcPort { get; set; }
        public int SrcPortFrom { get; set; }
        public int SrcPortTo { get; set; }

        public bool UseDstPort { get; set; }
        public int DstPortFrom { get; set; }
        public int DstPortTo { get; set; }

        public bool UseLength { get; set; }
        public int LengthMin { get; set; }
        public int LengthMax { get; set; }

        /// <summary>内容搜索。IsHex=true 时 Content 按 HEX 解析（如 "68 65 6C"），否则按文本（Latin1/UTF8 双路尝试）。</summary>
        public bool UseContent { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool ContentIsHex { get; set; }

        /// <summary>任意条件都不勾选 = 不构成约束（匹配组=全放行，排除组=不排除）。</summary>
        public bool IsEmpty =>
            !UseProtocol && !UseSrcIP && !UseDstIP && !UseSrcPort && !UseDstPort && !UseLength && !UseContent;

        public PacketMatch Clone()
        {
            return new PacketMatch
            {
                UseProtocol = UseProtocol, Protocols = new List<string>(Protocols ?? new List<string>()),
                UseSrcIP = UseSrcIP, SrcIP = SrcIP,
                UseDstIP = UseDstIP, DstIP = DstIP,
                UseSrcPort = UseSrcPort, SrcPortFrom = SrcPortFrom, SrcPortTo = SrcPortTo,
                UseDstPort = UseDstPort, DstPortFrom = DstPortFrom, DstPortTo = DstPortTo,
                UseLength = UseLength, LengthMin = LengthMin, LengthMax = LengthMax,
                UseContent = UseContent, Content = Content, ContentIsHex = ContentIsHex
            };
        }

        public bool IsSatisfiedBy(PacketEvent p)
        {
            if (p == null) return false;
            if (IsEmpty) return true;

            if (UseProtocol)
            {
                var list = Protocols ?? new List<string>();
                if (list.Count > 0 && !list.Any(x => string.Equals(x, p.Protocol, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            if (UseSrcIP && !IpMatches(SrcIP, p.SrcIP)) return false;
            if (UseDstIP && !IpMatches(DstIP, p.DstIP)) return false;
            if (UseSrcPort && !PortInRange(p.SrcPort, SrcPortFrom, SrcPortTo)) return false;
            if (UseDstPort && !PortInRange(p.DstPort, DstPortFrom, DstPortTo)) return false;
            if (UseLength)
            {
                int len = p.DataLength;
                if (LengthMin > 0 && len < LengthMin) return false;
                if (LengthMax > 0 && len > LengthMax) return false;
            }
            if (UseContent && !ContentMatches(Content, ContentIsHex, p.Data)) return false;
            return true;
        }

        /// <summary>端口范围：From 与 To 都为 0 视为不限；只填一个视为精确匹配。</summary>
        private static bool PortInRange(int port, int from, int to)
        {
            if (from <= 0 && to <= 0) return true;
            int lo = from <= 0 ? to : from;
            int hi = to <= 0 ? from : to;
            if (lo > hi) (lo, hi) = (hi, lo);
            return port >= lo && port <= hi;
        }

        /// <summary>IP 条件：支持逗号分隔多值、"*" 结尾的网段前缀（如 192.168.*）、精确匹配。</summary>
        public static bool IpMatches(string pattern, string ip)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            if (string.IsNullOrEmpty(ip)) return false;
            foreach (var raw in pattern.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.EndsWith("*", StringComparison.Ordinal))
                {
                    var prefix = p.Substring(0, p.Length - 1);
                    if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (string.Equals(p, ip, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>内容搜索：HEX 模式按字节序列找；文本模式按 UTF8 与 Latin1 双路尝试（大小写不敏感）。</summary>
        public static bool ContentMatches(string content, bool isHex, byte[] data)
        {
            if (string.IsNullOrEmpty(content) || data == null || data.Length == 0) return true;
            // 容错：即使用户把 "HEX:" 前缀一起写进了 Content（界面本应剥掉），这里也认
            if (content.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase))
            {
                isHex = true;
                content = content.Substring(4);
            }
            if (isHex)
            {
                byte[] needle;
                try { needle = HexUtils.FromHex(content); } catch { needle = Array.Empty<byte>(); }
                return Contains(data, needle);
            }
            byte[] u8 = Encoding.UTF8.GetBytes(content);
            if (Contains(data, u8)) return true;
            // Latin1 / 大小写不敏感 兜底
            var latinHay = Encoding.Latin1.GetString(data);
            return latinHay.IndexOf(content, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0) return false;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                if (hit) return true;
            }
            return false;
        }
    }

    /// <summary>一条转发规则。</summary>
    public sealed class ForwardRule
    {
        public string RuleID { get; set; } = Guid.NewGuid().ToString();
        public string RuleName { get; set; } = "新规则";
        public bool Enabled { get; set; } = true;
        public PacketMatch Match { get; set; } = new();
        public PacketMatch Exclude { get; set; } = new();
        public string Comment { get; set; } = string.Empty;

        // ===== 运行期统计（不落盘）=====
        [Newtonsoft.Json.JsonIgnore] public long HitCount { get; set; }
        [Newtonsoft.Json.JsonIgnore] public long SentCount { get; set; }

        /// <summary>命中判断：排除条件优先级高于匹配条件。</summary>
        public bool Accepts(PacketEvent p)
        {
            if (!Enabled) return false;
            if (Exclude != null && !Exclude.IsEmpty && Exclude.IsSatisfiedBy(p)) return false;
            if (Match == null || Match.IsEmpty) return true;   // 不勾选任何匹配条件 = 转发全部 TCP/UDP
            return Match.IsSatisfiedBy(p);
        }
    }

    /// <summary>转发器全局配置（随 config.json 保存）。</summary>
    public sealed class ForwarderConfig
    {
        public bool Enabled { get; set; } = false;
        /// <summary>转发目标 API 地址，例：http://127.0.0.1:9000/hook</summary>
        public string TargetUrl { get; set; } = string.Empty;
        /// <summary>后台 Worker 并发数，1~10。</summary>
        public int Workers { get; set; } = 3;
        /// <summary>队列上限，满时丢弃最旧的封包。</summary>
        public int QueueLimit { get; set; } = 2000;
        /// <summary>单次 POST 超时（毫秒）。超时只丢弃该封包、记录日志，不影响抓包。</summary>
        public int TimeoutMs { get; set; } = 5000;
        /// <summary>单包体上限：超过则不转发（防止大响应体撑爆队列），0 = 不限。</summary>
        public int MaxPacketBytes { get; set; } = 1024 * 1024;
        /// <summary>是否只转发 TCP/UDP 原始封包（Fatbeans 语义：HTTP 类封包不转发）。本复刻版把 HTTP 代理看到的 TCP 流按方向切成封包事件转发。</summary>
        public bool OnlyTcpUdp { get; set; } = true;
        public List<ForwardRule> Rules { get; set; } = new();

        public int NormalizedWorkers => Math.Clamp(Workers, 1, 10);
        public int NormalizedQueueLimit => Math.Clamp(QueueLimit, 10, 200000);

        public static ForwarderConfig CreateDefault()
        {
            var cfg = new ForwarderConfig();
            cfg.Rules.Add(new ForwardRule
            {
                RuleName = "示例：登录包",
                Comment = "命中内容含 hello 的 TCP 包（含排除条件示例）",
                Match = new PacketMatch { UseProtocol = true, Protocols = new List<string> { "TCP" }, UseContent = true, Content = "hello" },
                Exclude = new PacketMatch { UseDstPort = true, DstPortFrom = 443, DstPortTo = 443 }
            });
            return cfg;
        }
    }

    /// <summary>
    /// 转发器引擎：条件匹配（抓包线程同步、纯内存）→ 有界队列（满则丢最旧）→ 后台 Worker 并发 POST。
    /// 全流程 fire-and-forget，任何异常都只记日志，绝不向调用方抛出。
    /// </summary>
    public sealed class ForwarderEngine : IDisposable
    {
        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false                     // 出站直连，避免被抓包代理自己再套一层
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        private readonly ForwarderConfig _cfg;
        private readonly object _gate = new();
        private readonly Queue<Job> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _workers = new();
        private int _started;

        private long _accepted, _forwarded, _dropped, _failed, _filtered;

        public ForwarderEngine(ForwarderConfig cfg)
        {
            _cfg = cfg ?? new ForwarderConfig();
        }

        /// <summary>匹配计数：引擎收进来的封包总数（仅统计未被规则过滤掉的？不是——这里统计送入匹配器的总数）。</summary>
        public long AcceptedCount => Interlocked.Read(ref _accepted);
        public long ForwardedCount => Interlocked.Read(ref _forwarded);
        public long DroppedCount => Interlocked.Read(ref _dropped);
        public long FailedCount => Interlocked.Read(ref _failed);
        public long FilteredCount => Interlocked.Read(ref _filtered);
        public int QueueDepth { get { lock (_gate) return _queue.Count; } }
        public ForwarderConfig Config => _cfg;

        public bool Enabled => _cfg.Enabled && !string.IsNullOrWhiteSpace(_cfg.TargetUrl);

        private sealed class Job
        {
            public string RuleId;
            public string RuleName;
            public PacketEvent Packet;
        }

        private void EnsureWorkers()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            int n = _cfg.NormalizedWorkers;
            for (int i = 0; i < n; i++) _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token)));
            Log.I("转发", $"转发器已启动：并发 Worker = {n}，队列上限 = {_cfg.NormalizedQueueLimit}，目标 = {_cfg.TargetUrl}");
        }

        /// <summary>
        /// 抓包线程入口：条件匹配 + 入队。**同步部分只有纯内存比较与一次数组拷贝**，绝不等待网络。
        /// </summary>
        public void Observe(PacketEvent packet)
        {
            try
            {
                if (packet == null) return;
                Interlocked.Increment(ref _accepted);
                if (!Enabled) return;
                int max = _cfg.MaxPacketBytes;
                if (max > 0 && packet.DataLength > max)
                {
                    Interlocked.Increment(ref _filtered);
                    Log.D("转发", $"封包 {packet.DataLength} 字节超过单包上限 {max}，跳过转发");
                    return;
                }
                if (_cfg.OnlyTcpUdp &&
                    !string.Equals(packet.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(packet.Protocol, "UDP", StringComparison.OrdinalIgnoreCase))
                    return;

                var rules = _cfg.Rules;
                if (rules == null || rules.Count == 0) { Interlocked.Increment(ref _filtered); return; }

                bool any = false;
                foreach (var r in rules)
                {
                    if (r == null || !r.Enabled) continue;
                    if (!r.Accepts(packet)) continue;
                    any = true;
                    r.HitCount++;
                    // 字节复制只发生一次（按引用共享只读数组，序列化在 Worker 里 Base64 化）
                    Enqueue(new Job { RuleId = r.RuleID, RuleName = r.RuleName, Packet = packet });
                }
                if (!any) Interlocked.Increment(ref _filtered);
            }
            catch (Exception ex)
            {
                // 转发器绝不能影响抓包主流程
                Log.W("转发", "封包匹配异常（已忽略）: " + ex.Message);
            }
        }

        /// <summary>无规则匹配器的直接转发（供自测与 API 调用）。</summary>
        public void ObserveWithRule(PacketEvent packet, ForwardRule rule)
        {
            if (packet == null || rule == null) return;
            try
            {
                if (!Enabled) return;
                Interlocked.Increment(ref _accepted);
                if (!rule.Accepts(packet)) { Interlocked.Increment(ref _filtered); return; }
                rule.HitCount++;
                Enqueue(new Job { RuleId = rule.RuleID, RuleName = rule.RuleName, Packet = packet });
            }
            catch (Exception ex) { Log.W("转发", "直接转发异常（已忽略）: " + ex.Message); }
        }

        private void Enqueue(Job job)
        {
            EnsureWorkers();
            int limit = _cfg.NormalizedQueueLimit;
            lock (_gate)
            {
                while (_queue.Count >= limit)
                {
                    _queue.Dequeue();
                    _dropped++;
                }
                _queue.Enqueue(job);
            }
            try { _signal.Release(); } catch { }
        }

        private async Task WorkerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch { return; }

                Job job;
                lock (_gate)
                {
                    if (_queue.Count == 0) continue;
                    job = _queue.Dequeue();
                }
                await SendAsync(job).ConfigureAwait(false);
            }
        }

        private async Task SendAsync(Job job)
        {
            var p = job.Packet;
            try
            {
                var payload = new
                {
                    Protocol = p.Protocol,
                    SrcIP = p.SrcIP,
                    SrcPort = p.SrcPort,
                    DstIP = p.DstIP,
                    DstPort = p.DstPort,
                    PID = p.Pid,
                    ProcessName = p.ProcessName ?? string.Empty,
                    DataLength = p.DataLength,
                    DataBase64 = Convert.ToBase64String(p.Data ?? Array.Empty<byte>()),
                    HexData = HexUtils.ToHex(p.Data ?? Array.Empty<byte>()),
                    Timestamp = p.TimestampMs,
                    RuleID = job.RuleId,
                    RuleName = job.RuleName,
                    Direction = p.Direction == ForwardDirection.Send ? "Send" : "Receive"
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(Math.Max(500, _cfg.TimeoutMs));
                using var resp = await Http.PostAsync(_cfg.TargetUrl, content, cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _forwarded);
                Log.D("转发", $"{p.Protocol} {p.SrcIP}:{p.SrcPort} → {p.DstIP}:{p.DstPort} {p.DataLength}B 已转发 [{(int)resp.StatusCode}] 规则={job.RuleName}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Log.W("转发", $"转发失败 (规则={job.RuleName}): {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _signal.Release(64); } catch { }
        }
    }
}
