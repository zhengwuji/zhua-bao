using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Zhuabao.Core
{
    public static class RedirectEngine
    {
        public static string Rewrite(string url, IEnumerable<RedirectRule> rules)
        {
            if (string.IsNullOrEmpty(url) || rules == null) return url;
            foreach (var r in rules)
            {
                if (!r.Enabled || string.IsNullOrEmpty(r.Pattern)) continue;
                try { url = Regex.Replace(url, r.Pattern, r.Replacement ?? string.Empty); }
                catch { }
            }
            return url;
        }
    }

    public static class ReplaceEngine
    {
        /// <summary>
        /// V1.0.8 BUG 修复：改包规则的「次数限制」之前失效。
        ///
        /// 原始实现里规则对象就是 UI 网格/配置里那一份，界面每次 FormClosing 都会
        /// `_cfg.ReplaceRules.Clear()` 重建整套规则对象，甚至每次抓包请求都会
        /// `ConfigService.Load()` 出一份新对象 —— 计数随之清零，
        /// 于是「限制 1 次」的规则实际上无限次生效。
        ///
        /// 这里把计数挪到进程级静态表，键 = 规则语义指纹（方向/目标/匹配/类型/值/备注），
        /// 规则对象怎么重建都不影响；配置保存/加载/界面改动都不会重置，只有：
        ///   · 点「重置次数」/ 修改规则内容（指纹变了 = 新规则，天然从 0 开始）
        ///   · 调用 RuleRegistry.ResetAll()
        /// 才会清零。
        /// </summary>
        public static readonly RuleHitRegistry RuleRegistry = new();

        /// <summary>规则是否还有剩余次数（未启用限制时恒为 true）。</summary>
        public static bool HasBudget(ReplaceRule r) => r != null && RuleRegistry.HasBudget(r);

        /// <summary>对请求/响应执行改包规则（在转发前调用）。</summary>
        public static void ApplyRules(IEnumerable<ReplaceRule> rules, HttpRequest req, HttpResponse resp)
        {
            if (rules == null) return;
            foreach (var r in rules)
            {
                if (!r.Enabled) continue;
                // V1.0.8：次数限制检查必须在真正改包之前 —— 超预算直接跳过
                if (!RuleRegistry.HasBudget(r))
                {
                    Log.D("改包", "规则已达次数上限，跳过 [" + r.Target + " " + r.Match + "] (" + RuleRegistry.Used(r) + "/" + r.MaxHits + ")");
                    continue;
                }
                try
                {
                    bool isReq = string.Equals(r.Direction, "请求", StringComparison.OrdinalIgnoreCase);
                    if (isReq && req == null) continue;
                    if (!isReq && resp == null) continue;

                    bool applied = false;
                    switch (r.Target ?? string.Empty)
                    {
                        case "请求头": if (isReq) { EditHeader(req.Headers, r); applied = true; } break;
                        case "响应头": if (!isReq) { applied = ApplyResponseHeader(r, resp); } break;
                        case "请求体": if (isReq) { EditBody(req, r); applied = true; } break;
                        case "响应体": if (!isReq) { EditBody(resp, r); applied = true; } break;
                        case "URL": if (isReq) { req.Url = RegexText(req.Url, r); applied = true; } break;
                        case "状态行":
                            // V1.0.8 新功能③：修改响应头时可直接改状态码（Match 为空则命中任意响应）
                            if (!isReq && ApplyStatusLine(r, resp)) applied = true;
                            break;
                    }

                    // 只有真正改过才消耗次数（避免方向不匹配的规则被空跑掉配额）
                    if (applied) RuleRegistry.Consume(r);
                }
                catch (Exception ex) { Log.W("改包", "规则执行失败 [" + r.Target + " " + r.Match + "]: " + ex.Message); }
            }
        }

        /// <summary>
        /// V1.0.8 新功能③：改「响应头」时也能改状态码。
        /// 约定：把「匹配」栏写成伪头名 <c>:status</c>（也接受 <c>状态码</c> / <c>Status</c>），
        /// 值栏写目标状态码（"404"、"404 Not Found"，或区间 "200-299"），
        /// 该条规则就变成改状态码；其它情况仍按普通响应头增删改处理。
        /// 这样不破坏原有规则，也不额外增加列。
        /// </summary>
        private static bool ApplyResponseHeader(ReplaceRule r, HttpResponse resp)
        {
            if (resp == null) return false;
            string m = (r.Match ?? string.Empty).Trim();
            if (m.Equals(":status", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("状态码", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("statuscode", StringComparison.OrdinalIgnoreCase) ||
                m.Equals("status-code", StringComparison.OrdinalIgnoreCase))
            {
                return ApplyStatusLine(r, resp);
            }
            EditHeader(resp.Headers, r);
            return true;
        }

        /// <summary>状态码伪头名（UI 提示与文档统一用这个常量）。</summary>
        public const string StatusPseudoHeader = ":status";

        /// <summary>
        /// 改状态码。V1.0.8 起「修改响应头」里也能改状态码，取值的来源按顺序：
        ///   ① Value 里显式写的目标码（支持 "404" 或 "404 Not Found"）
        ///   ② Value 形如 "200-299" 的区间随机取码
        ///   ③ Match 里写的目标码（当用户把目标码填在「匹配」栏时）
        /// 均解析失败则不改动、也不消耗次数。
        /// </summary>
        private static bool ApplyStatusLine(ReplaceRule r, HttpResponse resp)
        {
            if (resp == null) return false;

            int code = 0;
            string reason = null;

            string raw = FirstNonEmpty(r.Value, r.Match);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();

            // 区间写法："200-299"
            int dash = raw.IndexOf('-');
            if (dash > 0 && int.TryParse(raw.Substring(0, dash).Trim(), out var lo) &&
                int.TryParse(raw.Substring(dash + 1).Trim(), out var hi))
            {
                if (lo > hi) (lo, hi) = (hi, lo);
                code = new Random().Next(lo, hi + 1);
            }
            else
            {
                var parts = raw.Split(new[] { ' ' }, 2);
                if (!int.TryParse(parts[0], out code)) return false;
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) reason = parts[1].Trim();
            }

            if (code < 100 || code > 599) return false;
            resp.StatusCode = code;
            resp.Status = string.IsNullOrEmpty(reason) ? HttpResponse.ReasonOf(code) : reason;
            Log.I("改包", "响应状态码已改为 " + code + " " + resp.Status + "（规则匹配 " + r.Match + "）");
            return true;
        }

        private static string FirstNonEmpty(params string[] xs)
        {
            foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x;
            return null;
        }

        private static void EditHeader(IDictionary<string, string> headers, ReplaceRule r)
        {
            switch (r.ReplaceType)
            {
                case "替换值":
                    if (headers.ContainsKey(r.Match)) headers[r.Match] = r.Value;
                    else headers[r.Match] = r.Value;
                    break;
                case "追加":
                    if (headers.TryGetValue(r.Match, out var old)) headers[r.Match] = old + r.Value;
                    else headers[r.Match] = r.Value;
                    break;
                case "删除":
                    headers.Remove(r.Match);
                    break;
                default:
                    if (headers.TryGetValue(r.Match, out var cur)) headers[r.Match] = RegexText(cur, r);
                    break;
            }
        }

        private static void EditBody(HttpMessage msg, ReplaceRule r)
        {
            string text = msg.GetBodyText();
            string newText = r.ReplaceType == "正则替换"
                ? Regex.Replace(text, r.Match, r.Value ?? string.Empty)
                : (r.ReplaceType == "替换值" ? text.Replace(r.Match, r.Value ?? string.Empty) : text);
            if (!ReferenceEquals(newText, text) && newText != text) msg.SetBody(newText);
            msg.Headers["Content-Length"] = msg.Body.Length.ToString();
        }

        private static string RegexText(string input, ReplaceRule r)
        {
            if (r.ReplaceType == "正则替换") return Regex.Replace(input ?? string.Empty, r.Match, r.Value ?? string.Empty);
            if (r.ReplaceType == "替换值") return r.Value ?? string.Empty;
            return input;
        }
    }

    /// <summary>
    /// V1.0.8 新增：改包规则的次数计数表。
    /// 计数按「规则指纹」而非对象引用保存，因此配置重载 / UI 网格重建 / 保存 config.json
    /// 都不会把已用次数清零 —— 这正是原版「次数限制失效」的根因。
    /// 线程安全（多连接并发命中同一规则时用 Interlocked）。
    /// </summary>
    public sealed class RuleHitRegistry
    {
        private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public int Used(ReplaceRule r)
        {
            if (r == null) return 0;
            lock (_gate) return _used.TryGetValue(r.Signature, out var v) ? v : 0;
        }

        public bool HasBudget(ReplaceRule r)
        {
            if (r == null) return false;
            if (!r.LimitEnabled || r.MaxHits <= 0) return true;   // 未限制 = 无限
            lock (_gate)
            {
                _used.TryGetValue(r.Signature, out var used);
                return used < r.MaxHits;
            }
        }

        /// <summary>消耗一次配额，返回消耗后的已用次数。</summary>
        public int Consume(ReplaceRule r)
        {
            if (r == null) return 0;
            lock (_gate)
            {
                _used.TryGetValue(r.Signature, out var used);
                used++;
                _used[r.Signature] = used;
                r.HitsUsed = used;
                if (r.LimitEnabled && r.MaxHits > 0 && used == r.MaxHits)
                    Log.I("改包", "规则 [" + r.Target + " " + r.Match + "] 已用满 " + r.MaxHits + " 次，之后不再生效");
                return used;
            }
        }

        /// <summary>把界面上的规则对象回填已用次数（打开修改器时显示）。</summary>
        public void SyncDisplay(IEnumerable<ReplaceRule> rules)
        {
            if (rules == null) return;
            lock (_gate)
                foreach (var r in rules)
                    if (r != null && _used.TryGetValue(r.Signature, out var v)) r.HitsUsed = v;
        }

        /// <summary>单条规则清零（界面「重置次数」按钮）。</summary>
        public void Reset(ReplaceRule r)
        {
            if (r == null) return;
            lock (_gate)
            {
                _used.Remove(r.Signature);
                r.HitsUsed = 0;
            }
            Log.I("改包", "规则次数已重置 [" + r.Target + " " + r.Match + "]");
        }

        /// <summary>全部清零。</summary>
        public void ResetAll()
        {
            lock (_gate) _used.Clear();
            Log.I("改包", "所有改包规则次数已重置");
        }

        public int Count { get { lock (_gate) return _used.Count; } }
    }

    public sealed class ExtractHit
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Source { get; set; }
    }

    public static class ExtractEngine
    {
        public static List<ExtractHit> Apply(IEnumerable<ExtractRule> rules, HttpResponse resp, HttpRequest req)
        {
            var hits = new List<ExtractHit>();
            if (rules == null) return hits;
            foreach (var r in rules)
            {
                if (!r.Enabled) continue;
                string scope = r.Scope ?? string.Empty;
                string subject = null, source = null;

                try
                {
                    if (resp != null && scope.StartsWith("响应"))
                    {
                        if (scope.Contains("JSON")) { source = "响应-JSON"; subject = FindJsonPath(resp.GetBodyText(), r.Input); }
                        else if (scope.Contains("头")) { resp.Headers.TryGetValue(r.Input, out subject); source = "响应-头"; }
                        else if (scope.Contains("Cookie")) { resp.Headers.TryGetValue("Set-Cookie", out subject); source = "Cookie"; }
                        else { source = "响应-表单"; subject = FindFormField(resp.GetBodyText(), r.Input); }
                    }
                    else if (req != null && scope.StartsWith("请求"))
                    {
                        if (scope.Contains("头")) { req.Headers.TryGetValue(r.Input, out subject); source = "请求-头"; }
                        else { source = "请求-体"; subject = FindFormField(req.GetBodyText(), r.Input); }
                    }

                    if (subject == null) continue;

                    if (!string.IsNullOrEmpty(r.Regex))
                    {
                        var m = Regex.Match(subject, r.Regex);
                        if (!m.Success) continue;
                        subject = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                    }

                    if (!string.IsNullOrEmpty(subject))
                        hits.Add(new ExtractHit { Name = r.Name, Value = subject, Source = source });
                }
                catch (Exception ex) { Log.W("取值", "规则失败 [" + r.Name + "]: " + ex.Message); }
            }
            return hits;
        }

        private static string FindFormField(string html, string name)
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(name)) return null;
            var m = Regex.Match(html,
                @"name\s*=\s*['""]" + Regex.Escape(name) + @"['""][^>]*value\s*=\s*['""]([^'""]*)['""]",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(html,
                @"value\s*=\s*['""]([^'""]*)['""][^>]*name\s*=\s*['""]" + Regex.Escape(name) + @"['""]",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FindJsonPath(string json, string path)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return null;
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(json);
                foreach (var part in path.Split('.'))
                {
                    if (token is Newtonsoft.Json.Linq.JObject o) token = o[part];
                    else if (token is Newtonsoft.Json.Linq.JArray a && int.TryParse(part, out var idx)) token = a[idx];
                    else return null;
                    if (token == null) return null;
                }
                return token.Type == Newtonsoft.Json.Linq.JTokenType.String ? ((Newtonsoft.Json.Linq.JValue)token).Value as string : token.ToString();
            }
            catch { return null; }
        }
    }
}
