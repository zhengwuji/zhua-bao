using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>
    /// 主窗口：菜单 + 工具栏 + 左侧封包列表 + 右侧桌面/面板 + 底部过滤条与状态栏。
    /// 布局仿照 Fatbeans Creator。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly CertCache _certs;
        private readonly SessionStore _store;
        private ProxyEngine _engine;
        private ApiService _api;
        private SessionDetailPanel _detailPanel;

        private MenuStrip _menu;
        private ToolStrip _toolbar;
        private ListView _list;
        private Label _emptyHint;
        private Panel _rightPanel;
        private ToolStrip _rightToolbar;
        private Panel _filterBar;
        private StatusStrip _status;
        private ToolStripStatusLabel _stConfig, _stCapture, _stShown, _stIntercept, _stModified, _stSent, _stRedirect, _stProxy, _stApi, _stHook, _stMcp, _stFwd, _stSel;
        private SplitContainer _split;
        private TabControl _leftTabs;
        private Button _btnStart, _btnPause, _btnStop, _btnRestart;
        private ToolStripLabel _tsCaptureCount;

        // ===== 封包列表（虚拟模式）=====
        // _all 全量会话；_view 过滤后的视图；后台线程只入 _pending 队列，UI 定时器批量刷新，避免高频抓包卡界面
        private readonly List<HttpSession> _all = new();
        private readonly List<HttpSession> _view = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<HttpSession> _pending = new();
        private readonly HashSet<string> _seenIds = new();     // 引擎对一条会话会发两次事件（请求时 + 完成时），只入列表一次
        private System.Windows.Forms.Timer _uiTimer;
        private bool _follow = true;                           // 自动滚动到最新（用户向上滚动时暂停）
        private string _filterKeyword = "";
        private string _filterProcess = "";
        private readonly Dictionary<string, List<ToolStripButton>> _protoButtons = new();

        public MainForm(bool autostart = false)
        {
            ConfigService.UseDir(Path.Combine(AppContext.BaseDirectory, "Config"));
            _cfg = ConfigService.Load();
            _certs = new CertCache(Path.Combine(AppContext.BaseDirectory, "Config", "certs"));
            string dataDir = Path.Combine(AppContext.BaseDirectory, "Capture");
            _store = new SessionStore(dataDir);

            InitUi();
            UpdateStatus();

            // 只做检查与记录：装证书会弹 Windows「安全警告」确认框，必须在用户在场时进行
            try
            {
                if (CertTrust.IsTrusted(_certs.Ca))
                {
                    Log.I("证书", "根证书已受信任，HTTPS 解密就绪");
                }
                else
                {
                    Log.W("证书", "根证书未受信任 → 抓 HTTPS 会报证书错误；点启动时会提示安装（HTTP 不受影响）");
                }
            }
            catch (Exception ex) { Log.E("证书", "根证书信任检查失败", ex); }

            Load += (s, e) => { Welcome(); if (autostart) ToggleProxy(); };
        }

        // =============== UI 构建 ===============
        private void InitUi()
        {
            // 版本号统一取 AppInfo.Version（递增式 v1/v2/v3…），改版本只改 AppInfo 一处
            Text = AppInfo.WindowTitle;
            Width = 1310; Height = 1005;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Ui.PanelBg;
            KeyPreview = true;

            BuildMenu();
            BuildToolbar();

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 660,
                SplitterWidth = 6,
                BackColor = Ui.Border
            };

            BuildLeftPanel();
            _split.Panel2.Controls.Add(BuildRightContainer());

            _filterBar = BuildFilterBar();

            var leftContainer = new ToolStripContainer { Dock = DockStyle.Fill };
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Ui.PanelBg };
            bottomPanel.Controls.Add(_filterBar);
            _filterBar.Dock = DockStyle.Fill;

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 1 };
            mainSplit.Panel1.Controls.Add(_split);
            mainSplit.Panel2.Controls.Add(bottomPanel);
            mainSplit.SplitterDistance = mainSplit.Height - 30;

            Controls.Add(mainSplit);
            Controls.Add(_toolbar);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            BuildStatus();
        }

        private void BuildMenu()
        {
            _menu = new MenuStrip
            {
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f),
                RenderMode = ToolStripRenderMode.System
            };

            var mFile = Ui.Menu("文件(F)", null,
                Ui.Item("打开封包文件...", (s, e) => OpenCaptureFile(), Keys.Control | Keys.O),
                Ui.Item("保存选中封包...", (s, e) => SaveSelectedSession(), Keys.Control | Keys.S),
                new ToolStripSeparator(),
                Ui.Item("退出", (s, e) => Close()));
            var mEdit = Ui.Menu("编辑(E)", null,
                Ui.Item("全选", (s, e) =>
                {
                    // 虚拟模式下不能枚举 Items，只能按索引逐个选中
                    for (int i = 0; i < _view.Count; i++) _list.SelectedIndices.Add(i);
                }),
                Ui.Item("清空列表", (s, e) => ClearList(), Keys.Control | Keys.Delete),
                Ui.Item("复制 URL", (s, e) => CopySelectedUrl()));
            var miTakeOver = new ToolStripMenuItem("启动时接管系统代理")
            {
                CheckOnClick = true,
                Checked = _cfg.TakeOverSystemProxyEnabled,
                ToolTipText = "开启后浏览器无需手动配置代理；停止/退出时自动恢复原设置"
            };
            miTakeOver.CheckedChanged += (s, e) =>
            {
                _cfg.TakeOverSystemProxy = miTakeOver.Checked;
                ConfigService.Save(_cfg);
                Log.I("系统代理", "启动时接管系统代理 = " + miTakeOver.Checked);
                // 代理已在运行时立即生效
                if (_engine != null && _engine.Running)
                {
                    if (miTakeOver.Checked) SystemProxy.TakeOver("127.0.0.1:" + _cfg.Port);
                    else SystemProxy.Restore();
                }
                ShowEmptyHint();
                UpdateStatus();
            };
            var mProxy = Ui.Menu("代理(P)", null,
                Ui.Item("配置代理端口...", (s, e) => ShowConfig()),
                Ui.Item("安装根证书...", (s, e) => InstallCert()),
                Ui.Item("打开系统代理设置", (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-proxy") { UseShellExecute = true })),
                new ToolStripSeparator(),
                miTakeOver,
                Ui.Item("立即接管系统代理", (s, e) =>
                {
                    if (_engine != null && _engine.Running)
                        SystemProxy.TakeOver("127.0.0.1:" + _cfg.Port);
                    else
                        MessageBox.Show("代理未启动，先点 启动(F5)。", "提示");
                    UpdateStatus();
                }),
                Ui.Item("恢复系统代理原设置", (s, e) => { SystemProxy.Restore(); UpdateStatus(); }),
                new ToolStripSeparator(),
                Ui.Item("选择抓包网卡...", (s, e) => ShowNicPicker()),
                Ui.Item("手机 / Mac 抓包向导（局域网）...", (s, e) => ShowLanGuide()),
                new ToolStripSeparator(),
                // V1.0.8 新功能①：转发器（TCP/UDP 条件匹配 → 旁路 POST 到自己的 API）
                Ui.Item("转发器(旁路转发 TCP/UDP)...", (s, e) => ShowForwarder()));

            var miAutoCert = new ToolStripMenuItem("点启动时提示安装根证书")
            {
                CheckOnClick = true,
                Checked = _cfg.AutoInstallCert,
                ToolTipText = "开启后点『启动』会提示安装根证书；安装时 Windows 会弹安全警告，点『是』即可"
            };
            miAutoCert.CheckedChanged += (s, e) =>
            {
                _cfg.AutoInstallCert = miAutoCert.Checked;
                ConfigService.Save(_cfg);
                Log.I("证书", "点启动时提示安装根证书 = " + miAutoCert.Checked);
            };
            var mCert = Ui.Menu("证书(C)", null,
                Ui.Item("查看/安装根证书状态", (s, e) => InstallCert()),
                new ToolStripSeparator(),
                Ui.Item("导出根证书 .cer（Windows 双击安装）", (s, e) => ExportCert("cer")),
                Ui.Item("导出通用根证书 .crt（Mac / Android / iOS）", (s, e) => ExportCert("crt")),
                Ui.Item("导出 iOS 描述文件 .mobileconfig", (s, e) => ExportCert("mobileconfig")),
                new ToolStripSeparator(),
                miAutoCert,
                Ui.Item("卸载根证书（从本机信任库移除）", (s, e) =>
                {
                    if (MessageBox.Show("从当前用户信任库移除抓包豆 CA？\n移除后抓 HTTPS 会再次报证书错误。", "确认卸载",
                        MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        CertTrust.Uninstall(_certs.Ca);
                        UpdateStatus();
                    }
                }));
            var mAi = Ui.Menu("AI智能助手(I)", null,
                Ui.Item("启用 MCP 服务 (端口 " + _cfg.McpPort + ")", (s, e) => ToggleMcp()),
                Ui.Item("打开 API 服务 (端口 " + _cfg.ApiPort + ")", (s, e) => ToggleApi()),
                Ui.Item("配置 WebHook...", (s, e) => ShowWebHookDialog()),
                new ToolStripSeparator(),
                // V1.0.8 新功能②：自定义大模型接口（支持 Anthropic 兼容模式）
                Ui.Item("自定义大模型接口...", (s, e) => ShowLlmConfig()),
                Ui.Item("用大模型分析当前封包...", (s, e) => AnalyzeSelectedWithLlm()));
            var mView = Ui.Menu("视图(V)", null,
                Ui.Item("刷新", (s, e) => RefreshView()));
            var mRel = Ui.Menu("相关软件(S)", null, Ui.Item("官网", (s, e) => MessageBox.Show("Zhuabao Creator - 开源复刻版")));
            var mHelp = Ui.Menu("帮助(H)", null,
                Ui.Item("运行日志 (实时调试)", (s, e) => ShowLog()),
                Ui.Item("打开日志文件夹", (s, e) => OpenLogFolder()),
                new ToolStripSeparator(),
                Ui.Item("使用说明", (s, e) => MessageBox.Show("1. 点击 启动(F5) 开启代理\n2. 将系统/浏览器代理设为 127.0.0.1:8899\n3. 安装根证书以解密 HTTPS\n4. 出问题时查看 帮助→运行日志 定位", "帮助")),
                Ui.Item("关于", (s, e) => MessageBox.Show(AppInfo.AboutText, "关于 " + AppInfo.Name + " " + AppInfo.Version)));

            _menu.Items.AddRange(new ToolStripItem[] { mFile, mEdit, mProxy, mCert, mAi, mView, mRel, mHelp });
        }

        private void BuildToolbar()
        {
            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Ui.PanelBg,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(6, 2, 6, 2),
                ImageScalingSize = new Size(20, 20)
            };

            var modeLabel = new ToolStripLabel("选择模式:")
            {
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };
            _toolbar.Items.Add(modeLabel);
            var modeCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            modeCombo.Items.AddRange(new object[] { "全能模式", "仅HTTP", "仅HTTPS" });
            modeCombo.SelectedIndex = 0;
            _toolbar.Items.Add(modeCombo);
            _toolbar.Items.Add(new ToolStripSeparator());

            var cfgBtn = Ui.MakeButton("配置(F4)", 76, 26);
            cfgBtn.Click += (s, e) => ShowConfig();
            _toolbar.Items.Add(new ToolStripControlHost(cfgBtn));
            _toolbar.Items.Add(new ToolStripSeparator());

            _toolbar.Items.Add(new ToolStripLabel("目标进程:"));
            var procCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            procCombo.Items.AddRange(new object[] { "所有进程", "浏览器", "自定义..." });
            procCombo.SelectedIndex = 0;
            _toolbar.Items.Add(procCombo);
            _toolbar.Items.Add(new ToolStripSeparator());

            _btnStart = Ui.MakeButton("▶ 启动(F5)", 92, 26);
            _btnStart.BackColor = Color.FromArgb(0, 160, 80);
            _btnStart.ForeColor = Color.White;
            _btnStart.Click += (s, e) => { PromptInstallCertIfNeeded(); ToggleProxy(); };
            _btnPause = Ui.MakeButton("⏸ 挂机", 72, 26);
            _btnPause.Click += (s, e) => TogglePause();
            _btnStop = Ui.MakeButton("⏹ 停止(F6)", 84, 26);
            _btnStop.Click += (s, e) => StopProxy();
            _btnRestart = Ui.MakeButton("⟳ 重新开始(F7)", 110, 26);
            _btnRestart.Click += (s, e) => { ClearList(); StartProxySafe(); };
            var clearBtn = Ui.MakeButton("清空", 60, 26);
            clearBtn.Click += (s, e) => ClearList();

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripControlHost(_btnStart), new ToolStripControlHost(_btnPause),
                new ToolStripControlHost(_btnStop), new ToolStripControlHost(_btnRestart),
                new ToolStripControlHost(clearBtn), new ToolStripSeparator()
            });

            var sniLabel = new ToolStripLabel("HTTP 断点[禁用中]");
            _toolbar.Items.Add(sniLabel);
            _toolbar.Items.Add(new ToolStripSeparator());

            var logBtn = Ui.MakeButton("📋 日志", 66, 26);
            logBtn.Click += (s, e) => ShowLog();
            _toolbar.Items.Add(new ToolStripControlHost(logBtn));
            _toolbar.Items.Add(new ToolStripSeparator());

            _tsCaptureCount = new ToolStripLabel("捕获数量: 无限制") { Font = new Font("Microsoft YaHei UI", 9f) };
            _toolbar.Items.Add(_tsCaptureCount);
            _toolbar.Items.Add(new ToolStripSeparator());

            var tsTools = new ToolStripLabel("工具箱") { ForeColor = Ui.Accent, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), IsLink = true };
            tsTools.Click += (s, e) => MessageBox.Show("工具箱包含: 解码器 / 正则测试 / 时间戳转换\n(完整工具箱在右侧面板)", "工具箱");
            _toolbar.Items.Add(tsTools);
        }

        private Control BuildRightContainer()
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

            _rightToolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Dock = DockStyle.Top,
                BackColor = Color.White,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(4, 2, 4, 2)
            };

            void AddLabel(string text, Color color, EventHandler onClick)
            {
                var lbl = new ToolStripLabel(text)
                {
                    ForeColor = color,
                    Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
                };
                if (onClick != null) lbl.Click += onClick;
                _rightToolbar.Items.Add(lbl);
            }

            AddLabel("⬆ HTTP发送器", Color.FromArgb(0, 122, 204), (s, e) => new SenderForm(_engine).ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("⬇ 回复器", Color.FromArgb(0, 160, 80), (s, e) => new RepeaterForm().ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("★ 取值器", Color.FromArgb(255, 160, 0), (s, e) => new ExtractorForm(_cfg).ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("⇄ 重定向", Color.FromArgb(0, 122, 204), (s, e) => new RedirectForm(_cfg).ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            // V1.0.8 新功能①：转发器（旁路转发 TCP/UDP 到自定义 API）
            AddLabel("⇉ 转发器", Color.FromArgb(200, 60, 60), (s, e) => ShowForwarder());
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("▣ 封包对比", Color.FromArgb(120, 80, 200), (s, e) => new CompareForm(_store).ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("⚙ 工具箱", Color.FromArgb(80, 80, 80), (s, e) => new ToolboxForm().ShowDialog(this));
            _rightToolbar.Items.Add(new ToolStripSeparator());
            AddLabel("⌂ 桌面", Color.FromArgb(0, 122, 204), null);

            var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9f) };
            var tabDesktop = new TabPage("桌面");
            tabDesktop.BackColor = Color.White;
            var tabCapture = new TabPage("封包");
            tabCapture.BackColor = Color.White;
            var tabData = new TabPage("数据流量");
            tabData.BackColor = Color.White;
            var tabApi = new TabPage("API");
            tabApi.BackColor = Color.White;

            // ===== 桌面页 (欢迎页) =====
            var welcome = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var title = new Label
            {
                Text = "🍉 抓包豆 Creator",
                Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
                ForeColor = Ui.AccentOrange,
                AutoSize = true,
                Location = new Point(30, 60)
            };
            var lblRecent = Ui.MakeLabel("近期打开文件", Color.FromArgb(90, 90, 90), 10f);
            lblRecent.Location = new Point(30, 130);
            var linkClear = new LinkLabel { Text = "清空列表", AutoSize = true, Location = new Point(560, 132) };
            linkClear.LinkColor = Ui.AccentOrange;
            linkClear.Click += (s, e) => { _cfg.RecentFiles.Clear(); ConfigService.Save(_cfg); };
            var line = new Panel { BackColor = Color.FromArgb(220, 220, 220), Location = new Point(28, 158), Size = new Size(600, 1) };
            var lblVideo = Ui.MakeLabel("视频教学", Color.FromArgb(90, 90, 90), 9f);
            lblVideo.Location = new Point(660, 110);
            var linkVideo = new LinkLabel { Text = "B站教学视频", AutoSize = true, Location = new Point(660, 132) };
            var lblQQ = Ui.MakeLabel("QQ交流群", Color.FromArgb(90, 90, 90), 9f);
            lblQQ.Location = new Point(660, 165);
            var linkQQ = new LinkLabel { Text = "QQ群: 758758758", AutoSize = true, Location = new Point(660, 187) };
            welcome.Controls.AddRange(new Control[] { title, lblRecent, linkClear, line, lblVideo, linkVideo, lblQQ, linkQQ });
            foreach (Control c in welcome.Controls) if (c is LinkLabel ll) ll.LinkColor = Ui.Accent;
            tabDesktop.Controls.Add(welcome);

            // ===== 封包页（详情面板：点击会话后显示全部内容，可编辑、多种修改方式）=====
            _detailPanel = new SessionDetailPanel();
            // V1.0.8 BUG 修复：详情页「按此内容重发」也走副本，原始封包不被改写
            _detailPanel.OnResendRequested += ses =>
            {
                if (ses?.Request == null) return;
                var edited = _detailPanel.EditedRequestText();
                OpenSenderForResend(ses.CloneWithNewId(" (重发源)"), edited);
            };
            tabCapture.Controls.Add(_detailPanel);

            // ===== 数据流量页 =====
            var dataPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var lblData = Ui.MakeLabel("图表数据统计 (LiveCharts 风格占位)", Color.Gray, 10f);
            lblData.Dock = DockStyle.Top;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            for (int i = 0; i < 6; i++)
            {
                var card = new Panel { Size = new Size(170, 90), BackColor = Ui.PanelBg, Margin = new Padding(6) };
                var t = Ui.MakeLabel(new[] { "总请求数", "响应 2xx", "响应 4xx", "响应 5xx", "平均耗时", "总流量" }[i], Ui.Accent, 9.5f, true);
                t.Location = new Point(10, 10);
                var v = Ui.MakeLabel("0", Color.Black, 16f, true);
                v.Name = "stat" + i;
                v.Location = new Point(10, 40);
                card.Controls.Add(t); card.Controls.Add(v);
                flow.Controls.Add(card);
            }
            dataPanel.Controls.Add(flow);
            dataPanel.Controls.Add(lblData);
            tabData.Controls.Add(dataPanel);

            // ===== API 页 =====
            var apiPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var apiInfo = new TextBox
            {
                Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f), BackColor = Color.White,
                Text = "REST API:\r\n  GET /sessions          列出最近 200 条会话\r\n  GET /session/{id}      查看会话详情\r\n  GET /stats             统计信息\r\n  POST /decode           {text, mode: base64|hex|url}\r\n\r\nMCP (JSON-RPC):\r\n  initialize / tools/list / tools/call\r\n  tools: list_sessions | get_session | stats"
            };
            apiPanel.Controls.Add(apiInfo);
            tabApi.Controls.Add(apiPanel);

            tabs.TabPages.AddRange(new[] { tabDesktop, tabCapture, tabData, tabApi });
            _rightPanel = root;
            root.Controls.Add(tabs);
            root.Controls.Add(_rightToolbar);
            return root;
        }

        private void BuildLeftPanel()
        {
            _leftTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9f) };
            var tabList = new TabPage("封包列表");
            var tabBatch = new TabPage("批量解码");

            // 协议过滤 tab（虚拟模式下真正生效：多选按 AND 叠加，"全部"清空其它）
            var protoBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, BackColor = Color.White, RenderMode = ToolStripRenderMode.System };
            var allBtn = new ToolStripButton("全部") { Checked = true };
            var filters = new[] { "发送", "接收", "TCP", "UDP", "HTTP", "HTTPS", "WS/WSS", "GET", "POST", "正常放行", "已拦截" };
            foreach (var f in filters)
            {
                var b = new ToolStripButton(f) { CheckOnClick = true };
                b.Click += (s, e) => RefreshView();
                protoBar.Items.Add(b);
                if (!_protoButtons.TryGetValue(f, out var lst)) _protoButtons[f] = lst = new List<ToolStripButton>();
                lst.Add(b);
            }
            protoBar.Items.Insert(0, allBtn);
            allBtn.Click += (s, e) =>
            {
                foreach (ToolStripItem it in protoBar.Items) if (it is ToolStripButton b && b != allBtn) b.Checked = false;
                RefreshView();
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                BackColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 9f),
                VirtualMode = true,
                VirtualListSize = 0,
                MultiSelect = true
            };
            typeof(ListView).GetProperty("DoubleBuffered")?.SetValue(_list, true); // protected 属性，反射开启双缓冲减少闪烁
            _list.Columns.Add("序号", 46);
            _list.Columns.Add("备注", 60);
            _list.Columns.Add("时间", 70);
            _list.Columns.Add("进程", 90);
            _list.Columns.Add("协议", 60);
            _list.Columns.Add("方法", 52);
            _list.Columns.Add("访问地址", 300);
            _list.Columns.Add("状态", 46);
            _list.ContextMenuStrip = BuildListMenu();
            _list.RetrieveVirtualItem += (s, e) => e.Item = BuildListItem(_view[e.ItemIndex], e.ItemIndex);
            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedIndices.Count > 0)
                {
                    int idx = _list.SelectedIndices[0];
                    if (idx >= 0 && idx < _view.Count)
                    {
                        _detailPanel.ShowSession(_view[idx]);
                        TrySelectRightTab("封包");
                    }
                }
                UpdateStatus();
            };
            // 用户向上滚动即暂停跟随最新，回到底部恢复
            _list.MouseWheel += (s, e) => { if (e.Delta > 0) _follow = false; else _follow = IsListAtBottom(); };
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Home || e.KeyCode == Keys.PageUp) _follow = false; };

            // 空列表提示（Dock.Fill 放在 ListView 上层，不可见时不挡操作）
            _emptyHint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = new Font("Microsoft YaHei UI", 10.5f),
                Visible = false
            };

            tabList.Controls.Add(_emptyHint);
            tabList.Controls.Add(_list);
            tabList.Controls.Add(protoBar);
            _leftTabs.TabPages.Add(tabList);
            _leftTabs.TabPages.Add(tabBatch);

            _split.Panel1.Controls.Add(_leftTabs);

            // UI 批量刷新定时器：后台高频抓包时只入队，这里每 150ms 合并刷一次
            _uiTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _uiTimer.Tick += (s, e) => DrainPendingSessions();
            _uiTimer.Start();
        }

        private ListViewItem BuildListItem(HttpSession s, int index)
        {
            var item = new ListViewItem(new[]
            {
                (index + 1).ToString(),
                s.Comment,
                s.Time.ToString("HH:mm:ss"),
                s.Process,
                s.Scheme.ToUpper(),
                s.Method,
                s.Url,
                s.StatusCode
            })
            { UseItemStyleForSubItems = false, Tag = s };

            if (int.TryParse(s.StatusCode, out var code))
                item.SubItems[7].ForeColor = code < 300 ? Color.Green : code < 400 ? Color.DarkOrange : Color.Firebrick;
            else item.SubItems[7].ForeColor = Color.Gray;
            return item;
        }

        private bool IsListAtBottom()
        {
            try
            {
                if (_view.Count == 0 || _list.TopItem == null) return true;
                int rowH = _list.Items.Count > 0 ? _list.GetItemRect(0).Height : 20;
                int visible = Math.Max(1, _list.ClientSize.Height / rowH);
                return _list.TopItem.Index + visible >= _view.Count;
            }
            catch { return true; }
        }

        /// <summary>把引擎事件里攒下的会话并入列表（后台线程只入队，UI 线程在这里消费）。</summary>
        private void DrainPendingSessions()
        {
            bool added = false;
            while (_pending.TryDequeue(out var s))
            {
                _all.Add(s);
                added = true;
            }
            if (added) RefreshView(autoScroll: true);
        }

        /// <summary>按当前过滤条件重建视图并更新列表（虚拟模式）。</summary>
        private void RefreshView(bool autoScroll = false)
        {
            if (_list == null) return;
            _view.Clear();
            var conds = ActiveFilterConds();
            foreach (var s in _all)
            {
                bool ok = true;
                foreach (var c in conds) { if (!c(s)) { ok = false; break; } }
                if (ok && (_filterKeyword.Length == 0 || (s.Url?.IndexOf(_filterKeyword, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                    (_filterProcess.Length == 0 || (s.Process?.IndexOf(_filterProcess, StringComparison.OrdinalIgnoreCase) >= 0)))
                    _view.Add(s);
            }
            _list.VirtualListSize = _view.Count;
            if (autoScroll && _follow && _view.Count > 0) _list.EnsureVisible(_view.Count - 1);
            _emptyHint.Visible = _view.Count == 0;
            if (_view.Count == 0) ShowEmptyHint();
            UpdateStatus();
        }

        /// <summary>把勾选的协议过滤按钮翻译成会话谓词（AND 叠加）。</summary>
        private List<Func<HttpSession, bool>> ActiveFilterConds()
        {
            var conds = new List<Func<HttpSession, bool>>();
            foreach (var kv in _protoButtons)
            {
                var btn = kv.Value[0];
                if (!btn.Checked) continue;
                switch (kv.Key)
                {
                    case "HTTP": conds.Add(s => s.Protocol == "HTTP"); break;
                    case "HTTPS": conds.Add(s => s.Protocol == "HTTPS"); break;
                    case "WS/WSS": conds.Add(s => s.Protocol == "WS" || s.Protocol == "WSS"); break;
                    case "TCP": conds.Add(s => s.Protocol == "HTTP" || s.Protocol == "HTTPS" || s.Protocol == "WSS"); break;
                    case "UDP": conds.Add(s => false); break; // 本工具只抓 HTTP(S)，无 UDP 会话
                    case "GET": conds.Add(s => s.Method == "GET"); break;
                    case "POST": conds.Add(s => s.Method == "POST"); break;
                    case "发送": conds.Add(s => (s.Request?.Body?.Length ?? 0) > 0); break;   // 有请求体 = 发送了数据
                    case "接收": conds.Add(s => (s.Response?.Body?.Length ?? 0) > 0); break; // 有响应体 = 接收了数据
                    case "已拦截": conds.Add(s => s.InterceptStatus == "拦截"); break;
                    case "正常放行": conds.Add(s => s.InterceptStatus == "放行" || (s.InterceptStatus != "拦截" && s.InterceptStatus != "")); break;
                }
            }
            return conds;
        }

        private ContextMenuStrip BuildListMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(Ui.Item("重发此请求", (s, e) => ResendSelected()));
            menu.Items.Add(Ui.Item("复制 URL", (s, e) => CopySelectedUrl()));
            menu.Items.Add(Ui.Item("复制请求头", (s, e) => CopySelectedHeaders()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Ui.Item("保存为文件...", (s, e) => SaveSelectedSession()));
            menu.Items.Add(Ui.Item("删除", (s, e) => DeleteSelected()));
            return menu;
        }

        private Panel BuildFilterBar()
        {
            var bar = new Panel { BackColor = Ui.PanelBg, Padding = new Padding(6, 4, 6, 4) };
            var lblFilter = Ui.MakeLabel("列表筛选");
            lblFilter.Location = new Point(6, 7);
            var lblHint = Ui.MakeLabel("(左键修改，右键删除)", Color.Gray, 8.5f);
            lblHint.Location = new Point(58, 8);
            var linkClear = new LinkLabel { Text = "清空", AutoSize = true, Location = new Point(180, 7), LinkColor = Ui.AccentOrange };
            linkClear.Click += (s, e) => ClearList();
            bar.Controls.AddRange(new Control[] { lblFilter, lblHint, linkClear });

            var contentBtn = Ui.MakeButton("🔍 内容", 78, 24);
            contentBtn.Location = new Point(260, 3);
            contentBtn.Click += (s, e) =>
            {
                string kw = InputDialog.Show(this, "输入关键字过滤 URL/响应:", "内容过滤");
                if (kw != null) FilterByKeyword(kw);
            };
            var procBtn = Ui.MakeButton("🖥 进程", 78, 24);
            procBtn.Location = new Point(344, 3);
            procBtn.Click += (s, e) =>
            {
                string kw = InputDialog.Show(this, "输入进程名过滤:", "进程过滤");
                if (kw != null) FilterByProcess(kw);
            };
            var tcpBtn = Ui.MakeButton("🔌 TCP/UDP/WS", 118, 24);
            tcpBtn.Location = new Point(428, 3);
            var httpBtn = Ui.MakeButton("🌐 HTTP", 88, 24);
            httpBtn.Location = new Point(552, 3);

            bar.Controls.AddRange(new Control[] { contentBtn, procBtn, tcpBtn, httpBtn });
            return bar;
        }

        private void BuildStatus()
        {
            _status = new StatusStrip { BackColor = Ui.PanelBg, SizingGrip = true };
            ToolStripStatusLabel L(string t) { var l = new ToolStripStatusLabel(t) { Font = new Font("Microsoft YaHei UI", 9f), Spring = false, AutoSize = true }; return l; }

            _stConfig = L("当前配置: 默认配置");
            _stCapture = L("已捕获: 0");
            _stShown = L("已显示: 0");
            _stIntercept = L("已拦截: 0");
            _stModified = L("已修改: 0");
            _stSent = L("已发送: 0");
            _stRedirect = L("重定向: 0");
            _stProxy = L("代理服务: 已关闭");
            _stProxy.ForeColor = Color.Firebrick;
            _stApi = L("API: 已关闭");
            _stHook = L("WebHook: 已关闭");
            _stMcp = L("MCP服务: 已关闭");
            _stFwd = L("转发器: 已关闭");
            _stSel = L("已选中: 0 字节");
            _stSel.Spring = true;
            _stSel.TextAlign = ContentAlignment.MiddleRight;

            _status.Items.AddRange(new ToolStripItem[]
            {
                _stConfig, new ToolStripStatusLabel(" | "),
                _stCapture, new ToolStripStatusLabel(" | "), _stShown, new ToolStripStatusLabel(" | "),
                _stIntercept, new ToolStripStatusLabel(" | "), _stModified, new ToolStripStatusLabel(" | "), _stSent, new ToolStripStatusLabel(" | "), _stRedirect,
                new ToolStripStatusLabel("      "), _stProxy, new ToolStripStatusLabel("   "), _stApi,
                new ToolStripStatusLabel("   "), _stHook, new ToolStripStatusLabel("   "), _stMcp,
                new ToolStripStatusLabel("   "), _stFwd, _stSel
            });
            Controls.Add(_status);
        }

        // =============== 功能 ===============
        private void Welcome() { }

        /// <summary>列表无数据时给出下一步指引，有数据则隐藏。</summary>
        private void ShowEmptyHint()
        {
            if (_list == null || _emptyHint == null) return;
            bool empty = _view.Count == 0;
            _emptyHint.Visible = empty;
            if (!empty) return;

            bool running = _engine?.Running ?? false;
            var (sysOn, sysServer) = SystemProxy.ReadCurrent();
            bool pointed = sysOn && sysServer != null && sysServer.Contains(_cfg.Port.ToString());

            if (!running)
                _emptyHint.Text = "代理未启动 —— 点击上方 ▶ 启动(F5) 开始抓包";
            else if (SystemProxy.TakenOver || pointed)
                _emptyHint.Text = "等待流量… 系统代理已指向 127.0.0.1:" + _cfg.Port +
                    "，打开任意网页即可抓到（若仍无数据，看 帮助→运行日志）";
            else
                _emptyHint.Text = "等待流量… 尚无流量经过代理：请在 配置(F4) 勾选「接管系统代理」，" +
                    "或手动把浏览器代理设为 127.0.0.1:" + _cfg.Port;
        }

        private void ToggleProxy()
        {
            if (_engine != null && _engine.Running) { StopProxy(); return; }
            try
            {
                _engine = new ProxyEngine(_cfg, _certs, _store);
                _engine.OnSession += OnSessionArrived;
                _engine.Start(_cfg.Port);

                // 接管系统代理，浏览器免配置即可抓包
                if (_cfg.TakeOverSystemProxyEnabled)
                {
                    try { SystemProxy.TakeOver("127.0.0.1:" + _cfg.Port); }
                    catch (Exception ex) { Log.W("系统代理", "接管失败（可手动把浏览器代理设为 127.0.0.1:" + _cfg.Port + "）: " + ex.Message); }
                }
                else
                {
                    Log.I("系统代理", "未接管（配置已关）。请手动将浏览器/系统代理设为 127.0.0.1:" + _cfg.Port);
                }

                _btnStart.Text = "■ 关闭(F5)";
                _btnStart.BackColor = Color.Firebrick;
                _btnStop.Enabled = true;
                ShowEmptyHint();
            }
            catch (Exception ex)
            {
                Log.E("代理", "启动失败 (端口 " + _cfg.Port + ")", ex);
                MessageBox.Show("代理启动失败: " + ex.Message +
                    "\n\n常见原因:\n1. 端口 " + _cfg.Port + " 被其他程序占用（含本程序另一个实例）\n2. 防火墙拦截\n\n详情见 帮助→运行日志。", "启动失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateStatus();
        }

        private void StopProxy()
        {
            _engine?.Stop();
            try { if (SystemProxy.TakenOver) SystemProxy.Restore(); } catch (Exception ex) { Log.W("系统代理", "恢复失败: " + ex.Message); }
            _btnStart.Text = "▶ 启动(F5)";
            _btnStart.BackColor = Color.FromArgb(0, 160, 80);
            ShowEmptyHint();
            UpdateStatus();
        }

        private void TogglePause()
        {
            _cfg.Paused = !_cfg.Paused;
            _btnPause.Text = _cfg.Paused ? "▶ 继续" : "⏸ 挂机";
            UpdateStatus();
        }

        /// <summary>引擎线程调用：只入队（同一条会话的"请求时/完成时"两次事件只收一次），由 UI 定时器批量刷新。</summary>
        private void OnSessionArrived(HttpSession s)
        {
            lock (_seenIds)
            {
                if (!_seenIds.Add(s.Id)) return;
            }
            _pending.Enqueue(s);
        }

        /// <summary>UI 自动化测试注入：同步加入列表并立即刷新（不经过定时器）。</summary>
        internal void UiTestInjectSession(HttpSession s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<HttpSession>(UiTestInjectSession), s); return; }
            lock (_seenIds) _seenIds.Add(s.Id);
            _all.Add(s);
            RefreshView(autoScroll: true);
        }

        /// <summary>当前选中的会话（虚拟模式下从视图取，不依赖 Items）。</summary>
        private IEnumerable<HttpSession> SelectedSessions()
        {
            foreach (int idx in _list.SelectedIndices)
                if (idx >= 0 && idx < _view.Count) yield return _view[idx];
        }

        private void ClearList()
        {
            _pending.Clear();
            lock (_seenIds) _seenIds.Clear();
            _all.Clear();
            RefreshView();
        }

        private void FilterByKeyword(string kw)
        {
            _filterKeyword = kw ?? "";
            RefreshView();
        }

        private void FilterByProcess(string kw)
        {
            _filterProcess = kw ?? "";
            RefreshView();
        }

        private void TrySelectRightTab(string title)
        {
            if (_rightPanel?.Controls.Count == 0) return;
            var tabs = _rightPanel.Controls.OfType<TabControl>().FirstOrDefault();
            if (tabs == null) return;
            foreach (TabPage t in tabs.TabPages) if (t.Text == title) { tabs.SelectedTab = t; break; }
        }

        /// <summary>网卡选择：选定后引擎上游连接绑定该网卡 IP，只有走这块网卡的流量会被抓到。</summary>
        private void ShowNicPicker()
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => new { Nic = n.Name, Ip = a.Address.ToString() }))
                .ToList();

            using var dlg = new Form
            {
                Text = "选择抓包网卡",
                Size = new Size(460, 380),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var lbl = Ui.MakeLabel("上游流量绑定到指定网卡：只有走这块网卡的请求会被抓取。\n(全部网卡 = 不限制，默认)", Ui.Accent, 9f);
            lbl.Location = new Point(12, 10);
            var list = new ListBox { Location = new Point(12, 48), Size = new Size(420, 220) };
            list.Items.Add("全部网卡（不限制）");
            foreach (var n in nics) list.Items.Add(n.Ip + "  —  " + n.Nic);
            int cur = _cfg.NicIp == null || _cfg.NicIp == "" ? 0 : nics.FindIndex(n => n.Ip == _cfg.NicIp) + 1;
            list.SelectedIndex = cur < 0 ? 0 : cur;
            var btnOk = Ui.MakeButton("确定", 80, 28);
            btnOk.Location = new Point(240, 285);
            var btnCancel = Ui.MakeButton("取消", 80, 28);
            btnCancel.Location = new Point(335, 285);
            btnOk.Click += (s, e) =>
            {
                int idx = list.SelectedIndex;
                _cfg.NicIp = idx <= 0 ? "" : nics[idx - 1].Ip;
                ConfigService.Save(_cfg);
                Log.I("配置", "抓包网卡 = " + (_cfg.NicIp == "" ? "(全部)" : _cfg.NicIp));
                if (_engine != null && _engine.Running)
                {
                    MessageBox.Show("网卡设置已保存，对之后的新连接立即生效。\n（本机/回环地址不受限制）", "提示");
                }
                UpdateStatus();
                dlg.Close();
            };
            btnCancel.Click += (s, e) => dlg.Close();
            dlg.Controls.AddRange(new Control[] { lbl, list, btnOk, btnCancel });
            dlg.ShowDialog(this);
        }

        private void CopySelectedUrl()
        {
            var s = SelectedSessions().FirstOrDefault();
            if (s != null) ClipboardEx.SetText(s.Url ?? "");
        }

        private void CopySelectedHeaders()
        {
            var s = SelectedSessions().FirstOrDefault();
            if (s?.Request != null) ClipboardEx.SetText(s.Request.HeaderText());
        }

        private void ResendSelected()
        {
            var s = SelectedSessions().FirstOrDefault();
            if (s?.Request == null) return;
            // V1.0.8 BUG 修复：重发走副本，绝不覆盖被选中的那条原始封包。
            // 原实现把编辑后的请求直接写回 s.Request（甚至 s.Request = Parse(...)），
            // 于是列表里的原抓包记录被就地改写，原始报文再也找不回来。
            var copy = s.CloneWithNewId(" (重发源)");
            OpenSenderForResend(copy, null);
        }

        /// <summary>
        /// 打开发送器执行重发：编辑框里放 <paramref name="rawOverride"/>（无则用副本自身的请求原文），
        /// 发送产生的新会话通过 SessionCreated 事件回流入列表 —— 原封包始终只读。
        /// </summary>
        internal void OpenSenderForResend(HttpSession copy, string rawOverride)
        {
            var dlg = new SenderForm(_engine, copy?.Request, rawOverride, copy);
            dlg.SessionCreated += s =>
            {
                // 引擎事件线程 → UI 线程
                if (InvokeRequired) { try { BeginInvoke(new Action<HttpSession>(s2 => { lock (_seenIds) _seenIds.Add(s2.Id); _all.Add(s2); RefreshView(autoScroll: true); }), s); } catch { } return; }
                lock (_seenIds) _seenIds.Add(s.Id);
                _all.Add(s);
                RefreshView(autoScroll: true);
            };
            dlg.ShowDialog(this);
        }

        /// <summary>V1.0.8 新功能①：打开转发器面板。</summary>
        private void ShowForwarder()
        {
            using var dlg = new ForwarderForm(_cfg, _engine);
            dlg.ShowDialog(this);
            // 面板关闭时已保存；若引擎在跑，配置会立刻生效
            UpdateStatus();
        }

        /// <summary>V1.0.8 新功能②：打开自定义大模型接口配置（含 Anthropic 兼容模式）。</summary>
        private void ShowLlmConfig()
        {
            using var dlg = new LlmConfigForm(_cfg);
            dlg.ShowDialog(this);
            UpdateStatus();
        }

        /// <summary>
        /// V1.0.8 新功能②配套：把当前选中会话发给自定义大模型接口分析。
        /// 请求与响应都做长度截断，避免超长报文把 token 打爆。
        /// </summary>
        private async void AnalyzeSelectedWithLlm()
        {
            var s = SelectedSessions().FirstOrDefault();
            if (s == null)
            {
                MessageBox.Show("请先在左侧列表选中一条封包。", "提示");
                return;
            }
            if (_cfg.Llm == null || !_cfg.Llm.Enabled)
            {
                MessageBox.Show("自定义大模型接口未启用。\n\n请先打开 菜单 AI智能助手(I) → 自定义大模型接口... 填写并勾选启用。", "提示");
                return;
            }

            string prompt =
                "请分析下面这条抓包会话：判断它属于什么业务、有哪些可疑点（敏感字段、越权迹象、可重放性），并给出下一步测试建议。\n\n" +
                "===== 请求 =====\n" + Clip(s.Request?.HeaderText() + s.Request?.GetBodyText(), 6000) + "\n\n" +
                "===== 响应 =====\n" + Clip(s.Response?.HeaderText() + s.Response?.GetBodyText(), 6000);

            var client = new LlmClient(_cfg.Llm);
            try
            {
                Cursor = Cursors.WaitCursor;
                var r = await client.ChatAsync(new[] { new LlmMessage("user", prompt) });
                Cursor = Cursors.Default;
                if (!r.Ok)
                {
                    MessageBox.Show("调用失败: " + r.Error, "大模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Log.I("大模型", $"分析完成 {s.Method} {s.Url} ({r.ElapsedMs}ms, mode={_cfg.Llm.Provider})");
                using var dlg = new LlmResultForm("大模型分析 — " + s.Method + " " + s.Url, r);
                dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                Log.E("大模型", "分析失败", ex);
                MessageBox.Show("分析失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "(空)";
            return text.Length <= max ? text : text.Substring(0, max) + "\n...（已截断 " + (text.Length - max) + " 字符）";
        }

        private void DeleteSelected()
        {
            var gone = new HashSet<HttpSession>(SelectedSessions());
            if (gone.Count == 0) return;
            _all.RemoveAll(x => gone.Contains(x));
            lock (_seenIds) foreach (var g in gone) _seenIds.Remove(g.Id);
            RefreshView();
        }

        private void OpenCaptureFile()
        {
            using var dlg = new OpenFileDialog { Filter = "抓包文件 (*.zb)|*.zb|所有文件 (*.*)|*.*", Multiselect = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int loaded = 0;
            foreach (var f in dlg.FileNames)
            {
                var s = SessionStore.LoadSession(f);
                if (s == null) continue;
                lock (_seenIds) _seenIds.Add(s.Id);
                _all.Add(s);
                loaded++;
            }
            if (loaded > 0) RefreshView(autoScroll: true);
        }

        private void SaveSelectedSession()
        {
            var sel = SelectedSessions().FirstOrDefault();
            if (sel == null) return;
            using var dlg = new SaveFileDialog { Filter = "抓包文件 (*.zb)|*.zb", FileName = "session.zb" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                sel.Time, sel.Method, sel.Url, sel.StatusCode,
                request = sel.Request?.HeaderText() + sel.Request?.GetBodyText(),
                response = sel.Response?.HeaderText() + sel.Response?.GetBodyText()
            }, Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
        }

        /// <summary>导出根证书（只导出公钥，私钥留在 Config\certs）。kind: cer | crt | mobileconfig</summary>
        private void ExportCert(string kind)
        {
            try
            {
                var ca = _certs.Ca;
                if (kind == "mobileconfig")
                {
                    using var ios = new SaveFileDialog { Filter = "Apple 描述文件 (*.mobileconfig)|*.mobileconfig", FileName = "ZhuabaoRootCA.mobileconfig" };
                    if (ios.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(ios.FileName, CertExport.MobileConfig(ca), new UTF8Encoding(false));
                    Log.I("证书", "已导出 iOS 描述文件: " + ios.FileName);
                    MessageBox.Show(
                        "iOS 描述文件已导出:\n" + ios.FileName + "\n\n" +
                        "手机上安装后再做关键一步:\n设置 → 通用 → 关于本机 → 证书信任设置 → 打开完全信任。\n" +
                        "不做这一步 HTTPS 一定会报证书错误。",
                        "导出成功");
                    return;
                }

                if (kind == "crt")
                {
                    using var crt = new SaveFileDialog { Filter = "通用证书 (*.crt)|*.crt", FileName = "ZhuabaoRootCA.crt" };
                    if (crt.ShowDialog(this) != DialogResult.OK) return;
                    CertExport.SavePem(ca, crt.FileName);
                    Log.I("证书", "已导出通用证书(PEM): " + crt.FileName);
                    MessageBox.Show(
                        "通用根证书已导出（PEM 格式，无 BOM）:\n" + crt.FileName + "\n\n" +
                        "macOS: 双击 → 钥匙串访问 → 系统 → 设为『始终信任』\n" +
                        "Android: 设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书\n" +
                        "iOS: 也可用「导出 iOS 描述文件」更省事（需再开完全信任）",
                        "导出成功");
                    return;
                }

                using var dlg = new SaveFileDialog { Filter = "证书 (*.cer)|*.cer", FileName = "ZhuabaoRootCA.cer" };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllBytes(dlg.FileName, CertExport.Der(ca));
                Log.I("证书", "已导出 DER 证书: " + dlg.FileName);
                MessageBox.Show("根证书已导出: " + dlg.FileName +
                    "\n\nWindows 双击该文件 → 安装证书 → 本地计算机 → 受信任的根证书颁发机构。", "导出成功");
            }
            catch (Exception ex)
            {
                Log.E("证书", "导出根证书失败 (kind=" + kind + ")", ex);
                MessageBox.Show("导出失败: " + ex.Message + "\n详情见 帮助→运行日志。", "导出失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 手机 / Mac 抓包向导（实现见 LanGuideForm，独立成类便于自动化测试驱动）。
        /// </summary>
        private void ShowLanGuide()
        {
            using var dlg = new LanGuideForm(_cfg, _engine, kind => ExportCert(kind), () => { ShowEmptyHint(); UpdateStatus(); });
            dlg.ShowDialog(this);
            UpdateStatus();
        }

        /// <summary>点启动时若根证书未受信任，友好提示安装（安装会弹 Windows 安全警告，需用户点『是』）。</summary>
        private void PromptInstallCertIfNeeded()
        {
            try
            {
                if (CertTrust.IsTrusted(_certs.Ca)) return;
                if (!_cfg.AutoInstallCert)
                {
                    Log.W("证书", "根证书未受信任且已关闭提示，HTTPS 站点会报证书错误");
                    return;
                }
                var r = MessageBox.Show(
                    "抓 HTTPS 需要先把根证书装进系统信任库（只影响本机）。\n\n" +
                    "现在安装吗？安装时 Windows 会弹出【安全警告】窗口，请点『是』。\n" +
                    "（选『否』也能继续抓 HTTP，HTTPS 站点会显示证书错误）",
                    "根证书未安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    Log.W("证书", "用户选择暂不安装根证书，HTTPS 抓包将不可用");
                    return;
                }
                if (CertTrust.InstallToCurrentUserRoot(_certs.Ca))
                    MessageBox.Show("根证书已安装，HTTPS 可以正常解密了。", "安装成功");
                else
                    MessageBox.Show("安装未完成。可菜单 证书(C) → 导出根证书，手动双击导入「受信任的根证书颁发机构」。", "安装未完成");
            }
            catch (Exception ex)
            {
                Log.E("证书", "提示安装根证书时出错", ex);
            }
        }

        private void InstallCert()
        {
            bool already = CertTrust.IsTrusted(_certs.Ca);
            if (already)
            {
                MessageBox.Show("根证书已受信任，HTTPS 解密可用。\n\n指纹: " + _certs.Ca.Thumbprint, "证书状态");
                return;
            }
            if (CertTrust.InstallToCurrentUserRoot(_certs.Ca))
                MessageBox.Show("根证书已安装到 当前用户\\受信任的根证书颁发机构，HTTPS 解密已可用。\n\n指纹: " + _certs.Ca.Thumbprint, "安装成功");
            else
                MessageBox.Show("安装失败（可能需要管理员权限）。\n可先「导出根证书...」再手动双击安装到「受信任的根证书颁发机构」。", "安装失败");
        }

        private void ToggleApi()
        {
            if (_api == null) _api = new ApiService(_store, _engine);
            if (_api.ApiRunning) { _api.StopAll(); }
            else _api.StartApi(_cfg.ApiPort);
            UpdateStatus();
        }

        private void ToggleMcp()
        {
            if (_api == null) _api = new ApiService(_store, _engine);
            if (_api.McpRunning) { _api.StopAll(); }
            else _api.StartMcp(_cfg.McpPort);
            UpdateStatus();
        }

        private void ShowConfig()
        {
            using var dlg = new ConfigForm(_cfg);
            if (dlg.ShowDialog(this) == DialogResult.OK) ConfigService.Save(_cfg);
            UpdateStatus();
        }

        private void ShowLog()
        {
            foreach (Form f in Application.OpenForms)
                if (f is LogViewerForm lv) { lv.Activate(); return; }
            new LogViewerForm().Show(this);
        }

        private void OpenLogFolder()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }

        private void ShowWebHookDialog()
        {
            string input = InputDialog.Show(this, "WebHook URL (POST JSON):", "WebHook 配置", _cfg.WebHookUrl);
            if (input != null) { _cfg.WebHookUrl = input; _cfg.WebHookEnabled = !string.IsNullOrWhiteSpace(input); ConfigService.Save(_cfg); UpdateStatus(); }
        }

        private void UpdateStatus()
        {
            _stCapture.Text = "已捕获: " + (_engine?.CapturedCount ?? 0);
            _stShown.Text = "已显示: " + _view.Count;
            _stIntercept.Text = "已拦截: " + (_engine?.InterceptedCount ?? 0);
            _stModified.Text = "已修改: " + (_engine?.ModifiedCount ?? 0);
            _stSent.Text = "已发送: " + (_engine?.SentCount ?? 0);
            _stRedirect.Text = "重定向: " + (_engine?.RedirectedCount ?? 0);
            bool proxyOn = _engine?.Running ?? false;
            _stProxy.Text = "代理服务: " + (proxyOn ? "已开启 (127.0.0.1:" + _cfg.Port + ")" : "已关闭");
            if (proxyOn && SystemProxy.TakenOver) _stProxy.Text += " 已接管系统代理";
            _stProxy.ForeColor = proxyOn ? Color.SeaGreen : Color.Firebrick;
            bool apiOn = _api?.ApiRunning ?? false;
            _stApi.Text = "API: " + (apiOn ? "已开启" : "已关闭");
            _stApi.ForeColor = apiOn ? Color.SeaGreen : Color.Firebrick;
            _stHook.Text = "WebHook: " + (_cfg.WebHookEnabled ? "已开启" : "已关闭");
            _stMcp.Text = "MCP服务: " + ((_api?.McpRunning ?? false) ? "已开启" : "已关闭");
            // V1.0.8：转发器状态（开启且配了目标地址才算真的在转发）
            var fw = _engine?.Forwarder;
            bool fwdOn = fw != null && fw.Enabled;
            _stFwd.Text = "转发器: " + (fwdOn ? ("已开启 转发" + fw.ForwardedCount) : "已关闭");
            _stFwd.ForeColor = fwdOn ? Color.SeaGreen : Color.Firebrick;
            _stSel.Text = "已选中: " + _list.SelectedIndices.Count + " 项";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ConfigService.Save(_cfg);
            _engine?.Stop();
            _api?.StopAll();
            // 退出前必须把系统代理还回去，否则用户断网
            try { if (SystemProxy.TakenOver) SystemProxy.Restore(); } catch { }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F5: ToggleProxy(); return true;
                case Keys.F6: StopProxy(); return true;
                case Keys.F7: ClearList(); StartProxySafe(); return true;
                case Keys.F4: ShowConfig(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void StartProxySafe()
        {
            if (_engine == null || !_engine.Running) ToggleProxy();
        }
    }
}
