# Lyrx

**Lyrx** 是一款面向 Windows 的独立沉浸式双语歌词伴侣窗口，基于系统 SMTC（System Media Transport Controls）协议适配任意桌面与网页音乐播放器。

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
├── src/
│   ├── Lyrx.Core/            # 歌词解析、四大在线歌词源、翻译接口与 SQLite 本地缓存
│   ├── Lyrx.Engine/          # 播放状态协调器、歌词自动择优与翻译调度引擎
│   └── Lyrx.Windows/         # WPF 桌面端界面、SMTC 媒体会话监听、系统托盘与设置面板
└── dist/                     # 独立可分发版本输出目录 (Lyrx.exe)
```

## 编译与发布

在 PowerShell 中运行根目录脚本即可生成无需安装 .NET 运行时的独立版本：

```powershell
.\build.ps1
```

编译产物位于 `dist\Lyrx.exe`。用户配置文件与本地歌词缓存保存在 `%LOCALAPPDATA%\Lyrx\`。
