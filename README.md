# M3U8-Downloader

Fork of [magicdmer/M3U8-Downloader](https://github.com/magicdmer/M3U8-Downloader) (itself based on [nilaoda/The-New-M3U8-Downloader](https://github.com/nilaoda/The-New-M3U8-Downloader)). Upstream last shipped v2.1 on 2019-10-07 and has no license file.

## 现在还适不适合用

原程序只做一件事：把你已经拿到的 m3u8 地址交给 `Tools\ffmpeg.exe`，`-c copy` 合成 mp4。它不解析网页，也不带站点请求头。

仍然适用：

- 直接的、有总时长的 VOD m3u8（点播列表能下完）
- 可选 HTTP 代理
- 多行地址按顺序下载

已经过时、不能当通用下载器：

- 只编译到 .NET Framework 4.6，Windows 桌面，x86
- README 里的 ffmpeg 下载站（ffmpeg.zeranoe.com）已关
- 不支持 AES-128、fMP4/DASH、自定义 Header、主播放列表选画质
- 进度条靠解析 ffmpeg 的 Duration。直播没有总时长，进度会失真，不限时录会一直跑到你按停止
- B 站直播不能把房间链接直接丢进去。2026-09 实测 `getRoomPlayInfo` 仍返回 `http_hls` + `ts` + `avc` 的 m3u8，但分片在 `*.bilivideo.com`，不带 `Referer: https://live.bilibili.com/` 会被拒

## B 站直播

地址框可填：

- `https://live.bilibili.com/5050`
- `5050`

保存名照旧。直播没有总时长，进度条会失真；停录用原来的停止按钮，不自动限时。

普通 m3u8 地址行为不变。

云编译产物在 Actions 的 `M3U8-Downloader-win-x86` artifact 里，内含 `Tools\ffmpeg.exe`。原仓库没有声明许可证，本 fork 只在此基础上加直播解析，不新增许可证声明。
