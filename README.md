# M3U8-Downloader

Fork of [magicdmer/M3U8-Downloader](https://github.com/magicdmer/M3U8-Downloader) (itself based on [nilaoda/The-New-M3U8-Downloader](https://github.com/nilaoda/The-New-M3U8-Downloader)). Upstream last shipped v2.1 on 2019-10-07 and has no license file.

## 现在还适不适合用

原程序只做一件事：把你已经拿到的 m3u8 地址交给 `Tools\ffmpeg.exe`，`-c copy` 合成 mp4。它不解析网页，也不带站点请求头。本 fork 当前版本 2.3.0。

仍然适用：

- 直接的、有总时长的 VOD m3u8（点播列表能下完）
- 可选 HTTP 代理
- 多个地址同时下载（任务列表，默认同时 3 个，其余排队）

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

短链会先跳到 `live.bilibili.com/<房间号>`。优先取最高清晰度的 HLS（AVC + TS）。这份地址连不上时，改用同清晰度的 FLV。直播录制不再套 `aac_adtstoasc`，否则封装出的 AAC 会变成几 kb/s、几乎无声。

直播没有总时长，进度列只显示已录时间和大小。点播 m3u8 按总时长显示百分比。

## 同时下载多个

地址框每行一个地址，点「开始下载」后全部进入下面的任务列表，地址框随即清空。「同时下载」默认 3（1–8），超出的排队，前面的结束一个就补一个。点播和直播可以混在一起。

每行显示文件名、状态（排队中 / 解析中 / 下载中 / 正在停止 / 完成 / 已停止 / 已强制停止 / 失败 / 已取消）、进度、大小、分辨率或错误原因。底部是汇总计数和总进度（只算完成和进行中的点播）。

- 没选中任务时，按钮是「全部停止」「全部强制停止」；选中一行或多行后变成「停止选中」「强制停止选中」
- 右键：停止、强制停止、重新下载、播放文件、打开所在位置、复制地址和错误、从列表移除
- 双击播放，Delete 移除，Ctrl+C 复制地址
- 「清除已结束」只清列表，不删文件
- 有任务时关闭窗口会先确认，然后给每个 ffmpeg 发 `q`，最多等 5 秒，剩下的强制结束

## 停止与保存

- 「停止」给 ffmpeg 发 `q`，让它自己收尾；15 秒内没退出就强制结束。排队中的任务直接取消，不会启动。
- 输出是分片 MP4（`-movflags +frag_keyframe+default_base_moof -flush_packets 1`），边下边写。强制停止、断网、程序被关掉，已下部分都能播。
- 2.2.3 的参数里多了 `empty_moov`。直播录制不经过 `aac_adtstoasc`，ffmpeg 在开头拿不到 AAC 头信息，第一包就报 `Error muxing a packet` 退出，于是点下载秒结束。2.3.0 去掉了它。
- 文件名 = 前缀 + 这一批里的行号。同名文件已存在、或者另一个任务正在用这个名字时，另存为 `Video0 (1).mp4`、`Video0 (2).mp4`。ffmpeg 加了 `-n`，就算名字撞了也不会覆盖。
- 失败的任务在「信息」列显示 ffmpeg 最后一条错误，例如 `Server returned 403 Forbidden`。

保存目录在菜单「设置」里，选好路径点确定，会写进程序目录的 `M3u8_Downloader_Settings.xml`。主界面没有单独的路径框。不改的话，默认存到程序所在目录。「同时下载」的数值关闭时写回同一个文件。

普通 m3u8 地址行为不变。

云编译产物在 Actions 的 `M3U8-Downloader-win-x86` artifact 里，内含 `Tools\ffmpeg.exe`。原仓库没有声明许可证，本 fork 只在此基础上加直播解析，不新增许可证声明。
