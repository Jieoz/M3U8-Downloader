using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace M3U8_Downloader
{
    /// <summary>工具 → Streamlink 插件：显示是否装好、在哪、什么版本；给出下载地址；可以手动指定 streamlink.exe。</summary>
    public class PluginForm : Form
    {
        readonly bool en;
        readonly Label labelStatus, labelPath, labelHelp;
        readonly Button buttonDownload, buttonBrowse, buttonClear, buttonRecheck, buttonClose;
        readonly LinkLabel linkHome;

        /// <summary>用户手动选的路径（空 = 自动查找）。对话框关闭后由调用方存进设置。</summary>
        public string ChosenPath { get; private set; }

        public PluginForm(bool english)
        {
            en = english;
            ChosenPath = StreamlinkPlugin.ConfiguredPath ?? "";
            Text = en ? "Streamlink plugin" : "Streamlink 插件";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 300);

            labelHelp = new Label
            {
                Location = new Point(16, 14),
                Size = new Size(528, 76),
                Text = en
                    ? "Bilibili and Douyin work without it. For other live sites (Huya, Douyu, Twitch, YouTube ...), install Streamlink (free, about 64 MB, 64-bit Windows). This program then asks Streamlink whether the room is live and records with its own ffmpeg. Not bundled."
                    : "B 站、抖音不需要它。录其他网站的直播间（虎牙、斗鱼、Twitch、YouTube 等）要另装 Streamlink（免费，约 64 MB，需要 64 位 Windows）。装好后本软件用它查开播和取播放地址，录制仍用内置 ffmpeg。软件不内置 Streamlink。"
            };
            linkHome = new LinkLabel
            {
                Location = new Point(16, 92),
                AutoSize = true,
                Text = en ? "Supported sites: " + StreamlinkPlugin.PluginsPage
                          : "支持的网站列表：" + StreamlinkPlugin.PluginsPage
            };
            linkHome.LinkClicked += (s, e) => Open(StreamlinkPlugin.PluginsPage);
            labelStatus = new Label { Location = new Point(16, 124), Size = new Size(528, 22), Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            labelPath = new Label { Location = new Point(16, 148), Size = new Size(528, 40) };

            buttonDownload = new Button { Location = new Point(16, 200), Size = new Size(170, 32), UseVisualStyleBackColor = true,
                Text = en ? "Open download page" : "打开下载页" };
            buttonDownload.Click += (s, e) => Open(StreamlinkPlugin.DownloadPage);
            buttonBrowse = new Button { Location = new Point(196, 200), Size = new Size(170, 32), UseVisualStyleBackColor = true,
                Text = en ? "Choose streamlink.exe..." : "指定 streamlink.exe…" };
            buttonBrowse.Click += (s, e) => Browse();
            buttonRecheck = new Button { Location = new Point(376, 200), Size = new Size(168, 32), UseVisualStyleBackColor = true,
                Text = en ? "Check again" : "重新检测" };
            buttonRecheck.Click += (s, e) => Refresh2();
            buttonClear = new Button { Location = new Point(16, 248), Size = new Size(170, 32), UseVisualStyleBackColor = true,
                Text = en ? "Auto-detect path" : "改回自动查找" };
            buttonClear.Click += (s, e) => { ChosenPath = ""; StreamlinkPlugin.ConfiguredPath = ""; Refresh2(); };
            buttonClose = new Button { Location = new Point(444, 248), Size = new Size(100, 32), UseVisualStyleBackColor = true,
                Text = en ? "Close" : "关闭", DialogResult = DialogResult.OK };
            AcceptButton = CancelButton = buttonClose;

            Controls.AddRange(new Control[] { labelHelp, linkHome, labelStatus, labelPath, buttonDownload, buttonBrowse, buttonRecheck, buttonClear, buttonClose });
            Shown += (s, e) => Refresh2();
        }

        void Browse()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = en ? "Choose streamlink.exe" : "选择 streamlink.exe（在 Streamlink 目录的 bin 里）";
                dlg.Filter = "streamlink.exe|streamlink.exe|*.exe|*.exe";
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                ChosenPath = dlg.FileName;
                StreamlinkPlugin.ConfiguredPath = ChosenPath;
                Refresh2();
            }
        }

        // 查版本要启动一次 streamlink（1~2 秒），放后台，别卡界面
        void Refresh2()
        {
            string exe = StreamlinkPlugin.Find();
            buttonClear.Enabled = ChosenPath.Length > 0;
            if (exe == null)
            {
                labelStatus.ForeColor = Color.FromArgb(192, 57, 43);
                labelStatus.Text = en ? "Not installed" : "未安装";
                labelPath.Text = ChosenPath.Length > 0 && !File.Exists(ChosenPath)
                    ? (en ? "The chosen file no longer exists: " : "指定的文件已不存在：") + ChosenPath
                    : (en ? "Install it (installer or portable zip), then click Check again. Portable: unzip into Tools\\streamlink next to this program, or choose bin\\streamlink.exe."
                          : "下载安装版或便携版后点「重新检测」。便携版可以解压到本软件的 Tools\\streamlink 目录，或者用「指定 streamlink.exe」选它 bin 里的 streamlink.exe。");
                return;
            }
            labelStatus.ForeColor = SystemColors.ControlText;
            labelStatus.Text = en ? "Found, checking version..." : "已找到，正在检查版本…";
            labelPath.Text = exe;
            buttonRecheck.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string version = null;
                try { version = StreamlinkPlugin.Version(exe); } catch (Exception) { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        buttonRecheck.Enabled = true;
                        if (version != null)
                        {
                            labelStatus.ForeColor = Color.FromArgb(39, 120, 60);
                            labelStatus.Text = (en ? "Ready: " : "可用：") + version;
                        }
                        else
                        {
                            labelStatus.ForeColor = Color.FromArgb(192, 57, 43);
                            labelStatus.Text = en ? "Found but it does not run (32-bit Windows is not supported)" : "找到了但运行不了（Streamlink 不支持 32 位 Windows）";
                        }
                    }));
                }
                catch (InvalidOperationException) { }   // 对话框已关
            });
        }

        static void Open(string url)
        {
            try { Process.Start(url); } catch (Exception) { }
        }
    }
}
