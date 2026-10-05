using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>
    /// 会话详情面板：点击封包列表后显示请求/响应全部内容，支持多方式查看与编辑修改。
    /// Tab: 预览 | 原始报文 | 请求头 | 请求体 | 响应头 | 响应体 | 修改
    /// </summary>
    public sealed class SessionDetailPanel : UserControl
    {
        private HttpSession _session;
        private TextBox _txtRaw;          // 原始报文（可编辑：整个请求原文）
        private TextBox _txtReqHeaders;
        private TextBox _txtReqBody;
        private TextBox _txtRespHeaders;
        private TextBox _txtRespBody;
        private WebBrowser _preview;      // 响应体 HTML 预览
        private TextBox _previewSrc;      // 预览源码
        private ComboBox _cmbPreviewMode;
        private ComboBox _cmbBodyView;    // 请求体查看方式：文本/JSON美化/Base64/Hex
        private ComboBox _cmbRespView;    // 响应体查看方式
        private TextBox _txtEdit;         // 修改页的大编辑框
        private ComboBox _cmbEditTarget;  // 修改对象：请求头/请求体/响应头/响应体/完整原始
        private CheckBox _chkBase64;      // 按Base64理解内容

        public event Action<HttpSession> SessionModified;

        public SessionDetailPanel()
        {
            Build();
        }

        public HttpSession Session => _session;

        public void ShowSession(HttpSession s)
        {
            _session = s;
            ReloadAll();
        }

        public void Clear()
        {
            _session = null;
            _txtRaw.Text = "";
            _txtReqHeaders.Text = "";
            _txtReqBody.Text = "";
            _txtRespHeaders.Text = "";
            _txtRespBody.Text = "";
            _previewSrc.Text = "";
            _txtEdit.Text = "";
        }

        private static TextBox MakeEditBox()
        {
            return new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                ReadOnly = false,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.White
            };
        }

        private void Build()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };

            // ---- 预览 ----
            var tabPreview = new TabPage("预览");
            var pvTop = new Panel { Dock = DockStyle.Top, Height = 30 };
            _cmbPreviewMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Location = new Point(6, 4) };
            _cmbPreviewMode.Items.AddRange(new object[] { "渲染网页", "源码" });
            _cmbPreviewMode.SelectedIndex = 0;
            _cmbPreviewMode.SelectedIndexChanged += (s, e) => SwitchPreview();
            pvTop.Controls.Add(_cmbPreviewMode);
            _preview = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowNavigation = true };
            _previewSrc = new TextBox { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f), Visible = false, BackColor = Color.White };
            tabPreview.Controls.Add(_preview);
            tabPreview.Controls.Add(_previewSrc);
            tabPreview.Controls.Add(pvTop);
            tabPreview.Enter += (s, e) => RefreshPreview();

            // ---- 原始 ----
            var tabRaw = new TabPage("原始报文");
            _txtRaw = MakeEditBox();
            tabRaw.Controls.Add(_txtRaw);

            // ---- 请求 ----
            var tabReq = new TabPage("请求");
            var reqSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            var reqHeadPane = new Panel { Dock = DockStyle.Fill };
            var reqHeadBar = new Panel { Dock = StyleTop() };
            reqHeadBar.Height = 30;
            reqHeadBar.Controls.Add(new Label { Text = "请求头（可直接编辑）", AutoSize = true, Location = new Point(6, 7), ForeColor = Color.DimGray });
            _txtReqHeaders = MakeEditBox();
            reqHeadPane.Controls.Add(_txtReqHeaders);
            reqHeadPane.Controls.Add(reqHeadBar);
            _txtReqHeaders.Location = new Point(0, 30);
            _txtReqHeaders.Height = 120;

            var reqBodyPane = new Panel { Dock = DockStyle.Fill };
            var reqBodyBar = new Panel { Dock = DockStyle.Top, Height = 30 };
            reqBodyBar.Controls.Add(new Label { Text = "请求体", AutoSize = true, Location = new Point(6, 7), ForeColor = Color.DimGray });
            _cmbBodyView = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Location = new Point(70, 3) };
            _cmbBodyView.Items.AddRange(new object[] { "文本", "JSON美化", "Base64", "Hex" });
            _cmbBodyView.SelectedIndexChanged += (s, e) => FillBody(_txtReqBody, CurrentRequest(), _cmbBodyView.SelectedIndex);
            reqBodyBar.Controls.Add(_cmbBodyView);
            var btnReqApply = new Button { Text = "应用修改", Location = new Point(200, 2), Width = 90, Height = 25 };
            btnReqApply.Click += (s, e) => ApplyBodyEdit(_txtReqBody, CurrentRequest(), _cmbBodyView.SelectedIndex);
            reqBodyBar.Controls.Add(btnReqApply);
            _txtReqBody = MakeEditBox();
            reqBodyPane.Controls.Add(_txtReqBody);
            reqBodyPane.Controls.Add(reqBodyBar);
            _txtReqBody.Location = new Point(0, 30);
            _txtReqBody.Height = 200;

            reqSplit.Panel1.Controls.Add(reqHeadPane);
            reqSplit.Panel2.Controls.Add(reqBodyPane);
            tabReq.Controls.Add(reqSplit);
            tabs.TabPages.Add(tabRaw);
            tabs.TabPages.Add(tabReq);

            // ---- 响应 ----
            var tabResp = new TabPage("响应");
            var respSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            var respHeadPane = new Panel { Dock = DockStyle.Fill };
            respHeadPane.Controls.Add(new Label { Text = "响应头（可直接编辑）", AutoSize = true, Location = new Point(6, 7), ForeColor = Color.DimGray });
            _txtRespHeaders = MakeEditBox();
            _txtRespHeaders.Location = new Point(0, 30);
            _txtRespHeaders.Height = 120;
            respHeadPane.Controls.Add(_txtRespHeaders);
            var respBodyPane = new Panel { Dock = DockStyle.Fill };
            var respBodyBar = new Panel { Dock = DockStyle.Top, Height = 30 };
            respBodyBar.Controls.Add(new Label { Text = "响应体", AutoSize = true, Location = new Point(6, 7), ForeColor = Color.DimGray });
            _cmbRespView = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Location = new Point(70, 3) };
            文本Items(_cmbRespView);
            _cmbRespView.SelectedIndexChanged += (s, e) => FillBody(_txtRespBody, CurrentResponse(), _cmbRespView.SelectedIndex);
            respBodyBar.Controls.Add(_cmbRespView);
            var btnRespApply = new Button { Text = "应用修改", Location = new Point(200, 2), Width = 90, Height = 25 };
            btnRespApply.Click += (s, e) => ApplyBodyEdit(_txtRespBody, CurrentResponse(), _cmbRespView.SelectedIndex);
            respBodyBar.Controls.Add(btnRespApply);
            _txtRespBody = MakeEditBox();
            respBodyPane.Controls.Add(_txtRespBody);
            respBodyPane.Controls.Add(respBodyBar);
            _txtRespBody.Location = new Point(0, 30);
            _txtRespBody.Height = 200;
            respSplit.Panel1.Controls.Add(respHeadPane);
            respSplit.Panel2.Controls.Add(respBodyPane);
            tabResp.Controls.Add(respSplit);
            tabs.TabPages.Add(tabResp);

            // ---- 修改 ----
            var tabEdit = new TabPage("修改");
            var editBar = new Panel { Dock = DockStyle.Top, Height = 64 };
            _cmbEditTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Location = new Point(6, 6) };
            _cmbEditTarget.Items.AddRange(new object[] { "完整原始报文", "请求头", "请求体", "响应头", "响应体" });
            _cmbEditTarget.SelectedIndex = 0;
            _cmbEditTarget.SelectedIndexChanged += (s, e) => LoadEditTarget();
            editBar.Controls.Add(_cmbEditTarget);

            var btnLoad = new Button { Text = "载入当前内容", Location = new Point(146, 5), Width = 110, Height = 25 };
            btnLoad.Click += (s, e) => LoadEditTarget();
            editBar.Controls.Add(btnLoad);

            var btnSave = new Button { Text = "✔ 应用到会话", Location = new Point(262, 5), Width = 120, Height = 25, BackColor = Color.FromArgb(0, 160, 80), ForeColor = Color.White };
            btnSave.Click += (s, e) => ApplyEditToSession();
            editBar.Controls.Add(btnSave);

            var btnResend = new Button { Text = "⬆ 按此内容重发", Location = new Point(390, 5), Width = 130, Height = 25 };
            btnResend.Click += (s, e) => ResendEdited();
            editBar.Controls.Add(btnResend);

            var btnSaveRule = new Button { Text = "存为改包规则", Location = new Point(528, 5), Width = 120, Height = 25 };
            btnSaveRule.Click += (s, e) => SaveAsReplaceRule();
            editBar.Controls.Add(btnSaveRule);

            var btnSaveRedir = new Button { Text = "存为重定向", Location = new Point(656, 5), Width = 110, Height = 25 };
            btnSaveRedir.Click += (s, e) => SaveAsRedirect();
            editBar.Controls.Add(btnSaveRedir);

            _chkBase64 = new CheckBox { Text = "内容按 Base64 解码/编码", Location = new Point(6, 36), AutoSize = true };
            editBar.Controls.Add(_chkBase64);

            _txtEdit = MakeEditBox();
            tabEdit.Controls.Add(_txtEdit);
            tabEdit.Controls.Add(editBar);
            tabs.TabPages.Add(tabEdit);

            Controls.Add(tabs);
        }

        private static DockStyle StyleTop() => DockStyle.Top;

        private static void 文本Items(ComboBox c)
        {
            c.Items.AddRange(new object[] { "文本", "JSON美化", "Base64", "Hex" });
            c.SelectedIndex = 0;
        }

        private HttpRequest CurrentRequest() => _session?.Request;
        private HttpResponse CurrentResponse() => _session?.Response;

        private void ReloadAll()
        {
            if (_session == null) { Clear(); return; }

            // 原始：请求原文 + 响应原文
            var sb = new StringBuilder();
            if (_session.Request != null)
            {
                sb.AppendLine("========== 请求 ==========");
                sb.Append(Encoding.Latin1.GetString(_session.Request.ToRawBytes()));
                sb.AppendLine();
            }
            if (_session.Response != null)
            {
                sb.AppendLine("========== 响应 ==========");
                sb.Append(Encoding.Latin1.GetString(_session.Response.ToRawBytes()));
            }
            _txtRaw.Text = sb.ToString();

            _txtReqHeaders.Text = _session.Request?.HeaderText() ?? "";
            _txtRespHeaders.Text = _session.Response?.HeaderText() ?? "";
            _cmbBodyView.SelectedIndex = 0;
            _cmbRespView.SelectedIndex = 0;
            FillBody(_txtReqBody, _session.Request, 0);
            FillBody(_txtRespBody, _session.Response, 0);
            RefreshPreview();
            LoadEditTarget();
        }

        private static string BodyAsText(HttpMessage m, int mode)
        {
            if (m == null) return "";
            byte[] b = m.Body ?? Array.Empty<byte>();
            switch (mode)
            {
                case 1: // JSON美化
                    try
                    {
                        var tok = Newtonsoft.Json.Linq.JToken.Parse(Encoding.UTF8.GetString(b));
                        return tok.ToString(Newtonsoft.Json.Formatting.Indented);
                    }
                    catch { return Encoding.UTF8.GetString(b); }
                case 2: return b.Length == 0 ? "" : Convert.ToBase64String(b);
                case 3: return HexUtils.ToHex(b);
                default: return m.GetBodyText();
            }
        }

        private static void FillBody(TextBox box, HttpMessage m, int mode)
        {
            if (box == null) return;
            box.Text = BodyAsText(m, mode);
        }

        /// <summary>把编辑框内容按当前查看方式解析后写回报文体。</summary>
        private void ApplyBodyEdit(TextBox box, HttpMessage m, int mode)
        {
            if (m == null) { MessageBox.Show("该会话没有此报文", "提示"); return; }
            try
            {
                string text = box.Text ?? "";
                byte[] body;
                switch (mode)
                {
                    case 2: body = Convert.FromBase64String(text.Trim()); break;              // Base64 输入
                    case 3: body = HexUtils.FromHex(text); break;                             // Hex 输入
                    case 1: body = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(
                              Newtonsoft.Json.Linq.JToken.Parse(text))); break;               // JSON 压回紧凑
                    default: body = Encoding.UTF8.GetBytes(text); break;                      // 文本
                }
                m.Body = body;
                m.Headers["Content-Length"] = body.Length.ToString();
                SessionModified?.Invoke(_session);
                Log.I("改包", "请求/响应体已更新 (" + body.Length + " 字节)");
                MessageBox.Show("体内容已应用。可右键列表『重发此请求』验证。", "修改成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show("解析失败: " + ex.Message, "修改失败");
            }
        }

        private void SwitchPreview()
        {
            bool render = _cmbPreviewMode.SelectedIndex == 0;
            _preview.Visible = render;
            _previewSrc.Visible = !render;
        }

        private void RefreshPreview()
        {
            if (_session?.Response == null) { _previewSrc.Text = "(无响应)"; return; }
            string text = _session.Response.GetBodyText();
            if (_session.Response.IsBinary()) { _previewSrc.Text = "(二进制体 " + _session.Response.Body.Length + " 字节，无法预览)"; return; }
            _previewSrc.Text = text;
            if (_cmbPreviewMode.SelectedIndex == 0)
            {
                try { _preview.DocumentText = text; } catch { }
            }
        }

        // ============ 修改 ============
        private void LoadEditTarget()
        {
            if (_session == null) return;
            switch (_cmbEditTarget.SelectedIndex)
            {
                case 1: _txtEdit.Text = _session.Request?.HeaderText() ?? ""; break;
                case 2: _txtEdit.Text = BodyAsText(_session.Request, 0); break;
                case 3: _txtEdit.Text = _session.Response?.HeaderText() ?? ""; break;
                case 4: _txtEdit.Text = BodyAsText(_session.Response, 0); break;
                default:
                    var sb = new StringBuilder();
                    if (_session.Request != null) sb.Append(Encoding.Latin1.GetString(_session.Request.ToRawBytes()));
                    _txtEdit.Text = sb.ToString();
                    break;
            }
        }

        /// <summary>把编辑框内容按所选对象写回会话。</summary>
        public void ApplyEditToSession()
        {
            if (_session == null) { MessageBox.Show("未选中会话", "提示"); return; }
            string text = _chkBase64.Checked ? TryDecodeBase64(_txtEdit.Text) : _txtEdit.Text;
            try
            {
                switch (_cmbEditTarget.SelectedIndex)
                {
                    case 1: // 请求头
                        if (_session.Request == null) return;
                        ParseHeadersInto(_session.Request, text);
                        break;
                    case 2: // 请求体
                        _session.Request?.SetBody(text);
                        _session.Request?.Headers.Remove("Content-Length");
                        break;
                    case 3: // 响应头
                        if (_session.Response == null) return;
                        ParseHeadersInto(_session.Response, text);
                        break;
                    case 4: // 响应体
                        _session.Response?.SetBody(text);
                        _session.Response?.Headers.Remove("Content-Length");
                        break;
                    default: // 完整原始 = 覆盖请求（重发时使用）
                        _session.Request = HttpRequest.Parse(Encoding.UTF8.GetBytes(text));
                        break;
                }
                SessionModified?.Invoke(_session);
                ReloadAll();
                Log.I("改包", "会话 " + _session.Id + " 已按编辑内容更新 (" + _cmbEditTarget.SelectedItem + ")");
                MessageBox.Show("已应用到会话。可右键列表『重发此请求』验证效果。", "修改成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show("内容格式有误: " + ex.Message, "修改失败");
                Log.W("改包", "应用编辑失败: " + ex.Message);
            }
        }

        /// <summary>
        /// V1.0.8 BUG 修复：重发不再直接改写被选中的原会话，而是把编辑后的内容交给发送器，
        /// 由发送器生成 **新会话**（原封包保持原样）。这里只把编辑区内容读出来，不做任何回写。
        /// </summary>
        private void ResendEdited()
        {
            if (_session == null) { MessageBox.Show("未选中会话", "提示"); return; }
            string text = _chkBase64.Checked ? TryDecodeBase64(_txtEdit.Text) : _txtEdit.Text;
            _pendingResendText = _cmbEditTarget.SelectedIndex == 0 ? text : null;
            OnResendRequested?.Invoke(_session);
        }

        private string _pendingResendText;

        /// <summary>供主窗口取走「重发用的编辑后原文」：编辑对象是完整原始报文时返回编辑框内容，否则返回 null（让发送器用会话本身的请求）。</summary>
        public string EditedRequestText()
        {
            var t = _pendingResendText;
            _pendingResendText = null;
            return t;
        }

        public event Action<HttpSession> OnResendRequested;

        /// <summary>把编辑器内容存成全局改包规则（按"原内容→新内容"替换）。</summary>
        private void SaveAsReplaceRule()
        {
            if (_session == null) return;
            int idx = _cmbEditTarget.SelectedIndex;
            if (idx != 2 && idx != 4)
            {
                MessageBox.Show("请先在『修改对象』选择 请求体 或 响应体，再存为改包规则。\n（请求头/响应头的修改请用 菜单→改包规则 界面）", "提示");
                return;
            }
            string after = _chkBase64.Checked ? TryDecodeBase64(_txtEdit.Text) : _txtEdit.Text;
            bool isResp = idx == 4;
            string before = isResp ? BodyAsText(_session.Response, 0) : BodyAsText(_session.Request, 0);
            if (before == after) { MessageBox.Show("内容没有变化，无需存规则。", "提示"); return; }
            var cfg = ConfigService.Load();
            cfg.ReplaceRules.Add(new ReplaceRule
            {
                Enabled = true,
                Direction = isResp ? "响应" : "请求",
                Target = isResp ? "响应体" : "请求体",
                Match = before,
                ReplaceType = "替换值",
                Value = after,
                Comment = "来自会话 " + _session.Id
            });
            ConfigService.Save(cfg);
            MessageBox.Show("已存为改包规则（" + (isResp ? "响应体" : "请求体") + " 精确替换）。\n后续匹配的流量将自动替换。", "改包规则");
        }

        private void SaveAsRedirect()
        {
            if (_session?.Request == null) return;
            try
            {
                string target = InputDialog.Show(this.FindForm(), "URL 匹配正则（命中即重定向）:", "存为重定向",
                    Regex.Escape(_session.Request.Url));
                if (string.IsNullOrWhiteSpace(target)) return;
                string replacement = InputDialog.Show(this.FindForm(), "重定向到（替换后的URL）:", "存为重定向", _session.Request.Url);
                if (string.IsNullOrWhiteSpace(replacement)) return;
                var cfg = ConfigService.Load();
                cfg.Redirects.Add(new RedirectRule
                {
                    Enabled = true,
                    Pattern = target,
                    Replacement = replacement,
                    Comment = "来自会话 " + _session.Id
                });
                ConfigService.Save(cfg);
                MessageBox.Show("已存为重定向规则。", "重定向");
            }
            catch (Exception ex) { MessageBox.Show("失败: " + ex.Message, "错误"); }
        }

        private static string TryDecodeBase64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())); }
            catch { return s; }
        }

        /// <summary>把"头: 值"文本解析回报文头（首行若像起始行则一并应用）。</summary>
        private static void ParseHeadersInto(HttpMessage m, string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            // 找第一行含 : 的作为起点；之前的行若像起始行则应用
            var headers = new System.Collections.Generic.Dictionary<string, string>();
            string startLine = null;
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                int c = l.IndexOf(':');
                if (c > 0 && !l.Contains("==========" )) { headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim(); }
                else if (l.TrimStart().StartsWith("GET ") || l.TrimStart().StartsWith("POST ") || l.TrimStart().StartsWith("HTTP/")) startLine = l.Trim();
            }
            foreach (var kv in headers) m.Headers[kv.Key] = kv.Value;
            if (m is HttpRequest hr && startLine != null)
            {
                // SetStartLine 是 internal，跨工程不可见；用"改 Host 头 + 重新解析"等效实现
                var p = startLine.Split(' ');
                if (p.Length >= 3)
                {
                    hr.Method = p[0];
                    hr.HttpVersion = p[2];
                    string target = p[1];
                    string host = hr.Host;
                    if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        target = "http://" + (string.IsNullOrEmpty(host) ? "unknown" : host) + target;
                    hr.Url = target;
                    try { hr.Host = new Uri(target).Host; } catch { }
                }
            }
            if (m is HttpResponse hresp && startLine != null)
            {
                var p = startLine.Split(' ', 3);
                if (p.Length >= 2 && int.TryParse(p[1], out var code))
                {
                    hresp.HttpVersion = p[0];
                    hresp.StatusCode = code;
                    hresp.Status = p.Length > 2 ? p[2] : HttpResponse.ReasonOf(code);
                }
            }
        }
    }
}
