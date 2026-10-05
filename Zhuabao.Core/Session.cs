using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Zhuabao.Core
{
    /// <summary>一条会话 = 请求 + 响应（响应可能为空 = 未完成）。</summary>
    public sealed class HttpSession
    {
        private static int _seq;

        public string Id { get; } = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Interlocked.Increment(ref _seq);
        public DateTime Time { get; set; } = DateTime.Now;
        public string Process { get; set; } = string.Empty;
        public string Protocol { get; set; } = "HTTP";
        public string Comment { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string Peer { get; set; } = string.Empty;
        public string StatusCode { get; set; } = string.Empty;
        public HttpRequest Request { get; set; }
        public HttpResponse Response { get; set; }
        public long RequestBytes { get; set; }
        public long ResponseBytes { get; set; }
        public int DurationMs { get; set; }
        public string InterceptStatus { get; set; } = string.Empty; // 空|放行|拦截|修改

        public bool Done => Response != null;

        public void SetResponse(HttpResponse r)
        {
            Response = r;
            StatusCode = r.StatusCode.ToString();
            ResponseBytes = r.Body?.Length ?? 0;
            Result = r.StatusCode.ToString();
        }

        public string Method => Request?.Method ?? string.Empty;
        public string Url => Request?.Url ?? string.Empty;
        public string Host => Request?.Host ?? string.Empty;
        public string Scheme => Request?.Scheme ?? "http";

        /// <summary>
        /// V1.0.8 BUG 修复支撑：复制一条会话，**带全新的 Id**（原封包保持原样）。
        ///
        /// 之前「右键 → 重发此请求」直接把编辑结果写回被选中的那条会话对象，
        /// 于是原抓包记录被覆盖（列表里那一条变成了重发后的内容，原始报文再也找不回来）。
        /// 现在重发一律在副本上进行：副本有自己的 Id，落盘成新的 .zb，
        /// 原会话连同它的原始字节一字不动。
        /// </summary>
        public HttpSession CloneWithNewId(string commentSuffix = null)
        {
            var copy = new HttpSession
            {
                Time = DateTime.Now,
                Process = Process,
                Protocol = Protocol,
                Comment = commentSuffix == null ? Comment : (Comment ?? "") + commentSuffix,
                Result = Result,
                Peer = Peer,
                StatusCode = StatusCode,
                RequestBytes = RequestBytes,
                ResponseBytes = ResponseBytes,
                DurationMs = DurationMs,
                InterceptStatus = InterceptStatus
            };
            // 报文按原始字节重新解析 = 真正的深拷贝（Headers 字典与 Body 数组都不共享）
            if (Request != null) copy.Request = HttpRequest.Parse(Request.ToRawBytes());
            if (Response != null) copy.Response = HttpResponse.Parse(Response.ToRawBytes());
            return copy;
        }
    }

    /// <summary>
    /// 会话仓库：内存索引（有上限）+ 后台队列异步落盘（每会话一个 .zb json，不阻塞请求路径）。
    /// Capture 目录文件数超过上限时启动时清理最旧的。
    /// </summary>
    public sealed class SessionStore : IDisposable
    {
        private const int MemoryCap = 2000;        // 内存里最多留这么多条（UI 用的是引擎事件，这里只为计数）
        public const int DiskFileCap = 20000;      // 落盘文件数上限，超出启动时清理最旧的
        private const int DiskCleanupTarget = 15000;

        private readonly string _dir;
        private readonly List<HttpSession> _list = new();
        private readonly object _gate = new();
        private readonly Channel<HttpSession> _queue;
        private readonly Task _writer;

        public SessionStore(string dir)
        {
            _dir = dir ?? throw new ArgumentNullException(nameof(dir));
            Directory.CreateDirectory(_dir);
            _queue = Channel.CreateBounded<HttpSession>(new System.Threading.Channels.BoundedChannelOptions(10000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
            });
            _writer = Task.Run(PersistLoopAsync);
            _ = Task.Run(CleanupDiskAsync);
        }

        public int Count { get { lock (_gate) return _list.Count; } }
        public string Dir => _dir;

        public event Action<HttpSession> OnAdded;
        public event Action<HttpSession> OnUpdated;

        public void Add(HttpSession s)
        {
            lock (_gate)
            {
                _list.Add(s);
                while (_list.Count > MemoryCap) _list.RemoveAt(0);
            }
            OnAdded?.Invoke(s);
            // 落盘走后台队列，不阻塞请求处理
            if (!_queue.Writer.TryWrite(s)) Log.W("会话", "写盘队列已满，丢弃 " + s.Id);
        }

        public void Update(HttpSession s)
        {
            OnUpdated?.Invoke(s);
        }

        private async Task PersistLoopAsync()
        {
            try
            {
                await foreach (var s in _queue.Reader.ReadAllAsync())
                    Persist(s);
            }
            catch (Exception ex) { Log.E("会话", "写盘循环异常退出", ex); }
        }

        private void Persist(HttpSession s)
        {
            try
            {
                var dto = new SessionDto
                {
                    Id = s.Id, Time = s.Time, Process = s.Process, Protocol = s.Protocol,
                    Comment = s.Comment, StatusCode = s.StatusCode, Result = s.Result,
                    RequestBytes = s.RequestBytes, ResponseBytes = s.ResponseBytes,
                    DurationMs = s.DurationMs,
                    Request = s.Request == null ? null : new MsgDto { RawB64 = s.Request.ToRawBytes() },
                    Response = s.Response == null ? null : new MsgDto { RawB64 = s.Response.ToRawBytes() }
                };
                File.WriteAllText(Path.Combine(_dir, s.Id + ".zb"), JsonConvert.SerializeObject(dto, Formatting.None), Encoding.UTF8);
            }
            catch (Exception ex) { Log.W("会话", "持久化失败 " + s.Id + ": " + ex.Message); }
        }

        /// <summary>Capture 目录文件超上限时删除最旧的，防止磁盘无限增长。</summary>
        private async Task CleanupDiskAsync()
        {
            try
            {
                await Task.Delay(2000);
                var files = Directory.GetFiles(_dir, "*.zb");
                if (files.Length <= DiskFileCap) return;
                var toDelete = files.OrderByDescending(File.GetLastWriteTime).Skip(DiskCleanupTarget).ToList();
                int deleted = 0;
                foreach (var f in toDelete)
                {
                    try { File.Delete(f); deleted++; } catch { }
                }
                Log.I("会话", $"历史封包清理：删除最旧的 {deleted} 个文件（上限 {DiskFileCap}）");
            }
            catch (Exception ex) { Log.W("会话", "历史封包清理失败: " + ex.Message); }
        }

        public static HttpSession LoadSession(string path)
        {
            try
            {
                var dto = JsonConvert.DeserializeObject<SessionDto>(File.ReadAllText(path, Encoding.UTF8));
                if (dto == null) return null;
                var s = new HttpSession
                {
                    Time = dto.Time, Process = dto.Process ?? "", Protocol = dto.Protocol ?? "HTTP",
                    Comment = dto.Comment ?? "", StatusCode = dto.StatusCode ?? "", Result = dto.Result ?? "",
                    RequestBytes = dto.RequestBytes, ResponseBytes = dto.ResponseBytes, DurationMs = dto.DurationMs
                };
                if (dto.Request?.RawB64 is { Length: > 0 } reqRaw) s.Request = HttpRequest.Parse(reqRaw);
                if (dto.Response?.RawB64 is { Length: > 0 } respRaw) s.Response = HttpResponse.Parse(respRaw);
                return s;
            }
            catch (Exception ex) { Log.E("会话", "LoadSession 失败: " + path, ex); return null; }
        }

        public void Dispose()
        {
            try
            {
                _queue.Writer.TryComplete();
                _writer.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }

    internal sealed class SessionDto
    {
        public string Id { get; set; }
        public DateTime Time { get; set; }
        public string Process { get; set; }
        public string Protocol { get; set; }
        public string Comment { get; set; }
        public string StatusCode { get; set; }
        public string Result { get; set; }
        public long RequestBytes { get; set; }
        public long ResponseBytes { get; set; }
        public int DurationMs { get; set; }
        public MsgDto Request { get; set; }
        public MsgDto Response { get; set; }
    }

    internal sealed class MsgDto
    {
        public byte[] RawB64 { get; set; }
    }
}
