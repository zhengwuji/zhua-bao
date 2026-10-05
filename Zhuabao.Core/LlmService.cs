using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zhuabao.Core
{
    /// <summary>自定义大模型接口的协议方言。</summary>
    public enum LlmDialect
    {
        /// <summary>OpenAI 兼容（/v1/chat/completions + Authorization: Bearer）。</summary>
        OpenAiCompatible = 0,
        /// <summary>V1.0.8 新增：Anthropic 兼容（/v1/messages + x-api-key + anthropic-version，system 独立顶层字段）。</summary>
        AnthropicCompatible = 1
    }

    /// <summary>一条对话消息。</summary>
    public sealed class LlmMessage
    {
        public string Role { get; set; } = "user";   // system | user | assistant
        public string Content { get; set; } = string.Empty;

        public LlmMessage() { }
        public LlmMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>大模型调用结果。</summary>
    public sealed class LlmResult
    {
        public bool Ok { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string StopReason { get; set; } = string.Empty;
        public string RawJson { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// 自定义大模型接口配置（V1.0.8：新增 Anthropic 兼容模式）。
    /// 两种方言共用同一套字段：BaseUrl / ApiKey / Model / SystemPrompt / Temperature / MaxTokens，
    /// 差异只在端点路径、鉴权头与请求体结构上。
    /// </summary>
    public sealed class LlmConfig
    {
        public bool Enabled { get; set; } = false;
        /// <summary>"OpenAI兼容" 或 "Anthropic兼容"。</summary>
        public string Provider { get; set; } = "OpenAI兼容";
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string SystemPrompt { get; set; } = "你是抓包分析助手，根据 HTTP 会话内容给出简洁的结论。";
        public double Temperature { get; set; } = 0.7;
        public int MaxTokens { get; set; } = 1024;
        public int TimeoutMs { get; set; } = 60000;

        public LlmDialect Dialect => IsAnthropic(Provider) ? LlmDialect.AnthropicCompatible : LlmDialect.OpenAiCompatible;

        public static bool IsAnthropic(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return false;
            var p = provider.Trim().ToLowerInvariant();
            return p.Contains("anthropic") || p.Contains("claude") || p.Contains("anthropic兼容") || p.Contains("anthropic 兼容");
        }

        /// <summary>未填写 BaseUrl 时按方言给官方默认值。</summary>
        public string EffectiveBaseUrl
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(BaseUrl)) return BaseUrl.Trim().TrimEnd('/');
                return Dialect == LlmDialect.AnthropicCompatible ? "https://api.anthropic.com" : "https://api.openai.com";
            }
        }

        /// <summary>拼出真正的对话端点：已经是完整端点（含 /messages 或 /chat/completions）则原样使用。</summary>
        public string Endpoint
        {
            get
            {
                var b = EffectiveBaseUrl;
                if (b.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ||
                    b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    return b;
                return Dialect == LlmDialect.AnthropicCompatible ? b + "/v1/messages" : b + "/v1/chat/completions";
            }
        }

        public LlmConfig Clone() => (LlmConfig)MemberwiseClone();
    }

    /// <summary>
    /// V1.0.8 新增：自定义大模型接口客户端。
    ///
    /// 支持两种方言，请求与响应结构差异全部在这里消化：
    ///  · OpenAI 兼容：POST /v1/chat/completions
    ///      headers: Authorization: Bearer &lt;key&gt;
    ///      body:    { model, messages:[{role:system|user|assistant, content}], temperature, max_tokens }
    ///      resp:    choices[0].message.content
    ///  · Anthropic 兼容：POST /v1/messages
    ///      headers: x-api-key: &lt;key&gt;, anthropic-version: 2023-06-01
    ///      body:    { model, max_tokens(必填), system(顶层独立字段), messages:[{role:user|assistant, content}], temperature }
    ///               注意：Anthropic 的 messages 里不接受 system 角色，且 user/assistant 必须交替，
    ///               因此这里会把 system 抽出到顶层，并合并连续同角色消息。
    ///      resp:    content[] 里取 type=="text" 的 text 拼接；另有 stop_reason 与 usage
    ///
    /// 网络层用独立 HttpClient（UseProxy=false，避免抓包代理把自己套住），
    /// 超时只影响本次调用，失败只返回 LlmResult.Ok=false，不抛异常。
    /// </summary>
    public sealed class LlmClient
    {
        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        public const string AnthropicVersion = "2023-06-01";

        private readonly LlmConfig _cfg;

        public LlmClient(LlmConfig cfg) { _cfg = cfg ?? new LlmConfig(); }

        public LlmConfig Config => _cfg;

        public async Task<LlmResult> ChatAsync(IEnumerable<LlmMessage> messages, CancellationToken ct = default)
        {
            var result = new LlmResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!_cfg.Enabled) { result.Error = "自定义大模型接口未启用（配置里勾选『启用』）"; return result; }
                if (string.IsNullOrWhiteSpace(_cfg.ApiKey)) { result.Error = "未配置 API Key"; return result; }
                if (string.IsNullOrWhiteSpace(_cfg.Model)) { result.Error = "未配置模型名（如 claude-3-5-sonnet-latest / gpt-4o-mini）"; return result; }

                var list = (messages ?? Enumerable.Empty<LlmMessage>()).Where(m => m != null).ToList();
                string json = BuildRequestJson(_cfg, list);

                using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Endpoint);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                ApplyAuthHeaders(req, _cfg, _cfg.Dialect);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Math.Max(1000, _cfg.TimeoutMs));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                result.RawJson = body;
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;

                if (!resp.IsSuccessStatusCode)
                {
                    result.Error = "HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "： " + Shorten(body, 600);
                    Log.W("大模型", "调用失败 " + result.Error);
                    return result;
                }

                result.Text = ParseResponseText(_cfg.Dialect, body, out var inTok, out var outTok, out var stop);
                result.InputTokens = inTok;
                result.OutputTokens = outTok;
                result.StopReason = stop;
                result.Ok = !string.IsNullOrEmpty(result.Text);
                if (!result.Ok && string.IsNullOrEmpty(result.Error)) result.Error = "响应里没有取到文本内容： " + Shorten(body, 600);
                Log.I("大模型", $"调用成功 ({_cfg.Model}, {_cfg.Dialect}) in={inTok} out={outTok} stop={stop} {result.ElapsedMs}ms");
                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.Error = "调用超时（" + _cfg.TimeoutMs + "ms）";
                Log.W("大模型", result.Error);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.Error = ex.Message;
                Log.W("大模型", "调用异常: " + ex.Message);
                return result;
            }
        }

        // ================= 请求构造（纯函数，便于离线自测） =================

        public static string BuildRequestJson(LlmConfig cfg, IEnumerable<LlmMessage> messages)
        {
            var msgs = (messages ?? Enumerable.Empty<LlmMessage>()).Where(m => m != null).ToList();
            return cfg.Dialect == LlmDialect.AnthropicCompatible
                ? BuildAnthropicBody(cfg, msgs)
                : BuildOpenAiBody(cfg, msgs);
        }

        /// <summary>OpenAI 兼容体：system 作为 messages 里的第一条。</summary>
        internal static string BuildOpenAiBody(LlmConfig cfg, List<LlmMessage> msgs)
        {
            var arr = new JArray();
            if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt))
                arr.Add(new JObject { ["role"] = "system", ["content"] = cfg.SystemPrompt });
            bool anyUser = false;
            foreach (var m in msgs)
            {
                string role = NormalizeRole(m.Role, allowSystem: true);
                if (role == "user") anyUser = true;
                arr.Add(new JObject { ["role"] = role, ["content"] = m.Content ?? string.Empty });
            }
            if (!anyUser) arr.Add(new JObject { ["role"] = "user", ["content"] = "你好" });

            var body = new JObject
            {
                ["model"] = cfg.Model ?? string.Empty,
                ["messages"] = arr,
                ["temperature"] = Math.Clamp(cfg.Temperature, 0d, 2d)
            };
            // OpenAI 新版推荐 max_completion_tokens；max_tokens 兼容面更广，这里沿用后者
            body["max_tokens"] = Math.Max(1, cfg.MaxTokens);
            return body.ToString(Formatting.None);
        }

        /// <summary>
        /// Anthropic 兼容体：system 提到顶层独立字段；messages 只允许 user/assistant 且必须交替，
        /// 连续的相同角色会被合并成一条（Fatbeans 的 Anthropic 兼容模式也是这样收敛的）。
        /// </summary>
        internal static string BuildAnthropicBody(LlmConfig cfg, List<LlmMessage> msgs)
        {
            // 先按顺序把 system 抽出来追加到顶层 system 文本
            var sysParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt)) sysParts.Add(cfg.SystemPrompt);
            var conv = new List<LlmMessage>();
            foreach (var m in msgs)
            {
                string role = NormalizeRole(m.Role, allowSystem: false);
                if (role == "system") { if (!string.IsNullOrEmpty(m.Content)) sysParts.Add(m.Content); continue; }
                if (conv.Count > 0 && conv[conv.Count - 1].Role == role)
                    conv[conv.Count - 1].Content = (conv[conv.Count - 1].Content ?? "") + "\n" + (m.Content ?? "");
                else
                    conv.Add(new LlmMessage(role, m.Content ?? string.Empty));
            }
            if (conv.Count == 0) conv.Add(new LlmMessage("user", "你好"));
            if (conv[0].Role != "user") conv.Insert(0, new LlmMessage("user", "（继续）"));

            var arr = new JArray();
            foreach (var m in conv)
                arr.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content ?? string.Empty });

            var body = new JObject
            {
                ["model"] = cfg.Model ?? string.Empty,
                // Anthropic 的 max_tokens 是必填字段，缺了直接 400
                ["max_tokens"] = Math.Max(1, cfg.MaxTokens),
                ["system"] = string.Join("\n", sysParts),
                ["messages"] = arr,
                ["temperature"] = Math.Clamp(cfg.Temperature, 0d, 1d)   // Anthropic 温度上限是 1
            };
            return body.ToString(Formatting.None);
        }

        private static string NormalizeRole(string role, bool allowSystem)
        {
            var r = (role ?? "user").Trim().ToLowerInvariant();
            switch (r)
            {
                case "assistant": case "ai": case "model": return "assistant";
                case "system": case "developer": return allowSystem ? "system" : "system";
                default: return "user";
            }
        }

        public static void ApplyAuthHeaders(HttpRequestMessage req, LlmConfig cfg, LlmDialect dialect)
        {
            if (dialect == LlmDialect.AnthropicCompatible)
            {
                req.Headers.TryAddWithoutValidation("x-api-key", cfg.ApiKey ?? string.Empty);
                req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            }
            else
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (cfg.ApiKey ?? string.Empty));
            }
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
        }

        // ================= 响应解析 =================

        public static string ParseResponseText(LlmDialect dialect, string json, out int inputTokens, out int outputTokens, out string stopReason)
        {
            inputTokens = outputTokens = 0;
            stopReason = string.Empty;
            try
            {
                var root = JObject.Parse(json ?? "{}");
                if (dialect == LlmDialect.AnthropicCompatible)
                {
                    var sb = new StringBuilder();
                    if (root["content"] is JArray arr)
                        foreach (var item in arr)
                            if (string.Equals(item["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                                sb.Append(item["text"]?.ToString() ?? "");
                    stopReason = root["stop_reason"]?.ToString() ?? "";
                    inputTokens = root["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                    outputTokens = root["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                    return sb.ToString();
                }

                var content = root["choices"]?[0]?["message"]?["content"]?.ToString();
                stopReason = root["choices"]?[0]?["finish_reason"]?.ToString() ?? "";
                inputTokens = root["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
                outputTokens = root["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
                return content ?? string.Empty;
            }
            catch (Exception ex)
            {
                Log.W("大模型", "响应解析失败: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>从异常响应体里抠出错误消息（Anthropic 给 {"type":"error","error":{"message":...}}）。</summary>
        public static string ExtractErrorMessage(string json)
        {
            try
            {
                var root = JObject.Parse(json ?? "{}");
                return root["error"]?["message"]?.ToString()
                       ?? root["error"]?.ToString()
                       ?? root["message"]?.ToString()
                       ?? json;
            }
            catch { return json ?? string.Empty; }
        }

        private static string Shorten(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");
    }
}
