using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// Android 端应用配置（保存在 <c>Context.FilesDir/settings.json</c>）。
/// 与 Windows 版保持相同的核心语义和默认策略（默认仅使用歌词源自带翻译、动态背景取色）。
/// </summary>
public sealed class AndroidAppSettings
{
    [JsonIgnore]
    private ISecretStore _secretStore = new AndroidSecretStore();

    [JsonIgnore]
    private string _settingsPath = "";

    public bool ShowTranslation { get; set; }

    public bool ShowOnlyTargetTranslation { get; set; } = true;

    public bool HideSameTranslation { get; set; } = true;

    /// <summary>"dynamic" = 跟随专辑封面动态取色（默认）。</summary>
    public string BackgroundMode { get; set; } = "dynamic";

    /// <summary>手动同步偏移（秒）。正数表示歌词提前。</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>按曲目记忆的额外时间偏移（秒），与 <see cref="ManualOffsetSeconds"/> 叠加。</summary>
    public Dictionary<string, double> TrackOffsetSeconds { get; set; } = new();

    public double GetTrackOffset(string? key) =>
        !string.IsNullOrEmpty(key) && TrackOffsetSeconds.TryGetValue(key, out var value) ? value : 0;

    public void SetTrackOffset(string? key, double value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (Math.Abs(value) < 0.001) TrackOffsetSeconds.Remove(key);
        else TrackOffsetSeconds[key] = Math.Round(value, 2);
    }

    /// <summary>歌词行距档位："compact" | "normal" | "loose"。</summary>
    public string LyricsLineSpacing { get; set; } = "normal";

    [JsonIgnore]
    public (double LineHeight, double Gap) LineSpacingScales => LyricsLineSpacing?.ToLowerInvariant() switch
    {
        "compact" => (0.94, 0.85),
        "loose" => (1.12, 1.45),
        _ => (1.0, 1.0),
    };

    /// <summary>手动滚动歌词后自动回到当前行的时间（秒）。0 表示关闭。</summary>
    public int AutoSyncSeconds { get; set; } = 5;

    /// <summary>支持逐字时间标签时显示平滑推进高亮。</summary>
    public bool CharacterKaraoke { get; set; } = true;

    /// <summary>"auto" = 自动检测活跃音乐会话；其他值 = 锁定特定应用包名。</summary>
    public string PlaybackSource { get; set; } = "auto";

    public bool IncludeBrowsers { get; set; }

    [JsonPropertyName("preferredSources")]
    public List<string> PreferredSources { get; set; } = new();

    public List<string> SuppressedTracks { get; set; } = new();

    public List<string> EnabledLyricsSources { get; set; } = LyricsSourceRegistry.AllIds.ToList();

    public string? TranslationEngine { get; set; } = TranslatorRegistry.None;

    public bool ApiTranslationEnabled { get; set; }

    public string TargetLanguage { get; set; } = DefaultTargetLanguage();

    public string? LibreTranslateEndpoint { get; set; }

    public string? OpenRouterModel { get; set; }

    [JsonIgnore] public string? DeeplApiKey { get; set; }
    [JsonPropertyName("deeplApiKeyEnc")] public string? DeeplApiKeyEncrypted { get; set; }

    [JsonIgnore] public string? GoogleApiKey { get; set; }
    [JsonPropertyName("googleApiKeyEnc")] public string? GoogleApiKeyEncrypted { get; set; }

    [JsonIgnore] public string? LibreTranslateApiKey { get; set; }
    [JsonPropertyName("libreTranslateApiKeyEnc")] public string? LibreTranslateApiKeyEncrypted { get; set; }

    [JsonIgnore] public string? OpenRouterApiKey { get; set; }
    [JsonPropertyName("openRouterApiKeyEnc")] public string? OpenRouterApiKeyEncrypted { get; set; }

    public bool TimingDiagnosticsEnabled { get; set; }

    [JsonIgnore]
    public string EffectiveTranslationEngine =>
        !string.IsNullOrWhiteSpace(TranslationEngine) ? TranslationEngine!.Trim().ToLowerInvariant()
        : !string.IsNullOrWhiteSpace(DeeplApiKey) ? "deepl"
        : TranslatorRegistry.None;

    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? DefaultTargetLanguage() : TargetLanguage.Trim().ToUpperInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultTargetLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var two = culture.TwoLetterISOLanguageName.ToUpperInvariant();
        return two switch
        {
            "ZH" => culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                    || culture.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) ? "ZH-HANT" : "ZH",
            "EN" => "EN-US",
            "JA" or "KO" or "DE" or "FR" or "ES" or "IT" => two,
            _ => "ZH",
        };
    }

    public static AndroidAppSettings Load(string dataDirectory, ISecretStore? secretStore = null)
    {
        var store = secretStore ?? new AndroidSecretStore();
        var path = Path.Combine(dataDirectory, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AndroidAppSettings>(File.ReadAllText(path), JsonOptions)
                             ?? new AndroidAppSettings();
                loaded._secretStore = store;
                loaded._settingsPath = path;
                loaded.ResolveSecrets();
                return loaded;
            }
        }
        catch
        {
            // 配置文件损坏时回退到默认配置
        }

        return new AndroidAppSettings
        {
            _secretStore = store,
            _settingsPath = path,
        };
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        try
        {
            DeeplApiKeyEncrypted = Seal(DeeplApiKey, DeeplApiKeyEncrypted);
            GoogleApiKeyEncrypted = Seal(GoogleApiKey, GoogleApiKeyEncrypted);
            LibreTranslateApiKeyEncrypted = Seal(LibreTranslateApiKey, LibreTranslateApiKeyEncrypted);
            OpenRouterApiKeyEncrypted = Seal(OpenRouterApiKey, OpenRouterApiKeyEncrypted);

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失败不阻断运行
        }
    }

    private void ResolveSecrets()
    {
        DeeplApiKey = _secretStore.Unprotect(DeeplApiKeyEncrypted);
        GoogleApiKey = _secretStore.Unprotect(GoogleApiKeyEncrypted);
        LibreTranslateApiKey = _secretStore.Unprotect(LibreTranslateApiKeyEncrypted);
        OpenRouterApiKey = _secretStore.Unprotect(OpenRouterApiKeyEncrypted);
    }

    private string? Seal(string? plain, string? existingCipher) =>
        string.IsNullOrEmpty(plain) ? existingCipher : _secretStore.Protect(plain);
}
