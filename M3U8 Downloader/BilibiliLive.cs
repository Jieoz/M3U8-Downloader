using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace M3U8_Downloader
{
    /// <summary>
    /// B 站直播间：识别地址、查开播状态和主播名、拼 ffmpeg 录制参数。
    /// 只用 getRoomPlayInfo 和 get_anchor_in_room 两个接口（2026-10 实测不需要登录、
    /// 没触发风控；getInfoByRoom 已经返回 -352，不用）。
    /// </summary>
    public static class BilibiliLive
    {
        // 测试时指向本地假接口
        public static string ApiBase = "https://api.live.bilibili.com";
        // 设置里开了 HTTP 代理时，查房间、展开短链也走同一个代理（和 ffmpeg 保持一致）
        public static Func<string> Proxy = () => null;

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

        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        public const string Referer = "https://live.bilibili.com/";

        static readonly Regex RoomUrl = new Regex(
            @"https?://live\.bilibili\.com/(?:blanc/|h5/)?(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex ShareUrl = new Regex(
            @"https?://(?:b23\.tv|bili2233\.cn)/(?<code>[A-Za-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParseRoomId(string input, out string roomId)
        {
            roomId = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;
            input = input.Trim();
            Match match = RoomUrl.Match(input);
            if (match.Success)
            {
                roomId = match.Groups["id"].Value;
                return true;
            }
            if (Regex.IsMatch(input, @"^\d{1,12}$"))
            {
                roomId = input;
                return true;
            }
            Match share = ShareUrl.Match(input);
            if (!share.Success)
                return false;
            string landed = ExpandShareUrl("https://b23.tv/" + share.Groups["code"].Value);
            match = RoomUrl.Match(landed);
            if (!match.Success)
                return false;
            roomId = match.Groups["id"].Value;
            return true;
        }

        /// <summary>只看格式，不联网：房间链接、纯数字房间号、b23.tv 短链都算直播。</summary>
        public static bool LooksLikeLive(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;
            input = input.Trim();
            return RoomUrl.IsMatch(input) || Regex.IsMatch(input, @"^\d{1,12}$") || ShareUrl.IsMatch(input);
        }

        public static string BuildPlayInfoUrl(string roomId)
        {
            return ApiBase + "/xlive/web-room/v2/index/getRoomPlayInfo"
                + "?room_id=" + Uri.EscapeDataString(roomId)
                + "&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8";
        }

        /// <summary>
        /// 查一次房间。未开播返回 Live=false（live_status 0 未开播、2 轮播都算）。
        /// 房间不存在抛 LiveRoomException(permanent: true)；网络错误、风控码等抛普通异常，调用方稍后再查。
        /// </summary>
        public static LiveRoomStatus Check(string roomId)
        {
            string json = HttpGet(BuildPlayInfoUrl(roomId));
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
                throw new InvalidDataException("B 站接口返回空内容");

            int code = ToInt(root, "code", -1);
            if (code != 0)
            {
                string message = ToString(root, "message");
                string text = "B 站接口 code " + code + (string.IsNullOrEmpty(message) ? "" : "：" + message);
                // 60004 = 房间不存在；再查也不会变
                if (code == 60004)
                    throw new LiveRoomException("房间 " + roomId + " 不存在", true);
                throw new LiveRoomException(text, false);
            }

            var data = AsDict(root, "data");
            var status = new LiveRoomStatus { RoomId = roomId };
            string longId = ToString(data, "room_id");
            if (longId.Length > 0 && longId != "0")
                status.RoomId = longId;   // 短号（如 6）换成真实房间号，文件名用这个
            if (ToInt(data, "live_status", 0) != 1)
                return status;

            var playurlInfo = AsDict(data, "playurl_info");
            var playurl = AsDict(playurlInfo, "playurl");
            List<LiveStream> all = PickAll(AsList(playurl, "stream"));
            if (all.Count == 0)
                throw new LiveRoomException("房间 " + status.RoomId + " 已开播，但接口没给可用的播放地址", false);
            foreach (var stream in all)
                stream.RoomId = status.RoomId;
            status.Live = true;
            status.Streams = all;
            return status;
        }

        /// <summary>主播昵称；查不到返回空串，不抛异常。</summary>
        public static string GetAnchorName(string roomId)
        {
            try
            {
                string json = HttpGet(ApiBase + "/live_user/v1/UserInfo/get_anchor_in_room?roomid=" + Uri.EscapeDataString(roomId));
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (root == null || ToInt(root, "code", -1) != 0)
                    return "";
                return ToString(AsDict(AsDict(root, "data"), "info"), "uname").Trim();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 列出全部可用线路，好的在前：清晰度高 → HLS 优先于 FLV → AVC 优先于 HEVC → TS 优先于 fMP4；
        /// 同一种格式的多个 CDN 节点（url_info）各算一条。某条连不上时由调用方换下一条。
        /// </summary>
        public static List<LiveStream> PickAll(object streamsObj)
        {
            var result = new List<LiveStream>();
            var streams = streamsObj as System.Collections.ArrayList;
            if (streams == null)
                return result;
            foreach (object streamObj in streams)
            {
                var stream = streamObj as Dictionary<string, object>;
                if (stream == null)
                    continue;
                string protocol = ToString(stream, "protocol_name");
                bool hls = string.Equals(protocol, "http_hls", StringComparison.OrdinalIgnoreCase);
                bool flv = string.Equals(protocol, "http_stream", StringComparison.OrdinalIgnoreCase);
                if (!hls && !flv)
                    continue;
                foreach (object formatObj in AsList(stream, "format"))
                {
                    var format = formatObj as Dictionary<string, object>;
                    if (format == null)
                        continue;
                    string formatName = ToString(format, "format_name");
                    if (flv && !string.Equals(formatName, "flv", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (object codecObj in AsList(format, "codec"))
                    {
                        var codec = codecObj as Dictionary<string, object>;
                        if (codec == null)
                            continue;
                        foreach (string url in JoinUrls(codec))
                        {
                            result.Add(new LiveStream
                            {
                                Url = url,
                                FormatName = flv ? "flv" : formatName,
                                CodecName = ToString(codec, "codec_name"),
                                Quality = ToInt(codec, "current_qn", 0)
                            });
                        }
                    }
                }
            }
            // 稳定排序：同等条件下保持接口给的节点顺序
            var ordered = new List<LiveStream>();
            foreach (var item in result.Select((x, i) => new { x, i })
                .OrderByDescending(a => a.x.Quality)
                .ThenByDescending(a => Rank(a.x))
                .ThenBy(a => a.i))
                ordered.Add(item.x);
            return ordered;
        }

        public static LiveStream Pick(object streamsObj)
        {
            var all = PickAll(streamsObj);
            return all.Count > 0 ? all[0] : null;
        }

        static int Rank(LiveStream stream)
        {
            int rank = 0;
            if (!string.Equals(stream.FormatName, "flv", StringComparison.OrdinalIgnoreCase))
                rank += 4;
            if (string.Equals(stream.CodecName, "avc", StringComparison.OrdinalIgnoreCase))
                rank += 2;
            if (string.Equals(stream.FormatName, "ts", StringComparison.OrdinalIgnoreCase))
                rank += 1;
            return rank;
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
            // 以前的 -headers 里写的是字面量 "\r\n"，ffmpeg 不转义，Referer 值后面会拖着整段 UA。
            command.Append(" -referer ").Append(Quote(Referer));
            command.Append(" -user_agent ").Append(Quote(UserAgent));
            command.Append(" -i ").Append(Quote(stream.Url));
            command.Append(" -c copy ").Append(DownloadManager.Mp4Flags).Append(' ');
            command.Append(Quote(outputPath));
            return command.ToString();
        }

        static List<string> JoinUrls(Dictionary<string, object> codec)
        {
            var urls = new List<string>();
            string baseUrl = ToString(codec, "base_url");
            if (string.IsNullOrEmpty(baseUrl))
                return urls;
            foreach (object urlObj in AsList(codec, "url_info"))
            {
                var urlInfo = urlObj as Dictionary<string, object>;
                if (urlInfo == null)
                    continue;
                string host = ToString(urlInfo, "host");
                if (string.IsNullOrEmpty(host))
                    continue;
                urls.Add(host + baseUrl + ToString(urlInfo, "extra"));
            }
            return urls;
        }

        static string ExpandShareUrl(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = false;
            ApplyProxy(request);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string location = response.Headers["Location"];
                if (string.IsNullOrEmpty(location))
                    return url;
                return new Uri(new Uri(url), location).GetLeftPart(UriPartial.Path);
            }
        }

        static string HttpGet(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.Referer = Referer;
            request.Accept = "application/json";
            ApplyProxy(request);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        static Dictionary<string, object> AsDict(Dictionary<string, object> parent, string key)
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

        static System.Collections.ArrayList AsList(Dictionary<string, object> parent, string key)
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

        static string ToString(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent != null && parent.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value);
            return "";
        }

        static int ToInt(Dictionary<string, object> parent, string key, int fallback)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }
    }

    public sealed class LiveRoomStatus
    {
        public string RoomId;      // 真实（长）房间号
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
    }
}
