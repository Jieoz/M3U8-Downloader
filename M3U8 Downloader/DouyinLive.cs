using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace M3U8_Downloader
{
    /// <summary>
    /// 抖音直播间：识别地址、查开播状态、主播名和播放地址。
    /// 盯的是直播间号 web_rid（live.douyin.com/后面那串，每场不变）；分享短链里的 room_id 每场都换，
    /// 只在第一次用来换出 web_rid。
    /// 用的接口（2026-10 实测，不登录、不签名）：
    ///   live.douyin.com/webcast/room/web/enter/?web_rid=…   只要 ttwid 这个 cookie（首页 Set-Cookie 给）
    ///   webcast.amemv.com/webcast/room/reflow/info/        分享短链 → web_rid
    /// </summary>
    public static class DouyinLive
    {
        // 测试时指向本地假接口
        public static string LiveBase = "https://live.douyin.com";
        public static string ReflowBase = "https://webcast.amemv.com";
        public const string Referer = "https://live.douyin.com/";

        static readonly Regex RoomUrl = new Regex(
            @"https?://live\.douyin\.com/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ShareUrl = new Regex(
            @"https?://v\.douyin\.com/[A-Za-z0-9_\-]+/?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReflowUrl = new Regex(
            @"https?://webcast\.amemv\.com/douyin/webcast/reflow/(?<room>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly object cookieLock = new object();
        static string ttwid;   // 一次拿到可以一直用；接口返回空内容时丢掉重拿

        /// <summary>只看格式，不联网。分享文案里夹着链接也认（抖音复制出来是一段话 + 短链）。</summary>
        public static bool LooksLikeLive(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;
            return RoomUrl.IsMatch(input) || ShareUrl.IsMatch(input) || ReflowUrl.IsMatch(input);
        }

        /// <summary>
        /// 查一次。knownRoom 是上次查到的 web_rid，空的话先从地址里解析（短链要联网跳转）。
        /// 地址认不出、第一次查就说直播间不存在 → LiveRoomException(permanent)；其他错误稍后再查。
        /// </summary>
        public static LiveRoomStatus Check(string source, string knownRoom)
        {
            bool first = string.IsNullOrEmpty(knownRoom);
            string webRid = first ? ResolveWebRid(source) : knownRoom;

            var root = Enter(webRid);
            int code = LiveCommon.Int(root, "status_code", -1);
            var data = LiveCommon.AsDict(root, "data");
            if (code != 0)
            {
                string msg = LiveCommon.Str(data, "prompts");
                if (msg.Length == 0) msg = LiveCommon.Str(data, "message");
                string text = "抖音接口 status_code " + code + (msg.Length > 0 ? "：" + msg : "");
                if (first && code == 4001038)
                    throw new LiveRoomException("抖音直播间 " + webRid + " 不存在或无法查看", true);
                throw new LiveRoomException(text, false);
            }

            var status = new LiveRoomStatus { RoomId = webRid };
            status.Anchor = LiveCommon.Str(LiveCommon.AsDict(data, "user"), "nickname").Trim();
            var rooms = LiveCommon.AsList(data, "data");
            var room = rooms.Count > 0 ? rooms[0] as Dictionary<string, object> : null;
            if (status.Anchor.Length == 0 && room != null)
                status.Anchor = LiveCommon.Str(LiveCommon.AsDict(room, "owner"), "nickname").Trim();
            // room_status 0 + 房间 status 2 = 正在播；status 4 = 已下播
            if (room == null || LiveCommon.Int(data, "room_status", -1) != 0 || LiveCommon.Int(room, "status", 0) != 2)
                return status;

            List<LiveStream> all = PickAll(LiveCommon.AsDict(room, "stream_url"));
            if (all.Count == 0)
                throw new LiveRoomException("抖音直播间 " + webRid + " 在播，但接口没给可用的播放地址", false);
            foreach (var s in all)
                s.RoomId = webRid;
            status.Live = true;
            status.Streams = all;
            return status;
        }

        static string ResolveWebRid(string source)
        {
            Match m = RoomUrl.Match(source);
            if (m.Success)
                return m.Groups["id"].Value;
            string landed = source.Trim();
            Match share = ShareUrl.Match(landed);
            if (share.Success)
            {
                landed = LiveCommon.GetRedirect(share.Value);
                m = RoomUrl.Match(landed);
                if (m.Success)
                    return m.Groups["id"].Value;
            }
            Match reflow = ReflowUrl.Match(landed);
            if (!reflow.Success)
                throw new LiveRoomException("认不出抖音直播间链接（分享的可能不是直播间）", true);
            Match sec = Regex.Match(landed, @"[?&]sec_user_id=(?<v>[^&]+)");
            string url = ReflowBase + "/webcast/room/reflow/info/?type_id=0&live_id=1&version_code=99.99.99&app_id=1128"
                + "&room_id=" + reflow.Groups["room"].Value
                + (sec.Success ? "&sec_user_id=" + sec.Groups["v"].Value : "");
            var root = Parse(LiveCommon.HttpGet(url, Referer));
            var owner = LiveCommon.AsDict(LiveCommon.AsDict(LiveCommon.AsDict(root, "data"), "room"), "owner");
            string webRid = LiveCommon.Str(owner, "web_rid");
            if (webRid.Length == 0)
                throw new LiveRoomException("抖音分享链接没解析出直播间号（status_code "
                    + LiveCommon.Int(root, "status_code", -1) + "）", false);
            return webRid;
        }

        public static string BuildEnterUrl(string webRid)
        {
            return LiveBase + "/webcast/room/web/enter/?aid=6383&app_name=douyin_web&live_id=1&device_platform=web"
                + "&language=zh-CN&browser_language=zh-CN&browser_platform=Win32&browser_name=Chrome&browser_version=124.0.0.0"
                + "&web_rid=" + Uri.EscapeDataString(webRid);
        }

        static Dictionary<string, object> Enter(string webRid)
        {
            for (int attempt = 0; ; attempt++)
            {
                string cookie;
                lock (cookieLock)
                {
                    if (string.IsNullOrEmpty(ttwid))
                        ttwid = LiveCommon.GetCookie(LiveBase + "/", "ttwid");
                    cookie = ttwid;
                }
                string json = LiveCommon.HttpGet(BuildEnterUrl(webRid), Referer, cookie);
                if (!string.IsNullOrWhiteSpace(json))
                    return Parse(json);
                // 没有 / 过期的 ttwid 时接口直接回空内容：换一个再试一次
                lock (cookieLock) ttwid = null;
                if (attempt >= 1)
                    throw new InvalidDataException("抖音接口返回空内容（可能触发了风控，稍后再查）");
            }
        }

        static Dictionary<string, object> Parse(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
                throw new InvalidDataException("抖音接口返回空内容");
            return root;
        }

        /// <summary>
        /// 全部线路，好的在前：清晰度高（蓝光 → 超清 → 高清 → 标清）→ 同清晰度 FLV 优先于 HLS
        /// （抖音网页播放器默认 FLV；实测经代理 FLV 一条长连接 25 秒录满 22 秒，HLS 逐片拉只录到 9 秒）。
        /// 抖音 CDN 的地址换成 https：走 HTTP 代理时 CDN 对 http 地址回 405。
        /// </summary>
        public static List<LiveStream> PickAll(Dictionary<string, object> streamUrl)
        {
            var result = new List<LiveStream>();
            var pull = LiveCommon.AsDict(LiveCommon.AsDict(streamUrl, "live_core_sdk_data"), "pull_data");
            string streamData = LiveCommon.Str(pull, "stream_data");
            if (streamData.Length > 0)
            {
                try
                {
                    var data = LiveCommon.AsDict(Parse(streamData), "data");
                    var qualities = LiveCommon.AsList(LiveCommon.AsDict(pull, "options"), "qualities")
                        .OfType<Dictionary<string, object>>()
                        .OrderByDescending(q => LiveCommon.Int(q, "level", 0));
                    foreach (var q in qualities)
                    {
                        if (LiveCommon.Int(q, "disable", 0) != 0)
                            continue;
                        var main = LiveCommon.AsDict(LiveCommon.AsDict(data, LiveCommon.Str(q, "sdk_key")), "main");
                        string codec = LiveCommon.Str(q, "v_codec") == "265" ? "hevc" : "avc";
                        Add(result, LiveCommon.Str(main, "flv"), "flv", codec, q);
                        Add(result, LiveCommon.Str(main, "hls"), "hls", codec, q);
                    }
                }
                catch (Exception)
                {
                    result.Clear();   // 格式变了就退到下面的旧字段
                }
            }
            if (result.Count == 0)
            {
                // 旧字段：FULL_HD1 原画 → HD1 → SD1 → SD2
                string[] keys = { "FULL_HD1", "HD1", "SD1", "SD2" };
                string[] names = { "原画", "高清", "标清", "流畅" };
                var hls = LiveCommon.AsDict(streamUrl, "hls_pull_url_map");
                var flv = LiveCommon.AsDict(streamUrl, "flv_pull_url");
                for (int i = 0; i < keys.Length; i++)
                {
                    var q = new Dictionary<string, object> { { "name", names[i] }, { "level", keys.Length - i } };
                    Add(result, LiveCommon.Str(flv, keys[i]), "flv", "avc", q);
                    Add(result, LiveCommon.Str(hls, keys[i]), "hls", "avc", q);
                }
            }
            return result;
        }

        static bool IsDouyinCdn(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u))
                return false;
            string h = u.Host.ToLowerInvariant();
            return h.EndsWith(".douyincdn.com") || h.EndsWith(".douyinliving.com") || h.EndsWith(".douyinvod.com");
        }

        static void Add(List<LiveStream> list, string url, string format, string codec, Dictionary<string, object> quality)
        {
            if (string.IsNullOrEmpty(url))
                return;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && IsDouyinCdn(url))
                url = "https://" + url.Substring(7);
            if (list.Any(s => s.Url == url))
                return;
            list.Add(new LiveStream
            {
                Url = url,
                FormatName = format,
                CodecName = codec,
                Quality = LiveCommon.Int(quality, "level", 0),
                QualityName = LiveCommon.Str(quality, "name"),
                Referer = Referer
            });
        }
    }
}
