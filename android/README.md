# Lyrx Android (.NET for Android)

## 1. 目录结构

```text
android/
├─ Lyrx.Android.sln                 # Android 独立解决方案（引用 Lyrx.Core 与 Lyrx.Engine）
├─ build-android.ps1                # 自动探测 SDK / JDK 并执行构建或部署
├─ build-android.bat                # Windows 快捷入口
├─ README.md
└─ Lyrx.Android/
   ├─ Lyrx.Android.csproj           # net10.0-android (SupportedOSPlatformVersion 26.0)
   ├─ AndroidManifest.xml
   ├─ LyrxApp.cs                    # Application 入口，装配引擎与设置
   ├─ MainActivity.cs               # 主界面（专辑封面取色、同步滚动歌词、点按跳转、译文/偏移控制）
   ├─ Resources/values/
   │  ├─ strings.xml
   │  └─ styles.xml
   ├─ Services/
   │  ├─ AndroidLog.cs              # Logcat + 应用私有目录日志文件
   │  ├─ AndroidEngineDispatcher.cs # IEngineDispatcher / IEngineTimer (Looper.MainLooper)
   │  ├─ AndroidSecretStore.cs      # ISecretStore (AndroidKeyStore AES-256-GCM)
   │  ├─ AndroidAppSettings.cs      # settings.json 持久化
   │  ├─ MediaListenerService.cs    # NotificationListenerService 声明
   │  └─ AndroidNowPlayingSource.cs # INowPlayingSource (MediaSessionManager + 进度外推 + 传输控制)
   └─ UI/
      └─ LyricsPalette.cs           # 专辑封面 HSL 分桶动态背景取色
```

## 2. 跨平台复用说明

`Lyrx.Android` 直接通过 `ProjectReference` 引用：
- `../src/Lyrx.Core/Lyrx.Core.csproj`（LRC 解析、多源搜索、SQLite 缓存、多引擎翻译）
- `../src/Lyrx.Engine/Lyrx.Engine.csproj`（`LyricsCoordinator` 状态机、广告判定、关键词净化）

Android 端仅重写平台抽象与界面层，无需重新实现歌词搜索、评分过滤、本地缓存或同步状态机。

## 3. 环境准备与构建

### ① 安装 .NET Android 工作负载（首次）

由于安装 workload 需要写入 .NET SDK 目录（`C:\Program Files\dotnet`），请以 **管理员身份** 打开 PowerShell 或命令提示符执行：

```powershell
dotnet workload install android
```

### ② Android SDK 与 JDK

构建脚本 [`build-android.ps1`](./build-android.ps1) 会自动按优先级探测：
- Android SDK：`$env:ANDROID_HOME` → `$env:ANDROID_SDK_ROOT` → `D:\IDE\Android\plugin` → `%LOCALAPPDATA%\Android\Sdk`
- JDK：`$env:JAVA_HOME` → `D:\IDE\Android\Android Studio\jbr`

### ③ 构建与真机部署

```cmd
:: 默认构建 Release 包
android\build-android.bat

:: 构建 Debug 包（按需）
android\build-android.bat -Configuration Debug

:: 构建并直接安装 Release 包到已连接 ADB 的设备 / 模拟器
android\build-android.bat -Install
```

## 4. 首次运行权限说明

1. 启动应用后点击顶部的 **「去授权」**，在系统 **「通知使用权 / 通知读取权限」** 页面开启 **Lyrx**，应用才能通过 `MediaSessionManager` 读取系统媒体播放信息与进度。
2. **Android 13+ 注意事项**：若通过侧载（APK 直接安装）出现通知使用权开关为灰色不可点，请前往 **系统设置 → 应用 → Lyrx → 右上角 `⋮` → 允许受限设置**，再返回开启通知使用权。
