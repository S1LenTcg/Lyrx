# Lyrx

Lyrx 是一款面向 Windows 和 Android 的双语歌词伴侣。在你平常用的播放器里播放音乐，Lyrx 会跟随播放进度显示歌词。你可以逐字跟唱、切换译文，也可以选几行歌词做成图片分享。

## 下载与开始使用

请前往 [GitHub Releases](https://github.com/S1LenTcg/Lyrx/releases)，选择适合你的平台的发布文件。也可以从源码自行构建。

- **Windows 便携版（Windows 10 2004+ 或 Windows 11，x64）** — 下载并完整解压 `Lyrx-Windows-x64.zip`。运行 `Lyrx.exe`，再在播放器里播放一首歌。
- **Android APK（Android 8.0+，arm64）** — 安装 `Lyrx-Android-arm64.apk`，首次打开时按提示授予「通知使用权」，然后在播放器里播放音乐。
  - **Android 13 及以上注意**：若通过 APK 安装后「通知使用权」开关呈灰色无法开启，请先前往 **系统设置 → 应用 → Lyrx → 右上角 `⋮` → 允许受限设置**，再返回授予权限。

Lyrx 通过系统媒体会话读取曲目和进度；暂停、切歌、跳转等控制取决于播放器开放的能力。使用网页播放器时，Windows 端默认已包含浏览器音频，Android 端可在设置中开启「包含浏览器音频」；两端均支持在设置中通过偏好白名单或固定播放源过滤不需要的媒体应用。

## 功能

- **同步歌词与双语显示** — 支持并行检索网易云音乐、QQ 音乐、酷狗音乐和 LRCLIB，自动优选匹配歌词；歌词带逐字时间轴时可逐字高亮，点击歌词行可跳转播放进度，也支持全局或按曲目记忆的时间偏移微调。译文支持显示歌词源自带翻译，并可选用 MyMemory、DeepL、Google Cloud Translation 或 OpenRouter 在线补充翻译。
- **歌词卡片分享** — 两端均可通过底部工具栏分享按钮、Windows 右键歌词行或 Android 长按歌词行进入选词模式（最多 6 行），支持切换卡片样式、译文显示与背景效果，并可导出 PNG、复制图片或调用系统分享。
- **Windows** — 支持窗口置顶、双击或 `F11` 全屏、自定义歌词字体（`.ttf` / `.otf`）、系统托盘快捷控制，以及手动搜索、编辑和导出 `.lrc` 歌词（关闭主窗口会退出程序）。
- **Android** — 支持手机和平板布局、横竖屏切换、横屏常亮、沉浸式隐藏导航栏、底部音频输出/蓝牙设备显示与音量浮窗，以及重新搜索、通过系统分享发送 LRC 文本（无法打开分享面板时复制到剪贴板）和保存卡片到相册。

### 联网与服务说明

- 查找歌词时，Lyrx 会向启用的歌词源查询歌名、歌手等曲目信息（网易云音乐、QQ 音乐、酷狗音乐通过公开非官方接口获取，可用性受第三方服务影响）。
- 当播放器未通过系统媒体会话提供专辑封面时，Lyrx 会按曲目信息在线检索或拉取对应封面图片。Windows 端识别到浏览器或视频来源时，会优先在线查找封面。
- 当歌词缺少自带译文且开启在线翻译时，需要翻译的歌词会发送到所选翻译服务。**Android 端默认开启 MyMemory 在线翻译，Windows 端默认关闭在线机器翻译**，均可在「设置 → 双语与翻译」中调整。MyMemory 免费且无需 API Key。DeepL、Google Cloud Translation 和 OpenRouter 需要在设置中填入各自的 API Key。

## 开发历程

Lyrx 从 Windows 桌面歌词窗口开始。早期工作的重点是封面取色、双语切换，以及切歌和拖动进度后的歌词同步。后来，Android 端复用了同一套歌词搜索与同步引擎，并加入了平板和横屏布局。歌词卡片分享先在 Android 实现，随后移植到 Windows。

## 从源码构建

- **Windows** — 安装 .NET 8 SDK，在项目根目录运行 `.\build.bat`。默认输出自包含多文件目录到 `dist/`（分发时需保留整个目录）；如需单文件版可运行 `powershell -ExecutionPolicy Bypass -File .\build.ps1 -SingleFile`。首次还原依赖后，也可运行 `.\build-fast.bat` 快速输出到 `dist-fast/`（需本机安装 .NET 8 Desktop Runtime）。运行配置与本地缓存保存在 `%LOCALAPPDATA%\Lyrx\`。
- **Android** — 配置 .NET 10 Android 工作负载、Android SDK 和 JDK，再运行 `.\android\build-android.bat`，默认在 `android/Lyrx.Android/bin/Release/net10.0-android/` 下输出 arm64 APK。详见 [Android 构建文档](android/README.md)。

| 目录 | 用途 |
| --- | --- |
| `src/Lyrx.Windows/` | Windows WPF 界面和媒体会话 |
| `android/Lyrx.Android/` | Android 界面、媒体监听和分享 |
| `src/Lyrx.Core/` | 两端共用的歌词搜索、解析、翻译和缓存 |
| `src/Lyrx.Engine/` | 两端共用的播放状态、歌词匹配和同步 |

## 许可证与致谢

Lyrx 源码采用 [Mozilla Public License 2.0](LICENSE)。第三方组件与素材适用各自的许可证。歌词分享卡片中的播放器样式与品牌标识仅作视觉致敬，相关商标归各自权利人所有，Lyrx 与上述平台及各歌词、翻译服务提供商无官方关联。

Lyrx 基于 [Musebase for Windows](https://github.com/countnine/musebase) 的桌面歌词实现继续开发。部分歌词解析与搜索实现经 Musebase 从 [LyricsKit](https://github.com/ddddxxx/LyricsKit) 移植。感谢 [Sleexycg](https://github.com/Sleexycg) 对 Android 端、平板适配和歌词分享的贡献。

## 问题反馈

欢迎在 [GitHub Issues](https://github.com/S1LenTcg/Lyrx/issues) 反馈。请附上 Lyrx 版本（如 Release 标签或安装包文件名）、系统版本、播放器名称和复现步骤。歌词问题可以说明是「识别不到歌曲」「找不到歌词」，还是「歌词进度不对」。

排查识别或同步异常时，可在「设置 → 播放与窗口 → 开发者模式」开启「导出log」并附上日志（Windows 日志位于 `%LOCALAPPDATA%\Lyrx\app.log`，Android 可在设置中直接点击「分享最近日志」）。
