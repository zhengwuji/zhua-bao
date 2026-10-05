using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>
    /// --uitest：无人值守「把每个功能点一遍」。
    ///
    /// 覆盖范围：左侧页签/协议过滤条、右侧页签与工具栏、封包列表右键菜单、封包详情
    /// （预览/原始报文/请求/响应/修改）的每个页签与按钮、全部菜单项、工具栏按钮、
    /// 底部筛选条，以及每个对话框（配置/发送器/回复器/取值器/重定向/封包对比/
    /// 工具箱/修改器/日志查看器/局域网向导）内部的控件。
    ///
    /// 弹出的模态对话框由后台守卫线程自动关闭（WM_CLOSE ≈ 取消），所以不会卡住。
    /// 会弹出 UAC 的按钮（放行防火墙）自动跳过并记录。
    ///
    /// 运行结束后自动：还原系统代理注册表快照、清理测试期间装上的临时根证书、
    /// 收集日志里的 ERROR，写出 uitest-report.txt，返回失败数（0 = 全部正常）。
    /// </summary>
    internal static class UiTest
    {
        // ---------- 状态 ----------
        private sealed class UiStep { public string Label; public Action Act; public bool Skip; }

        private static readonly List<UiStep> Steps = new();
        private static readonly List<string> Report = new();
        private static readonly HashSet<IntPtr> Protected = new();
        private static readonly HashSet<Control> Visited = new();
        private static MainForm _main;
        private static System.Windows.Forms.Timer _timer;
        private static volatile bool _busy, _stop;
        private static volatile string _current = "(空闲)";
        private static DateTime _currentStart = DateTime.Now;
        private static int _ok, _fail, _skip;
        private static string _regBackup, _regBackupConn;
        private static string _proxyBefore;
        private static bool _closeExplorer, _closeNotepad;
        private static bool _caTrustedBefore;

        private const int WM_CLOSE = 0x0010;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static readonly MethodInfo OnClickMethod =
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// 自动跳过（记录原因）：
        ///  · 放行防火墙 —— 会弹 UAC 提权框
        ///  · 打开系统代理设置 —— 只是拉起系统设置页，且在本机会卡住
        ///  · 安装/查看/卸载根证书 —— 会弹 Windows【安全警告】系统保护框，
        ///    该框不接受程序关闭（WM_CLOSE / Esc 都无效），必须人工点『是』
        /// </summary>
        private static readonly string[] SkipTexts =
        {
            "放行防火墙", "打开系统代理设置", "安装根证书...", "查看/安装根证书状态", "卸载根证书",
            // V1.0.8：新面板里会真的发起网络请求 / 拉起浏览器的项，跳过并记录原因
            "测试连接", "打开示例代码", "发送测试封包", "用大模型分析"
        };

        /// <summary>
        /// 预期内的日志 ERROR：自动化把系统确认框当成「用户点了否」，
        /// 所以证书安装这类需要人工点『是』的操作必然失败，不算产品缺陷。
        /// </summary>
        private static readonly string[] ExpectedErrors = { "安装根证书" };

        // ==================================================================
        public static int Run()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            Say("===== 抓包豆 Creator 自动化 UI 测试 =====");
            Say("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            SnapshotRegistry();

            _main = new MainForm(false);
            _caTrustedBefore = SafeIsCaTrusted();

            BuildSteps();

            // 模态框守卫线程 + 卡死保护
            var guard = new Thread(GuardLoop) { IsBackground = true, Name = "zb-uitest-guard" };
            guard.Start();

            _timer = new System.Windows.Forms.Timer { Interval = 120 };
            _timer.Tick += Tick;
            _timer.Start();

            Application.Run(_main);

            _stop = true;
            Thread.Sleep(200);
            return Finish();
        }

        // ---------- 步骤编排 ----------
        private static void BuildSteps()
        {
            // 0. 注入一条测试会话，让详情面板/右键菜单有数据可操作
            Step("注入测试会话并选中", () =>
            {
                var s = MakeSession();
                Invoke(_main, "UiTestInjectSession", s);
                var list = (ListView)Field(_main, "_list");
                if (list.VirtualListSize > 0) list.SelectedIndices.Add(list.VirtualListSize - 1);
            });

            // 1. 封包详情面板（各页签 + 每个按钮）
            WalkInto(() => _main, "主界面", onlyUnder: (Control)Field(_main, "_detailPanel"));

            // 2. 封包列表右键菜单
            var list2 = (ListView)Field(_main, "_list");
            var ctx = list2.ContextMenuStrip;
            if (ctx != null)
                foreach (ToolStripItem it in ctx.Items)
                {
                    if (it is ToolStripSeparator) continue;
                    if (it.Text.Contains("删除")) continue;      // 放最后，避免清掉数据
                    var item = it;
                    Step("右键菜单: " + item.Text, () =>
                    {
                        if (list2.VirtualListSize > 0) list2.SelectedIndices.Add(0);
                        item.PerformClick();
                    });
                }

            // 3. 菜单栏全部叶子项（退出放最后）
            var menu = (MenuStrip)Field(_main, "_menu");
            foreach (var leaf in MenuLeaves(menu.Items))
            {
                if (leaf.Text.StartsWith("退出")) continue;
                var item = leaf;
                if (SkipTexts.Any(s => item.Text.Contains(s)))
                {
                    SkipStep("菜单: " + MenuPath(item), "需人工确认/外部启动");
                    continue;
                }
                Step("菜单: " + MenuPath(item), () =>
                {
                    if (item.Text.Contains("打开日志文件夹")) _closeExplorer = true;
                    item.PerformClick();
                });
            }

            // 4. 工具栏 / 过滤条 / 页签 / 其余控件
            WalkInto(() => _main, "主界面(其余)", skipUnder: new[]
            {
                (Control)Field(_main, "_menu"),
                (Control)Field(_main, "_detailPanel")
            });

            // 5. 各对话框内部控件（逐个打开 → 点一遍 → 关闭）
            AddDialog("配置窗口", () => new ConfigForm((AppConfig)Field(_main, "_cfg")));
            AddDialog("HTTP发送器(空)", () => new SenderForm((ProxyEngine)Field(_main, "_engine")));
            AddDialog("HTTP发送器(带请求)", () => new SenderForm((ProxyEngine)Field(_main, "_engine"), MakeSession().Request));
            AddDialog("回复器", () => new RepeaterForm());
            AddDialog("取值器", () => new ExtractorForm((AppConfig)Field(_main, "_cfg")));
            AddDialog("重定向", () => new RedirectForm((AppConfig)Field(_main, "_cfg")));
            AddDialog("封包对比", () => new CompareForm((SessionStore)Field(_main, "_store")));
            AddDialog("工具箱", () => new ToolboxForm());
            AddDialog("修改器", () => new ModifierForm((AppConfig)Field(_main, "_cfg")));
            // V1.0.8 新面板
            AddDialog("转发器", () => new ForwarderForm((AppConfig)Field(_main, "_cfg"), (ProxyEngine)Field(_main, "_engine")));
            AddDialog("自定义大模型接口", () => new LlmConfigForm((AppConfig)Field(_main, "_cfg")));
            AddDialog("日志查看器", () => new LogViewerForm());
            AddDialog("局域网向导", () => new LanGuideForm(
                (AppConfig)Field(_main, "_cfg"),
                (ProxyEngine)Field(_main, "_engine"),
                k =>
                {
                    var ca = ((CertCache)Field(_main, "_certs")).Ca;
                    string p = Path.Combine(Path.GetTempPath(), "zb-uitest-export-" + k);
                    if (k == "crt") CertExport.SavePem(ca, p);
                    else if (k == "mobileconfig") File.WriteAllText(p, CertExport.MobileConfig(ca), new UTF8Encoding(false));
                    else File.WriteAllBytes(p, CertExport.Der(ca));
                    Zhuabao.Core.Log.I("UiTest", "向导导出 " + k + " → " + p + " (" + new FileInfo(p).Length + " 字节)");
                },
                () => { }));

            // 6. 右键删除（重新注入一条再删）
            Step("重新注入会话", () =>
            {
                Invoke(_main, "UiTestInjectSession", MakeSession());
                var list = (ListView)Field(_main, "_list");
                if (list.VirtualListSize > 0) list.SelectedIndices.Add(list.VirtualListSize - 1);
            });
            var list3 = (ListView)Field(_main, "_list");
            var del = list3.ContextMenuStrip?.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text.Contains("删除"));
            if (del != null)
                Step("右键菜单: " + del.Text, () =>
                {
                    if (list3.VirtualListSize > 0) list3.SelectedIndices.Add(0);
                    del.PerformClick();
                });

            // 7. 最后：退出（关窗 → 触发 OnFormClosing 的代理还原 → 消息循环结束）
            var quit = MenuLeaves(menu.Items).FirstOrDefault(i => i.Text.StartsWith("退出"));
            if (quit != null) Step("菜单: 文件 → 退出", () => quit.PerformClick());
        }

        private static void AddDialog(string name, Func<Form> factory)
        {
            Form dlg = null;
            Step("打开对话框: " + name, () =>
            {
                dlg = factory();
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.Show();
                Protected.Add(dlg.Handle);
                Say("      ↳ 已打开 " + name + " (" + dlg.Controls.Count + " 个顶层控件)");
                // 把该对话框内部的控件点击步骤插到「关闭」之前
                var inner = new List<UiStep>();
                WalkInto(() => dlg, name, sink: inner);
                Steps.InsertRange(0, inner);
            });
            Step("关闭对话框: " + name, () =>
            {
                if (dlg == null) return;
                // 对话框内部的「关闭」按钮可能已经把它关掉并释放了，避免二次关闭抛异常
                try { if (!dlg.IsDisposed && dlg.IsHandleCreated) Protected.Remove(dlg.Handle); } catch { }
                if (!dlg.IsDisposed) { dlg.Close(); dlg.Dispose(); }
            });
        }

        // ---------- 控件遍历 ----------
        private static void WalkInto(Func<Control> rootGetter, string prefix,
            Control onlyUnder = null, Control[] skipUnder = null, List<UiStep> sink = null)
        {
            var target = sink ?? Steps;
            var root = rootGetter();
            if (root == null) return;

            foreach (var c in Descendants(root))
            {
                if (skipUnder != null && skipUnder.Any(s => s != null && IsIn(s, c))) continue;
                if (onlyUnder != null && !IsIn(onlyUnder, c)) continue;
                if (Visited.Contains(c)) continue;
                AddControlSteps(c, prefix, target);
            }
        }

        private static void AddControlSteps(Control c, string prefix, List<UiStep> sink)
        {
            if (c is TabControl tc)
            {
                foreach (TabPage page in tc.TabPages)
                {
                    var p = page;
                    Step2(sink, "切换页签: " + p.Text, () => tc.SelectedTab = p);
                }
                return;   // TabPage 本身不点
            }
            if (c is ButtonBase btn)
            {
                // 注意：未选中的 TabPage 上的控件 Control.Visible 返回 false，
                // 但按钮依然存在且可点，所以这里不能按 Visible 过滤。
                if (string.IsNullOrEmpty(btn.Text)) return;
                if (SkipTexts.Any(s => btn.Text.Contains(s))) { Step2(sink, prefix + " 按钮: " + btn.Text, null, skip: true); return; }
                Visited.Add(c);
                bool opensNotepad = btn.Text.Contains("打开日志文件");
                Step2(sink, prefix + " 按钮: " + btn.Text, () =>
                {
                    if (opensNotepad) _closeNotepad = true;
                    if (btn is Button b) b.PerformClick();
                    else OnClickMethod.Invoke(btn, new object[] { EventArgs.Empty });
                });
                return;
            }
            if (c is LinkLabel ll)
            {
                if (string.IsNullOrEmpty(ll.Text)) return;
                // V1.0.8：工具箱里的专用解码器 GitHub 链接不能自动点（会拉起浏览器打开外链）
                if (ll.Text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Step2(sink, prefix + " 链接: " + ll.Text, null, skip: true);
                    return;
                }
                Visited.Add(c);
                Step2(sink, prefix + " 链接: " + ll.Text, () => OnClickMethod.Invoke(ll, new object[] { EventArgs.Empty }));
                return;
            }
            if (c is ComboBox cb && cb.Items.Count > 0 && cb.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                Visited.Add(c);
                for (int i = 0; i < Math.Min(cb.Items.Count, 3); i++)
                {
                    int idx = i;
                    Step2(sink, prefix + " 下拉框选第 " + (idx + 1) + " 项", () => cb.SelectedIndex = idx);
                }
                return;
            }
            if (c is ToolStrip ts && !(c is MenuStrip))
            {
                foreach (ToolStripItem it in ts.Items)
                {
                    if (it is ToolStripSeparator) continue;
                    if (it is ToolStripButton || it is ToolStripLabel || it is ToolStripMenuItem)
                    {
                        if (string.IsNullOrEmpty(it.Text) || it.Text.Trim() == "|") continue;
                        if (SkipTexts.Any(s => it.Text.Contains(s))) { Step2(sink, prefix + " 工具条: " + it.Text, null, skip: true); continue; }
                        var item = it;
                        Step2(sink, prefix + " 工具条: " + item.Text, () => item.PerformClick());
                    }
                }
            }
        }

        private static void Step2(List<UiStep> sink, string label, Action act, bool skip = false)
        {
            if (sink == null) Steps.Add(new UiStep { Label = label, Act = act, Skip = skip });
            else sink.Add(new UiStep { Label = label, Act = act, Skip = skip });
        }

        private static void Step(string label, Action act) => Steps.Add(new UiStep { Label = label, Act = act });

        private static void SkipStep(string label, string reason) =>
            Steps.Add(new UiStep { Label = label + "  ← " + reason, Skip = true, Act = () => { } });

        private static IEnumerable<Control> Descendants(Control root)
        {
            var stack = new Stack<Control>();
            var seen = new HashSet<Control>();
            foreach (Control c in root.Controls) stack.Push(c);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c)) continue;
                yield return c;
                foreach (Control k in c.Controls) stack.Push(k);
                if (c is ToolStrip ts) foreach (Control k in ts.Controls) stack.Push(k);
            }
        }

        private static bool IsIn(Control ancestor, Control c)
        {
            for (var p = c; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        private static IEnumerable<ToolStripMenuItem> MenuLeaves(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripMenuItem mi)
                {
                    if (mi.DropDownItems.Count > 0)
                        foreach (var sub in MenuLeaves(mi.DropDownItems)) yield return sub;
                    else if (!string.IsNullOrWhiteSpace(mi.Text))
                        yield return mi;
                }
            }
        }

        private static string MenuPath(ToolStripMenuItem item)
        {
            var parts = new List<string>();
            for (ToolStripItem i = item; i != null; i = i.OwnerItem) parts.Insert(0, i.Text.Replace("&", ""));
            return string.Join(" → ", parts);
        }

        // ---------- 执行循环 ----------
        private static void Tick(object sender, EventArgs e)
        {
            if (_busy) return;
            if (Steps.Count == 0)
            {
                _timer.Stop();
                _main.Close();
                return;
            }
            var st = Steps[0];
            Steps.RemoveAt(0);
            _busy = true; _current = st.Label; _currentStart = DateTime.Now;
            try
            {
                if (st.Skip) { _skip++; Say("[SKIP] " + st.Label); return; }
                st.Act();
                _ok++;
                Say("[ OK ] " + st.Label);
            }
            catch (Exception ex)
            {
                _fail++;
                Say("[FAIL] " + st.Label + "  → " + ex.GetType().Name + ": " + ex.Message);
                Zhuabao.Core.Log.E("UiTest", "点击失败: " + st.Label, ex);
            }
            finally
            {
                _busy = false; _current = "(空闲)";
            }
        }

        /// <summary>后台守卫：关掉弹出来的模态对话框，并在卡死时强制收尾。</summary>
        private static void GuardLoop()
        {
            // 注意：窗口句柄会被系统复用，计数必须带上标题/类名，
            // 否则前一个对话框的失败次数会污染下一个对话框（曾导致整轮测试被误终止）。
            var attempts = new Dictionary<string, int>();
            while (!_stop)
            {
                try
                {
                    IntPtr main = _main != null && _main.IsHandleCreated ? _main.Handle : IntPtr.Zero;
                    uint me = (uint)Environment.ProcessId;
                    EnumWindows((h, l) =>
                    {
                        if (!IsWindowVisible(h)) return true;
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        var cls = ClassName(h);
                        var title = WindowText(h);

                        if (pid == me)
                        {
                            if (h == main || Protected.Contains(h)) return true;
                            if (!_busy) return true;   // 没有模态框在挡路，交给遍历器处理
                            if (cls.StartsWith("#32770") || cls.StartsWith("WindowsForms10"))
                            {
                                string key = h + "|" + cls + "|" + title;
                                int n = attempts.TryGetValue(key, out var v) ? v + 1 : 1;
                                attempts[key] = n;
                                if (n == 1) Say("      ↳ 自动关闭对话框 [" + cls + "] " + title);
                                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                if (n == 40)
                                {
                                    // 顽固窗口（少数系统确认框不响应 WM_CLOSE）：补一记 Esc
                                    Say("      ↳ 该对话框不响应关闭，改发 Esc: " + title);
                                    PostMessage(h, 0x0100, (IntPtr)0x1B, IntPtr.Zero);   // WM_KEYDOWN VK_ESCAPE
                                    PostMessage(h, 0x0101, (IntPtr)0x1B, IntPtr.Zero);   // WM_KEYUP
                                }
                            }
                            return true;
                        }

                        // 我们点开的系统窗口：顺手关掉，保持桌面干净
                        if (_closeExplorer && cls == "CabinetWClass" && title.Contains("logs"))
                        { Say("      ↳ 关闭资源管理器窗口: " + title); PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                        if (_closeNotepad && cls == "Notepad" && title.Contains("zhuabao-"))
                        { Say("      ↳ 关闭记事本窗口: " + title); PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                if (_busy && (DateTime.Now - _currentStart).TotalSeconds > 40)
                {
                    _fail++;
                    Say("[FAIL] 卡死超时，强制结束。卡在: " + _current);
                    try { _main?.Close(); } catch { }
                    Thread.Sleep(500);
                    Finish();
                    Environment.Exit(3);
                }
                Thread.Sleep(120);
            }
        }

        // ---------- 收尾 ----------
        private static int Finish()
        {
            Say("");
            Say("===== 结果 =====");
            Say("点击通过: " + _ok + "    失败: " + _fail + "    跳过: " + _skip);

            RestoreRegistry();
            string proxyAfter = ProxyState();
            Say("系统代理 前: " + _proxyBefore);
            Say("系统代理 后: " + proxyAfter);
            if (_proxyBefore != proxyAfter) { _fail++; Say("[FAIL] 系统代理注册表与测试前不一致！"); }

            CleanupTempCa();

            int errs = 0, expected = 0;
            foreach (var e in Zhuabao.Core.Log.Snapshot())
            {
                if (e.Level != LogLevel.Error) continue;
                // 自动化会代替用户关闭系统确认框（等同点『否』），这类“失败”是预期内的
                if (ExpectedErrors.Any(p => e.Message.Contains(p)))
                {
                    expected++;
                    Say("[日志(预期内)] [" + e.Module + "] " + e.Message);
                }
                else
                {
                    errs++;
                    Say("[日志ERROR] [" + e.Module + "] " + e.Message);
                }
            }
            Say("日志 ERROR: " + errs + " 条，预期内: " + expected + " 条");
            _fail += errs;

            Say(_fail == 0 ? "===== 全部功能测试通过 =====" : "===== 有 " + _fail + " 项需要处理 =====");
            WriteReport();
            return _fail;
        }

        private static void CleanupTempCa()
        {
            try
            {
                if (_caTrustedBefore) return;                  // 本来就没装，不用管
                var certs = (CertCache)Field(_main, "_certs");
                var ca = certs?.Ca;
                if (ca == null) return;
                string sub = @"Software\Microsoft\SystemCertificates\Root\Certificates\" + ca.Thumbprint;
                using var key = Registry.CurrentUser.OpenSubKey(sub);
                if (key == null) return;
                key.Close();
                Registry.CurrentUser.DeleteSubKeyTree(sub, false);
                Say("已清理测试期间装进 HKCU 根信任库的临时证书 " + ca.Thumbprint);
                Zhuabao.Core.Log.I("UiTest", "已移除临时根证书 " + ca.Thumbprint);
            }
            catch (Exception ex) { Say("清理临时证书失败(可忽略): " + ex.Message); }
        }

        private static void SnapshotRegistry()
        {
            try
            {
                _proxyBefore = ProxyState();
                string tmp = Path.GetTempPath();
                _regBackup = Path.Combine(tmp, "zb-inet-" + Guid.NewGuid().ToString("N") + ".reg");
                _regBackupConn = Path.Combine(tmp, "zb-inetconn-" + Guid.NewGuid().ToString("N") + ".reg");
                RunReg("export \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" \"" + _regBackup + "\" /y");
                RunReg("export \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\\Connections\" \"" + _regBackupConn + "\" /y");
                Say("已备份系统代理注册表 → " + Path.GetFileName(_regBackup));
            }
            catch (Exception ex) { Say("备份注册表失败: " + ex.Message); }
        }

        private static void RestoreRegistry()
        {
            try
            {
                if (File.Exists(_regBackup)) RunReg("import \"" + _regBackup + "\"");
                if (File.Exists(_regBackupConn)) RunReg("import \"" + _regBackupConn + "\"");
                SystemProxy.Restore();
                Say("已还原系统代理注册表快照");
            }
            catch (Exception ex) { Say("还原注册表失败: " + ex.Message); }
        }

        private static void RunReg(string args)
        {
            var psi = new ProcessStartInfo("reg.exe", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
        }

        private static string ProxyState()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                return "Enable=" + k?.GetValue("ProxyEnable") + " Server=" + k?.GetValue("ProxyServer") +
                       " Override=" + k?.GetValue("ProxyOverride");
            }
            catch (Exception ex) { return "(读取失败: " + ex.Message + ")"; }
        }

        private static void WriteReport()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "uitest-report.txt");
                File.WriteAllLines(path, Report, new UTF8Encoding(true));
                Say("报告文件: " + path);
            }
            catch (Exception ex) { Say("写报告失败: " + ex.Message); }
        }

        // ---------- 小工具 ----------
        private static void Say(string s)
        {
            Report.Add(s);
            Console.WriteLine(s);
            Zhuabao.Core.Log.I("UiTest", s);
        }

        private static object Field(object o, string name)
        {
            if (o == null) return null;
            var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return f?.GetValue(o);
        }

        private static void Invoke(object o, string method, params object[] args)
        {
            var m = o.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (m == null) throw new MissingMethodException(o.GetType().Name + "." + method);
            m.Invoke(o, args);
        }

        private static bool SafeIsCaTrusted()
        {
            try { return CertTrust.IsTrusted(((CertCache)Field(_main, "_certs")).Ca); }
            catch { return true; }
        }

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>造一条内容完整的假会话，用于驱动详情面板与右键菜单。</summary>
        private static HttpSession MakeSession()
        {
            var req = HttpRequest.Parse(
                "POST /api/uitest?x=1 HTTP/1.1\r\nHost: 127.0.0.1:18080\r\n" +
                "User-Agent: zb-uitest\r\nContent-Type: application/json\r\n" +
                "Cookie: sid=abc123\r\nContent-Length: 13\r\n\r\n{\"a\":\"测试\"}");
            req.Url = "http://127.0.0.1:18080/api/uitest?x=1";

            var resp = new HttpResponse { Status = "200 OK", StatusCode = 200 };
            resp.Headers["Content-Type"] = "application/json; charset=utf-8";
            resp.Headers["Server"] = "zb-uitest";
            resp.SetBody("{\"ok\":true,\"name\":\"张三\",\"v\":12345}");

            var s = new HttpSession { Request = req, Protocol = "HTTP", Process = "uitest.exe", Comment = "UI自测" };
            s.SetResponse(resp);
            return s;
        }
    }
}
