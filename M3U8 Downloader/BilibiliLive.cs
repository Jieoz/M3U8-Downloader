using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace M3U8_Downloader
{
    /// <summary>
    /// Turns a Bilibili live room URL or room id into an ffmpeg-ready HLS input.
    /// Live playlists have no duration. Recording runs until the existing Stop
    /// button, the same way a normal ffmpeg copy does.
    /// </summary>
    public static class BilibiliLive
    {
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

        public static string BuildPlayInfoUrl(string roomId)
        {
            return "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo"
                + "?room_id=" + Uri.EscapeDataString(roomId)
                + "&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web&ptype=8";
        }

        public static LiveStream Resolve(string roomId)
        {
            string json = HttpGet(BuildPlayInfoUrl(roomId));
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
                throw new InvalidDataException("Bilibili live API returned empty JSON.");

            int code = ToInt(root, "code", -1);
            if (code != 0)
            {
                string message = ToString(root, "message");
                throw new InvalidDataException("Bilibili live API code " + code + (string.IsNullOrEmpty(message) ? "" : ": " + message));
            }

            var data = AsDict(root, "data");
            int liveStatus = ToInt(data, "live_status", 0);
            if (liveStatus != 1)
                throw new InvalidOperationException("房间 " + roomId + " 当前未开播（live_status=" + liveStatus + "）。");

            var playurlInfo = AsDict(data, "playurl_info");
            var playurl = AsDict(playurlInfo, "playurl");
            var streams = AsList(playurl, "stream");
            LiveStream best = Pick(streams);
            if (best == null)
                throw new InvalidDataException("房间 " + roomId + " 已开播，但没有可用的 http_hls 地址。");
            best.RoomId = roomId;
            return best;
        }

        public static LiveStream Pick(object streamsObj)
        {
            var streams = streamsObj as System.Collections.ArrayList;
            if (streams == null)
                return null;

            LiveStream fallback = null;
            foreach (object streamObj in streams)
            {
                var stream = streamObj as Dictionary<string, object>;
                if (stream == null || !string.Equals(ToString(stream, "protocol_name"), "http_hls", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (object formatObj in AsList(stream, "format"))
                {
                    var format = formatObj as Dictionary<string, object>;
                    if (format == null)
                        continue;
                    string formatName = ToString(format, "format_name");
                    foreach (object codecObj in AsList(format, "codec"))
                    {
                        var codec = codecObj as Dictionary<string, object>;
                        if (codec == null)
                            continue;
                        string url = JoinUrl(codec);
                        if (string.IsNullOrEmpty(url))
                            continue;
                        var candidate = new LiveStream
                        {
                            Url = url,
                            FormatName = formatName,
                            CodecName = ToString(codec, "codec_name"),
                            Quality = ToInt(codec, "current_qn", 0)
                        };
                        if (Better(candidate, fallback))
                            fallback = candidate;
                    }
                }
            }
            return fallback;
        }

        static bool Better(LiveStream candidate, LiveStream current)
        {
            if (current == null)
                return true;
            if (candidate.Quality != current.Quality)
                return candidate.Quality > current.Quality;
            return Rank(candidate) > Rank(current);
        }

        static int Rank(LiveStream stream)
        {
            int rank = 0;
            if (string.Equals(stream.CodecName, "avc", StringComparison.OrdinalIgnoreCase))
                rank += 2;
            if (string.Equals(stream.FormatName, "ts", StringComparison.OrdinalIgnoreCase))
                rank += 1;
            return rank;
        }

        public static string FfmpegHeaders()
        {
            return "Referer: " + Referer + "\\r\\nUser-Agent: " + UserAgent + "\\r\\n";
        }

        public static string BuildRecordCommand(string inputUrl, string outputPath, string httpProxy)
        {
            var command = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(httpProxy))
                command.Append("-http_proxy ").Append(Quote(httpProxy.Trim())).Append(' ');
            command.Append("-hide_banner -rw_timeout 15000000 -user_agent ").Append(Quote(UserAgent));
            command.Append(" -headers ").Append(Quote(FfmpegHeaders()));
            command.Append(" -i ").Append(Quote(inputUrl));
            command.Append(" -c copy -y -bsf:a aac_adtstoasc -movflags +faststart ");
            command.Append(Quote(outputPath));
            return command.ToString();
        }

        static string JoinUrl(Dictionary<string, object> codec)
        {
            string baseUrl = ToString(codec, "base_url");
            foreach (object urlObj in AsList(codec, "url_info"))
            {
                var urlInfo = urlObj as Dictionary<string, object>;
                if (urlInfo == null)
                    continue;
                string host = ToString(urlInfo, "host");
                if (string.IsNullOrEmpty(host))
                    continue;
                return host + baseUrl + ToString(urlInfo, "extra");
            }
            return null;
        }

        static string ExpandShareUrl(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = false;
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

        static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
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
