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
    /// B 站直播间：识别地址、查开播状态和主播名。
    /// 只用 getRoomPlayInfo 和 get_anchor_in_room 两个接口（2026-10 实测不需要登录、
    /// 没触发风控；getInfoByRoom 已经返回 -352，不用）。
    /// </summary>
    public static class BilibiliLive
    {
        // 测试时指向本地假接口
        public static string ApiBase = "https://api.live.bilibili.com";
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

            int code = LiveCommon.Int(root, "code", -1);
            if (code != 0)
            {
                string message = LiveCommon.Str(root, "message");
                string text = "B 站接口 code " + code + (string.IsNullOrEmpty(message) ? "" : "：" + message);
                // 60004 = 房间不存在；再查也不会变
                if (code == 60004)
                    throw new LiveRoomException("房间 " + roomId + " 不存在", true);
                throw new LiveRoomException(text, false);
            }

            var data = LiveCommon.AsDict(root, "data");
            var status = new LiveRoomStatus { RoomId = roomId };
            string longId = LiveCommon.Str(data, "room_id");
            if (longId.Length > 0 && longId != "0")
                status.RoomId = longId;   // 短号（如 6）换成真实房间号，文件名用这个
            if (LiveCommon.Int(data, "live_status", 0) != 1)
                return status;

            var playurlInfo = LiveCommon.AsDict(data, "playurl_info");
            var playurl = LiveCommon.AsDict(playurlInfo, "playurl");
            List<LiveStream> all = PickAll(LiveCommon.AsList(playurl, "stream"));
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
                if (root == null || LiveCommon.Int(root, "code", -1) != 0)
                    return "";
                return LiveCommon.Str(LiveCommon.AsDict(LiveCommon.AsDict(root, "data"), "info"), "uname").Trim();
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
                string protocol = LiveCommon.Str(stream, "protocol_name");
                bool hls = string.Equals(protocol, "http_hls", StringComparison.OrdinalIgnoreCase);
                bool flv = string.Equals(protocol, "http_stream", StringComparison.OrdinalIgnoreCase);
                if (!hls && !flv)
                    continue;
                foreach (object formatObj in LiveCommon.AsList(stream, "format"))
                {
                    var format = formatObj as Dictionary<string, object>;
                    if (format == null)
                        continue;
                    string formatName = LiveCommon.Str(format, "format_name");
                    if (flv && !string.Equals(formatName, "flv", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (object codecObj in LiveCommon.AsList(format, "codec"))
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
                                CodecName = LiveCommon.Str(codec, "codec_name"),
                                Quality = LiveCommon.Int(codec, "current_qn", 0),
                                QualityName = "qn=" + LiveCommon.Int(codec, "current_qn", 0),
                                Referer = Referer
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

        static List<string> JoinUrls(Dictionary<string, object> codec)
        {
            var urls = new List<string>();
            string baseUrl = LiveCommon.Str(codec, "base_url");
            if (string.IsNullOrEmpty(baseUrl))
                return urls;
            foreach (object urlObj in LiveCommon.AsList(codec, "url_info"))
            {
                var urlInfo = urlObj as Dictionary<string, object>;
                if (urlInfo == null)
                    continue;
                string host = LiveCommon.Str(urlInfo, "host");
                if (string.IsNullOrEmpty(host))
                    continue;
                urls.Add(host + baseUrl + LiveCommon.Str(urlInfo, "extra"));
            }
            return urls;
        }

        static string ExpandShareUrl(string url)
        {
            return new Uri(LiveCommon.GetRedirect(url)).GetLeftPart(UriPartial.Path);
        }

        static string HttpGet(string url)
        {
            return LiveCommon.HttpGet(url, Referer);
        }
    }
}
