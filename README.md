# Lyrx

**Lyrx** 是一款面向 Windows/Android 的独立沉浸式双语歌词伴侣窗口，基于系统 SMTC（System Media Transport Controls）协议适配任意桌面与网页音乐播放器。

## 核心特性

- **全播放器音频适配**：自动跟随或指定锁定 Spotify、Apple Music、网易云音乐、QQ 音乐、foobar2000、MusicBee 以及 Chrome / Edge / Firefox 网页版音乐。
- **沉浸式舞台歌词界面**：
  - 自动提取专辑封面主题色（或切换 7 套内置暗色主题预设，可在 `ClassicLyricsPalette.cs` 中自由扩展）；
  - 无边框圆角窗口、任意空白区域拖拽移动、双击或 `F11` 全屏切换、小窗自适应紧凑布局。
- **丝滑双语歌词切换（`文A`）**：
  - 并行检索网易云音乐、QQ 音乐、酷狗音乐、LRCLIB 四大曲库并自动优选包含双语译文的高匹配度时间轴歌词；
  - 点击 `文A` 按钮时锚定当前演唱行，上下对称平滑展开/收起译文；
  - 支持接入 MyMemory、DeepL、Google Cloud Translation、OpenRouter 在线机器翻译补充。
- **实时生效的深色设置面板**：
  - 4 栏分页导航（播放与窗口 / 歌词搜索源 / 双语与翻译 / 当前歌曲工具），无需重启即改即生效。

## 目录结构

```text
Lyrx/
├── Lyrx.sln                  # 解决方案文件
├── build.ps1                 # 一键编译发布脚本（输出至 dist/）
├── LICENSE                   # LGPL-2.1 开源许可证
├── android/                  # Android 解决方案、界面和构建脚本
├── src/
│   ├── Lyrx.Core/            # 歌词解析、四大在线歌词源、翻译接口与 SQLite 本地缓存
│   ├── Lyrx.Engine/          # 播放状态协调器、歌词自动择优与翻译调度引擎
│   └── Lyrx.Windows/         # WPF 桌面端界面、SMTC 媒体会话监听、系统托盘与设置面板
├── dist/                     # Windows 发布输出
└── dist-fast/                # Windows 快速验证输出
```

## 编译与发布

本项目已配置通用编译脚本，支持任意装有 .NET 8 SDK 的 Windows 电脑一键构建：

- **方法 1（推荐）**：在文件资源管理器中直接双击 `build.bat`，或在 CMD / 终端中运行：
  ```cmd
  .\build.bat
  ```
- **方法 2**：在 PowerShell 中执行：
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  ```
  *(可选支持参数：`-SingleFile` 输出单文件独立 Exe；`-Configuration Debug` 输出调试版)*

默认发布目录为 `dist`，启动文件为 `dist\Lyrx.exe`。普通自包含发布请分发整个输出目录。正式发布时可以用 `-OutputDir` 指定空目录，以免混入旧产物。SDK 不在 PATH 中时可用 `-DotNet` 指定 `dotnet.exe` 的路径。离线构建可用 `-PackageSource` 指定完整的本地 NuGet 源。用户配置文件与本地歌词缓存保存在 `%LOCALAPPDATA%\Lyrx\`。

首次执行常规构建并还原依赖后，可运行 `.\build-fast.bat` 快速输出到 `dist-fast`。快速构建跳过包还原，运行产物需要 .NET 8 Desktop Runtime。修改项目依赖后，先执行一次常规构建。

Android 构建步骤见 [Android 文档](android/README.md)。

## Windows 自定义歌词字体

在「设置 → 播放与窗口 → 歌词字体」导入或拖入 `.ttf` 或 `.otf` 文件，即可预览和切换歌词及译文的字体。也可以把文件放入 `%LOCALAPPDATA%\Lyrx\fonts\`，再点「刷新列表」选择字体。选择「Windows 默认字体」可恢复系统字体。

> **字体授权提示**：项目默认附带的 `SpotifyMix*.ttf`（Windows 输出目录 `local-fonts/` 及 Android `Assets/fonts/`）仅作本地开发/界面展示样本，公开或商业分发前请自行核对字体授权；若移除 `local-fonts/` 目录，Windows 端会自动回退至系统字体。
