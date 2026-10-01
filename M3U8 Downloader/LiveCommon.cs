using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace M3U8_Downloader
{
    public enum LiveSite { None, Bilibili, Douyin }

    /// <summary>
    /// 各直播站共用：识别是哪个站、HTTP 请求（走设置里的代理）、拼 ffmpeg 录制参数。
    /// 站点自己的接口解析在 BilibiliLive / DouyinLive。
    /// </summary>
    public static class LiveCommon
    {
        // 设置里开了 HTTP 代理时，查房间、展开短链也走同一个代理（和 ffmpeg 保持一致）
        public static Func<string> Proxy = () => null;

        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        /// <summary>只看格式，不联网。</summary>
        public static LiveSite Detect(string input)
        {
            if (BilibiliLive.LooksLikeLive(input))
                return LiveSite.Bilibili;
            if (DouyinLive.LooksLikeLive(input))
                return LiveSite.Douyin;
            return LiveSite.None;
        }

        public static LiveRoomStatus Check(LiveSite site, string source, string knownRoom)
        {
            if (site == LiveSite.Douyin)
                return DouyinLive.Check(source, knownRoom);
            string roomId = knownRoom;
            if (string.IsNullOrEmpty(roomId) && !BilibiliLive.TryParseRoomId(source, out roomId))
                throw new LiveRoomException("认不出直播间地址", true);
            return BilibiliLive.Check(roomId);
        }

        static void ApplyProxy(HttpWebRequest request)
        {
            string proxy = Proxy();
            if (string.IsNullOrWhiteSpace(proxy))
                return;
            proxy = proxy.Trim();
            if (!proxy.Contains("://"))
                proxy = "http://" + proxy;
            request.Proxy = new WebProxy(new Uri(proxy));
        }

        static HttpWebRequest NewRequest(string url, string referer, string cookie)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            if (!string.IsNullOrEmpty(referer))
                request.Referer = referer;
            if (!string.IsNullOrEmpty(cookie))
                request.Headers[HttpRequestHeader.Cookie] = cookie;
            ApplyProxy(request);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            return request;
        }

        public static string HttpGet(string url, string referer, string cookie = null)
        {
            var request = NewRequest(url, referer, cookie);
            request.Accept = "application/json, text/plain, */*";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>不跟随跳转，返回 Location（绝对地址）；没有跳转返回原地址。</summary>
        public static string GetRedirect(string url)
        {
            var request = NewRequest(url, null, null);
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string location = response.Headers["Location"];
                if (string.IsNullOrEmpty(location))
                    return url;
                return new Uri(new Uri(url), location).AbsoluteUri;
            }
        }

        /// <summary>取某个 Set-Cookie 的值（只要 name=value 部分），没有返回空串。</summary>
        public static string GetCookie(string url, string name)
        {
            var request = NewRequest(url, null, null);
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string[] all = response.Headers.GetValues("Set-Cookie") ?? new string[0];
                foreach (string header in all)
                    foreach (string part in header.Split(','))
                    {
                        string p = part.Trim();
                        if (p.StartsWith(name + "=", StringComparison.Ordinal))
                        {
                            int semi = p.IndexOf(';');
                            return semi > 0 ? p.Substring(0, semi) : p;
                        }
                    }
                return "";
            }
        }

        public static string BuildRecordCommand(LiveStream stream, string outputPath, string httpProxy)
        {
            var command = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(httpProxy))
                command.Append("-http_proxy ").Append(Quote(httpProxy.Trim())).Append(' ');
            // 断流先在 ffmpeg 里重试，尽量不把一场切成多段：HLS 单个分片失败重试 3 次；
            // 列表连续刷不出新分片默认 3 次就当结束，放宽到 20 次（约 40 秒卡顿）；
            // FLV 是单条 HTTP 长连接，用 reconnect
            if (string.Equals(stream.FormatName, "flv", StringComparison.OrdinalIgnoreCase))
                command.Append("-reconnect 1 -reconnect_streamed 1 -reconnect_on_network_error 1 -reconnect_delay_max 10 ");
            else
                command.Append("-seg_max_retry 3 -max_reload 20 ");
            command.Append("-rw_timeout 15000000");
            // 用 ffmpeg 的 -referer / -user_agent，HLS 会把它们带到每个分片请求上。
            if (!string.IsNullOrEmpty(stream.Referer))
                command.Append(" -referer ").Append(Quote(stream.Referer));
            command.Append(" -user_agent ").Append(Quote(UserAgent));
            command.Append(" -i ").Append(Quote(stream.Url));
            command.Append(" -c copy ").Append(DownloadManager.Mp4Flags).Append(' ');
            command.Append(Quote(outputPath));
            return command.ToString();
        }

        public static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        // ---------- JSON 小工具（JavaScriptSerializer 的结果） ----------

        public static Dictionary<string, object> AsDict(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent != null && parent.TryGetValue(key, out value))
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null)
                    return dict;
            }
            return new Dictionary<string, object>();
        }

        public static System.Collections.ArrayList AsList(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent != null && parent.TryGetValue(key, out value))
            {
                var list = value as System.Collections.ArrayList;
                if (list != null)
                    return list;
            }
            return new System.Collections.ArrayList();
        }

        public static string Str(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent != null && parent.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }

        public static int Int(Dictionary<string, object> parent, string key, int fallback)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    public sealed class LiveRoomStatus
    {
        public string RoomId;      // B 站：真实（长）房间号；抖音：直播间号 web_rid（每场不变）
        public string Anchor = ""; // 接口顺带给了主播名就填（抖音），B 站另查
        public bool Live;
        public List<LiveStream> Streams = new List<LiveStream>();  // Live 时才有，好的在前
    }

    public sealed class LiveRoomException : Exception
    {
        public readonly bool Permanent;
        public LiveRoomException(string message, bool permanent) : base(message)
        {
            Permanent = permanent;
        }
    }

    public sealed class LiveStream
    {
        public string RoomId { get; set; }
        public string Url { get; set; }
        public string FormatName { get; set; }
        public string CodecName { get; set; }
        public int Quality { get; set; }
        public string QualityName { get; set; }  // 界面显示用，如 qn=10000 / 原画
        public string Referer { get; set; }
    }
}
