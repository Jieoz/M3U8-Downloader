using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Microsoft.WindowsAPICodePack.Taskbar;

namespace M3U8_Downloader
{
    public partial class Form1 : Form
    {
        public const string AppVersion = "2.6.0";
        const string ReleasesUrl = "https://github.com/Jieoz/M3U8-Downloader/releases";

        [DllImport("user32.dll")]
        public static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

        readonly TaskbarManager windowsTaskbar = TaskbarManager.Instance;
        readonly DownloadManager manager;
        readonly Dictionary<DownloadTask, ListViewItem> rows = new Dictionary<DownloadTask, ListViewItem>();

        string CurrentLanguage = "zh";
        string m_path;
        string m_proxy;

        // 代码里加的控件（设计器里只有旧的单任务界面）
        ListView listTasks;
        Button button_Clear;
        Button button_Retry;
        Label label_Parallel;
        NumericUpDown numParallel;
        ContextMenuStrip taskMenu;
        ToolStripMenuItem menu_Plugin;
        ToolStripMenuItem cmStop, cmKill, cmRetry, cmOpenFile, cmOpenFolder, cmCopy, cmRemove;

        const int ColProgress = 3;

        string SettingsPath { get { return Path.Combine(Environment.CurrentDirectory, "M3u8_Downloader_Settings.xml"); } }

        //不影响点击任务栏图标最大最小化
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_MINIMIZEBOX = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.Style = cp.Style | WS_MINIMIZEBOX;
                return cp;
            }
        }

        public Form1()
        {
            InitializeComponent();
            manager = new DownloadManager(a => { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); });
            manager.OutputDir = () => m_path;
            manager.Proxy = () => menu_Proxy.Checked ? m_proxy : null;
            manager.TaskAdded += AddRow;
            manager.TaskChanged += UpdateRow;
            manager.TaskRemoved += RemoveRow;
            manager.BecameIdle += OnIdle;
            BuildTaskUi();
            ApplyLayout();
            ApplyTexts();
        }

        // ---------------- 界面 ----------------

        void BuildTaskUi()
        {
            // 旧的单任务控件不再用
            textBox_forRegex.Visible = false;
            label8.Visible = false;

            listTasks = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                OwnerDraw = true,
                ShowItemToolTips = true,
                Font = new Font("Microsoft YaHei", 9F),
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(listTasks, true, null);
            listTasks.Columns.Add("#", 36);
            listTasks.Columns.Add("文件", 130);
            listTasks.Columns.Add("状态", 76);
            listTasks.Columns.Add("进度", 190);
            listTasks.Columns.Add("大小", 84);
            listTasks.Columns.Add("信息", 210);
            listTasks.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            listTasks.DrawItem += (s, e) => { };
            listTasks.DrawSubItem += DrawSubItem;
            listTasks.SelectedIndexChanged += (s, e) => UpdateButtons();
            listTasks.DoubleClick += (s, e) => OpenSelectedFile();
            listTasks.KeyDown += ListTasks_KeyDown;

            taskMenu = new ContextMenuStrip();
            cmStop = new ToolStripMenuItem("停止", null, (s, e) => ForSelected(manager.Stop));
            cmKill = new ToolStripMenuItem("强制停止", null, (s, e) => ForSelected(manager.Kill));
            cmRetry = new ToolStripMenuItem("重新开始", null, (s, e) => ForSelected(manager.Retry));
            cmOpenFile = new ToolStripMenuItem("播放文件", null, (s, e) => OpenSelectedFile());
            cmOpenFolder = new ToolStripMenuItem("打开所在位置", null, (s, e) => RevealSelected());
            cmCopy = new ToolStripMenuItem("复制地址和错误", null, (s, e) => CopySelected());
            cmRemove = new ToolStripMenuItem("从列表移除", null, (s, e) => ForSelected(manager.Remove));
            taskMenu.Items.AddRange(new ToolStripItem[] { cmStop, cmKill, cmRetry, new ToolStripSeparator(), cmOpenFile, cmOpenFolder, cmCopy, new ToolStripSeparator(), cmRemove });
            taskMenu.Opening += TaskMenu_Opening;
            listTasks.ContextMenuStrip = taskMenu;

            button_Clear = new Button { UseVisualStyleBackColor = true };
            button_Clear.Click += (s, e) => manager.ClearEnded();
            // 停止后不用右键也能接着录 / 重下
            button_Retry = new Button { UseVisualStyleBackColor = true };
            button_Retry.Click += (s, e) =>
            {
                if (listTasks.SelectedItems.Count > 0)
                    ForSelected(manager.Retry);
                else
                    manager.RetryAllEnded();
            };

            label_Parallel = new Label { AutoSize = true };
            numParallel = new NumericUpDown { Minimum = 1, Maximum = 8, Value = 3 };
            numParallel.ValueChanged += (s, e) => manager.MaxParallel = (int)numParallel.Value;

            // 工具 → Streamlink 插件（录 B 站、抖音以外的直播间）
            menu_Plugin = new ToolStripMenuItem("Streamlink 插件…", null, (s, e) => ShowPluginDialog());
            工具TToolStripMenuItem.DropDownItems.Add(menu_Plugin);

            Controls.Add(listTasks);
            Controls.Add(button_Clear);
            Controls.Add(button_Retry);
            Controls.Add(label_Parallel);
            Controls.Add(numParallel);

            ProgressBar.Style = ProgressBarStyle.Continuous;
            ProgressBar.Minimum = 0;
            ProgressBar.Maximum = 1000;
            label7.Visible = true;
            label7.AutoSize = false;
            label7.TextAlign = ContentAlignment.MiddleLeft;
            // 设计器给 label1 用的是 ActiveCaptionText，部分主题下是白字，看不清
            label1.ForeColor = label2.ForeColor = label7.ForeColor = SystemColors.ControlText;
        }

        // 设计器是 520x401 的单任务窗口；这里按 DPI 重新排版成任务列表窗口。
        // 切换语言会用 resx 把旧坐标套回去，所以 ChangeLanguage 之后也要再调一次。
        void ApplyLayout()
        {
            float k;
            using (Graphics g = CreateGraphics())
                k = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * k);

            SuspendLayout();
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            // 先把锚点全部去掉再改尺寸，否则改 ClientSize 时旧锚点会把控件拉变形
            foreach (Control c in Controls)
                c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            ClientSize = new Size(S(760), S(620));
            MinimumSize = new Size(S(640), S(520));
            int W = ClientSize.Width, H = ClientSize.Height;
            int pad = S(12), inner = W - 2 * pad;

            menuStrip1.Dock = DockStyle.None;
            menuStrip1.Location = new Point(W - menuStrip1.Width - S(6), S(3));
            menuStrip1.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            label1.Location = new Point(pad, S(12));
            textBox_Adress.Location = new Point(pad, S(34));
            textBox_Adress.Size = new Size(inner, S(110));
            textBox_Adress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            label2.Location = new Point(pad, S(152));
            textBox_Name.Location = new Point(pad, S(172));
            textBox_Name.Size = new Size(S(300), S(23));
            label_Parallel.Location = new Point(S(330), S(176));
            numParallel.Location = new Point(S(420), S(172));
            numParallel.Size = new Size(S(56), S(23));

            int by = S(206), bh = S(34), gap = S(8);
            Button[] buttons = { button_Download, button_Stop, button_ForceStop, button_Retry, button_Clear, button_OpenFolder };
            int bw = (inner - gap * (buttons.Length - 1)) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Location = new Point(pad + i * (bw + gap), by);
                buttons[i].Size = new Size(bw, bh);
            }

            int listTop = by + bh + S(10);
            int bottomArea = S(54);
            listTasks.Location = new Point(pad, listTop);
            listTasks.Size = new Size(inner, H - listTop - bottomArea);
            listTasks.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            int[] widths = { 36, 130, 76, 190, 84, 210 };
            for (int i = 0; i < widths.Length; i++)
                listTasks.Columns[i].Width = S(widths[i]);

            ProgressBar.Location = new Point(pad, H - bottomArea + S(8));
            ProgressBar.Size = new Size(inner, S(14));
            ProgressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            label7.Location = new Point(pad, H - bottomArea + S(26));
            label7.Size = new Size(inner, S(22));
            label7.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            ResumeLayout(true);
        }

        void ApplyTexts()
        {
            bool en = CurrentLanguage == "en";
            label1.Text = en ? "Addresses (one per line, downloaded in parallel)" : "下载地址（每行一个，可同时下载多个）";
            label2.Text = en ? "VOD name prefix (Video0.mp4 ...; live: anchor_room_time.mp4)" : "点播文件名前缀（Video0.mp4…；直播按 主播_房间号_时间 命名）";
            label_Parallel.Text = en ? "Parallel:" : "同时下载：";
            menu_Plugin.Text = en ? "Streamlink plugin..." : "Streamlink 插件…";
            button_Download.Text = en ? "Start" : "开始下载";
            button_Clear.Text = en ? "Clear ended" : "清除已结束";
            button_OpenFolder.Text = en ? "Open folder" : "打开目录";
            string[] cols = en
                ? new[] { "#", "File", "State", "Progress", "Size", "Info" }
                : new[] { "#", "文件", "状态", "进度", "大小", "信息" };
            for (int i = 0; i < cols.Length; i++)
                listTasks.Columns[i].Text = cols[i];
            toolTip1.SetToolTip(button_Stop, en ? "Ask ffmpeg to finish the file (selected tasks, or all)" : "让 ffmpeg 收尾后停止（选中的任务；没选就是全部）");
            toolTip1.SetToolTip(button_ForceStop, en ? "Kill ffmpeg now; the part already written still plays" : "立即结束 ffmpeg；已下载的部分仍可播放");
            toolTip1.SetToolTip(button_Retry, en
                ? "Live: watch the room again and record a new file. VOD: download again to a new file."
                : "没选中时作用于全部已结束的任务（完成的点播除外）。直播：接着盯房间，开播录新文件；点播：重新下载到新文件，不覆盖");
            toolTip1.SetToolTip(numParallel, en ? "VOD only; extra addresses wait in the queue. Live rooms never queue." : "只限点播，超过的排队；直播间不占名额、开播就录");
            UpdateButtons();
            UpdateSummary();
            foreach (var pair in rows)
                FillRow(pair.Value, pair.Key);
        }

        void UpdateButtons()
        {
            bool en = CurrentLanguage == "en";
            bool sel = listTasks.SelectedItems.Count > 0;
            button_Stop.Text = sel ? (en ? "Stop selected" : "停止选中") : (en ? "Stop all" : "全部停止");
            button_ForceStop.Text = sel ? (en ? "Kill selected" : "强制停止选中") : (en ? "Kill all" : "全部强制停止");
            // 6 个按钮挤一行，「全部重新开始」在常见字体下会被截掉；没选中时用「重新开始」，提示里说明作用于全部
            button_Retry.Text = sel ? (en ? "Restart sel." : "重开选中") : (en ? "Restart" : "重新开始");
            var tasks = sel ? SelectedTasks() : manager.Tasks.Where(t => !(t.State == TaskState.Completed && !t.IsLive)).ToList();
            button_Retry.Enabled = tasks.Any(t => t.IsEnded);
        }

        // ---------------- 任务列表 ----------------

        void AddRow(DownloadTask task)
        {
            var item = new ListViewItem(new string[6]) { Tag = task };
            rows[task] = item;
            FillRow(item, task);
            listTasks.Items.Add(item);
            item.EnsureVisible();
            UpdateSummary();
        }

        void RemoveRow(DownloadTask task)
        {
            ListViewItem item;
            if (rows.TryGetValue(task, out item))
            {
                listTasks.Items.Remove(item);
                rows.Remove(task);
            }
            UpdateButtons();
            UpdateSummary();
        }

        void UpdateRow(DownloadTask task)
        {
            ListViewItem item;
            if (rows.TryGetValue(task, out item))
                FillRow(item, task);
            UpdateButtons();
            UpdateSummary();
        }

        void FillRow(ListViewItem item, DownloadTask task)
        {
            bool en = CurrentLanguage == "en";
            SetText(item, 0, task.Id.ToString());
            SetText(item, 1, task.FileName);
            SetText(item, 2, en ? task.State.ToString() : DownloadManager.StateText(task.State));
            SetText(item, 3, ProgressText(task));
            SetText(item, 4, task.SizeBytes > 0 ? FormatFileSize(task.SizeBytes) : "");
            string info = InfoText(task);
            SetText(item, 5, info);
            item.ForeColor = task.State == TaskState.Failed ? Color.FromArgb(192, 57, 43)
                : task.IsEnded && task.State != TaskState.Completed ? Color.DimGray : SystemColors.WindowText;
            item.ToolTipText = task.Source + (task.Error.Length > 0 ? Environment.NewLine + task.Error : "");
            listTasks.Invalidate(item.Bounds);
        }

        string InfoText(DownloadTask task)
        {
            bool en = CurrentLanguage == "en";
            if (task.State == TaskState.Failed && task.Error.Length > 0)
                return task.Error;
            if (!task.IsLive)
                return task.Info.Length > 0 ? task.Info : task.Source;
            var parts = new List<string>();
            string site = task.Site == LiveSite.Douyin ? (en ? "Douyin " : "抖音 ")
                : task.Site == LiveSite.Streamlink ? StreamlinkPlugin.SiteLabel(task.SiteName) + " "
                : (en ? "Bilibili " : "B站 ");
            parts.Add(site + (en ? "room " : "房间 ") + (task.RoomId.Length > 0 ? task.RoomId : task.Source));
            if (task.Sessions > 0)
                parts.Add(en ? task.Sessions + " recorded" : "已录 " + task.Sessions + " 场");
            if (task.State == TaskState.Running || task.State == TaskState.Stopping)
            {
                if (task.Info.Length > 0) parts.Add(task.Info);
            }
            else if (task.Note.Length > 0)
                parts.Add(task.Note);
            return string.Join(en ? ", " : "，", parts);
        }

        static void SetText(ListViewItem item, int col, string text)
        {
            if (item.SubItems[col].Text != text)
                item.SubItems[col].Text = text;
        }

        string ProgressText(DownloadTask task)
        {
            bool en = CurrentLanguage == "en";
            if (task.State == TaskState.Waiting)
                return (en ? "Next check " : "下次检查 ") + task.NextCheck.ToString("HH:mm:ss");
            if (task.State == TaskState.Queued || task.State == TaskState.Resolving || task.State == TaskState.Cancelled)
                return "";
            string done = FormatTime(task.DoneSec);
            if (task.DurationSec > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.0}%  {1} / {2}", task.Percent, done, FormatTime(task.DurationSec));
            if (!task.IsLive)
                return (en ? "Downloaded " : "已下载 ") + done;   // 点播还没读到总时长
            return (task.IsActive ? (en ? "Live " : "直播 ") : (en ? "Recorded " : "已录 ")) + done;
        }

        // 进度列画成静态条：点播按百分比，直播（无总时长）只写时间。不用走马灯，停止后不会自己动
        void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != ColProgress)
            {
                e.DrawDefault = true;
                return;
            }
            var task = (DownloadTask)e.Item.Tag;
            Rectangle r = e.Bounds;
            bool selected = e.Item.Selected;
            using (var bg = new SolidBrush(selected ? SystemColors.Highlight : listTasks.BackColor))
                e.Graphics.FillRectangle(bg, r);
            double pct = task.Percent;
            if (pct >= 0 && task.State != TaskState.Queued && task.State != TaskState.Cancelled && task.State != TaskState.Waiting)
            {
                Rectangle bar = new Rectangle(r.X + 3, r.Y + 3, r.Width - 7, r.Height - 7);
                using (var track = new SolidBrush(Color.FromArgb(230, 230, 230)))
                    e.Graphics.FillRectangle(track, bar);
                Color fill = task.State == TaskState.Failed ? Color.FromArgb(231, 76, 60)
                    : task.IsEnded && task.State != TaskState.Completed ? Color.FromArgb(160, 160, 160)
                    : Color.FromArgb(46, 160, 67);
                int w = (int)(bar.Width * pct / 100.0);
                if (w > 0)
                    using (var b = new SolidBrush(fill))
                        e.Graphics.FillRectangle(b, bar.X, bar.Y, w, bar.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, listTasks.Font, bar, Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, listTasks.Font, r,
                    selected ? SystemColors.HighlightText : listTasks.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        void UpdateSummary()
        {
            bool en = CurrentLanguage == "en";
            var all = manager.Tasks.Where(t => t.State != TaskState.Cancelled).ToList();
            int running = all.Count(t => t.IsActive);
            int queued = all.Count(t => t.State == TaskState.Queued);
            int waiting = all.Count(t => t.State == TaskState.Waiting);
            int done = all.Count(t => t.State == TaskState.Completed);
            int failed = all.Count(t => t.State == TaskState.Failed);
            int stopped = all.Count(t => t.State == TaskState.Stopped || t.State == TaskState.Killed);

            label7.Text = en
                ? string.Format("Running {0}  Waiting {5}  Queued {1}  Done {2}  Failed {3}  Stopped {4}", running, queued, done, failed, stopped, waiting)
                : string.Format("下载中 {0}　等待开播 {5}　排队 {1}　完成 {2}　失败 {3}　已停止 {4}", running, queued, done, failed, stopped, waiting);

            // 总进度：只算完成和进行中的任务。完成算 1，点播按百分比，直播算 0；
            // 停止/失败的不计入，否则全部停掉后会显示 100%
            double sum = 0;
            int counted = 0;
            foreach (var t in all)
            {
                if (t.State == TaskState.Completed) { sum += 1; counted++; }
                else if (!t.IsEnded) { sum += Math.Max(0, t.Percent) / 100.0; counted++; }
            }
            int value = counted == 0 ? 0 : (int)(sum / counted * 1000);
            ProgressBar.Value = Math.Max(0, Math.Min(1000, value));

            bool busy = running + queued + waiting > 0;
            Text = busy
                ? string.Format("M3U8 Downloader {0} - {1}", AppVersion, en ? running + " running" : "下载中 " + running)
                : "M3U8 Downloader " + AppVersion;
            if (!IsHandleCreated)
                return;
            try
            {
                if (!busy)
                    windowsTaskbar.SetProgressState(TaskbarProgressBarState.NoProgress, Handle);
                else
                {
                    windowsTaskbar.SetProgressState(TaskbarProgressBarState.Normal, Handle);
                    windowsTaskbar.SetProgressValue(Math.Max(1, value), 1000, Handle);
                }
            }
            catch { }
        }

        void OnIdle()
        {
            FlashWindow(Handle, true);
            UpdateSummary();
        }

        List<DownloadTask> SelectedTasks()
        {
            return listTasks.SelectedItems.Cast<ListViewItem>().Select(i => (DownloadTask)i.Tag).ToList();
        }

        void ForSelected(Action<DownloadTask> action)
        {
            foreach (var t in SelectedTasks())
                action(t);
        }

        void TaskMenu_Opening(object sender, CancelEventArgs e)
        {
            var sel = SelectedTasks();
            if (sel.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            bool en = CurrentLanguage == "en";
            cmStop.Text = en ? "Stop" : "停止";
            cmKill.Text = en ? "Kill" : "强制停止";
            cmRetry.Text = en ? "Restart" : "重新开始";
            cmOpenFile.Text = en ? "Play file" : "播放文件";
            cmOpenFolder.Text = en ? "Show in folder" : "打开所在位置";
            cmCopy.Text = en ? "Copy address and error" : "复制地址和错误";
            cmRemove.Text = en ? "Remove from list" : "从列表移除";
            cmStop.Enabled = sel.Any(t => t.State == TaskState.Queued || t.State == TaskState.Waiting || t.State == TaskState.Resolving || t.State == TaskState.Running);
            cmKill.Enabled = sel.Any(t => t.IsActive || t.State == TaskState.Queued || t.State == TaskState.Waiting);
            cmRetry.Enabled = sel.Any(t => t.IsEnded);
            bool hasFile = sel.Any(HasFile);
            cmOpenFile.Enabled = hasFile;
            cmOpenFolder.Enabled = hasFile;
            cmRemove.Enabled = sel.Any(t => !t.IsActive);   // 等待开播的移除 = 不再盯
        }

        static bool HasFile(DownloadTask t)
        {
            return t.OutputPath != null && File.Exists(t.OutputPath);
        }

        void ListTasks_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
                ForSelected(manager.Remove);
            else if (e.Control && e.KeyCode == Keys.A)
                foreach (ListViewItem i in listTasks.Items) i.Selected = true;
            else if (e.Control && e.KeyCode == Keys.C)
                CopySelected();
        }

        void OpenSelectedFile()
        {
            var t = SelectedTasks().FirstOrDefault(HasFile);
            if (t != null)
                try { Process.Start(t.OutputPath); } catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        void RevealSelected()
        {
            var t = SelectedTasks().FirstOrDefault(HasFile);
            if (t != null)
                Process.Start("explorer.exe", "/select,\"" + t.OutputPath + "\"");
        }

        void CopySelected()
        {
            var sb = new StringBuilder();
            foreach (var t in SelectedTasks())
            {
                sb.AppendLine(t.Source);
                if (t.Error.Length > 0)
                    sb.AppendLine("  " + t.Error);
            }
            if (sb.Length > 0)
                Clipboard.SetText(sb.ToString());
        }

        // ---------------- 按钮 ----------------

        private void button_Download_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(textBox_Adress.Text))
                return;
            try
            {
                if (!Directory.Exists(m_path))
                    Directory.CreateDirectory(m_path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存目录不可用：" + ex.Message, "M3U8 Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string prefix = textBox_Name.Text.Trim();
            if (prefix.Length == 0)
                prefix = "Video";
            manager.MaxParallel = (int)numParallel.Value;
            if (manager.AddLines(textBox_Adress.Text, prefix).Count > 0)
                textBox_Adress.Clear();   // 已经进了列表；不清空的话再点一次会重复下载
        }

        private void button_Stop_Click(object sender, EventArgs e)
        {
            if (listTasks.SelectedItems.Count > 0)
                ForSelected(manager.Stop);
            else
                manager.StopAll();
        }

        private void button_ForceStop_Click(object sender, EventArgs e)
        {
            if (listTasks.SelectedItems.Count > 0)
            {
                ForSelected(manager.Kill);
                return;
            }
            if (!manager.IsBusy)
                return;
            string msg = CurrentLanguage == "en"
                ? "Kill all downloads now? The part already written stays playable."
                : "立即强制停止全部任务吗？已下载的部分仍可播放。";
            if (MessageBox.Show(msg, "M3U8 Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                manager.KillAll();
        }

        private void button_OpenFolder_Click(object sender, EventArgs e)
        {
            try { Process.Start(m_path); } catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        // ---------------- 设置 ----------------

        public static String FormatFileSize(Double fileSize)
        {
            if (fileSize < 0)
                throw new ArgumentOutOfRangeException("fileSize");
            if (fileSize >= 1024 * 1024 * 1024)
                return string.Format("{0:0.00} GB", fileSize / (1024 * 1024 * 1024));
            if (fileSize >= 1024 * 1024)
                return string.Format("{0:0.00} MB", fileSize / (1024 * 1024));
            if (fileSize >= 1024)
                return string.Format("{0:0.00} KB", fileSize / 1024);
            return string.Format("{0} B", fileSize);
        }

        static string FormatTime(double sec)
        {
            if (sec < 0) sec = 0;
            var t = TimeSpan.FromSeconds(Math.Floor(sec));
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        public void CreateSettingFile(string xmlPath)
        {
            XElement xElement = new XElement("Settings",
                new XElement("DownPath", m_path),
                new XElement("EnableProxy", 0),
                new XElement("HttpProxy", m_proxy),
                new XElement("MaxParallel", 3));
            XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
            using (XmlWriter xw = XmlWriter.Create(xmlPath, settings))
                xElement.Save(xw);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            if (!File.Exists(manager.FfmpegPath))
            {
                MessageBox.Show("没有找到Tools\\ffmpeg.exe" + Environment.NewLine + "Missing Tools\\ffmpeg.exe", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Dispose();
                Application.Exit();
                return;
            }

            if (File.Exists(SettingsPath))
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(SettingsPath);
                    m_path = doc.SelectSingleNode("//DownPath").InnerText;
                    m_proxy = doc.SelectSingleNode("//HttpProxy").InnerText;
                    menu_Proxy.CheckState = doc.SelectSingleNode("//EnableProxy").InnerText.Trim() == "1" ? CheckState.Checked : CheckState.Unchecked;
                    XmlNode sl = doc.SelectSingleNode("//StreamlinkPath");
                    if (sl != null)
                        StreamlinkPlugin.ConfiguredPath = sl.InnerText.Trim();
                    XmlNode par = doc.SelectSingleNode("//MaxParallel");
                    int n;
                    if (par != null && int.TryParse(par.InnerText, out n))
                        numParallel.Value = Math.Max(numParallel.Minimum, Math.Min(numParallel.Maximum, n));
                }
                catch
                {
                    m_path = Environment.CurrentDirectory;
                    m_proxy = "";
                }
            }
            else
            {
                m_path = Environment.CurrentDirectory;
                m_proxy = "";
                CreateSettingFile(SettingsPath);
            }
            if (string.IsNullOrWhiteSpace(m_path))
                m_path = Environment.CurrentDirectory;
            if (m_proxy == null)
                m_proxy = "";
            manager.MaxParallel = (int)numParallel.Value;
            UpdateSummary();
        }

        void SaveSettingsOnExit()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    CreateSettingFile(SettingsPath);
                XmlDocument doc = new XmlDocument();
                doc.Load(SettingsPath);
                doc.SelectSingleNode("//EnableProxy").InnerText = menu_Proxy.Checked ? "1" : "0";
                XmlNode par = doc.SelectSingleNode("//MaxParallel");
                if (par == null)
                {
                    par = doc.CreateElement("MaxParallel");
                    doc.DocumentElement.AppendChild(par);
                }
                par.InnerText = ((int)numParallel.Value).ToString();
                XmlNode sl = doc.SelectSingleNode("//StreamlinkPath");
                if (sl == null)
                {
                    sl = doc.CreateElement("StreamlinkPath");
                    doc.DocumentElement.AppendChild(sl);
                }
                sl.InnerText = StreamlinkPlugin.ConfiguredPath ?? "";
                doc.Save(SettingsPath);
            }
            catch { }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (manager.IsBusy)
            {
                string msg = CurrentLanguage == "en"
                    ? "Downloads are still running. Stop them and exit?"
                    : "还有任务在下载，停止全部并退出吗？";
                if (MessageBox.Show(msg, "M3U8 Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                Cursor = Cursors.WaitCursor;
                // 每个 ffmpeg 发 q，最多一起等 5 秒，剩下的强杀；分片 MP4 被强杀也能播
                manager.ShutdownBlocking(5000);
            }
            SaveSettingsOnExit();
        }

        private void menu_Proxy_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(m_proxy))
            {
                MessageBox.Show("请设置代理后使用！" + Environment.NewLine + "Please select proxy!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                menu_Proxy.CheckState = CheckState.Unchecked;
                return;
            }
            if (!m_proxy.StartsWith("http://"))
            {
                MessageBox.Show("代理地址格式错误！" + Environment.NewLine + "The proxy address format is incorrect!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                menu_Proxy.CheckState = CheckState.Unchecked;
                return;
            }
            menu_Proxy.CheckState = menu_Proxy.CheckState == CheckState.Unchecked ? CheckState.Checked : CheckState.Unchecked;
        }

        void ShowPluginDialog()
        {
            using (var dlg = new PluginForm(CurrentLanguage == "en"))
            {
                dlg.ShowDialog(this);
                StreamlinkPlugin.ConfiguredPath = dlg.ChosenPath;
            }
            SaveSettingsOnExit();
        }

        private void menu_About_Click(object sender, EventArgs e)
        {
            MessageBox.Show("M3U8 Downloader " + AppVersion + "\n基于 magicdmer / nilaoda 的原版\n" + ReleasesUrl, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void menu_Set_Click(object sender, EventArgs e)
        {
            SetForm setDlg = new SetForm(CurrentLanguage == "zh" ? "default" : CurrentLanguage);
            if (DialogResult.OK == setDlg.ShowDialog())
            {
                m_path = setDlg.m_path;
                m_proxy = setDlg.m_proxy ?? "";
            }
        }

        private void menu_FFmepg_Click(object sender, EventArgs e)
        {
            Process.Start("https://ffmpeg.org/download.html#build-windows");
        }

        private void 软件更新ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(ReleasesUrl);
        }

        private void LanguageChinese_Click(object sender, EventArgs e)
        {
            ChangeLanguage("zh");
        }

        private void LanguageEnglish_Click(object sender, EventArgs e)
        {
            ChangeLanguage("en");
        }

        private void ChangeLanguage(string languageCode)
        {
            CurrentLanguage = languageCode;
            ComponentResourceManager resources = new ComponentResourceManager(typeof(Form1));
            var culture = new CultureInfo(languageCode);
            foreach (Control c in this.Controls)
            {
                if (c == listTasks || c == button_Clear || c == button_Retry || c == label_Parallel || c == numParallel)
                    continue;
                resources.ApplyResources(c, c.Name, culture);
                if (c is MenuStrip)
                {
                    foreach (ToolStripMenuItem menuitem in ((MenuStrip)c).Items)
                    {
                        resources.ApplyResources(menuitem, menuitem.Name, culture);
                        foreach (var submenuitem in menuitem.DropDownItems)
                            if (submenuitem is ToolStripMenuItem)
                                resources.ApplyResources(submenuitem, ((ToolStripMenuItem)submenuitem).Name, culture);
                    }
                }
            }
            // resx 会把旧的单任务坐标和文字套回来
            textBox_forRegex.Visible = false;
            label8.Visible = false;
            label7.Visible = true;
            label7.AutoSize = false;
            ApplyLayout();
            ApplyTexts();
        }
    }
}
