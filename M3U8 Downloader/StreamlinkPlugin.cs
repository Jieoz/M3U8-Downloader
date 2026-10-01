using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace M3U8_Downloader
{
    /// <summary>
    /// 可选插件：用户自己装的 Streamlink（https://streamlink.github.io），给 B 站、抖音以外的直播间
    /// 查开播状态和播放地址，录制仍由内置 ffmpeg 来做（盯播、命名、换线、整理时长都和原生一样）。
    /// 软件不内置 Streamlink；没装时这些地址照旧当普通地址交给 ffmpeg。
    /// 只调用 `streamlink --json [--http-proxy P] 地址`，解析它给的 metadata 和 streams。
    /// </summary>
    public static class StreamlinkPlugin
    {
        public const string DownloadPage = "https://github.com/streamlink/windows-builds/releases/latest";
        public const string PluginsPage = "https://streamlink.github.io/plugins.html";

        // 用户在「工具 → Streamlink 插件」里手动选的路径（存进设置）；测试时直接指向假 streamlink
        public static string ConfiguredPath = "";
        public static int TimeoutMs = 60000;

        static readonly string[] MediaExt = {
            ".m3u8", ".m3u", ".mp4", ".flv", ".ts", ".mpd", ".m4s", ".m4v", ".mkv", ".mov", ".webm",
            ".avi", ".wmv", ".mp3", ".aac", ".m4a", ".f4v"
        };

        /// <summary>找 streamlink.exe：手动设置 → 软件目录 Tools\streamlink* → Program Files\Streamlink → PATH。找不到返回 null。</summary>
        public static string Find()
        {
            if (!string.IsNullOrWhiteSpace(ConfiguredPath) && File.Exists(ConfiguredPath))
                return ConfiguredPath;
            var candidates = new List<string>();
            try
            {
                string tools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
                candidates.Add(Path.Combine(tools, "streamlink", "bin", "streamlink.exe"));
                if (Directory.Exists(tools))
                    foreach (string dir in Directory.GetDirectories(tools, "streamlink*"))
                    {
                        candidates.Add(Path.Combine(dir, "bin", "streamlink.exe"));
                        candidates.Add(Path.Combine(dir, "streamlink.exe"));
                    }
            }
            catch (Exception) { }
            // 本程序是 32 位：ProgramFiles 在 64 位系统上指向 Program Files (x86)，安装版在 ProgramW6432 下
            foreach (string env in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
            {
                string root = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(root))
                    continue;
                candidates.Add(Path.Combine(root, "Streamlink", "bin", "streamlink.exe"));
                candidates.Add(Path.Combine(root, "Programs", "Streamlink", "bin", "streamlink.exe"));
            }
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(Path.PathSeparator))
                if (dir.Trim().Length > 0)
                    candidates.Add(Path.Combine(dir.Trim().Trim('"'), "streamlink.exe"));
            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; }
                catch (Exception) { }
            }
            return null;
        }

        public static bool Available { get { return Find() != null; } }

        /// <summary>像网页（http/https、不是 .m3u8/.mp4 这类媒体文件）的地址才交给 Streamlink 试。</summary>
        public static bool LooksLikePage(string input)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out u))
                return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                return false;
            string ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            return !MediaExt.Contains(ext);
        }

        /// <summary>直播间的固定标识，用在文件名和界面上：地址最后一段路径（YouTube 这种用 ?v=）。</summary>
        public static string RoomKey(string url)
        {
            Uri u;
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u))
                return "live";
            string[] parts = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string key = parts.Length > 0 ? Uri.UnescapeDataString(parts[parts.Length - 1]) : "";
            foreach (string kv in u.Query.TrimStart('?').Split('&'))
                if (kv.StartsWith("v=", StringComparison.Ordinal) && kv.Length > 2)
                    key = Uri.UnescapeDataString(kv.Substring(2));
            if (key.Length == 0)
                key = u.Host;
            return key;
        }

        /// <summary>界面上显示的站名：常见的中文名，其余用插件名。</summary>
        public static string SiteLabel(string plugin)
        {
            switch ((plugin ?? "").ToLowerInvariant())
            {
                case "huya": return "虎牙";
                case "douyu": return "斗鱼";
                case "bilibili": return "B站";
                case "douyin": return "抖音";
                case "twitch": return "Twitch";
                case "youtube": return "YouTube";
                case "": return "Streamlink";
                default: return plugin;
            }
        }

        public static string Version(string exe)
        {
            string stdout, stderr;
            int code = Run(exe, "--version", 15000, out stdout, out stderr);
            string text = (stdout + " " + stderr).Trim();
            return code == 0 && text.Length > 0 ? text : null;
        }

        /// <summary>
        /// 查一次。Streamlink 没有这个站的插件 → StreamlinkUnsupportedException（调用方改当普通地址下载）；
        /// 「没有可播放的流」= 没开播；其他错误（网络、站点改版）稍后再查。
        /// </summary>
        public static LiveRoomStatus Check(string url, string proxy)
        {
            string exe = Find();
            if (exe == null)
                throw new LiveRoomException("没找到 Streamlink 插件（工具 → Streamlink 插件）", true);
            var args = new StringBuilder("--json ");
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                proxy = proxy.Trim();
                if (!proxy.Contains("://"))
                    proxy = "http://" + proxy;
                args.Append("--http-proxy ").Append(LiveCommon.Quote(proxy)).Append(' ');
            }
            args.Append(LiveCommon.Quote(url.Trim()));
            string stdout, stderr;
            int code = Run(exe, args.ToString(), TimeoutMs, out stdout, out stderr);
            if (code == int.MinValue)
                throw new LiveRoomException("Streamlink 超过 " + TimeoutMs / 1000 + " 秒没返回", false);
            return Parse(stdout, stderr, url);
        }

        public static LiveRoomStatus Parse(string json, string stderr, string url)
        {
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json ?? "");
            }
            catch (Exception)
            {
                root = null;
            }
            if (root == null)
            {
                string tail = (stderr ?? "").Trim();
                if (tail.Length > 200) tail = tail.Substring(tail.Length - 200);
                throw new LiveRoomException("Streamlink 没有返回结果" + (tail.Length > 0 ? "：" + tail : ""), false);
            }
            string error = LiveCommon.Str(root, "error");
            var status = new LiveRoomStatus { RoomId = RoomKey(url) };
            if (error.Length > 0)
            {
                if (error.StartsWith("No plugin can handle URL", StringComparison.OrdinalIgnoreCase))
                    throw new StreamlinkUnsupportedException(error);
                if (error.StartsWith("No playable streams found", StringComparison.OrdinalIgnoreCase))
                    return status;   // 没开播
                throw new LiveRoomException("Streamlink：" + error, false);
            }
            status.Site = LiveCommon.Str(root, "plugin");
            var meta = LiveCommon.AsDict(root, "metadata");
            status.Anchor = LiveCommon.Str(meta, "author").Trim();
            var streams = LiveCommon.AsDict(root, "streams");
            status.Streams = PickStreams(streams);
            if (status.Streams.Count == 0)
            {
                if (streams.Count == 0)
                    return status;
                var types = streams.Values.OfType<Dictionary<string, object>>().Select(s => LiveCommon.Str(s, "type")).Distinct();
                throw new LiveRoomException("Streamlink 给出的流类型（" + string.Join("/", types) + "）内置 ffmpeg 录不了", false);
            }
            foreach (var s in status.Streams)
                s.RoomId = status.RoomId;
            status.Live = true;
            return status;
        }

        // best 在前，其余按 Streamlink 给的顺序从高到低（它是从低到高列的）；同一个地址只留一次。
        // 只要 ffmpeg 能直接录的 hls / http（flv 等）。
        static List<LiveStream> PickStreams(Dictionary<string, object> streams)
        {
            var names = new List<string>();
            if (streams.ContainsKey("best"))
                names.Add("best");
            names.AddRange(streams.Keys.Where(k => k != "best" && k != "worst").Reverse());
            var list = new List<LiveStream>();
            foreach (string name in names)
            {
                var s = streams[name] as Dictionary<string, object>;
                string type = LiveCommon.Str(s, "type").ToLowerInvariant();
                string streamUrl = LiveCommon.Str(s, "url");
                if ((type != "hls" && type != "http") || streamUrl.Length == 0 || list.Any(x => x.Url == streamUrl))
                    continue;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in LiveCommon.AsDict(s, "headers"))
                    if (kv.Value != null)
                        headers[kv.Key] = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                string referer;
                headers.TryGetValue("Referer", out referer);
                string label = name;
                if (name == "best")
                {
                    // best 是某个具体档位的别名，显示成那个档位的名字
                    string alias = streams.Keys.FirstOrDefault(k => k != "best" && k != "worst"
                        && LiveCommon.Str(streams[k] as Dictionary<string, object>, "url") == streamUrl);
                    if (alias != null) label = alias;
                }
                list.Add(new LiveStream
                {
                    Url = streamUrl,
                    FormatName = type == "hls" ? "hls" : (streamUrl.IndexOf(".flv", StringComparison.OrdinalIgnoreCase) >= 0 ? "flv" : "http"),
                    CodecName = "",
                    QualityName = label,
                    Referer = referer,
                    Headers = headers
                });
                if (list.Count >= 8)
                    break;
            }
            return list;
        }

        /// <summary>跑一次，返回退出码；超时杀掉并返回 int.MinValue。</summary>
        static int Run(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            var p = new Process();
            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments = args;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            // Windows 上 Python 往管道写默认用系统代码页，中文主播名会乱码；强制 UTF-8
            p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            p.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
            using (p)
            {
                p.Start();
                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    stdout = stderr = "";
                    return int.MinValue;
                }
                p.WaitForExit();
                stdout = outTask.Result;
                stderr = errTask.Result;
                return p.ExitCode;
            }
        }
    }

    /// <summary>Streamlink 没有这个网站的插件：不是它认识的直播间，按普通地址下载。</summary>
    public sealed class StreamlinkUnsupportedException : Exception
    {
        public StreamlinkUnsupportedException(string message) : base(message) { }
    }
}
