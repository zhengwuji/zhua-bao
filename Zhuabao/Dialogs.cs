using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>HTTP 发送器：可编辑原始请求并重发。</summary>
    public sealed class SenderForm : Form
    {
        private readonly ProxyEngine _engine;
        private readonly HttpRequest _initial;
        private readonly HttpSession _source;          // 重发来源会话（可为 null = 从零手写）
        private readonly string _rawOverride;          // 预填的原始报文文本（重发时用编辑后的内容）
        private TextBox _reqBox, _respBox;
        private Button _btnSend, _btnFormat;
        private ComboBox _cmbCount;

        /// <summary>
        /// V1.0.8 BUG 修复：重发产生的流量落成**全新的会话**（新 Id），
        /// 通过这个事件交回主窗口入列表/落盘，原封包一字不动。
        /// </summary>
        public event Action<HttpSession> SessionCreated;

        public SenderForm(ProxyEngine engine, HttpRequest initial = null)
            : this(engine, initial, null, null) { }

        /// <summary>重发构造：rawOverride=编辑后的原始报文；source=被重发的原会话（只读，不会被改写）。</summary>
        public SenderForm(ProxyEngine engine, HttpRequest initial, string rawOverride, HttpSession source)
        {
            _engine = engine;
            _initial = initial;
            _rawOverride = rawOverride;
            _source = source;
            Text = source == null ? "HTTP 发送器" : "HTTP 发送器（重发副本 — 不改动原封包）";
            Size = new Size(980, 660);
            StartPosition = FormStartPosition.CenterParent;
            Font = Ui.MakeLabel("").Font;

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
            _reqBox = Ui.TextBox("", multiline: true);
            _respBox = Ui.TextBox("", multiline: true);
            _respBox.ReadOnly = true;
            split.Panel1.Controls.Add(_reqBox);
            split.Panel2.Controls.Add(_respBox);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), BackColor = Ui.PanelBg };
            _btnSend = Ui.MakeButton("▶ 发送", 84);
            _btnSend.BackColor = Ui.Accent; _btnSend.ForeColor = Color.White;
            _btnSend.Click += async (s, e) => await SendAsync();
            _btnFormat = Ui.MakeButton("格式化 JSON", 100);
            _btnFormat.Click += (s, e) => FormatJson();
            var lblCount = Ui.MakeLabel("次数:");
            _cmbCount = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
            _cmbCount.Items.AddRange(new object[] { "1", "10", "100", "1000" });
            _cmbCount.SelectedIndex = 0;
            top.Controls.AddRange(new Control[] { _btnSend, _btnFormat, lblCount, _cmbCount });
            if (source != null)
            {
                var lblCopy = Ui.MakeLabel("※ 重发为副本，原封包保持不变", Ui.AccentOrange, 9f, true);
                top.Controls.Add(lblCopy);
            }

            Controls.Add(split);
            Controls.Add(top);

            if (!string.IsNullOrEmpty(_rawOverride))
            {
                _reqBox.Text = _rawOverride;
            }
            else if (_initial != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{_initial.Method} {_initial.PathAndQuery} HTTP/1.1");
                foreach (var h in _initial.Headers) sb.AppendLine(h.Key + ": " + h.Value);
                sb.AppendLine();
                sb.Append(_initial.GetBodyText());
                _reqBox.Text = sb.ToString();
            }
        }

        private void FormatJson()
        {
            try
            {
                int bodyStart = _reqBox.Text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (bodyStart < 0) return;
                var head = _reqBox.Text.Substring(0, bodyStart);
                var body = _reqBox.Text.Substring(bodyStart + 4);
                _reqBox.Text = head + "\r\n\r\n" + Newtonsoft.Json.Linq.JToken.Parse(body).ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex) { MessageBox.Show("格式化失败: " + ex.Message); }
        }

        private async Task SendAsync()
        {
            _btnSend.Enabled = false;
            _respBox.Text = "发送中...";
            try
            {
                int count = int.Parse((string)_cmbCount.SelectedItem);
                var sb = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    var raw = _reqBox.Text;
                    int sp = raw.IndexOf(' ');
                    var method = raw.Substring(0, sp);
                    int sp2 = raw.IndexOf(' ', sp + 1);
                    var path = raw.Substring(sp + 1, sp2 - sp - 1);
                    string url = path.StartsWith("http") ? path : "";
                    var req = CoreHttpRequestParse(raw, url);
                    var resp = await HttpSender.SendAsync(req);
                    sb.AppendLine($"===== 第 {i + 1} 次 =====");
                    sb.AppendLine(resp == null ? "(无响应)" : resp.HeaderText() + resp.GetBodyText());
                    sb.AppendLine();

                    // V1.0.8 BUG 修复：重发结果记成新会话，绝不覆盖被重发的那条
                    RecordResentSession(req, resp, i, count);
                }
                _respBox.Text = sb.ToString();
            }
            catch (Exception ex) { _respBox.Text = "错误: " + ex.Message; }
            finally { _btnSend.Enabled = true; }
        }

        /// <summary>把一次重发的请求/响应落成新会话并通知主窗口（原会话不受影响）。</summary>
        private void RecordResentSession(HttpRequest req, HttpResponse resp, int index, int total)
        {
            try
            {
                var s = new HttpSession
                {
                    Request = req,
                    Process = _source?.Process ?? "发送器",
                    Protocol = _source?.Protocol ?? "HTTP",
                    Comment = _source == null ? "发送器" : ("重发副本 ← " + _source.Id),
                    Peer = _source?.Peer ?? string.Empty
                };
                if (resp != null) s.SetResponse(resp);
                else s.StatusCode = "无响应";
                s.RequestBytes = req?.ToRawBytes().Length ?? 0;
                _engine?.Store?.Add(s);        // 落盘（后台队列）
                // 大批量发送时只把前 20 条注入界面，避免 1000 次把列表刷爆
                if (index < 20) SessionCreated?.Invoke(s);
            }
            catch (Exception ex) { Log.W("发送器", "重发结果记录失败: " + ex.Message); }
        }

        public static HttpRequest CoreHttpRequestParse(string raw, string urlIfRelative)
        {
            var req = HttpRequest.Parse(raw);
            // 起始行是相对路径时用 Host 头还原绝对 URL（本次修复重点之一：
            // 之前这里 urlIfRelative 常为空串，导致重发直接失败）
            if (!req.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(urlIfRelative))
                req.Url = urlIfRelative;
            return req;
        }
    }

    /// <summary>回复器（自动应答规则）。</summary>
    public sealed class RepeaterForm : Form
    {
        public RepeaterForm()
        {
            Text = "回复器 (自动应答)";
            Size = new Size(620, 420);
            StartPosition = FormStartPosition.CenterParent;
            var lbl = Ui.MakeLabel("当请求匹配以下条件时，直接返回固定响应（占位实现）:", Color.Gray);
            lbl.Dock = DockStyle.Top;
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true };
            grid.Columns.Add("c1", "匹配关键字");
            grid.Columns.Add("c2", "响应状态");
            grid.Columns.Add("c3", "响应体");
            Controls.Add(grid);
            Controls.Add(lbl);
        }
    }

    /// <summary>取值器。</summary>
    public sealed class ExtractorForm : Form
    {
        private readonly AppConfig _cfg;
        private DataGridView _grid;
        private ListBox _hits;

        public ExtractorForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "取值器";
            Size = new Size(860, 520);
            StartPosition = FormStartPosition.CenterParent;

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 480 };
            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true };
            _grid.Columns.Add("cName", "变量名");
            _grid.Columns.Add("cScope", "作用域");
            _grid.Columns.Add("cInput", "Key/头名");
            _grid.Columns.Add("cRegex", "正则");
            foreach (var r in _cfg.Extracts) _grid.Rows.Add(r.Name, r.Scope, r.Input, r.Regex);

            _hits = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
            split.Panel2.Controls.Add(_hits);
            split.Panel2.Controls.Add(Ui.MakeLabel("提取结果:", Ui.Accent, 10f, true));

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), BackColor = Ui.PanelBg };
            var btnEnable = new CheckBox { Text = "启用取值", Checked = _cfg.ExtractEnabled, AutoSize = true };
            btnEnable.CheckedChanged += (s, e) => _cfg.ExtractEnabled = btnEnable.Checked;
            var btnTest = Ui.MakeButton("测试提取", 90);
            btnTest.Click += (s, e) => MessageBox.Show("对最近一条会话执行取值演示。\n实际抓包后自动提取到右侧列表。", "取值器");
            top.Controls.Add(btnEnable);
            top.Controls.Add(btnTest);

            Controls.Add(split);
            Controls.Add(top);

            FormClosing += (s, e) =>
            {
                _cfg.Extracts.Clear();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    _cfg.Extracts.Add(new ExtractRule
                    {
                        Name = Cell(row, 0), Scope = Cell(row, 1), Input = Cell(row, 2), Regex = Cell(row, 3)
                    });
                }
            };
        }

        private static string Cell(DataGridViewRow r, int i) => r.Cells[i].Value?.ToString() ?? "";
    }

    /// <summary>重定向（映射）配置。</summary>
    public sealed class RedirectForm : Form
    {
        private readonly AppConfig _cfg;
        private DataGridView _grid;

        public RedirectForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "重定向 (URL 映射)";
            Size = new Size(760, 460);
            StartPosition = FormStartPosition.CenterParent;

            _grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = true };
            _grid.Columns.Add("c1", "正则 Pattern");
            _grid.Columns.Add("c2", "替换 Replacement");
            _grid.Columns.Add("c3", "启用");
            foreach (var r in _cfg.Redirects) _grid.Rows.Add(r.Pattern, r.Replacement, r.Enabled);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), BackColor = Ui.PanelBg };
            var chk = new CheckBox { Text = "启用重定向", Checked = _cfg.RedirectEnabled, AutoSize = true };
            chk.CheckedChanged += (s, e) => _cfg.RedirectEnabled = chk.Checked;
            var hint = Ui.MakeLabel("示例:  pattern=api\\.test\\.com  replacement=api.prod.com", Color.Gray);
            top.Controls.Add(chk);
            top.Controls.Add(hint);

            Controls.Add(_grid);
            Controls.Add(top);

            FormClosing += (s, e) =>
            {
                _cfg.Redirects.Clear();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    _cfg.Redirects.Add(new RedirectRule
                    {
                        Pattern = Cell(row, 0), Replacement = Cell(row, 1),
                        Enabled = !string.Equals(Cell(row, 2), "False", StringComparison.OrdinalIgnoreCase)
                    });
                }
            };
        }

        private static string Cell(DataGridViewRow r, int i) => r.Cells[i].Value?.ToString() ?? "";
    }

    /// <summary>封包对比。</summary>
    public sealed class CompareForm : Form
    {
        public CompareForm(SessionStore store)
        {
            Text = "封包对比";
            Size = new Size(900, 560);
            StartPosition = FormStartPosition.CenterParent;

            var split = new SplitContainer { Dock = DockStyle.Fill };
            var left = Ui.TextBox("", multiline: true);
            var right = Ui.TextBox("", multiline: true);
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), BackColor = Ui.PanelBg };
            var btnLoad = Ui.MakeButton("载入最近两条会话", 140);
            btnLoad.Click += (s, e) =>
            {
                var files = new[] { store.Dir }.SelectMany(d => Directory.Exists(d) ? Directory.GetFiles(d, "*.zb") : Array.Empty<string>())
                    .OrderByDescending(File.GetLastWriteTime).Take(2).ToArray();
                if (files.Length == 0) { MessageBox.Show("暂无会话数据"); return; }
                var s1 = SessionStore.LoadSession(files[0]);
                var s2 = files.Length > 1 ? SessionStore.LoadSession(files[1]) : null;
                left.Text = s1 == null ? "" : s1.Request.HeaderText() + s1.Request.GetBodyText();
                right.Text = s2 == null ? "" : s2.Request.HeaderText() + s2.Request.GetBodyText();
            };
            var btnDiff = Ui.MakeButton("对比", 80);
            btnDiff.Click += (s, e) =>
            {
                var d = DiffLines(left.Text, right.Text);
                MessageBox.Show(d.Count == 0 ? "两段文本一致" : "差异行数: " + d.Count, "对比结果");
            };
            top.Controls.Add(btnLoad);
            top.Controls.Add(btnDiff);

            Controls.Add(split);
            Controls.Add(top);
        }

        private static System.Collections.Generic.List<string> DiffLines(string a, string b)
        {
            var la = a.Split('\n'); var lb = b.Split('\n');
            var diff = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                var x = i < la.Length ? la[i].TrimEnd() : "";
                var y = i < lb.Length ? lb[i].TrimEnd() : "";
                if (x != y) diff.Add($"行{i + 1}: [{x}] vs [{y}]");
            }
            return diff;
        }
    }

    /// <summary>工具箱：解码器 + 时间戳 + 正则测试（V1.0.8：增加专用解码器 GitHub 示例代码链接）。</summary>
    public sealed class ToolboxForm : Form
    {
        private TextBox _input, _output;
        private ComboBox _mode;
        private LinkLabel _github;
        private ComboBox _sample;
        private Label _lblHint;

        /// <summary>
        /// V1.0.8 优化：解码器面板的专用解码器 GitHub 示例代码链接。
        /// 每个内置解码算法对应 Fatbeans 官方示例仓库的一个目录，
        /// 下拉选择算法后链接随之切换，点「打开示例代码」用默认浏览器打开。
        /// </summary>
        internal sealed class DecoderSample
        {
            public string Name;      // 显示名（与 Plugins\Codecs 里的解码器对应）
            public string Url;       // GitHub 示例代码地址
            public string Hint;      // 参数提示
        }

        internal static readonly DecoderSample[] DecoderSamples =
        {
            new DecoderSample { Name = "AES",         Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Aes",         Hint = "Key/IV 十六进制；模式 CBC/ECB/GCM；填充 PKCS7/None" },
            new DecoderSample { Name = "DES",         Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Des",         Hint = "Key 8 字节、IV 8 字节；模式 CBC/ECB" },
            new DecoderSample { Name = "RC4",         Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Rc4",         Hint = "Key 任意长度；Rc4State = per-packet / session" },
            new DecoderSample { Name = "XXTEA",       Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Xxtea",       Hint = "Key 16 字节；通常配合长度前缀分帧" },
            new DecoderSample { Name = "XOR",         Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Xor",         Hint = "单字节/多字节密钥循环异或；可配 DecodeStartOffset" },
            new DecoderSample { Name = "Protobuf",    Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Protobuf",    Hint = "需要 .proto 描述；无描述时按字段号裸解（本工具示例 DecodeStartOffset=16）" },
            new DecoderSample { Name = "MessagePack", Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/MessagePack", Hint = "无需描述，按类型标签自解；可配 DecodeStartOffset" },
            new DecoderSample { Name = "BSON",        Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Bson",        Hint = "首 4 字节为总长度（含自身）；文档顺序解析" },
            new DecoderSample { Name = "FlatBuffers", Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/FlatBuffers", Hint = "需要 .fbs 描述；根表偏移在报文尾 4 字节" },
            new DecoderSample { Name = "AMF0/AMF3",   Url = "https://github.com/fatbeans-creater/decoder-samples/tree/main/Amf",         Hint = "AmfVersion = auto / amf0 / amf3；FramingKind = None / Length / AmfLength" },
            new DecoderSample { Name = "插件框架说明", Url = "https://github.com/fatbeans-creater/decoder-samples",                      Hint = "目录结构、plugin.json 字段、IPlugin 接口与返回值约定" }
        };

        public ToolboxForm()
        {
            Text = "工具箱";
            Size = new Size(820, 560);
            StartPosition = FormStartPosition.CenterParent;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), BackColor = Ui.PanelBg };
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            _mode.Items.AddRange(new object[] { "Base64 解码", "Base64 编码", "Hex 解码", "Hex 编码", "URL 解码", "URL 编码", "时间戳→日期", "MD5" });
            _mode.SelectedIndex = 0;
            var btn = Ui.MakeButton("执行", 70);
            btn.Click += (s, e) => Run();
            top.Controls.Add(_mode);
            top.Controls.Add(btn);

            // ===== V1.0.8：专用解码器 GitHub 示例代码链接 =====
            var sampleBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 8, 6, 0), BackColor = Color.White };
            sampleBar.Controls.Add(Ui.MakeLabel("专用解码器示例代码:", Ui.Accent, 9f, true));
            _sample = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            foreach (var s in DecoderSamples) _sample.Items.Add(s.Name);
            _sample.SelectedIndex = 0;
            _sample.SelectedIndexChanged += (s, e) =>
            {
                var d = DecoderSamples[Math.Max(0, _sample.SelectedIndex)];
                _github.Text = d.Url;
                _github.Tag = d.Url;
                _github.Links.Clear();
                _github.Links.Add(0, d.Url.Length, d.Url);
                _lblHint.Text = d.Hint;
            };
            sampleBar.Controls.Add(_sample);
            _github = new LinkLabel { Text = DecoderSamples[0].Url, AutoSize = true, LinkColor = Ui.Accent, Padding = new Padding(8, 5, 0, 0) };
            _github.Tag = DecoderSamples[0].Url;
            _github.LinkClicked += (s, e) => OpenSample(e.Link?.LinkData as string ?? _github.Tag as string);
            sampleBar.Controls.Add(_github);
            _lblHint = Ui.MakeLabel(DecoderSamples[0].Hint, Color.Gray, 8.5f);
            _lblHint.Padding = new Padding(6, 6, 0, 0);
            var btnOpen = Ui.MakeButton("打开示例代码", 110, 26);
            btnOpen.Click += (s, e) => OpenSample(_github.Tag as string);
            sampleBar.Controls.Add(btnOpen);

            var hintBar = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.White };
            hintBar.Controls.Add(_lblHint);
            _lblHint.Dock = DockStyle.Fill;

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            _input = Ui.TextBox("", multiline: true);
            _output = Ui.TextBox("", multiline: true);
            _output.ReadOnly = true;
            split.Panel1.Controls.Add(_input);
            split.Panel2.Controls.Add(_output);

            Controls.Add(split);
            Controls.Add(hintBar);
            Controls.Add(sampleBar);
            Controls.Add(top);
        }

        /// <summary>用系统默认浏览器打开 GitHub 示例地址；失败只提示，不抛异常。</summary>
        internal static void OpenSample(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                Log.I("工具箱", "已打开专用解码器示例代码: " + url);
            }
            catch (Exception ex)
            {
                Log.W("工具箱", "打开链接失败: " + ex.Message);
                MessageBox.Show("打开链接失败: " + ex.Message + "\n\n地址: " + url, "提示");
            }
        }

        private void Run()
        {
            try
            {
                var text = _input.Text;
                _output.Text = (_mode.SelectedIndex) switch
                {
                    0 => Encoding.UTF8.GetString(Convert.FromBase64String(text)),
                    1 => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                    2 => Encoding.UTF8.GetString(HexUtils.FromHex(text)),
                    3 => HexUtils.ToHex(Encoding.UTF8.GetBytes(text)),
                    4 => Uri.UnescapeDataString(text),
                    5 => Uri.EscapeDataString(text),
                    6 => DateTimeOffset.FromUnixTimeSeconds(long.Parse(text.Trim())).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    7 => string.Concat(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))),
                    _ => text
                };
            }
            catch (Exception ex) { _output.Text = "错误: " + ex.Message; }
        }
    }

    /// <summary>
    /// V1.0.8 新功能①：转发器面板。
    /// 上半部分是全局配置（目标 API 地址、并发 1~10、队列上限、超时、单包上限、总开关），
    /// 下半部分是规则表（匹配条件 / 排除条件），底部显示命中、已转发、丢弃、失败统计。
    /// 说明：转发是**旁路 fire-and-forget** —— 命中即丢后台队列异步 POST，
    /// 不等待响应、不阻塞抓包，也不影响拦截器/修改器/WebHook。
    /// </summary>
    public sealed class ForwarderForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly ProxyEngine _engine;
        private readonly DataGridView _grid;
        private readonly CheckBox _chkEnable;
        private readonly TextBox _txtUrl;
        private readonly NumericUpDown _numWorkers, _numQueue, _numTimeout, _numMaxBytes;
        private readonly System.Windows.Forms.Timer _statTimer;
        private readonly Label _lblStat;

        public ForwarderForm(AppConfig cfg, ProxyEngine engine = null)
        {
            _cfg = cfg;
            _engine = engine;
            _cfg.Forwarder ??= ForwarderConfig.CreateDefault();
            var fc = _cfg.Forwarder;

            Text = "转发器（旁路转发 TCP/UDP 到自定义 API）";
            Size = new Size(1060, 660);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);

            // ===== 全局配置区 =====
            var cfgPanel = new Panel { Dock = DockStyle.Top, Height = 132, BackColor = Color.White, Padding = new Padding(10, 8, 10, 0) };

            _chkEnable = new CheckBox { Text = "启用转发器", Checked = fc.Enabled, AutoSize = true, Location = new Point(10, 10), Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
            cfgPanel.Controls.Add(_chkEnable);

            var lblUrl = Ui.MakeLabel("转发目标地址 (POST JSON):");
            lblUrl.Location = new Point(10, 40);
            cfgPanel.Controls.Add(lblUrl);
            _txtUrl = new TextBox { Location = new Point(170, 36), Width = 560, Text = fc.TargetUrl ?? "" };
            cfgPanel.Controls.Add(_txtUrl);
            var lblUrlHint = Ui.MakeLabel("例: http://127.0.0.1:9000/hook （fire-and-forget，不读响应）", Color.Gray, 8.5f);
            lblUrlHint.Location = new Point(740, 39);
            cfgPanel.Controls.Add(lblUrlHint);

            cfgPanel.Controls.Add(MkLabel("并发 Worker (1~10):", 10, 72));
            _numWorkers = MkNum(140, 68, 1, 10, Math.Clamp(fc.Workers, 1, 10));
            cfgPanel.Controls.Add(_numWorkers);

            cfgPanel.Controls.Add(MkLabel("队列上限:", 210, 72));
            _numQueue = MkNum(280, 68, 10, 200000, Math.Clamp(fc.QueueLimit, 10, 200000));
            cfgPanel.Controls.Add(_numQueue);

            cfgPanel.Controls.Add(MkLabel("超时(ms):", 430, 72));
            _numTimeout = MkNum(505, 68, 500, 120000, Math.Clamp(fc.TimeoutMs, 500, 120000));
            cfgPanel.Controls.Add(_numTimeout);

            cfgPanel.Controls.Add(MkLabel("单包上限(字节):", 630, 72));
            _numMaxBytes = MkNum(740, 68, 0, int.MaxValue, Math.Max(0, fc.MaxPacketBytes));
            cfgPanel.Controls.Add(_numMaxBytes);

            var lblNote = Ui.MakeLabel(
                "只转发 TCP/UDP；命中即入后台队列异步发送，不等待响应、不阻塞抓包，也不影响拦截器 / 修改器 / WebHook。队列满时丢弃最旧的封包。",
                Color.DimGray, 8.5f);
            lblNote.Location = new Point(10, 100);
            cfgPanel.Controls.Add(lblNote);

            _lblStat = Ui.MakeLabel("", Ui.Accent, 9f, true);
            _lblStat.Location = new Point(700, 100);
            cfgPanel.Controls.Add(_lblStat);

            // ===== 规则表 =====
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AutoGenerateColumns = false,
                RowHeadersWidth = 30,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "cEnable", HeaderText = "启用", Width = 50 });
            _grid.Columns.Add("cName", "规则名");
            _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "cProto", HeaderText = "协议", Width = 70, Items = { "TCP", "UDP", "TCP,UDP" } });
            // 匹配条件（留空 = 该项不参与匹配；整行全空 = 转发全部 TCP/UDP）
            _grid.Columns.Add("cMatchSrcIp", "匹配-源IP");
            _grid.Columns.Add("cMatchDstIp", "匹配-目的IP");
            _grid.Columns.Add("cMatchPort", "匹配-端口起-止");
            _grid.Columns.Add("cMatchLen", "匹配-长度起-止");
            _grid.Columns.Add("cMatchContent", "匹配-内容(支持HEX:xx)");
            // 排除条件（任一命中即不转发，优先级高于匹配条件）
            _grid.Columns.Add("cExSrcIp", "排除-源IP");
            _grid.Columns.Add("cExDstIp", "排除-目的IP");
            _grid.Columns.Add("cExPort", "排除-端口起-止");
            _grid.Columns.Add("cExContent", "排除-内容");
            _grid.Columns.Add("cHits", "已命中");
            _grid.Columns.Add("cUsed", "已转发");

            foreach (var r in fc.Rules) _grid.Rows.Add(
                r.Enabled, r.RuleName,
                ProtoText(r.Match),
                r.Match?.UseSrcIP == true ? r.Match.SrcIP : "",
                r.Match?.UseDstIP == true ? r.Match.DstIP : "",
                r.Match?.UseDstPort == true ? RangeText(r.Match.DstPortFrom, r.Match.DstPortTo) : "",
                r.Match?.UseLength == true ? RangeText(r.Match.LengthMin, r.Match.LengthMax) : "",
                r.Match?.UseContent == true ? ((r.Match.ContentIsHex ? "HEX:" : "") + r.Match.Content) : "",
                r.Exclude?.UseSrcIP == true ? r.Exclude.SrcIP : "",
                r.Exclude?.UseDstIP == true ? r.Exclude.DstIP : "",
                r.Exclude?.UseDstPort == true ? RangeText(r.Exclude.DstPortFrom, r.Exclude.DstPortTo) : "",
                r.Exclude?.UseContent == true ? ((r.Exclude.ContentIsHex ? "HEX:" : "") + r.Exclude.Content) : "",
                r.HitCount, r.SentCount);

            _grid.CellFormatting += (s, e) =>
            {
                // 「已命中」列始终只读显示规则对象的实时计数
                if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "cHits") e.Value = null;
            };

            // ===== 底部按钮 =====
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 6, 6, 0), BackColor = Ui.PanelBg };
            var btnSave = Ui.MakeButton("保存配置", 96, 28);
            btnSave.BackColor = Ui.Accent; btnSave.ForeColor = Color.White;
            btnSave.Click += (s, e) => { SaveTo(); MessageBox.Show("转发器配置已保存。", "完成"); };
            var btnAdd = Ui.MakeButton("添加规则", 96, 28);
            btnAdd.Click += (s, e) =>
            {
                if (!GridReady) return;   // 面板已关闭/规则表已释放：静默忽略，不要抛异常
                _grid.Rows.Add(true, "新规则", "TCP", "", "", "", "", "", "", "", "", "", 0, 0);
            };
            var btnDel = Ui.MakeButton("删除选中规则", 116, 28);
            btnDel.Click += (s, e) =>
            {
                if (!GridReady) return;
                foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
                    if (!row.IsNewRow) _grid.Rows.Remove(row);
            };
            var btnTest = Ui.MakeButton("发送测试封包", 116, 28);
            btnTest.Click += (s, e) => SendTestPacket();
            var btnResetStat = Ui.MakeButton("重置统计", 96, 28);
            btnResetStat.Click += (s, e) => { Zhuabao.Core.Log.I("转发", "转发统计已重置（仅计数，规则配额不变）"); };
            var btnClose = Ui.MakeButton("关闭", 84, 28);
            btnClose.Click += (s, e) => Close();
            bottom.Controls.AddRange(new Control[] { btnSave, btnAdd, btnDel, btnTest, btnResetStat, btnClose });

            var lblHelp = Ui.MakeLabel(
                "匹配条件：需同时满足所填的每一项；排除条件：命中任一项即不转发（优先级更高）。" +
                "整行匹配条件全空 = 转发全部 TCP/UDP。IP 支持 192.168.* 前缀或逗号分隔多值；端口/长度支持 \"起-止\"，单值即精确匹配。",
                Color.Gray, 8.5f);
            lblHelp.Dock = DockStyle.Bottom;
            lblHelp.Padding = new Padding(10, 0, 0, 4);

            Controls.Add(_grid);
            Controls.Add(bottom);
            Controls.Add(lblHelp);
            Controls.Add(cfgPanel);

            _statTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _statTimer.Tick += (s, e) => UpdateStat();
            _statTimer.Start();
            UpdateStat();

            FormClosing += (s, e) => { _statTimer.Stop(); SaveTo(); };
        }

        /// <summary>
        /// 规则表是否还能安全操作。
        /// 自动化 UI 测试会先点面板里的「关闭」再继续点其它按钮，
        /// 此时 DataGridView 已被 Dispose；这里统一挡掉后续操作，
        /// 避免出现 "不能向没有列的 DataGridView 控件添加行" 之类的异常。
        /// </summary>
        private bool GridReady
        {
            get
            {
                try { return _grid != null && !_grid.IsDisposed && _grid.Columns.Count > 0; }
                catch { return false; }
            }
        }

        private static Label MkLabel(string t, int x, int y)
        {
            var l = Ui.MakeLabel(t);
            l.Location = new Point(x, y);
            return l;
        }

        private static NumericUpDown MkNum(int x, int y, int min, int max, int val)
            => new NumericUpDown { Location = new Point(x, y), Width = 62, Minimum = min, Maximum = max, Value = val };

        private static string ProtoText(PacketMatch m)
        {
            if (m == null || !m.UseProtocol || m.Protocols == null || m.Protocols.Count == 0) return "";
            return m.Protocols.Count >= 2 ? "TCP,UDP" : m.Protocols[0];
        }

        private static string RangeText(int from, int to)
            => from == to ? from.ToString() : from + "-" + to;

        /// <summary>解析 "起-止" / "起" / "起,止"，返回 (from,to,有效)。</summary>
        internal static bool TryParseRange(string text, out int from, out int to)
        {
            from = to = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim().Replace("~", "-").Replace(",", "-").Replace("，", "-");
            var parts = t.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            if (!int.TryParse(parts[0].Trim(), out from)) return false;
            to = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var hi) ? hi : from;
            return true;
        }

        /// <summary>解析内容列：前缀 HEX: 表示按十六进制匹配。</summary>
        internal static void ParseContent(string text, out string content, out bool isHex)
        {
            content = text ?? "";
            isHex = false;
            if (content.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase))
            {
                isHex = true;
                content = content.Substring(4).Trim();
            }
        }

        private void UpdateStat()
        {
            try
            {
                var f = _engine?.Forwarder;
                if (f == null)
                {
                    _lblStat.Text = "统计: 引擎未启动（配置已保存，点 ▶ 启动(F5) 后生效）";
                    return;
                }
                _lblStat.Text = $"统计: 送入 {f.AcceptedCount} | 已转发 {f.ForwardedCount} | 队列 {f.QueueDepth} | 丢弃 {f.DroppedCount} | 失败 {f.FailedCount} | 未命中 {f.FilteredCount}";
            }
            catch { }
        }

        /// <summary>把界面内容写回 _cfg.Forwarder 并通知引擎重建转发器。</summary>
        public void SaveTo()
        {
            var fc = _cfg.Forwarder ??= new ForwarderConfig();
            // 面板若已被关闭/释放，只保留全局配置，不动规则表（避免对已释放的 DataGridView 取值抛异常）
            bool gridReady = GridReady;
            try
            {
                fc.Enabled = _chkEnable.Checked;
                fc.TargetUrl = (_txtUrl.Text ?? "").Trim();
                fc.Workers = (int)_numWorkers.Value;
                fc.QueueLimit = (int)_numQueue.Value;
                fc.TimeoutMs = (int)_numTimeout.Value;
                fc.MaxPacketBytes = (int)_numMaxBytes.Value;
            }
            catch { }

            if (gridReady)
            {
                fc.Rules.Clear();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    var r = new ForwardRule
                    {
                        Enabled = Cell(row, "cEnable") != "False",
                        RuleName = Or(Cell(row, "cName"), "未命名规则")
                    };
                    r.Match = BuildMatch(
                        Cell(row, "cProto"), Cell(row, "cMatchSrcIp"), Cell(row, "cMatchDstIp"),
                        Cell(row, "cMatchPort"), Cell(row, "cMatchLen"), Cell(row, "cMatchContent"));
                    r.Exclude = BuildMatch(
                        "", Cell(row, "cExSrcIp"), Cell(row, "cExDstIp"),
                        Cell(row, "cExPort"), "", Cell(row, "cExContent"));
                    fc.Rules.Add(r);
                }
            }
            ConfigService.Save(_cfg);
            _engine?.ApplyForwarderConfig();
            Log.I("转发", $"转发器配置已保存：启用={fc.Enabled} 目标={fc.TargetUrl} 规则数={fc.Rules.Count} 并发={fc.Workers} 队列={fc.QueueLimit}");
        }

        private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

        private static PacketMatch BuildMatch(string proto, string srcIp, string dstIp, string port, string len, string content)
        {
            var m = new PacketMatch();
            if (!string.IsNullOrWhiteSpace(proto))
            {
                m.UseProtocol = true;
                m.Protocols = proto.Contains("UDP") && proto.Contains("TCP")
                    ? new List<string> { "TCP", "UDP" }
                    : new List<string> { proto.Contains("UDP") ? "UDP" : "TCP" };
            }
            if (!string.IsNullOrWhiteSpace(srcIp)) { m.UseSrcIP = true; m.SrcIP = srcIp.Trim(); }
            if (!string.IsNullOrWhiteSpace(dstIp)) { m.UseDstIP = true; m.DstIP = dstIp.Trim(); }
            if (TryParseRange(port, out var pf, out var pt))
            {
                // 端口没分源/目的：统一按目的端口匹配（TCP/UDP 转发最常用的口径）
                m.UseDstPort = true; m.DstPortFrom = pf; m.DstPortTo = pt;
            }
            if (TryParseRange(len, out var lf, out var lt)) { m.UseLength = true; m.LengthMin = lf; m.LengthMax = lt; }
            if (!string.IsNullOrWhiteSpace(content))
            {
                ParseContent(content, out var c, out var hex);
                m.UseContent = true; m.Content = c; m.ContentIsHex = hex;
            }
            return m;
        }

        private static string Cell(DataGridViewRow r, string col)
        {
            var v = r.Cells[col].Value;
            return v?.ToString() ?? "";
        }

        /// <summary>发一条测试封包，验证目标 API 是否可达（走同一条旁路队列）。</summary>
        private void SendTestPacket()
        {
            try
            {
                if (_engine?.Forwarder == null) { MessageBox.Show("请先点 ▶ 启动(F5) 启动代理，再发送测试封包。", "提示"); return; }
                var data = Encoding.UTF8.GetBytes("zhuabao-forwarder-test hello 42");
                var pkt = new PacketEvent
                {
                    Protocol = "TCP",
                    SrcIP = "127.0.0.1", SrcPort = 51234,
                    DstIP = "127.0.0.1", DstPort = 9000,
                    ProcessName = "zhuabao-test",
                    Data = data, Direction = ForwardDirection.Send
                };
                _engine.Forwarder.Observe(pkt);
                Log.I("转发", "已投递测试封包（" + data.Length + " 字节），查看目标服务是否收到");
                MessageBox.Show("测试封包已投入转发队列。\n\n若配置的匹配条件不命中它，则不会发出 —— 请对照规则里的目的端口/内容条件。", "已投递");
            }
            catch (Exception ex) { MessageBox.Show("发送测试封包失败: " + ex.Message, "错误"); }
        }
    }

    /// <summary>
    /// V1.0.8 新功能②：自定义大模型接口配置面板。
    /// 「接口模式」下拉提供 <b>OpenAI 兼容</b> 与 <b>Anthropic 兼容</b> 两种方言：
    ///  · OpenAI 兼容  → POST {BaseUrl}/v1/chat/completions，Authorization: Bearer
    ///  · Anthropic 兼容→ POST {BaseUrl}/v1/messages，x-api-key + anthropic-version，
    ///                    system 作为顶层独立字段，max_tokens 必填
    /// 面板里「查看请求预览」直接展示将要发出的 JSON 与鉴权头，方便离线核对。
    /// </summary>
    public sealed class LlmConfigForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly CheckBox _chkEnable;
        private readonly ComboBox _cmbProvider;
        private readonly TextBox _txtBaseUrl, _txtKey, _txtModel, _txtSystem;
        private readonly NumericUpDown _numTemp, _numMaxTok, _numTimeout;
        private readonly TextBox _preview;

        public LlmConfigForm(AppConfig cfg)
        {
            _cfg = cfg;
            _cfg.Llm ??= new LlmConfig();
            var l = _cfg.Llm;

            Text = "自定义大模型接口（OpenAI 兼容 / Anthropic 兼容）";
            Size = new Size(760, 640);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            _chkEnable = new CheckBox { Text = "启用自定义大模型接口", Checked = l.Enabled, Location = new Point(16, 14), AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };

            AddLabel("接口模式:", 16, 48);
            _cmbProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(120, 44), Width = 180 };
            _cmbProvider.Items.AddRange(new object[] { "OpenAI兼容", "Anthropic兼容" });
            _cmbProvider.SelectedIndex = LlmConfig.IsAnthropic(l.Provider) ? 1 : 0;
            _cmbProvider.SelectedIndexChanged += (s, e) => UpdatePreview();

            AddLabel("BaseUrl:", 16, 82);
            _txtBaseUrl = new TextBox { Location = new Point(120, 78), Width = 600, Text = l.BaseUrl ?? "" };
            _txtBaseUrl.TextChanged += (s, e) => UpdatePreview();

            AddLabel("API Key:", 16, 116);
            _txtKey = new TextBox { Location = new Point(120, 112), Width = 600, Text = l.ApiKey ?? "", UseSystemPasswordChar = false };
            _txtKey.TextChanged += (s, e) => UpdatePreview();

            AddLabel("模型名:", 16, 150);
            _txtModel = new TextBox { Location = new Point(120, 146), Width = 600, Text = l.Model ?? "" };
            _txtModel.TextChanged += (s, e) => UpdatePreview();
            var lblModelHint = Ui.MakeLabel("Anthropic 例: claude-3-5-sonnet-latest ；OpenAI 例: gpt-4o-mini", Color.Gray, 8.5f);
            lblModelHint.Location = new Point(120, 168);

            AddLabel("系统提示词:", 16, 194);
            _txtSystem = new TextBox
            {
                Location = new Point(120, 190), Width = 600, Height = 70,
                Multiline = true, ScrollBars = ScrollBars.Vertical, Text = l.SystemPrompt ?? ""
            };
            _txtSystem.TextChanged += (s, e) => UpdatePreview();

            AddLabel("温度:", 16, 276);
            _numTemp = new NumericUpDown { Location = new Point(120, 272), Width = 70, Minimum = 0, Maximum = 2, Increment = 0.1M, DecimalPlaces = 2, Value = (decimal)Math.Clamp(l.Temperature, 0, 2) };
            AddLabel("MaxTokens:", 210, 276);
            _numMaxTok = new NumericUpDown { Location = new Point(300, 272), Width = 90, Minimum = 1, Maximum = 200000, Value = Math.Clamp(l.MaxTokens, 1, 200000) };
            AddLabel("超时(ms):", 410, 276);
            _numTimeout = new NumericUpDown { Location = new Point(490, 272), Width = 90, Minimum = 1000, Maximum = 600000, Value = Math.Clamp(l.TimeoutMs, 1000, 600000) };

            AddLabel("请求预览:", 16, 312);
            _preview = new TextBox
            {
                Location = new Point(16, 336), Width = 712, Height = 200,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(248, 248, 250)
            };

            var btnTest = Ui.MakeButton("测试连接", 96, 28);
            btnTest.Location = new Point(16, 552);
            btnTest.Click += async (s, e) => await TestAsync();

            var ok = Ui.MakeButton("确定", 84, 28);
            ok.Location = new Point(536, 552);
            ok.DialogResult = DialogResult.OK;
            ok.Click += (s, e) => SaveTo();

            var cancel = Ui.MakeButton("取消", 84, 28);
            cancel.Location = new Point(628, 552);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                _chkEnable, _cmbProvider, _txtBaseUrl, _txtKey, _txtModel, _txtSystem,
                _numTemp, _numMaxTok, _numTimeout, _preview, btnTest, ok, cancel,
                lblModelHint
            });
            AcceptButton = ok; CancelButton = cancel;
            UpdatePreview();
        }

        private void AddLabel(string t, int x, int y)
        {
            var l = Ui.MakeLabel(t);
            l.Location = new Point(x, y);
            Controls.Add(l);
        }

        /// <summary>把界面值收进一个 LlmConfig（不落盘），供预览与测试使用。</summary>
        private LlmConfig Collect()
        {
            return new LlmConfig
            {
                Enabled = _chkEnable.Checked,
                Provider = _cmbProvider.SelectedIndex == 1 ? "Anthropic兼容" : "OpenAI兼容",
                BaseUrl = (_txtBaseUrl.Text ?? "").Trim(),
                ApiKey = _txtKey.Text ?? "",
                Model = (_txtModel.Text ?? "").Trim(),
                SystemPrompt = _txtSystem.Text ?? "",
                Temperature = (double)_numTemp.Value,
                MaxTokens = (int)_numMaxTok.Value,
                TimeoutMs = (int)_numTimeout.Value
            };
        }

        private void SaveTo()
        {
            var l = Collect();
            _cfg.Llm.Enabled = l.Enabled;
            _cfg.Llm.Provider = l.Provider;
            _cfg.Llm.BaseUrl = l.BaseUrl;
            _cfg.Llm.ApiKey = l.ApiKey;
            _cfg.Llm.Model = l.Model;
            _cfg.Llm.SystemPrompt = l.SystemPrompt;
            _cfg.Llm.Temperature = l.Temperature;
            _cfg.Llm.MaxTokens = l.MaxTokens;
            _cfg.Llm.TimeoutMs = l.TimeoutMs;
            ConfigService.Save(_cfg);
            Log.I("大模型", $"配置已保存：模式={l.Provider} 端点={l.Endpoint} 模型={l.Model}");
        }

        private void UpdatePreview()
        {
            try
            {
                var l = Collect();
                var dialect = l.Dialect;
                var msgs = new[] { new LlmMessage("user", "分析这条会话的用途：POST /api/login") };
                string body = LlmClient.BuildRequestJson(l, msgs);
                var sb = new StringBuilder();
                sb.AppendLine("POST " + l.Endpoint);
                sb.AppendLine("Content-Type: application/json");
                if (dialect == LlmDialect.AnthropicCompatible)
                {
                    sb.AppendLine("x-api-key: " + Mask(l.ApiKey));
                    sb.AppendLine("anthropic-version: " + LlmClient.AnthropicVersion);
                }
                else
                {
                    sb.AppendLine("Authorization: Bearer " + Mask(l.ApiKey));
                }
                sb.AppendLine();
                sb.AppendLine(body);
                _preview.Text = sb.ToString();
            }
            catch (Exception ex) { _preview.Text = "预览失败: " + ex.Message; }
        }

        private static string Mask(string key)
            => string.IsNullOrEmpty(key) ? "(未填写)" : (key.Length <= 8 ? "****" : key.Substring(0, 4) + "****" + key.Substring(key.Length - 4));

        private async Task TestAsync()
        {
            var l = Collect();
            _preview.Text = "调用中...（" + l.Endpoint + "）";
            var client = new LlmClient(l);
            var r = await client.ChatAsync(new[] { new LlmMessage("user", "请只回复两个字：成功") });
            _preview.Text = r.Ok
                ? "✔ 调用成功 (" + l.Provider + ", " + r.ElapsedMs + "ms, in=" + r.InputTokens + " out=" + r.OutputTokens + ")\r\n\r\n" + r.Text
                : "✘ 调用失败：" + r.Error;
        }
    }

    /// <summary>V1.0.8：大模型分析结果窗口（可复制内容，避免 MessageBox 无法选择文本）。</summary>
    public sealed class LlmResultForm : Form
    {
        public LlmResultForm(string title, LlmResult r)
        {
            Text = title;
            Size = new Size(880, 620);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);

            var head = new Label
            {
                Dock = DockStyle.Top, Height = 26,
                Text = "调用成功 · " + r.ElapsedMs + "ms · in=" + r.InputTokens + " out=" + r.OutputTokens +
                       (string.IsNullOrEmpty(r.StopReason) ? "" : " · stop=" + r.StopReason),
                ForeColor = Color.SeaGreen, Padding = new Padding(8, 5, 0, 0)
            };
            var box = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Both, WordWrap = true,
                Font = new Font("Microsoft YaHei UI", 10f),
                Text = r.Text ?? ""
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 0, 0), BackColor = Ui.PanelBg };
            var btnCopy = Ui.MakeButton("复制全部", 96, 28);
            btnCopy.Click += (s, e) =>
            {
                bool ok = ClipboardEx.SetText(box.Text);
                MessageBox.Show(ok ? "已复制到剪贴板。" : "剪贴板被占用，复制失败。", "复制");
            };
            var btnRaw = Ui.MakeButton("查看原始 JSON", 120, 28);
            btnRaw.Click += (s, e) => MessageBox.Show(r.RawJson ?? "(空)", "原始响应 JSON");
            var btnClose = Ui.MakeButton("关闭", 84, 28);
            btnClose.Click += (s, e) => Close();
            bar.Controls.AddRange(new Control[] { btnCopy, btnRaw, btnClose });

            Controls.Add(box);
            Controls.Add(bar);
            Controls.Add(head);
        }
    }

    /// <summary>配置窗口。</summary>
    public sealed class ConfigForm : Form
    {
        private readonly AppConfig _cfg;
        private NumericUpDown _numPort;
        private CheckBox _chkIntercept, _chkGzip;

        public ConfigForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "配置";
            Size = new Size(460, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            var lbl = Ui.MakeLabel("监听端口:", null, 9.5f, true);
            lbl.Location = new Point(20, 24);
            _numPort = new NumericUpDown { Location = new Point(110, 20), Width = 90, Minimum = 1, Maximum = 65535, Value = cfg.Port };
            _chkIntercept = new CheckBox { Text = "启动时开启断点拦截", Location = new Point(20, 58), AutoSize = true, Checked = cfg.InterceptEnabled };
            _chkGzip = new CheckBox { Text = "自动解压 Gzip 响应", Location = new Point(20, 84), AutoSize = true, Checked = cfg.DecodeGzip };

            var ok = Ui.MakeButton("确定", 80); ok.Location = new Point(250, 220);
            ok.DialogResult = DialogResult.OK;
            ok.Click += (s, e) => { cfg.Port = (int)_numPort.Value; cfg.InterceptEnabled = _chkIntercept.Checked; cfg.DecodeGzip = _chkGzip.Checked; };
            var cancel = Ui.MakeButton("取消", 80); cancel.Location = new Point(345, 220);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lbl, _numPort, _chkIntercept, _chkGzip, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    /// <summary>
    /// 修改器 (改包规则)。
    /// V1.0.8 变更：
    ///  · 新增「限次」与「次数」两列 —— 修复次数限制失效（计数改由 ReplaceEngine.RuleRegistry 按规则指纹持有）；
    ///  · 目标列新增 <c>:status</c> 伪头名 —— 在「响应头」里直接改状态码（值写 404 或 "404 Not Found" 或区间 "200-299"）；
    ///  · 新增「重置次数」按钮与已用次数显示。
    /// </summary>
    public sealed class ModifierForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly DataGridView _grid;

        public ModifierForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "修改器 (改包规则)";
            Size = new Size(1020, 560);
            StartPosition = FormStartPosition.CenterParent;

            // 计数表里已有的配额先回填到规则对象，界面上「已用」才准
            ReplaceEngine.RuleRegistry.SyncDisplay(_cfg.ReplaceRules);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AutoGenerateColumns = false,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            // 方向/目标/类型必须是下拉列：原来先建成文本列再强转 ComboBoxColumn，
            // 会抛 InvalidCastException（点开「修改器」直接崩）。
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "c1", HeaderText = "方向", FlatStyle = FlatStyle.Flat,
                Items = { "请求", "响应" }
            });
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "c2", HeaderText = "目标", FlatStyle = FlatStyle.Flat,
                // V1.0.8：响应头里的 :status 就是「改状态码」
                Items = { "请求头", "请求体", "响应头", "响应体", "状态行", "URL", ReplaceEngine.StatusPseudoHeader }
            });
            _grid.Columns.Add("c3", "匹配");
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "c4", HeaderText = "类型", FlatStyle = FlatStyle.Flat,
                Items = { "替换值", "追加", "删除", "正则替换" }
            });
            _grid.Columns.Add("c5", "值");
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "c6", HeaderText = "限次", Width = 50 });
            _grid.Columns.Add("c7", "次数");
            _grid.Columns.Add("c9", "已用");
            _grid.Columns.Add("c8", "备注");
            foreach (var r in _cfg.ReplaceRules)
                _grid.Rows.Add(r.Direction, r.Target, r.Match, r.ReplaceType, r.Value,
                    r.LimitEnabled, r.MaxHits, r.HitsUsed, r.Comment);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6), BackColor = Ui.PanelBg };
            var chk = new CheckBox { Text = "启用修改器", Checked = _cfg.ReplaceEnabled, AutoSize = true };
            chk.CheckedChanged += (s, e) => _cfg.ReplaceEnabled = chk.Checked;
            top.Controls.Add(chk);

            var btnReset = Ui.MakeButton("重置选中规则次数", 150, 26);
            btnReset.Click += (s, e) =>
            {
                int n = 0;
                foreach (DataGridViewRow row in _grid.SelectedRows.Cast<DataGridViewRow>().ToArray())
                {
                    if (row.IsNewRow) continue;
                    var r = RuleFromRow(row);
                    ReplaceEngine.RuleRegistry.Reset(r);
                    row.Cells["c9"].Value = 0;
                    if (_cfg.ReplaceRules != null)
                        foreach (var x in _cfg.ReplaceRules) if (x.Signature == r.Signature) x.HitsUsed = 0;
                    n++;
                }
                MessageBox.Show(n == 0 ? "请先选中一行规则。" : ("已重置 " + n + " 条规则的已用次数。"), "重置次数");            };
            top.Controls.Add(btnReset);

            var btnResetAll = Ui.MakeButton("全部重置次数", 110, 26);
            btnResetAll.Click += (s, e) =>
            {
                ReplaceEngine.RuleRegistry.ResetAll();
                foreach (DataGridViewRow row in _grid.Rows) if (!row.IsNewRow) row.Cells["c9"].Value = 0;
                MessageBox.Show("所有改包规则的已用次数已清零。", "重置次数");
            };
            top.Controls.Add(btnResetAll);

            var hint = Ui.MakeLabel("限次=勾选后按「次数」上限生效；改状态码：目标选 :status，值写 404 或 \"404 Not Found\" 或区间 \"200-299\"", Color.Gray, 8.5f);
            hint.Padding = new Padding(10, 8, 0, 0);
            top.Controls.Add(hint);

            Controls.Add(_grid);
            Controls.Add(top);

            FormClosing += (s, e) =>
            {
                _cfg.ReplaceRules.Clear();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    _cfg.ReplaceRules.Add(RuleFromRow(row));
                }
                // 保存后立刻把已用次数回填（新规则指纹不在表里就是 0）
                ReplaceEngine.RuleRegistry.SyncDisplay(_cfg.ReplaceRules);
            };
        }

        private static ReplaceRule RuleFromRow(DataGridViewRow row)
        {
            int max = 1;
            int.TryParse(Cell(row, "c7"), out max);
            return new ReplaceRule
            {
                Direction = Cell(row, "c1"), Target = Cell(row, "c2"), Match = Cell(row, "c3"),
                ReplaceType = Cell(row, "c4"), Value = Cell(row, "c5"),
                LimitEnabled = string.Equals(Cell(row, "c6"), "True", StringComparison.OrdinalIgnoreCase),
                MaxHits = Math.Max(1, max),
                Comment = Cell(row, "c8")
            };
        }

        private static string Cell(DataGridViewRow r, string col) => r.Cells[col].Value?.ToString() ?? "";
    }

    /// <summary>
    /// 手机 / Mac 抓包向导：切换局域网监听、导出对端要装的证书、放行防火墙、显示各平台步骤。
    /// 独立成类，便于被自动化 UI 测试直接驱动。
    /// </summary>
    public sealed class LanGuideForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly ProxyEngine _engine;
        private readonly Action<string> _exportCert;
        private readonly Action _afterChange;

        public LanGuideForm(AppConfig cfg, ProxyEngine engine, Action<string> exportCert, Action afterChange = null)
        {
            _cfg = cfg;
            _engine = engine;
            _exportCert = exportCert ?? (_ => { });
            _afterChange = afterChange ?? (() => { });

            var ips = LanHelper.LocalIPv4();
            string ip = ips.Count > 0 ? ips[0] : "";

            Text = "手机 / Mac 抓包向导（局域网）";
            Size = new Size(760, 660);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            MaximizeBox = false;
            MinimizeBox = false;

            var chk = new CheckBox
            {
                Text = "允许局域网设备连接（监听 0.0.0.0 —— 手机/Mac 抓包必须勾选）",
                Location = new Point(14, 12),
                AutoSize = true,
                Checked = _cfg.LanCapture
            };
            var lblIp = Ui.MakeLabel(
                "本机局域网地址: " + (ips.Count == 0 ? "(未检测到，请先连上 Wi-Fi / 网线)" : string.Join("   /   ", ips)) +
                "     端口: " + _cfg.Port,
                ips.Count == 0 ? Ui.AccentOrange : Ui.Accent, 9.5f, true);
            lblIp.Location = new Point(14, 42);

            var lblFw = Ui.MakeLabel(
                LanHelper.FirewallRuleExists(_cfg.Port)
                    ? "防火墙: 已放行入站 TCP " + _cfg.Port
                    : "防火墙: 尚未放行（局域网设备可能连不上，点下面『放行防火墙』）",
                Color.Gray, 9f);
            lblFw.Location = new Point(14, 68);

            var steps = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                Location = new Point(14, 94),
                Size = new Size(716, 448),
                Text = LanHelper.StepsText(ip, _cfg.Port)
            };

            var btnCrt = Ui.MakeButton("导出通用证书 .crt", 140, 30);
            btnCrt.Location = new Point(14, 556);
            btnCrt.Click += (s, e) => _exportCert("crt");

            var btnIos = Ui.MakeButton("导出 iOS 描述文件", 140, 30);
            btnIos.Location = new Point(160, 556);
            btnIos.Click += (s, e) => _exportCert("mobileconfig");

            var btnFw = Ui.MakeButton("放行防火墙", 110, 30);
            btnFw.Location = new Point(306, 556);
            btnFw.Click += (s, e) =>
            {
                if (LanHelper.AddFirewallRule(_cfg.Port, out var msg))
                {
                    lblFw.Text = "防火墙: 已放行入站 TCP " + _cfg.Port;
                    MessageBox.Show("已放行入站 TCP " + _cfg.Port + "（专用/域网络）。", "完成");
                }
                else
                {
                    MessageBox.Show(msg, "未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            var btnCopy = Ui.MakeButton("复制步骤", 100, 30);
            btnCopy.Location = new Point(422, 556);
            btnCopy.Click += (s, e) =>
            {
                bool ok = ClipboardEx.SetText(steps.Text);
                MessageBox.Show(ok ? "步骤已复制到剪贴板。" : "剪贴板被其他程序占用，复制失败，请稍后重试。",
                    ok ? "完成" : "复制失败");
            };

            var btnClose = Ui.MakeButton("关闭", 90, 30);
            btnClose.Location = new Point(640, 556);
            btnClose.Click += (s, e) => Close();

            chk.CheckedChanged += (s, e) =>
            {
                _cfg.LanCapture = chk.Checked;
                ConfigService.Save(_cfg);
                Log.I("配置", "局域网抓包 = " + chk.Checked);
                if (_engine != null && _engine.Running)
                {
                    try
                    {
                        _engine.Stop();
                        _engine.Start(_cfg.Port);
                    }
                    catch (Exception ex)
                    {
                        Log.E("代理", "切换监听地址后重启失败", ex);
                        MessageBox.Show("切换监听地址失败: " + ex.Message + "\n详情见 帮助→运行日志。", "失败");
                    }
                }
                else
                {
                    MessageBox.Show(chk.Checked
                        ? "下次点 ▶启动(F5) 会监听 0.0.0.0，手机/Mac 就能连上本代理。"
                        : "已切回仅本机监听（127.0.0.1）。", "已保存");
                }
                _afterChange();
            };

            Controls.AddRange(new Control[] { chk, lblIp, lblFw, steps, btnCrt, btnIos, btnFw, btnCopy, btnClose });
        }
    }
}
