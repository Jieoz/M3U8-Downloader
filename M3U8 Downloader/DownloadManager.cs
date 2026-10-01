using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace M3U8_Downloader
{
    public enum TaskState
    {
        Queued,     // 排队中
        Resolving,  // 解析直播间
        Running,    // ffmpeg 运行中
        Stopping,   // 已发 q，等待收尾
        Completed,  // 正常下完
        Stopped,    // 用户停止（文件可播）
        Killed,     // 用户强制停止（分片 MP4，已写入部分可播）
        Failed,     // ffmpeg 报错或解析失败
        Cancelled   // 排队时被取消，从未启动
    }

    public class DownloadTask
    {
        public int Id;
        public string Source;          // 用户输入的那一行
        public string BaseName;        // 前缀 + 行号，例如 Video0
        public string OutputPath;      // 启动时才分配，保证不覆盖；排队时为 null
        public TaskState State = TaskState.Queued;
        public bool IsLive;
        public double DurationSec = -1; // -1 = 未知（直播）
        public double DoneSec;
        public long SizeBytes;
        public string Info = "";        // 分辨率 / 帧率 / 直播线路
        public string Error = "";
        internal Process Proc;
        internal bool StopRequested;
        internal bool KillRequested;
        internal Timer KillTimer;
        internal readonly LinkedList<string> Tail = new LinkedList<string>();
        internal string ProgressTime;   // 当前 progress 块里的值
        internal long ProgressSize = -1;

        public string FileName
        {
            get { return OutputPath != null ? Path.GetFileName(OutputPath) : BaseName + ".mp4"; }
        }

        public bool IsActive
        {
            get { return State == TaskState.Resolving || State == TaskState.Running || State == TaskState.Stopping; }
        }

        public bool IsEnded
        {
            get { return !IsActive && State != TaskState.Queued; }
        }

        public double Percent
        {
            get
            {
                if (State == TaskState.Completed) return 100;
                if (DurationSec <= 0) return -1;
                return Math.Max(0, Math.Min(100, DoneSec / DurationSec * 100));
            }
        }
    }

    /// <summary>
    /// 多任务下载引擎：每个地址一个 ffmpeg 进程，最多同时跑 MaxParallel 个，其余排队。
    /// 所有状态只在 post 进来的回调里改（WinForms 下就是 UI 线程），不需要加锁；
    /// 进程输出线程只往 Tail 里追加日志（加锁）并 post 进度。
    /// </summary>
    public class DownloadManager
    {
        // 分片 MP4：边下边写可独立解码的片段，强杀 / 断网也能播放已下部分。
        // 不能带 empty_moov：直播流是 ADTS AAC，没有 aac_adtstoasc 时 empty_moov 要求的
        // 音频头信息拿不到，ffmpeg 会立刻 "Error muxing a packet" 退出（2.2.3 秒结束的根因）。
        public const string Mp4Flags = "-movflags +frag_keyframe+default_base_moof -flush_packets 1";
        public const int StopGraceMs = 15000;

        readonly Action<Action> post;
        readonly List<DownloadTask> tasks = new List<DownloadTask>();
        readonly HashSet<string> reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nextId = 1;

        public int MaxParallel = 3;
        public string FfmpegPath = Path.Combine("Tools", "ffmpeg.exe");
        public Func<string> OutputDir = () => Environment.CurrentDirectory;
        public Func<string> Proxy = () => null;

        public event Action<DownloadTask> TaskAdded;
        public event Action<DownloadTask> TaskChanged;
        public event Action<DownloadTask> TaskRemoved;
        // 队列从忙变闲时触发一次
        public event Action BecameIdle;

        public DownloadManager(Action<Action> post)
        {
            this.post = post;
        }

        public IList<DownloadTask> Tasks { get { return tasks.AsReadOnly(); } }

        public int CountActive { get { return tasks.FindAll(t => t.IsActive).Count; } }
        public int CountQueued { get { return tasks.FindAll(t => t.State == TaskState.Queued).Count; } }
        public bool IsBusy { get { return CountActive + CountQueued > 0; } }

        /// <summary>每行一个地址；空行忽略。文件名 = 前缀 + 行号，已存在就加 (1)、(2)。</summary>
        public List<DownloadTask> AddLines(string text, string prefix)
        {
            var added = new List<DownloadTask>();
            string[] lines = (text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int index = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                var task = new DownloadTask { Id = nextId++, Source = line, BaseName = prefix + index.ToString(CultureInfo.InvariantCulture) };
                index++;
                tasks.Add(task);
                added.Add(task);
                Raise(TaskAdded, task);
            }
            Pump();
            return added;
        }

        public void Retry(DownloadTask task)
        {
            if (!task.IsEnded)
                return;
            var copy = new DownloadTask { Id = nextId++, Source = task.Source, BaseName = task.BaseName };
            tasks.Add(copy);
            Raise(TaskAdded, copy);
            Pump();
        }

        public void Remove(DownloadTask task)
        {
            if (task.IsActive)
                return;
            tasks.Remove(task);
            Raise(TaskRemoved, task);
            if (task.State == TaskState.Queued)
                CheckIdle(true);
        }

        public void ClearEnded()
        {
            foreach (var t in tasks.FindAll(x => x.IsEnded))
                Remove(t);
        }

        /// <summary>停止：排队中的直接取消；运行中的发 q 收尾，15 秒没退就强杀。</summary>
        public void Stop(DownloadTask task)
        {
            if (task.State == TaskState.Queued)
            {
                task.State = TaskState.Cancelled;
                Raise(TaskChanged, task);
                CheckIdle(true);
                return;
            }
            if (task.State == TaskState.Resolving)
            {
                task.StopRequested = true;   // 解析完回来会看到这个标记，不再启动
                return;
            }
            if (task.State != TaskState.Running)
                return;
            task.StopRequested = true;
            task.State = TaskState.Stopping;
            Raise(TaskChanged, task);
            try
            {
                task.Proc.StandardInput.Write("q");
                task.Proc.StandardInput.Flush();
            }
            catch { }
            task.KillTimer = new Timer(_ => post(() => { if (task.State == TaskState.Stopping) Kill(task); }),
                null, StopGraceMs, Timeout.Infinite);
        }

        public void Kill(DownloadTask task)
        {
            if (task.State == TaskState.Queued || task.State == TaskState.Resolving)
            {
                Stop(task);
                return;
            }
            if (task.State != TaskState.Running && task.State != TaskState.Stopping)
                return;
            task.KillRequested = true;
            try { task.Proc.Kill(); } catch { }
        }

        public void StopAll()
        {
            // 先取消排队的，否则运行中的一结束就会补位启动
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving))
                Stop(t);
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Running))
                Stop(t);
        }

        public void KillAll()
        {
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving))
                Stop(t);
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Running || x.State == TaskState.Stopping))
                Kill(t);
        }

        /// <summary>退出程序用：全部发 q，最多等 waitMs，剩下的强杀。阻塞调用方。</summary>
        public void ShutdownBlocking(int waitMs)
        {
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving))
            {
                t.StopRequested = true;
                t.State = TaskState.Cancelled;
            }
            var running = tasks.FindAll(x => x.Proc != null && (x.State == TaskState.Running || x.State == TaskState.Stopping));
            foreach (var t in running)
            {
                t.StopRequested = true;
                try { t.Proc.StandardInput.Write("q"); t.Proc.StandardInput.Flush(); } catch { }
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            foreach (var t in running)
            {
                int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try
                {
                    if (!t.Proc.WaitForExit(left))
                        t.Proc.Kill();
                }
                catch { }
            }
        }

        // ---------------- 调度 ----------------

        bool wasBusy;

        void Pump()
        {
            int slots = Math.Max(1, MaxParallel) - CountActive;
            foreach (var t in tasks.ToArray())
            {
                if (slots <= 0)
                    break;
                if (t.State != TaskState.Queued)
                    continue;
                slots--;
                Begin(t);
            }
            CheckIdle(false);
        }

        void CheckIdle(bool pumpFirst)
        {
            if (pumpFirst && CountQueued > 0 && CountActive < MaxParallel)
            {
                Pump();
                return;
            }
            bool busy = IsBusy;
            if (wasBusy && !busy)
            {
                wasBusy = false;
                var handler = BecameIdle;
                if (handler != null) handler();
            }
            if (busy)
                wasBusy = true;
        }

        void Begin(DownloadTask task)
        {
            task.State = TaskState.Resolving;
            task.OutputPath = ReservePath(OutputDir(), task.BaseName);
            Raise(TaskChanged, task);
            string source = task.Source;
            string proxy = Proxy();
            // 解析直播间要走网络（短链跳转 + getRoomPlayInfo），放到后台线程，避免卡界面
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string args = null, error = null, info = null;
                bool live = false;
                try
                {
                    string roomId;
                    if (BilibiliLive.TryParseRoomId(source, out roomId))
                    {
                        live = true;
                        LiveStream stream = BilibiliLive.Resolve(roomId);
                        info = "B站直播 " + roomId + " " + stream.FormatName + "/" + stream.CodecName + " qn=" + stream.Quality;
                        args = BilibiliLive.BuildRecordCommand(stream.Url, task.OutputPath, proxy);
                    }
                    else
                    {
                        args = BuildVodCommand(source, task.OutputPath, proxy);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                post(() => Launch(task, args, live, info, error));
            });
        }

        public static string BuildVodCommand(string input, string output, string proxy)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(proxy))
                sb.Append("-http_proxy ").Append(BilibiliLive.Quote(proxy.Trim())).Append(' ');
            sb.Append("-rw_timeout 10000000 -i ").Append(BilibiliLive.Quote(input));
            sb.Append(" -c copy -bsf:a aac_adtstoasc ").Append(Mp4Flags).Append(' ');
            sb.Append(BilibiliLive.Quote(output));
            return sb.ToString();
        }

        void Launch(DownloadTask task, string args, bool live, string info, string error)
        {
            task.IsLive = live;
            if (info != null)
                task.Info = info;
            if (task.StopRequested)
            {
                Finish(task, TaskState.Cancelled, null);
                return;
            }
            if (error != null)
            {
                Finish(task, TaskState.Failed, error);
                return;
            }

            var p = new Process();
            p.StartInfo.FileName = FfmpegPath;
            // -n：目标已存在就报错退出，绝不覆盖；-progress 给机器可读进度，-nostats 去掉刷屏行
            p.StartInfo.Arguments = "-hide_banner -nostats -progress pipe:1 -n " + args;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                Finish(task, TaskState.Failed, "无法启动 ffmpeg：" + ex.Message);
                return;
            }
            task.Proc = p;
            task.State = TaskState.Running;
            // 每条输出流一个专用线程读。BeginOutputReadLine 在 .NET Framework 上每条流占一个
            // 线程池线程阻塞读，同时开几个任务就会把线程池占满，后启动的任务拿不到进度。
            // 两条流都读到 EOF（进程已退出、输出已读完）才收尾，不会漏最后几行错误。
            int open = 2;
            Action streamDone = () =>
            {
                if (Interlocked.Decrement(ref open) == 0)
                    post(() => OnExited(task));
            };
            StartReader(p.StandardOutput, line => OnStdout(task, line), streamDone, "ffmpeg-out-" + task.Id);
            StartReader(p.StandardError, line => OnStderr(task, line), streamDone, "ffmpeg-err-" + task.Id);
            Raise(TaskChanged, task);
        }

        static void StartReader(StreamReader reader, Action<string> onLine, Action onEnd, string name)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        onLine(line);
                }
                catch { }
                onEnd();
            });
            thread.IsBackground = true;
            thread.Name = name;
            thread.Start();
        }

        // -progress 输出：一组 key=value，以 progress=continue/end 结尾
        void OnStdout(DownloadTask task, string line)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                return;
            string key = line.Substring(0, eq), value = line.Substring(eq + 1).Trim();
            if (key == "out_time_us" || key == "out_time_ms")   // 两者在 ffmpeg 里都是微秒
                task.ProgressTime = value;
            else if (key == "total_size")
            {
                long size;
                if (long.TryParse(value, out size)) task.ProgressSize = size;
            }
            else if (key == "progress")
            {
                string time = task.ProgressTime;
                long size = task.ProgressSize;
                post(() =>
                {
                    if (!task.IsActive) return;
                    long us;
                    if (time != null && long.TryParse(time, out us) && us > 0)
                        task.DoneSec = us / 1000000.0;
                    if (size >= 0)
                        task.SizeBytes = size;
                    Raise(TaskChanged, task);
                });
            }
        }

        static readonly Regex DurationRe = new Regex(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
        static readonly Regex VideoRe = new Regex(@"Video: .*?, (\d{2,}x\d{2,}).*?(?:, ([\d.]+) fps)?", RegexOptions.Compiled);

        void OnStderr(DownloadTask task, string line)
        {
            lock (task.Tail)
            {
                task.Tail.AddLast(line);
                while (task.Tail.Count > 30) task.Tail.RemoveFirst();
            }
            Match d = DurationRe.Match(line);
            if (d.Success)
            {
                double sec = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                    + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                post(() => { task.DurationSec = sec > 0 ? sec : -1; Raise(TaskChanged, task); });
                return;
            }
            Match v = VideoRe.Match(line);
            if (v.Success && line.Contains("Stream #0:"))
            {
                string res = v.Groups[1].Value;
                Match fps = Regex.Match(line, @", ([\d.]+) fps");
                string text = res + (fps.Success ? " " + fps.Groups[1].Value + "fps" : "");
                post(() =>
                {
                    if (!task.Info.Contains(res))
                        task.Info = (task.Info.Length > 0 ? task.Info + "，" : "") + text;
                    Raise(TaskChanged, task);
                });
            }
        }

        void OnExited(DownloadTask task)
        {
            if (task.Proc == null || !task.IsActive)
                return;
            int code = -1;
            try
            {
                task.Proc.WaitForExit();   // 输出流已到 EOF，这里只等进程句柄，立即返回
                code = task.Proc.ExitCode;
            }
            catch { }
            try { task.SizeBytes = new FileInfo(task.OutputPath).Length; } catch { }

            if (task.KillRequested)
                Finish(task, TaskState.Killed, null);
            else if (task.StopRequested)
                Finish(task, TaskState.Stopped, null);
            else if (code == 0)
                Finish(task, TaskState.Completed, null);
            else
                Finish(task, TaskState.Failed, LastError(task, code));
        }

        static string LastError(DownloadTask task, int code)
        {
            lock (task.Tail)
            {
                var node = task.Tail.Last;
                while (node != null)
                {
                    string s = node.Value.Trim();
                    if (s.Length > 0 && Regex.IsMatch(s, "error|failed|invalid|denied|not found|refused|timed out|exists|forbidden|403|404", RegexOptions.IgnoreCase))
                        return s;
                    node = node.Previous;
                }
                return "ffmpeg 退出码 " + code + (task.Tail.Count > 0 ? "：" + task.Tail.Last.Value.Trim() : "");
            }
        }

        void Finish(DownloadTask task, TaskState state, string error)
        {
            task.State = state;
            if (error != null)
                task.Error = error;
            if (task.KillTimer != null)
            {
                task.KillTimer.Dispose();
                task.KillTimer = null;
            }
            if (task.Proc != null)
            {
                try { task.Proc.Dispose(); } catch { }
                task.Proc = null;
            }
            // 没生成文件就释放名字，下一个任务还能用 Video0.mp4
            if (task.OutputPath != null && !File.Exists(task.OutputPath))
                reservedPaths.Remove(task.OutputPath);
            Raise(TaskChanged, task);
            Pump();
        }

        // ---------------- 文件名 ----------------

        // 已有同名文件、或别的任务已经占用这个名字时，依次试 "名字 (1).mp4"、"名字 (2).mp4"
        string ReservePath(string dir, string baseName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(c, '_');
            string path = Path.Combine(dir, baseName + ".mp4");
            for (int n = 1; File.Exists(path) || reservedPaths.Contains(path); n++)
                path = Path.Combine(dir, baseName + " (" + n + ").mp4");
            reservedPaths.Add(path);
            return path;
        }

        void Raise(Action<DownloadTask> handler, DownloadTask task)
        {
            if (handler != null)
                handler(task);
        }

        public static string StateText(TaskState s)
        {
            switch (s)
            {
                case TaskState.Queued: return "排队中";
                case TaskState.Resolving: return "解析中";
                case TaskState.Running: return "下载中";
                case TaskState.Stopping: return "正在停止";
                case TaskState.Completed: return "完成";
                case TaskState.Stopped: return "已停止";
                case TaskState.Killed: return "已强制停止";
                case TaskState.Failed: return "失败";
                case TaskState.Cancelled: return "已取消";
            }
            return s.ToString();
        }
    }
}
