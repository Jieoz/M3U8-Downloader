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
        Queued,     // 排队中（只有点播排队）
        Resolving,  // 解析直播间 / 检查是否开播
        Waiting,    // 直播间没开播，定时再查
        Running,    // ffmpeg 运行中
        Stopping,   // 已发 q，等待收尾
        Finalizing, // ffmpeg 已退出，正在把分片 MP4 整理成普通 MP4（时长、拖动才正常）
        Completed,  // 正常下完
        Stopped,    // 用户停止（文件可播）
        Killed,     // 用户强制停止（分片 MP4，已写入部分可播）
        Failed,     // ffmpeg 报错或解析失败
        Cancelled   // 排队时被取消，从未启动
    }

    /// <summary>同一批地址的点播文件名序号：Video0、Video1…</summary>
    public sealed class VodCounter
    {
        public string Prefix;
        int next;
        public string Take() { return Prefix + (next++).ToString(CultureInfo.InvariantCulture); }
    }

    public class DownloadTask
    {
        public int Id;
        public string Source;          // 用户输入的那一行
        public string BaseName;        // 点播：前缀 + 行号，例如 Video0；直播启动时按主播名生成
        public string OutputPath;      // 启动时才分配，保证不覆盖；排队时为 null。直播是最近一场的文件
        public TaskState State = TaskState.Queued;
        public LiveSite Site;           // None = 点播
        public bool IsLive { get { return Site != LiveSite.None; } }
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

        // 直播盯房间
        public string RoomId = "";      // 真实房间号
        public string SiteName = "";    // Streamlink 插件名（huya、douyu…）
        internal VodCounter VodNames;   // Streamlink 不认识这个网页时，从同一批的点播序号里接着取名
        public string Anchor = "";      // 主播名
        public int Sessions;            // 已录场数
        public bool Recording;          // 正在录（StartLine 置位、EndSession 复位）：EndSession 防重入用
        public DateTime NextCheck;      // Waiting 时下次检查的时间
        public string Note = "";        // 最近一次检查失败 / 上一场结束原因
        internal List<LiveStream> Lines = new List<LiveStream>();  // 这次开播查到的全部线路，好的在前
        internal int LineIndex;         // 正在用第几条
        internal bool AnchorLoaded;
        internal Timer WatchTimer;
        internal int WatchGen;          // 每次停止 / 重新计时 +1，过期的定时器回调直接丢掉
        internal DateTime SessionStart;
        internal Process FixProc;       // 整理文件用的 ffmpeg

        public string FileName
        {
            get
            {
                if (OutputPath != null) return Path.GetFileName(OutputPath);
                if (!string.IsNullOrEmpty(BaseName)) return BaseName + ".mp4";
                // 直播还没开录：先显示主播名 / 房间号
                string who = Anchor.Length > 0 ? Anchor + " " : "";
                return who + (RoomId.Length > 0 ? RoomId : Source);
            }
        }

        public bool IsActive
        {
            get { return State == TaskState.Resolving || State == TaskState.Running || State == TaskState.Stopping || State == TaskState.Finalizing; }
        }

        public bool IsEnded
        {
            get { return !IsActive && State != TaskState.Queued && State != TaskState.Waiting; }
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
    /// 多任务下载引擎：每个地址一个 ffmpeg 进程。点播最多同时跑 MaxParallel 个，其余排队；
    /// 直播不占名额、不排队：没开播就进入「等待开播」定时检查，开播就录，下播后接着等下一场，
    /// 每场一个新文件，直到用户停止。
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
        // 直播间检查间隔：基础间隔 + 随机 0~LiveCheckJitterMs，免得几个房间同一秒请求
        public int LiveCheckIntervalMs = 60000;
        public int LiveCheckJitterMs = 10000;
        readonly Random random = new Random();
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
            LiveCommon.Proxy = () => Proxy();
        }

        public IList<DownloadTask> Tasks { get { return tasks.AsReadOnly(); } }

        public int CountActive { get { return tasks.FindAll(t => t.IsActive).Count; } }
        // 占「同时下载」名额的只有点播
        int CountVodActive { get { return tasks.FindAll(t => t.IsActive && !t.IsLive).Count; } }
        public int CountQueued { get { return tasks.FindAll(t => t.State == TaskState.Queued).Count; } }
        public int CountWaiting { get { return tasks.FindAll(t => t.State == TaskState.Waiting).Count; } }
        public bool IsBusy { get { return CountActive + CountQueued + CountWaiting > 0; } }

        /// <summary>
        /// 每行一个地址；空行忽略。点播文件名 = 前缀 + 点播行号（Video0、Video1…），直播按
        /// 「主播名_房间号_开始时间」命名。已存在就加 (1)、(2)。
        /// </summary>
        public List<DownloadTask> AddLines(string text, string prefix)
        {
            var added = new List<DownloadTask>();
            string[] lines = (text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var vod = new VodCounter { Prefix = prefix };
            int liveIndex = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                var task = new DownloadTask { Id = nextId++, Source = line, Site = LiveCommon.Detect(line) };
                if (!task.IsLive)
                    task.BaseName = vod.Take();
                else if (task.Site == LiveSite.Streamlink)   // 插件不认识时才按点播取名，认出是直播间就不占序号
                    task.VodNames = vod;
                tasks.Add(task);
                added.Add(task);
                Raise(TaskAdded, task);
                if (task.IsLive)
                    StartWatch(task, (liveIndex++) * 1500);   // 同一批的直播间错开 1.5 秒查
            }
            Pump();
            return added;
        }

        /// <summary>
        /// 已结束的任务原地重新开始（同一行）：直播接着盯房间、开播就录新文件，已录场数保留；
        /// 点播重新下载到新文件名，不覆盖上次的文件。
        /// </summary>
        public void Retry(DownloadTask task)
        {
            if (!task.IsEnded)
                return;
            task.StopRequested = false;
            task.KillRequested = false;
            task.Error = "";
            task.Note = "";
            task.DoneSec = 0;
            task.DurationSec = -1;
            task.ProgressTime = null;
            task.ProgressSize = -1;
            lock (task.Tail) task.Tail.Clear();
            if (task.IsLive)
            {
                StartWatch(task, 0);
                return;
            }
            task.OutputPath = null;
            task.SizeBytes = 0;
            task.Info = "";
            task.State = TaskState.Queued;
            Raise(TaskChanged, task);
            Pump();
        }

        public void Remove(DownloadTask task)
        {
            if (task.State == TaskState.Waiting)
                Stop(task);
            if (task.IsActive)
                return;
            tasks.Remove(task);
            Raise(TaskRemoved, task);
            if (task.State == TaskState.Queued)
                CheckIdle(true);
        }

        /// <summary>没选中时「全部重新开始」：已停止 / 强制停止 / 失败 / 取消的；完成的点播不重下。</summary>
        public void RetryAllEnded()
        {
            foreach (var t in tasks.FindAll(x => x.IsEnded && !(x.State == TaskState.Completed && !x.IsLive)))
                Retry(t);
        }

        public void ClearEnded()
        {
            foreach (var t in tasks.FindAll(x => x.IsEnded))
                Remove(t);
        }

        /// <summary>停止：排队中的直接取消；运行中的发 q 收尾，15 秒没退就强杀。</summary>
        public void Stop(DownloadTask task)
        {
            if (task.State == TaskState.Waiting)
            {
                // 不再盯这个房间
                CancelWatch(task);
                task.StopRequested = true;
                Finish(task, task.Sessions > 0 ? TaskState.Stopped : TaskState.Cancelled, null);
                return;
            }
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
            if (task.State == TaskState.Queued || task.State == TaskState.Resolving || task.State == TaskState.Waiting)
            {
                Stop(task);
                return;
            }
            if (task.State == TaskState.Finalizing)
            {
                // 不等整理了：保留原文件（能播，只是时长显示不对）
                task.KillRequested = true;
                if (task.FixProc != null)
                    try { task.FixProc.Kill(); } catch { }
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
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving || x.State == TaskState.Waiting))
                Stop(t);
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Running))
                Stop(t);
        }

        public void KillAll()
        {
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving || x.State == TaskState.Waiting))
                Stop(t);
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Running || x.State == TaskState.Stopping || x.State == TaskState.Finalizing))
                Kill(t);
        }

        /// <summary>退出程序用：全部发 q，最多等 waitMs，剩下的强杀。阻塞调用方。</summary>
        public void ShutdownBlocking(int waitMs)
        {
            foreach (var t in tasks.FindAll(x => x.State == TaskState.Queued || x.State == TaskState.Resolving || x.State == TaskState.Waiting))
            {
                CancelWatch(t);
                t.StopRequested = true;
                t.State = TaskState.Cancelled;
            }
            // 正在整理的直接停掉：原文件还在，只是时长显示不对
            foreach (var t in tasks.FindAll(x => x.FixProc != null))
                try { t.FixProc.Kill(); } catch { }
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
            int slots = Math.Max(1, MaxParallel) - CountVodActive;
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
            if (pumpFirst && CountQueued > 0 && CountVodActive < MaxParallel)
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

        // 点播：分配文件名后直接启动 ffmpeg
        void Begin(DownloadTask task)
        {
            task.OutputPath = ReservePath(OutputDir(), task.BaseName);
            Launch(task, BuildVodCommand(task.Source, task.OutputPath, Proxy()), null);
        }

        // ---------------- 直播：盯房间 ----------------

        void StartWatch(DownloadTask task, int delayMs)
        {
            if (delayMs <= 0)
            {
                CheckRoom(task);
                return;
            }
            task.State = TaskState.Waiting;
            task.NextCheck = DateTime.Now.AddMilliseconds(delayMs);
            ScheduleCheck(task, delayMs);
            Raise(TaskChanged, task);
        }

        void ScheduleCheck(DownloadTask task, int delayMs)
        {
            CancelWatch(task);
            int gen = task.WatchGen;
            task.WatchTimer = new Timer(_ => post(() =>
            {
                if (task.WatchGen == gen && task.State == TaskState.Waiting)
                    CheckRoom(task);
            }), null, Math.Max(0, delayMs), Timeout.Infinite);
        }

        void CancelWatch(DownloadTask task)
        {
            task.WatchGen++;
            if (task.WatchTimer != null)
            {
                task.WatchTimer.Dispose();
                task.WatchTimer = null;
            }
        }

        // 盯播历史：输出目录下 历史.csv，一行一个事实（开播/下播/中断/检查失败/断档恢复/手动停止）。
        // 「检查失败」= 轮询时接口没答上（含请求超时）；「断档恢复」= 超时窗口内开播了也没错过，
        // 配合文件名里的开播时间，每场是否录到、断在哪，关掉软件也能事后翻账。
        void LogLiveEvent(DownloadTask task, string ev, string detail)
        {
            try
            {
                string dir = OutputDir();
                if (string.IsNullOrEmpty(dir)) return;
                string room = task.RoomId.Length > 0 ? task.RoomId : task.Source;
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, "历史.csv");
                using (var w = new StreamWriter(path, true, new UTF8Encoding(true)))   // 带 BOM：Excel 双击打开不乱码
                    w.WriteLine(Csv(time) + "," + Csv(room) + "," + Csv(ev) + "," + Csv(detail));
            }
            catch { }   // 记录失败不影响盯播
        }

        static string Csv(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        int NextInterval()
        {
            return LiveCheckIntervalMs + (LiveCheckJitterMs > 0 ? random.Next(LiveCheckJitterMs) : 0);
        }

        // 后台查一次：识别房间号（短链要跳转）→ 开播状态 → 主播名（只查一次）
        void CheckRoom(DownloadTask task)
        {
            CancelWatch(task);
            task.State = TaskState.Resolving;
            task.StopRequested = false;
            Raise(TaskChanged, task);
            string source = task.Source, knownRoom = task.RoomId;
            LiveSite site = task.Site;
            bool needAnchor = !task.AnchorLoaded;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LiveRoomStatus status = null;
                string anchor = null, error = null;
                bool permanent = false, unsupported = false;
                try
                {
                    status = LiveCommon.Check(site, source, knownRoom);
                    if (status.Anchor.Length > 0)
                        anchor = status.Anchor;   // 抖音接口顺带给了
                    else if (needAnchor && site == LiveSite.Bilibili)
                        anchor = BilibiliLive.GetAnchorName(status.RoomId);
                }
                catch (StreamlinkUnsupportedException ex)
                {
                    error = ex.Message;
                    unsupported = true;
                }
                catch (LiveRoomException ex)
                {
                    error = ex.Message;
                    permanent = ex.Permanent;
                }
                catch (Exception ex)
                {
                    error = ex.Message;   // 网络错误：稍后再查
                }
                if (unsupported)
                    post(() => FallBackToVod(task));
                else
                    post(() => OnRoomChecked(task, status, anchor, error, permanent));
            });
        }

        // Streamlink 没有这个网站的插件：不是直播间，按普通地址排队下载（和没装插件时一样）
        void FallBackToVod(DownloadTask task)
        {
            if (task.State != TaskState.Resolving)
                return;
            if (task.StopRequested)
            {
                Finish(task, TaskState.Cancelled, null);
                return;
            }
            task.Site = LiveSite.None;
            task.BaseName = task.VodNames != null ? task.VodNames.Take() : "Video";
            task.Note = "";
            task.State = TaskState.Queued;
            Raise(TaskChanged, task);
            Pump();
        }

        void OnRoomChecked(DownloadTask task, LiveRoomStatus status, string anchor, string error, bool permanent)
        {
            if (task.State != TaskState.Resolving)
                return;
            if (task.StopRequested)
            {
                Finish(task, task.Sessions > 0 ? TaskState.Stopped : TaskState.Cancelled, null);
                return;
            }
            if (status != null)
            {
                task.RoomId = status.RoomId;
                if (status.Site.Length > 0)
                    task.SiteName = status.Site;
            }
            if (anchor != null)
            {
                task.Anchor = anchor;
                task.AnchorLoaded = anchor.Length > 0;   // 没查到下次再查
            }
            if (error != null)
            {
                if (permanent)
                {
                    Finish(task, TaskState.Failed, error);
                    return;
                }
                bool firstFail = task.Note.Length == 0;   // 连续失败只记第一笔，不刷屏
                task.Note = "检查失败：" + error + "（" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "）";
                if (firstFail)
                    LogLiveEvent(task, "检查失败", error);
                Wait(task, NextInterval());
                return;
            }
            bool afterFail = task.Note.StartsWith("检查失败");
            if (!status.Live)
            {
                if (afterFail)
                {
                    // 仍离线=恢复检查；界面从「检查失败…」换成「检查已恢复」，不再残留旧错误
                    LogLiveEvent(task, "恢复检查", task.Note);
                    task.Note = "检查已恢复（" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "）";
                }
                Wait(task, NextInterval());
                return;
            }

            if (afterFail)   // 开播=断档恢复（超时/断网期间播了，没错过）
                LogLiveEvent(task, "断档恢复", task.Note);
            task.Note = "";
            task.SessionStart = DateTime.Now;
            task.BaseName = LiveBaseName(task.Anchor, task.RoomId, task.SessionStart);
            task.Lines = status.Streams;
            task.LineIndex = 0;
            LogLiveEvent(task, "开播", (task.Anchor.Length > 0 ? task.Anchor + "，" : "") + task.Lines.Count + " 条线路");
            StartLine(task);
        }

        // 用 task.Lines[task.LineIndex] 开录。换线时沿用同一个文件名（上一条没写出东西，名字已释放）
        void StartLine(DownloadTask task)
        {
            task.OutputPath = ReservePath(OutputDir(), task.BaseName);
            task.DoneSec = 0;
            task.SizeBytes = 0;
            task.DurationSec = -1;
            task.ProgressTime = null;
            task.ProgressSize = -1;
            lock (task.Tail) task.Tail.Clear();
            task.Recording = true;
            LiveStream stream = task.Lines[task.LineIndex];
            task.Info = stream.FormatName + "/" + stream.CodecName + " " + stream.QualityName
                + (task.Lines.Count > 1 ? " 线路 " + (task.LineIndex + 1) + "/" + task.Lines.Count : "");
            Launch(task, LiveCommon.BuildRecordCommand(stream, task.OutputPath, Proxy()), null);
        }

        void Wait(DownloadTask task, int delayMs)
        {
            task.State = TaskState.Waiting;
            task.NextCheck = DateTime.Now.AddMilliseconds(delayMs);
            ScheduleCheck(task, delayMs);
            Raise(TaskChanged, task);
            CheckIdle(false);
        }

        /// <summary>主播名_房间号_yyyyMMdd-HHmm（抖音是直播间号）；查不到主播名就只用房间号。</summary>
        public static string LiveBaseName(string anchor, string roomId, DateTime start)
        {
            string time = start.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            anchor = (anchor ?? "").Trim();
            return (anchor.Length > 0 ? anchor + "_" : "") + roomId + "_" + time;
        }

        // 一场录完（下播、断流且 ffmpeg 自己重连不上、或 ffmpeg 报错）：留下文件，接着等下一场
        void EndSession(DownloadTask task, int code)
        {
            bool manual = task.KillRequested || task.StopRequested;   // OnExited 与 EndSession 都可能到这
            if (!task.Recording) return;   // 防重入：已结束过/从未开始录（断档恢复路径误入）就不再记
            task.Recording = false;
            if (manual) return;   // 手动停止由 AfterExit 记「手动停止」，这里不重复

            try { task.SizeBytes = new FileInfo(task.OutputPath).Length; } catch { }
            double ran = (DateTime.Now - task.SessionStart).TotalSeconds;
            if (task.Proc != null)
            {
                try { task.Proc.Dispose(); } catch { }
                task.Proc = null;
            }
            bool wrote = File.Exists(task.OutputPath) && task.SizeBytes > 0;
            if (!wrote)
            {
                try { if (File.Exists(task.OutputPath)) File.Delete(task.OutputPath); } catch { }
                reservedPaths.Remove(task.OutputPath);
                // 这条线路一个字节都没录到（CDN 节点连不上、被拒）：马上换下一条，不用重新查接口
                if (task.LineIndex + 1 < task.Lines.Count)
                {
                    task.LineIndex++;
                    task.Note = "线路 " + task.LineIndex + " 连不上，换下一条：" + LastError(task, code);
                    StartLine(task);
                    return;
                }
            }
            else
                task.Sessions++;
            task.Lines = new List<LiveStream>();
            if (!manual)
                LogLiveEvent(task, wrote ? (code == 0 ? "下播" : "中断") : "所有线路连不上",
                    task.BaseName + (wrote ? "，" + FormatSize(task.SizeBytes) : "，" + LastError(task, code)));
            task.Note = code == 0 ? "上一场已结束" : (wrote ? "上一场中断：" : "所有线路都连不上：") + LastError(task, code);
            // 刚开就断（多半是地址失效或被拒）：等 30 秒再查，别连着打接口；否则 2 秒后确认是不是真下播了
            Wait(task, ran < 60 ? Math.Min(30000, LiveCheckIntervalMs) : 2000);
        }

        public static string BuildVodCommand(string input, string output, string proxy)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(proxy))
                sb.Append("-http_proxy ").Append(LiveCommon.Quote(proxy.Trim())).Append(' ');
            sb.Append("-rw_timeout 10000000 -i ").Append(LiveCommon.Quote(input));
            sb.Append(" -c copy -bsf:a aac_adtstoasc ").Append(Mp4Flags).Append(' ');
            sb.Append(LiveCommon.Quote(output));
            return sb.ToString();
        }

        void Launch(DownloadTask task, string args, string error)
        {
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
            try { task.Proc.Dispose(); } catch { }
            task.Proc = null;
            try { task.SizeBytes = new FileInfo(task.OutputPath).Length; } catch { }
            if (task.SizeBytes > 0)
                FixFile(task, () => AfterExit(task, code));
            else
                AfterExit(task, code);
        }

        void AfterExit(DownloadTask task, int code)
        {
            // 手动停掉的直播这场也算录过（重新开始后场数接着累计）
            if (task.IsLive && (task.KillRequested || task.StopRequested) && task.SizeBytes > 0)
            {
                task.Sessions++;
                LogLiveEvent(task, "手动停止", task.BaseName + "，已录 " + FormatSize(task.SizeBytes));
            }
            if (task.KillRequested)
                Finish(task, TaskState.Killed, null);
            else if (task.StopRequested)
                Finish(task, TaskState.Stopped, null);
            else if (task.IsLive)
                EndSession(task, code);
            else if (code == 0)
                Finish(task, TaskState.Completed, null);
            else
                Finish(task, TaskState.Failed, LastError(task, code) + PluginHint(task));
        }

        // 网页地址下载失败、又没装 Streamlink：多半是别的网站的直播间，提示一下插件
        static string PluginHint(DownloadTask task)
        {
            if (!StreamlinkPlugin.LooksLikePage(task.Source) || StreamlinkPlugin.Available)
                return "";
            return "（如果这是直播间网页：装 Streamlink 插件后可以录，见 工具 → Streamlink 插件）";
        }

        // 分片 MP4 的头里只记了第一个分片的时长（直播约 2~4 秒），播放器显示的总时长、
        // 进度条拖动都按这个来，虽然整段能播。录完把它原样转封装成普通 MP4（不重新编码，
        // 只读写一遍文件）。整理失败就保留原文件，照样能播。
        void FixFile(DownloadTask task, Action done)
        {
            string src = task.OutputPath;
            string tmp = src + ".fix.mp4";
            task.State = TaskState.Finalizing;
            Raise(TaskChanged, task);
            var p = new Process();
            p.StartInfo.FileName = FfmpegPath;
            p.StartInfo.Arguments = "-hide_banner -loglevel error -y -i " + LiveCommon.Quote(src)
                + " -map 0:v? -map 0:a? -c copy " + LiveCommon.Quote(tmp);
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string note = null;
                try
                {
                    p.Start();
                    post(() => task.FixProc = p);
                    p.StandardOutput.ReadToEndAsync();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    long srcLen = new FileInfo(src).Length;
                    long tmpLen = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
                    // 转封装几乎不改变大小；明显变小说明没读全，不替换
                    if (p.ExitCode == 0 && tmpLen > srcLen / 2)
                    {
                        File.Delete(src);
                        File.Move(tmp, src);
                    }
                    else
                        note = "整理文件失败，保留原文件（可播放，时长显示可能不对）" + (err.Trim().Length > 0 ? "：" + LastLine(err) : "");
                }
                catch (Exception ex)
                {
                    note = "整理文件失败，保留原文件：" + ex.Message;
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    try { p.Dispose(); } catch { }
                }
                post(() =>
                {
                    task.FixProc = null;
                    try { task.SizeBytes = new FileInfo(src).Length; } catch { }
                    if (note != null)
                        task.Note = note;
                    done();
                });
            });
        }

        static string LastLine(string text)
        {
            string[] lines = text.Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        static string FormatSize(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
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
            CancelWatch(task);
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
                case TaskState.Resolving: return "检查中";
                case TaskState.Waiting: return "等待开播";
                case TaskState.Running: return "下载中";
                case TaskState.Stopping: return "正在停止";
                case TaskState.Finalizing: return "整理文件";
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
