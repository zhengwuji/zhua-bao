using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>按钮统一样式工具。</summary>
    internal static class Ui
    {
        public static Color PanelBg = Color.FromArgb(240, 242, 245);
        public static Color Accent = Color.FromArgb(0, 122, 204);
        public static Color AccentOrange = Color.FromArgb(255, 140, 0);
        public static Color ListHeader = Color.FromArgb(245, 246, 248);
        public static Color Border = Color.FromArgb(200, 205, 212);

        public static Button MakeButton(string text, int width = 88, int height = 28)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Microsoft YaHei UI", 9f),
                UseVisualStyleBackColor = false,
                Margin = new Padding(3)
            };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 240, 254);
            return b;
        }

        public static Label MakeLabel(string text, Color? color = null, float size = 9f, bool bold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color ?? Color.FromArgb(60, 60, 60)
            };
        }

        public static ToolStripMenuItem Menu(string text, EventHandler onClick = null, params ToolStripItem[] items)
        {
            var m = new ToolStripMenuItem(text);
            if (onClick != null) m.Click += onClick;
            if (items.Length > 0) m.DropDownItems.AddRange(items);
            return m;
        }

        public static ToolStripMenuItem Item(string text, EventHandler onClick = null, Keys keys = Keys.None)
        {
            var m = new ToolStripMenuItem(text) { ShortcutKeys = keys };
            if (onClick != null) m.Click += onClick;
            return m;
        }

        public static TextBox TextBox(string text = "", bool multiline = false, int height = 0)
        {
            var t = new TextBox
            {
                Text = text,
                Multiline = multiline,
                Dock = multiline ? DockStyle.Fill : DockStyle.Left,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = true,
                ScrollBars = multiline ? ScrollBars.Both : ScrollBars.None
            };
            if (multiline && height > 0) t.Height = height;
            return t;
        }
    }

    /// <summary>
    /// 剪贴板写入：其他进程占用剪贴板时常见 ExternalException（剪贴板管理器、
    /// 远程桌面、另一个程序正在读写），直接 Clipboard.SetText 会抛异常打断功能。
    /// 这里重试几次，失败只记日志，不影响主流程。
    /// </summary>
    internal static class ClipboardEx
    {
        public static bool SetText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text ?? string.Empty); return true; }
                catch (Exception ex)
                {
                    Log.W("UI", $"写剪贴板失败(第 {i + 1} 次): {ex.Message}");
                    System.Threading.Thread.Sleep(120);
                }
            }
            return false;
        }
    }

    /// <summary>简易输入对话框（替代 Microsoft.VisualBasic.Interaction.InputBox）。</summary>
    internal static class InputDialog
    {
        public static string Show(IWin32Window owner, string prompt, string title, string defaultValue = "")
        {
            using var form = new Form
            {
                Text = title,
                Width = 440,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var lbl = new Label { Text = prompt, Location = new Point(12, 12), AutoSize = true };
            var txt = new TextBox { Location = new Point(14, 40), Width = 390, Text = defaultValue };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(230, 72), Width = 80 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(320, 72), Width = 80 };
            form.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            return form.ShowDialog(owner) == DialogResult.OK ? txt.Text : null;
        }
    }

    /// <summary>运行日志查看器：实时滚动显示全程序调试日志。</summary>
    internal sealed class LogViewerForm : Form
    {
        private readonly TextBox _box;
        private readonly ComboBox _level;
        private readonly CheckBox _follow;
        private readonly System.Windows.Forms.Timer _timer;
        private int _lastCount = -1;

        public LogViewerForm()
        {
            Text = "运行日志 (实时调试)";
            Size = new Size(980, 620);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), BackColor = Ui.PanelBg };
            top.Controls.Add(Ui.MakeLabel("级别:", Color.Gray));
            _level = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            _level.Items.AddRange(new object[] { "全部", "DEBUG+", "INFO+", "WARN+", "仅ERROR" });
            _level.SelectedIndex = 0;
            _level.SelectedIndexChanged += (s, e) => Reload();
            top.Controls.Add(_level);

            _follow = new CheckBox { Text = "自动滚动到底部", Checked = true, AutoSize = true, Padding = new Padding(6, 8, 0, 0) };
            top.Controls.Add(_follow);

            var btnClear = Ui.MakeButton("清空显示", 84, 26);
            btnClear.Click += (s, e) => { Log.ClearScreenBuffer(); Reload(); };
            top.Controls.Add(btnClear);

            var btnOpen = Ui.MakeButton("打开日志文件", 110, 26);
            btnOpen.Click += (s, e) =>
            {
                var p = Log.FilePath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + p + "\"") { UseShellExecute = true });
                else
                    MessageBox.Show("日志文件尚未创建。", "提示");
            };
            top.Controls.Add(btnOpen);

            var lblFile = Ui.MakeLabel("文件: " + (Log.FilePath ?? "(未启用)"), Color.Gray, 8.5f);
            lblFile.Padding = new Padding(8, 10, 0, 0);
            top.Controls.Add(lblFile);

            _box = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White
            };

            Controls.Add(_box);
            Controls.Add(top);

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();

            Log.OnEntry += OnLogEntry;
            FormClosed += (s, e) => { _timer.Stop(); Log.OnEntry -= OnLogEntry; };
            Reload();
        }

        private void OnLogEntry(Zhuabao.Core.LogEntry e)
        {
            if (InvokeRequired) { BeginInvoke(new Action<Zhuabao.Core.LogEntry>(AppendEntry), e); return; }
            AppendEntry(e);
        }

        private void Poll()
        {
            var all = Zhuabao.Core.Log.Snapshot();
            if (all.Length == _lastCount) return;
            _lastCount = all.Length;
            Reload();
        }

        private void Reload()
        {
            var all = Zhuabao.Core.Log.Snapshot();
            _lastCount = all.Length;
            int minLevel = _level.SelectedIndex switch
            {
                1 => 0, 2 => 1, 3 => 2, 4 => 3, _ => -1
            };
            _box.Lines = all
                .Where(e => minLevel < 0 || (int)e.Level >= minLevel)
                .Select(e => e.Line)
                .ToArray();
            if (_follow.Checked && _box.TextLength > 0)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
        }

        private void AppendEntry(Zhuabao.Core.LogEntry e)
        {
            int minLevel = _level.SelectedIndex switch
            {
                1 => 0, 2 => 1, 3 => 2, 4 => 3, _ => -1
            };
            if (minLevel >= 0 && (int)e.Level < minLevel) return;
            _box.AppendText(e.Line + Environment.NewLine);
            if (_follow.Checked)
            {
                _box.SelectionStart = _box.TextLength;
                _box.ScrollToCaret();
            }
        }
    }
}
