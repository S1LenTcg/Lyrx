using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// 应用设置（%LOCALAPPDATA%\Musebase\settings.json）。
/// M4 起使用 DeepL 密钥/目标语言。
/// </summary>
public sealed class AppSettings
{
    /// <summary>机密值（翻译 API 密钥）保护存储。可按平台替换（默认 Windows DPAPI）。</summary>
    [JsonIgnore]
    private ISecretStore _secretStore = new DpapiSecretStore();

    public bool OverlayVisible { get; set; } = true;

    /// <summary>关闭迷你窗口（X）时隐藏到托盘（从任务栏消失）。默认关闭 = 最小化常驻。</summary>
    public bool MiniWindowCloseToTray { get; set; }

    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    /// <summary>覆盖层尺寸。文字大小按高度比例自动决定。</summary>
    public double OverlayWidth { get; set; } = 780;
    public double OverlayHeight { get; set; } = 150;

    /// <summary>
    /// 控制面板（专辑封面窗口）的位置·大小。没有保存的值时（<c>null</c>）走首次运行布局 ——
    /// 浮在工作区右下角。这是长期常驻的窗口，每次都出现在屏幕正中会很碍事。
    /// </summary>
    public double? PanelX { get; set; }
    public double? PanelY { get; set; }

    /// <summary>正方形，因此只保存一条边。</summary>
    public double PanelSize { get; set; } = 360;

    public double? ClassicLyricsX { get; set; }
    public double? ClassicLyricsY { get; set; }
    public double ClassicLyricsWidth { get; set; } = 1060;
    public double ClassicLyricsHeight { get; set; } = 750;
    public bool ClassicShowTranslation { get; set; }
    public bool ClassicTopmost { get; set; }
    public string ClassicBackgroundMode { get; set; } = "dynamic";

    // ---- 覆盖层样式 (#RRGGBB) ----
    public string TextColor { get; set; } = "#FFFFFF";
    public string KaraokeColor { get; set; } = "#FFEB3B";
    public string TranslationColor { get; set; } = "#E8E8E8";
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 3.0;

    /// <summary>歌词出现与消失时平滑淡入/淡出。</summary>
    public bool FadeAnimation { get; set; } = true;

    /// <summary>显示覆盖层背景（半透明底板）。</summary>
    public bool OverlayBackgroundEnabled { get; set; }

    /// <summary>覆盖层背景色（#RRGGBB）。透明度由 OverlayBackgroundOpacity 调节。</summary>
    public string OverlayBackgroundColor { get; set; } = "#000000";

    /// <summary>覆盖层背景不透明度（0 = 完全透明 ~ 1 = 不透明）。</summary>
    public double OverlayBackgroundOpacity { get; set; } = 0.25;

    /// <summary>覆盖层背景（半透明底板）的圆角半径（DIP）。</summary>
    public double OverlayCornerRadius { get; set; } = 12;

    /// <summary>鼠标移到覆盖层上时暂时隐藏歌词·覆盖层（避免遮挡）。</summary>
    public bool HideOnMouseOver { get; set; }

    /// <summary>逐字卡拉OK（基于行内时间标签）。仅在支持的曲目（Kugou/QQ 等）上生效。</summary>
    public bool CharacterKaraoke { get; set; } = true;

    /// <summary>译文与原文相同时隐藏译文行（例：英文歌翻译成 EN 后与原文重复）。</summary>
    public bool HideSameTranslation { get; set; } = true;

    /// <summary>
    /// 只显示目标语言译文（DeepL），隐藏提供方插入的其他语言译文（多为中文）。
    /// 默认开启 → 没有 DeepL 密钥时只显示原文。但目标语言为中文（ZH）时，提供方译文本身就是中文，因此原样显示。
    /// </summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    /// <summary>标记为“错误歌词”以阻止搜索·显示的曲目键列表（规范化后的 标题|歌手）。</summary>
    public List<string> SuppressedTracks { get; set; } = new();

    /// <summary>手动同步偏移（秒）。为 + 时歌词提前。</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>
    /// 按曲记忆的偏移（秒），键是"标题|艺人"。
    /// 用来吸收同一音源与歌词时间轴之间的固定错位：例如 B 站视频比专辑版多几秒片头，
    /// 手动调一次就能记住，下次播同一首/同一视频自动套用。
    /// </summary>
    public Dictionary<string, double> TrackOffsetSeconds { get; set; } = new();

    /// <summary>取某首歌的记忆偏移（没有则 0）。</summary>
    public double GetTrackOffset(string? key) =>
        !string.IsNullOrEmpty(key) && TrackOffsetSeconds.TryGetValue(key, out var value) ? value : 0;

    /// <summary>写入/清除某首歌的记忆偏移；值为 0 时直接删掉这一项。</summary>
    public void SetTrackOffset(string? key, double value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (Math.Abs(value) < 0.001) TrackOffsetSeconds.Remove(key);
        else TrackOffsetSeconds[key] = Math.Round(value, 2);
    }

    /// <summary>
    /// 播放位置的固定补偿（毫秒）。按实测手感标定，默认 1400ms：值越大歌词显示得越早。
    /// 想更贴脸可以逐次减小（例如 1200 / 1000），直到自己听着最合适。
    /// </summary>
    public int SmtcLatencyCompensationMs { get; set; } = 1400;

    /// <summary>
    /// 是否向本地日志写入时间同步诊断样本（<c>[timing]</c>）。
    /// 开启后会持续记录当前曲目、来源应用与播放位置采样；默认关闭，仅在排查歌词同步问题时开启。
    /// </summary>
    public bool TimingDiagnosticsEnabled { get; set; }

    /// <summary>
    /// 歌词行距档位："compact" / "normal" / "loose"。
    /// 英文与韩文的字形比汉字更需要行间留白，所以留一个可调档位，而不是写死一套参数。
    /// </summary>
    public string LyricsLineSpacing { get; set; } = "normal";

    /// <summary>把行距档位换算成（行高倍数, 行间距倍数）。</summary>
    [JsonIgnore]
    public (double LineHeight, double Gap) LineSpacingScales => LyricsLineSpacing?.ToLowerInvariant() switch
    {
        "compact" => (0.94, 0.85),
        "loose" => (1.12, 1.45),
        _ => (1.0, 1.0),
    };

    /// <summary>
    /// 自动同步间隔（秒）。手动滚动歌词后，超过这个时长没有新的滚动操作就自动回到
    /// "跟随当前歌词行"，省得每次都要点「同步」。0 = 关闭自动同步。
    /// </summary>
    public int AutoSyncSeconds { get; set; } = 5;

    /// <summary>
    /// 播放源选择。"auto" = 自动检测，其他值 = 固定为特定播放器的 SourceAppUserModelId。
    /// （对应 macOS 版本的播放器选择 —— Windows 上按 SMTC 会话为单位固定）
    /// </summary>
    public string PlaybackSource { get; set; } = "auto";

    /// <summary>自动模式下是否把浏览器（Firefox/Chrome 等）纳入音乐源。默认排除（避免误把视频当音乐）。</summary>
    public bool IncludeBrowsers { get; set; } = true;

    /// <summary>
    /// 首选音乐应用（SourceAppUserModelId 列表）。为空时（默认）自动 ——
    /// 自动检测除浏览器·视频·播客应用之外的其余应用。只要选了其中一个，
    /// 就**只认这些应用**为播放源（与 Android 的 PreferredSources 同义）。
    /// </summary>
    [JsonPropertyName("preferredSources")]
    public List<string> PreferredSources { get; set; } = new();

    // ---- 浏览器显示（平板/TV 远程显示，Musebase.Browser 进程内托管） ----

    /// <summary>是否启用浏览器显示服务器（Kestrel）。启用后应用启动时自动运行。默认关闭。</summary>
    public bool BrowserDisplayEnabled { get; set; }

    /// <summary>浏览器显示监听端口（默认 5123）。</summary>
    public int BrowserDisplayPort { get; set; } = 5123;

    /// <summary>为 true 时绑定到 0.0.0.0，同一 LAN 上的其他设备（平板/TV）也能访问。默认仅限 localhost。</summary>
    public bool BrowserDisplayLan { get; set; }

    // ---- 歌词源选择（基于注册表，[[0002-pluggable-sources-and-translation]]） ----

    /// <summary>
    /// 启用的歌词源 id 列表（以 LyricsSourceRegistry 为准）。默认=全部。
    /// 公开发布时若要排除非官方源，就收窄到 LyricsSourceRegistry.OfficialIds。
    /// </summary>
    public List<string> EnabledLyricsSources { get; set; } = LyricsSourceRegistry.AllIds.ToList();

    // ---- 使用统计（遥测，ADR-0004） — 两步选择加入，默认全部关闭 ----

    /// <summary>首次运行的同意对话框是否已经展示过（只在首次显示一次）。</summary>
    public bool TelemetryConsentAsked { get; set; }

    /// <summary>① 基础统计（功能·性能·环境 — 不含歌曲信息）。默认关闭（需主动选择加入）。</summary>
    public bool TelemetryBasicEnabled { get; set; }

    /// <summary>② 质量报告（包含错误歌词/搜索失败曲目的标题·歌手 — 需单独同意）。默认关闭。</summary>
    public bool TelemetryQualityEnabled { get; set; }

    /// <summary>匿名客户端 ID（本地生成的随机 GUID）。首次开启同意时生成，可在设置中重置。</summary>
    public string? TelemetryClientId { get; set; }

    // ---- 翻译引擎选择 ----

    /// <summary>翻译引擎 id（TranslatorRegistry）。默认 "none"（仅使用歌词源自带翻译）。</summary>
    public string? TranslationEngine { get; set; } = TranslatorRegistry.None;

    /// <summary>LibreTranslate 端点（自托管等）。为空时使用注册表默认值。</summary>
    public string? LibreTranslateEndpoint { get; set; }

    /// <summary>
    /// 是否使用翻译 API（可在托盘即时切换）。关闭后不再发出新的翻译请求，
    /// 只显示已缓存的译文 — 就地切断付费 API（DeepL/Google）用量的开关。
    /// 引擎·密钥设置会原样保留，因此重新开启即恢复原有行为。默认关闭（仅使用歌词源自带翻译）。
    /// </summary>
    public bool ApiTranslationEnabled { get; set; }

    /// <summary>
    /// 主翻译引擎（DeepL 等）失败时自动切换到无需密钥的免费引擎（MyMemory）。
    /// 开启后歌词文本可能会被发送到免费翻译的公开服务器上（注意）。默认关闭。
    /// </summary>
    public bool TranslationFallbackToFree { get; set; }

    /// <summary>
    /// 实效翻译引擎。有显式值时照用；否则有 DeepL 密钥则为 deepl（保留既有用户的行为），
    /// 否则默认关闭在线机器翻译（none — 仅使用歌词源自带翻译）。
    /// </summary>
    [JsonIgnore]
    public string EffectiveTranslationEngine =>
        !string.IsNullOrWhiteSpace(TranslationEngine) ? TranslationEngine!.Trim().ToLowerInvariant()
        : !string.IsNullOrWhiteSpace(DeeplApiKey) ? "deepl"
        : TranslatorRegistry.None;

    /// <summary>
    /// 覆盖层·控制面板使用的字体名称。为空则为默认（Segoe UI）。
    /// 即使名称未安装，WPF 也会沿回退链自动降级，因此文字不会消失。
    /// </summary>
    public string? OverlayFontFamily { get; set; }

    /// <summary>
    /// OpenRouter 模型字符串（例：<c>anthropic/claude-opus-5</c>）。为空时使用注册表默认值。
    /// 只是名称而非密钥，因此不做加密。
    /// </summary>
    public string? OpenRouterModel { get; set; }

    /// <summary>DeepL API 密钥（明文） — 仅在应用内使用，不写入文件（只保存加密副本）。</summary>
    [JsonIgnore]
    public string? DeeplApiKey { get; set; }

    /// <summary>DeepL 密钥的 DPAPI 密文（base64）。settings.json 中实际保存的值。</summary>
    [JsonPropertyName("deeplApiKeyEnc")]
    public string? DeeplApiKeyEncrypted { get; set; }

    /// <summary>旧版本明文密钥（"deeplApiKey"） — 仅用于迁移（只读，保存时移除）。</summary>
    [JsonPropertyName("deeplApiKey")]
    public string? LegacyDeeplApiKey { get; set; }

    /// <summary>Google Cloud Translation API 密钥（明文） — 文件里只保存加密副本。</summary>
    [JsonIgnore]
    public string? GoogleApiKey { get; set; }

    /// <summary>Google 密钥的 DPAPI 密文（base64）。settings.json 中实际保存的值。</summary>
    [JsonPropertyName("googleApiKeyEnc")]
    public string? GoogleApiKeyEncrypted { get; set; }

    /// <summary>LibreTranslate API 密钥（明文，可选） — 文件里只保存加密副本。</summary>
    [JsonIgnore]
    public string? LibreTranslateApiKey { get; set; }

    /// <summary>LibreTranslate 密钥的 DPAPI 密文（base64）。</summary>
    [JsonPropertyName("libreTranslateApiKeyEnc")]
    public string? LibreTranslateApiKeyEncrypted { get; set; }

    /// <summary>
    /// 个人歌词服务器地址（例：<c>https://oracle.example.ts.net</c>）。为空则不使用。
    /// 在本地缓存未命中与提供方搜索之间查询此服务器，并把新找到的歌词也上传到这里
    /// （契约：<c>contracts/lyrics-api.md</c>）。没有服务器或连不上时，静默降级为原有行为。
    /// </summary>
    public string? LyricsServerEndpoint { get; set; }

    /// <summary>歌词服务器共享令牌（明文） — 文件里只保存加密副本。</summary>
    [JsonIgnore]
    public string? LyricsServerToken { get; set; }

    /// <summary>歌词服务器令牌的 DPAPI 密文（base64）。</summary>
    [JsonPropertyName("lyricsServerTokenEnc")]
    public string? LyricsServerTokenEncrypted { get; set; }

    /// <summary>OpenRouter API 密钥（明文） — 文件里只保存加密副本。</summary>
    [JsonIgnore]
    public string? OpenRouterApiKey { get; set; }

    /// <summary>OpenRouter 密钥的 DPAPI 密文（base64）。</summary>
    [JsonPropertyName("openRouterApiKeyEnc")]
    public string? OpenRouterApiKeyEncrypted { get; set; }

    /// <summary>
    /// 按引擎 id 查询 API 密钥（设置 UI 用它跟随所选引擎显示对应密钥）。
    /// 不使用密钥的引擎（MyMemory 等）返回 null。
    /// </summary>
    public string? GetTranslationApiKey(string engineId) => engineId?.ToLowerInvariant() switch
    {
        "deepl" => DeeplApiKey,
        "google" => GoogleApiKey,
        "libretranslate" => LibreTranslateApiKey,
        "openrouter" => OpenRouterApiKey,
        _ => null,
    };

    /// <summary>按引擎 id 保存 API 密钥（空字符串规范化为 null）。</summary>
    public void SetTranslationApiKey(string engineId, string? key)
    {
        var value = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        switch (engineId?.ToLowerInvariant())
        {
            case "deepl": DeeplApiKey = value; break;
            case "google": GoogleApiKey = value; break;
            case "libretranslate": LibreTranslateApiKey = value; break;
            case "openrouter": OpenRouterApiKey = value; break;
        }
    }

    /// <summary>DeepL target_lang。首次运行时按系统语言设置默认值（不支持则为 EN-US）。</summary>
    public string TargetLanguage { get; set; } = DefaultTargetLanguage();

    /// <summary>UI 显示语言。本项目只用中文，该值仅为兼容旧配置而保留（见 <see cref="Loc.SetLanguage"/>）。</summary>
    public string UiLanguage { get; set; } = Loc.UiLanguageCode;

    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? DefaultTargetLanguage() : TargetLanguage.Trim().ToUpperInvariant();

    /// <summary>
    /// 把系统 UI 语言映射为 DeepL target_lang 代码得到的默认翻译目标语言。
    /// （设置加载时尚未应用 UI 语言覆盖，因此 CurrentUICulture = 系统语言）
    /// </summary>
    public static string DefaultTargetLanguage()
    {
        var c = CultureInfo.CurrentUICulture;
        var two = c.TwoLetterISOLanguageName.ToUpperInvariant();
        return two switch
        {
            "EN" => "EN-US",
            "PT" => c.Name.EndsWith("-PT", StringComparison.OrdinalIgnoreCase) ? "PT-PT" : "PT-BR",
            "ZH" => c.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                    || c.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "ZH-HANT" : "ZH",
            // 若是 DeepL 以 target_lang 支持的语言，就直接使用两字母代码
            "KO" or "JA" or "DE" or "FR" or "ES" or "IT" or "NL" or "PL" or "RU" or "UK" or "TR"
                or "CS" or "ID" or "AR" or "VI" or "BG" or "DA" or "EL" or "ET" or "FI" or "HU"
                or "LT" or "LV" or "NB" or "RO" or "SK" or "SL" or "SV" => two,
            _ => "EN-US", // 不支持的系统语言 → 英语
        };
    }

    public static readonly string DataDirectory = InitDataDirectory();

    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");

    private static string InitDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var lyrxDir = Path.Combine(localAppData, "Lyrx");
        var legacyDir = Path.Combine(localAppData, "Musebase");
        try
        {
            Directory.CreateDirectory(lyrxDir);
            foreach (var file in new[] { "settings.json", "translations.db" })
            {
                var target = Path.Combine(lyrxDir, file);
                var source = Path.Combine(legacyDir, file);
                if (!File.Exists(target) && File.Exists(source))
                    File.Copy(source, target, overwrite: false);
            }
        }
        catch { }
        return lyrxDir;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // null 字段（旧明文密钥等）不写入
    };

    public static AppSettings Load(ISecretStore? secretStore = null)
    {
        var store = secretStore ?? new DpapiSecretStore();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                settings._secretStore = store;
                settings.ResolveSecrets();
                return settings;
            }
        }
        catch
        {
            // 损坏的设置回退为默认值
        }
        return new AppSettings { _secretStore = store };
    }

    /// <summary>解密密文或迁移旧版明文密钥 → 确定明文 API 密钥。</summary>
    private void ResolveSecrets()
    {
        if (!string.IsNullOrEmpty(DeeplApiKeyEncrypted))
            DeeplApiKey = _secretStore.Unprotect(DeeplApiKeyEncrypted);
        else if (!string.IsNullOrWhiteSpace(LegacyDeeplApiKey))
            DeeplApiKey = LegacyDeeplApiKey; // 旧版明文 → 下次 Save 时加密
        LegacyDeeplApiKey = null;            // 明文字段不再保留/记录

        GoogleApiKey = _secretStore.Unprotect(GoogleApiKeyEncrypted);
        LibreTranslateApiKey = _secretStore.Unprotect(LibreTranslateApiKeyEncrypted);
        OpenRouterApiKey = _secretStore.Unprotect(OpenRouterApiKeyEncrypted);
        LyricsServerToken = _secretStore.Unprotect(LyricsServerTokenEncrypted);
    }

    /// <summary>
    /// 用本机的 DPAPI 锁定明文。<b>明文为空但已有密文时，保持原样不动。</b>
    ///
    /// 以前无条件使用 <c>Protect(明文)</c>，而解密失败导致明文为 null 时，连密文也会
    /// 变成 null，并且因为 <c>WhenWritingNull</c>，密钥会从文件里彻底消失 — 在另一台 PC·账户上
    /// 只要打开一次 settings.json，<b>密钥就永久消失了</b>。解密失败并不等于“没有值”，
    /// 而只是“这里读不出来”。
    /// </summary>
    private string? Seal(string? plain, string? existingCipher) =>
        string.IsNullOrEmpty(plain) ? existingCipher : _secretStore.Protect(plain);

    /// <summary>与写入文件相同形态的 JSON — 备份用它承载内容。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 备份<b>不会覆盖</b>的属性。
    ///
    /// 前五个必须因设备而异 — 匿名标识符要按设备区分，窗口坐标则会在显示器
    /// 配置不同时跑到屏幕之外。
    ///
    /// 其余（机密值明文·密文）<b>一旦清除就无法恢复</b>，因此保留。备份文件里密文
    /// 本来就不会被写入（在别的 PC 上解不开），不带密码导出时明文也不会被写入 — 若把这一点
    /// 原样套用，本机的密钥就会在明文和密文两边同时消失。因此只把实际传入的值
    /// 在 <see cref="ApplyBackup"/> 末尾单独覆盖。
    /// </summary>
    private static readonly HashSet<string> KeptOnImport = new(StringComparer.Ordinal)
    {
        nameof(TelemetryClientId),
        nameof(OverlayX), nameof(OverlayY), nameof(PanelX), nameof(PanelY),
        nameof(ClassicLyricsX), nameof(ClassicLyricsY), nameof(ClassicLyricsWidth), nameof(ClassicLyricsHeight),
        nameof(DeeplApiKey), nameof(DeeplApiKeyEncrypted),
        nameof(GoogleApiKey), nameof(GoogleApiKeyEncrypted),
        nameof(LibreTranslateApiKey), nameof(LibreTranslateApiKeyEncrypted),
        nameof(OpenRouterApiKey), nameof(OpenRouterApiKeyEncrypted),
        nameof(LyricsServerToken), nameof(LyricsServerTokenEncrypted),
        nameof(LegacyDeeplApiKey),
    };

    /// <summary>
    /// 把导入的备份覆盖到本设置上。<b>要保留的内容</b>写在 <see cref="KeptOnImport"/> 里。
    ///
    /// 机密值<b>只在备份中确实带有该值时</b>才替换 — 空着的字段不是“清除”
    /// 而是“没有包含”的意思（不带密码导出的备份里根本没有密钥）。
    ///
    /// 传入的明文会由后续的 <see cref="Save"/> <b>用本机的 DPAPI 重新加密</b>。
    /// </summary>
    public void ApplyBackup(JsonElement settings, Musebase.Core.Settings.SettingsBackup.Secrets? secrets)
    {
        var incoming = settings.Deserialize<AppSettings>(JsonOptions)
            ?? throw new InvalidDataException("未能读取设置。");

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (KeptOnImport.Contains(property.Name)) continue;
            property.SetValue(this, property.GetValue(incoming));
        }

        if (secrets is not { } s) return;

        DeeplApiKey = Taken(s.DeeplApiKey, DeeplApiKey);
        GoogleApiKey = Taken(s.GoogleApiKey, GoogleApiKey);
        LibreTranslateApiKey = Taken(s.LibreTranslateApiKey, LibreTranslateApiKey);
        OpenRouterApiKey = Taken(s.OpenRouterApiKey, OpenRouterApiKey);
        LyricsServerToken = Taken(s.LyricsServerToken, LyricsServerToken);

        static string? Taken(string? fromBackup, string? current) =>
            string.IsNullOrEmpty(fromBackup) ? current : fromBackup;
    }

    public void Save()
    {
        try
        {
            // 明文密钥不写入文件，只保存密文（平台机密存储）
            DeeplApiKeyEncrypted = Seal(DeeplApiKey, DeeplApiKeyEncrypted);
            GoogleApiKeyEncrypted = Seal(GoogleApiKey, GoogleApiKeyEncrypted);
            LibreTranslateApiKeyEncrypted = Seal(LibreTranslateApiKey, LibreTranslateApiKeyEncrypted);
            OpenRouterApiKeyEncrypted = Seal(OpenRouterApiKey, OpenRouterApiKeyEncrypted);
            LyricsServerTokenEncrypted = Seal(LyricsServerToken, LyricsServerTokenEncrypted);
            LegacyDeeplApiKey = null;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失败并不致命
        }
    }
}
