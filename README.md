# M3U8-Downloader

Fork of [magicdmer/M3U8-Downloader](https://github.com/magicdmer/M3U8-Downloader) (itself based on [nilaoda/The-New-M3U8-Downloader](https://github.com/nilaoda/The-New-M3U8-Downloader)). Upstream last shipped v2.1 on 2019-10-07 and has no license file.

## 现在还适不适合用

原程序只做一件事：把你已经拿到的 m3u8 地址交给 `Tools\ffmpeg.exe`，`-c copy` 合成 mp4。它不解析网页，也不带站点请求头。本 fork 当前版本 2.6.0。

仍然适用：

- 直接的、有总时长的 VOD m3u8（点播列表能下完）
- 可选 HTTP 代理
- 多个地址同时下载（任务列表，默认同时 3 个，其余排队）
- B 站、抖音直播间自动盯播：没开播就等，开播自动录，下播后接着等下一场
- 其他网站的直播间（虎牙、斗鱼、Twitch、YouTube 等）：另装 [Streamlink](https://streamlink.github.io) 当插件后同样能盯播，见下面「其他网站直播（Streamlink 插件）」

已经过时、不能当通用下载器：

- 只编译到 .NET Framework 4.8，Windows 桌面，x86
- README 里的 ffmpeg 下载站（ffmpeg.zeranoe.com）已关
- 不支持 AES-128、fMP4/DASH、自定义 Header、主播放列表选画质
- 直播没有总时长，只显示已录时间和大小；不限时录会一直跑到你按停止
- B 站直播不能把房间链接直接丢进去。2026-09 实测 `getRoomPlayInfo` 仍返回 `http_hls` + `ts` + `avc` 的 m3u8，但分片在 `*.bilivideo.com`，不带 `Referer: https://live.bilibili.com/` 会被拒

## B 站直播

地址框可填：

- `https://live.bilibili.com/24624923?live_from=71002`
- `5050`
- 整行分享文案，例如 `【标题-哔哩哔哩直播】 https://b23.tv/wD77dU1`

短链会先跳到 `live.bilibili.com/<房间号>`。接口给的全部线路按「清晰度 → HLS 优先于 FLV → AVC 优先于 HEVC → TS 优先于 fMP4」排好，同一格式的每个 CDN 节点各算一条。当前线路一个字节都没录到（节点连不上、被拒）就立刻换下一条，不重新查接口，文件名不变。直播录制不再套 `aac_adtstoasc`，否则封装出的 AAC 会变成几 kb/s、几乎无声。

直播没有总时长，进度列只显示已录时间和大小。点播 m3u8 按总时长显示百分比。

### 自动盯播

- 房间没开播时任务显示「等待开播」，约每 60 秒查一次（每个房间随机错开最多 10 秒，避免同时打接口）
- 开播后马上开录；下播或断流且 ffmpeg 自己重连不上时，这一场收尾，任务回到「等待开播」，下一场录成新文件
- 断流先在 ffmpeg 里重试：HLS 分片失败重试 3 次、列表刷不出新分片最多等 20 次；FLV 自动重连
- 直播不占「同时下载」名额，开播就录；等待中的房间也不占。名额只管点播
- 房间不存在（code 60004）直接失败；风控码（如 -352）、网络错误只记在「信息」列，下一轮再查
- 「停止」= 不再盯这个房间，正在录的那一场正常收尾
- 程序要一直开着，电脑睡眠时录不到；开播后最多晚约 1 分钟开录

### 文件名

直播：`主播名_房间号_开播时间.mp4`，例如 `七海Nana7mi_21452505_20261001-2130.mp4`。主播名查不到时省略；文件名里不能用的字符换成 `_`。点播仍是 `前缀 + 行号`（`Video0`、`Video1`…）。

### 海外网络

B 站、抖音部分 CDN 节点从海外直连会超时或被拒。菜单「设置」里填一个国内出口的 HTTP 代理并勾选，查房间、展开短链和 ffmpeg 拉流都会走这个代理。

## 抖音直播

地址框可填：

- `https://live.douyin.com/888730465907`
- 分享短链 `https://v.douyin.com/xxxxxxx/`，或整段分享文案

盯的是直播间号（`live.douyin.com/` 后面那串，每场不变）。分享短链里带的房间 id 每场都换，只在第一次用来换出直播间号，之后下播再开播照样能录。文件名 `主播名_直播间号_开播时间.mp4`。

只用不登录、不签名的两个接口（2026-10 实测）：`live.douyin.com/webcast/room/web/enter/?web_rid=…`（只要首页给的 `ttwid` cookie）和分享页用的 `webcast.amemv.com/webcast/room/reflow/info/`。抖音收紧这两个接口的话需要跟进。

线路按「蓝光 → 超清 → 高清 → 标清，同清晰度 FLV 优先于 HLS」排好，连不上自动换下一条。抖音 CDN 的 `http://` 地址一律换成 `https://`：走 HTTP 代理时 CDN 对 http 地址回 405。

直播间不存在（status_code 4001038）直接失败；接口回空内容（cookie 失效）会换一个 cookie 重试，仍不行就下一轮再查。

## 其他网站直播（Streamlink 插件）

B 站、抖音以外的直播间不再逐站适配，交给 [Streamlink](https://streamlink.github.io/plugins.html)（开源、社区维护约 100 个站点插件）。软件不内置它，安装包大小不变。

1. 打开 [Streamlink Windows 版下载页](https://github.com/streamlink/windows-builds/releases/latest)，下安装版（`*-x86_64.exe`）或便携版（`*-x86_64.zip`，约 80 MB）。只有 64 位版
2. 安装版装好即可。便携版解压到本软件的 `Tools\streamlink\` 下（得到 `Tools\streamlink\bin\streamlink.exe`），或者解压到别处后在菜单「工具 → Streamlink 插件…」里用「指定 streamlink.exe」选它
3. 「工具 → Streamlink 插件…」显示「可用：streamlink x.y.z」就装好了

自动查找顺序：手动指定的路径 → `Tools\streamlink*\bin\streamlink.exe` → `Program Files\Streamlink\bin\` → `PATH`。

装好后，地址框里的网页地址（不是 `.m3u8`、`.mp4`、`.flv` 这类媒体文件）先交给 Streamlink 认：

- 它认出是直播间：和 B 站一样盯播。没开播显示「等待开播」，开播后由内置 ffmpeg 录，下播接着等；文件名 `主播名_房间标识_开播时间.mp4`，房间标识取地址最后一段（`huya.com/kpl` → `kpl`）
- 线路取 Streamlink 给的 `best` 及其余清晰度从高到低，只用 ffmpeg 能直接录的 HLS / HTTP-FLV 流，连不上自动换下一条。Streamlink 给的请求头（Referer、Origin、UA 等）原样带给 ffmpeg
- 它没有这个站的插件：按普通地址交给 ffmpeg 下载，和没装插件时一样
- 菜单「设置」里的代理会用 `--http-proxy` 传给 Streamlink

每次查房间要启动一次 Streamlink，比 B 站、抖音的原生接口慢 1～2 秒。哪个站解析失效，更新 Streamlink 即可，本软件不用改。2026-10 实测：斗鱼、虎牙可录；快手 Streamlink 没有插件。

## 开播记录

每场直播的开播、下播、中断和检查失败都记一行到输出目录的 `历史.csv`（Excel 可直接打开），程序重开接着写。想核实「刚才那次请求超时有没有错过直播」，看这份文件就行：

- 事件有：`开播`（主播名、线路数）、`下播`（文件名、大小）、`中断`（断流后 ffmpeg 也没连回来）、`手动停止`、`检查失败`（接口报错或网络超时的原文）、`断档恢复`（上一条是检查失败，之后查到开播，说明超时期间播了、已自动补录）、`恢复检查`（检查失败之后查到房间没播）
- 一次「请求超时」在文件里是一对 `检查失败` → `断档恢复` 行，中间夹着 `开播` 就是超时那会儿开播了、已经补录；只对着一个 `恢复检查` 就是那段时间本来没播，什么都没错过
- `断档恢复`、`恢复检查` 只在检查失败后恢复的第一轮各记一次，正常轮询不刷屏
- 房间标识取文件名里那个，和视频文件对得上；这份文件只追加不删除

## 同时下载多个

地址框每行一个地址，点「开始下载」后全部进入下面的任务列表，地址框随即清空。「同时下载」默认 3（1–8），超出的排队，前面的结束一个就补一个。点播和直播可以混在一起。

每行显示文件名、状态（排队中 / 等待开播 / 解析中 / 下载中 / 正在停止 / 整理文件 / 完成 / 已停止 / 已强制停止 / 失败 / 已取消）、进度、大小、分辨率或错误原因。底部是汇总计数和总进度（只算完成和进行中的点播）。

- 没选中任务时，按钮是「全部停止」「全部强制停止」「重新开始」；选中一行或多行后变成「停止选中」「强制停止选中」「重开选中」
- 「重新开始」：停掉的直播间接着盯，开播录到新文件，已录场数接着累计；停掉或失败的点播重新下到新文件名。没选中时「重新开始」作用于全部已结束的任务，但不重下已完成的点播
- 右键：停止、强制停止、重新开始、播放文件、打开所在位置、复制地址和错误、从列表移除
- 双击播放，Delete 移除，Ctrl+C 复制地址
- 「清除已结束」只清列表，不删文件
- 有任务时关闭窗口会先确认，然后给每个 ffmpeg 发 `q`，最多等 5 秒，剩下的强制结束

## 停止与保存

- 「停止」给 ffmpeg 发 `q`，让它自己收尾；15 秒内没退出就强制结束。排队中的任务直接取消，不会启动。
- 输出是分片 MP4（`-movflags +frag_keyframe+default_base_moof -flush_packets 1`），边下边写。强制停止、断网、程序被关掉，已下部分都能播。
- 分片 MP4 的头里只记第一个分片的时长，直播录出来播放器只显示 2~4 秒、拖不动进度条（实际整段能播）。所以每场录完（下播、停止、点播完成）都会原样转封装一遍成普通 MP4，状态显示「整理文件」，不重新编码，大文件也只是读写一遍。整理时强制停止或整理失败，保留原分片文件，照样能播
- 2.2.3 的参数里多了 `empty_moov`。直播录制不经过 `aac_adtstoasc`，ffmpeg 在开头拿不到 AAC 头信息，第一包就报 `Error muxing a packet` 退出，于是点下载秒结束。2.3.0 去掉了它。
- 点播文件名 = 前缀 + 这一批里的行号。同名文件已存在、或者另一个任务正在用这个名字时，另存为 `Video0 (1).mp4`、`Video0 (2).mp4`。ffmpeg 加了 `-n`，就算名字撞了也不会覆盖。
- 失败的任务在「信息」列显示 ffmpeg 最后一条错误，例如 `Server returned 403 Forbidden`。

保存目录在菜单「设置」里，选好路径点确定，会写进程序目录的 `M3u8_Downloader_Settings.xml`。主界面没有单独的路径框。不改的话，默认存到程序所在目录。「同时下载」的数值关闭时写回同一个文件。

普通 m3u8 地址行为不变。

云编译产物在 Actions 的 `M3U8-Downloader-win-x86` artifact 里，内含 `Tools\ffmpeg.exe`。原仓库没有声明许可证，本 fork 只在此基础上加直播解析，不新增许可证声明。
