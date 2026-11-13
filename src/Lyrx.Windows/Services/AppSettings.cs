using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// 앱 설정 (%LOCALAPPDATA%\Musebase\settings.json).
/// M4에서 DeepL 키/대상 언어를 사용한다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>비밀값(번역 API 키) 보호 저장소. 플랫폼별 교체 가능(기본 Windows DPAPI).</summary>
    [JsonIgnore]
    private ISecretStore _secretStore = new DpapiSecretStore();

    public bool OverlayVisible { get; set; } = true;

    /// <summary>미니창 닫기(X) 시 트레이로 숨긴다(작업표시줄에서 사라짐). 기본 꺼짐=최소화 상주.</summary>
    public bool MiniWindowCloseToTray { get; set; }

    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    /// <summary>오버레이 크기. 텍스트 크기는 높이에 비례해 자동 결정된다.</summary>
    public double OverlayWidth { get; set; } = 780;
    public double OverlayHeight { get; set; } = 150;

    /// <summary>
    /// 제어판(앨범 커버 창)의 위치·크기. 저장된 값이 없으면(<c>null</c>) 첫 실행 배치로 간다 —
    /// 작업영역 우하단에 살짝 띄워서. 늘 띄워 두는 창이라 매번 화면 한가운데 나타나면 방해가 된다.
    /// </summary>
    public double? PanelX { get; set; }
    public double? PanelY { get; set; }

    /// <summary>정사각이라 한 변만 저장한다.</summary>
    public double PanelSize { get; set; } = 360;

    public double? ClassicLyricsX { get; set; }
    public double? ClassicLyricsY { get; set; }
    public double ClassicLyricsWidth { get; set; } = 1060;
    public double ClassicLyricsHeight { get; set; } = 750;
    public bool ClassicShowTranslation { get; set; }
    public bool ClassicTopmost { get; set; }
    public string ClassicBackgroundMode { get; set; } = "dynamic";

    // ---- 오버레이 스타일 (#RRGGBB) ----
    public string TextColor { get; set; } = "#FFFFFF";
    public string KaraokeColor { get; set; } = "#FFEB3B";
    public string TranslationColor { get; set; } = "#E8E8E8";
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 3.0;

    /// <summary>가사가 나타나고 사라질 때 부드럽게 페이드 인/아웃한다.</summary>
    public bool FadeAnimation { get; set; } = true;

    /// <summary>오버레이 배경(반투명 판)을 표시한다.</summary>
    public bool OverlayBackgroundEnabled { get; set; }

    /// <summary>오버레이 배경 색(#RRGGBB). 투명도는 OverlayBackgroundOpacity로 조절.</summary>
    public string OverlayBackgroundColor { get; set; } = "#000000";

    /// <summary>오버레이 배경 불투명도(0=완전 투명 ~ 1=불투명).</summary>
    public double OverlayBackgroundOpacity { get; set; } = 0.25;

    /// <summary>오버레이 배경(반투명 판)의 모서리 라운드 반경(DIP).</summary>
    public double OverlayCornerRadius { get; set; } = 12;

    /// <summary>오버레이 위에 마우스를 올리면 가사·오버레이를 잠시 숨긴다(가림 방지).</summary>
    public bool HideOnMouseOver { get; set; }

    /// <summary>글자 단위 노래방(인라인 타임태그 기반). 지원 곡(Kugou/QQ 등)에서만 동작.</summary>
    public bool CharacterKaraoke { get; set; } = true;

    /// <summary>번역이 원문과 같으면 번역 줄을 숨긴다(예: 영어곡을 EN으로 번역해 원문과 중복).</summary>
    public bool HideSameTranslation { get; set; } = true;

    /// <summary>
    /// 대상 언어 번역(DeepL)만 표시하고, 제공자가 끼워 넣은 다른 언어 번역(주로 중국어)은 숨긴다.
    /// 기본 켬 → DeepL 키가 없으면 원문만 표시. 단, 대상이 중국어(ZH)면 제공자 번역이 곧 중국어이므로 그대로 표시.
    /// </summary>
    public bool ShowOnlyTargetTranslation { get; set; } = true;

    /// <summary>"틀린 가사"로 표시해 검색·표시를 막을 트랙 키 목록(정규화된 제목|아티스트).</summary>
    public List<string> SuppressedTracks { get; set; } = new();

    /// <summary>수동 싱크 오프셋(초). +면 가사가 빨라진다.</summary>
    public double ManualOffsetSeconds { get; set; }

    /// <summary>
    /// 재생 소스 선택. "auto" = 자동 감지, 그 외 = 특정 플레이어의 SourceAppUserModelId로 고정.
    /// (macOS 버전의 플레이어 선택에 대응 — Windows에서는 SMTC 세션 단위로 고정)
    /// </summary>
    public string PlaybackSource { get; set; } = "auto";

    /// <summary>자동 모드에서 브라우저(Firefox/Chrome 등)를 음악 소스로 포함할지. 기본 제외(영상 오인식 방지).</summary>
    public bool IncludeBrowsers { get; set; } = true;

    /// <summary>
    /// 선호 음악 앱(SourceAppUserModelId 목록). 비어 있으면(기본) 자동 —
    /// 브라우저·영상·팟캐스트 앱을 제외한 나머지를 자동 감지한다. 하나라도 고르면
    /// **그 앱들만** 재생 소스로 인정한다(Android의 PreferredSources와 같은 의미).
    /// </summary>
    [JsonPropertyName("preferredSources")]
    public List<string> PreferredSources { get; set; } = new();

    // ---- 브라우저 디스플레이 (태블릿/TV 원격 표시, Musebase.Browser 인프로세스 호스팅) ----

    /// <summary>브라우저 디스플레이 서버(Kestrel)를 켤지. 켜면 앱 시작 시 자동 기동. 기본 꺼짐.</summary>
    public bool BrowserDisplayEnabled { get; set; }

    /// <summary>브라우저 디스플레이 리슨 포트(기본 5123).</summary>
    public int BrowserDisplayPort { get; set; } = 5123;

    /// <summary>true면 0.0.0.0에 바인딩해 같은 LAN의 다른 기기(태블릿/TV)에서 접속 가능. 기본 localhost 전용.</summary>
    public bool BrowserDisplayLan { get; set; }

    // ---- 가사 소스 선택 (레지스트리 기반, [[0002-pluggable-sources-and-translation]]) ----

    /// <summary>
    /// 활성 가사 소스 id 목록(LyricsSourceRegistry 기준). 기본=전부.
    /// 공개 배포 시 비공식 소스를 빼려면 LyricsSourceRegistry.OfficialIds로 좁힌다.
    /// </summary>
    public List<string> EnabledLyricsSources { get; set; } = LyricsSourceRegistry.AllIds.ToList();

    // ---- 사용 통계(텔레메트리, ADR-0004) — 옵트인 2단계, 기본 모두 꺼짐 ----

    /// <summary>최초 실행 동의 다이얼로그를 이미 보여줬는지(최초 1회만 표시).</summary>
    public bool TelemetryConsentAsked { get; set; }

    /// <summary>① 기본 통계(기능·성능·환경 — 곡 정보 없음). 기본 꺼짐(옵트인).</summary>
    public bool TelemetryBasicEnabled { get; set; }

    /// <summary>② 품질 리포트(틀린가사/검색실패 곡의 제목·아티스트 포함 — 별도 동의). 기본 꺼짐.</summary>
    public bool TelemetryQualityEnabled { get; set; }

    /// <summary>익명 클라이언트 ID(로컬 생성 랜덤 GUID). 동의 최초 켜짐 시 생성, 설정에서 재설정 가능.</summary>
    public string? TelemetryClientId { get; set; }

    // ---- 번역 엔진 선택 ----

    /// <summary>번역 엔진 id(TranslatorRegistry). 비면 EffectiveTranslationEngine으로 자동 결정.</summary>
    public string? TranslationEngine { get; set; }

    /// <summary>LibreTranslate 엔드포인트(자체호스팅 등). 비면 레지스트리 기본값 사용.</summary>
    public string? LibreTranslateEndpoint { get; set; }

    /// <summary>
    /// 번역 API 사용 여부(트레이에서 즉시 토글). 끄면 새 번역 요청을 보내지 않고
    /// 이미 캐시된 번역만 표시한다 — 유료 API(DeepL/Google) 사용량을 그 자리에서 끊는 스위치.
    /// 엔진·키 설정은 그대로 보존되므로 다시 켜면 원래대로 동작한다. 기본 켬.
    /// </summary>
    public bool ApiTranslationEnabled { get; set; } = true;

    /// <summary>
    /// 주 번역 엔진(DeepL 등) 실패 시 무키 무료 엔진(MyMemory)으로 자동 전환한다.
    /// 켜면 가사 텍스트가 무료 번역 공개 서버로 전송될 수 있다(주의). 기본 꺼짐.
    /// </summary>
    public bool TranslationFallbackToFree { get; set; }

    /// <summary>
    /// 실효 번역 엔진. 명시값이 있으면 그대로, 없으면 DeepL 키가 있으면 deepl(기존 사용자 보존),
    /// 가사 서버가 있으면 끔 — 서버가 새 곡을 번역해 채우므로 기기는 가사를 밖으로 보내지 않는다,
    /// 둘 다 없으면 무키 무료 기본(MyMemory — 설치 후 바로 동작). LibreTranslate 공개 인스턴스가
    /// 유료 키 필수로 바뀌어 기본 무키 엔진에서 제외했다.
    /// </summary>
    [JsonIgnore]
    public string EffectiveTranslationEngine =>
        !string.IsNullOrWhiteSpace(TranslationEngine) ? TranslationEngine!.Trim().ToLowerInvariant()
        : !string.IsNullOrWhiteSpace(DeeplApiKey) ? "deepl"
        : !string.IsNullOrWhiteSpace(LyricsServerEndpoint) ? TranslatorRegistry.None
        : TranslatorRegistry.DefaultFreeEngine;

    /// <summary>
    /// 오버레이·제어판에 쓸 글꼴 이름. 비면 기본(Segoe UI).
    /// 설치돼 있지 않은 이름이어도 WPF가 폴백 체인으로 알아서 내려가므로 글자가 사라지지 않는다.
    /// </summary>
    public string? OverlayFontFamily { get; set; }

    /// <summary>
    /// OpenRouter 모델 문자열(예: <c>anthropic/claude-opus-5</c>). 비면 레지스트리 기본값.
    /// 키가 아니라 그냥 이름이므로 암호화하지 않는다.
    /// </summary>
    public string? OpenRouterModel { get; set; }

    /// <summary>DeepL API 키(평문) — 앱 내에서만 사용, 파일엔 저장하지 않는다(암호화본만 저장).</summary>
    [JsonIgnore]
    public string? DeeplApiKey { get; set; }

    /// <summary>DeepL 키의 DPAPI 암호문(base64). settings.json에 실제 저장되는 값.</summary>
    [JsonPropertyName("deeplApiKeyEnc")]
    public string? DeeplApiKeyEncrypted { get; set; }

    /// <summary>구버전 평문 키("deeplApiKey") — 마이그레이션 전용(읽기만, 저장 시 제거).</summary>
    [JsonPropertyName("deeplApiKey")]
    public string? LegacyDeeplApiKey { get; set; }

    /// <summary>Google Cloud Translation API 키(평문) — 파일엔 암호화본만 저장한다.</summary>
    [JsonIgnore]
    public string? GoogleApiKey { get; set; }

    /// <summary>Google 키의 DPAPI 암호문(base64). settings.json에 실제 저장되는 값.</summary>
    [JsonPropertyName("googleApiKeyEnc")]
    public string? GoogleApiKeyEncrypted { get; set; }

    /// <summary>LibreTranslate API 키(평문, 선택) — 파일엔 암호화본만 저장한다.</summary>
    [JsonIgnore]
    public string? LibreTranslateApiKey { get; set; }

    /// <summary>LibreTranslate 키의 DPAPI 암호문(base64).</summary>
    [JsonPropertyName("libreTranslateApiKeyEnc")]
    public string? LibreTranslateApiKeyEncrypted { get; set; }

    /// <summary>
    /// 개인 가사 서버 주소(예: <c>https://oracle.example.ts.net</c>). 비면 사용하지 않는다.
    /// 로컬 캐시 미스와 제공자 검색 사이에 이 서버를 조회하고, 새로 찾은 가사를 여기에도 올린다
    /// (계약: <c>contracts/lyrics-api.md</c>). 서버가 없거나 못 붙으면 조용히 기존 동작으로 강등된다.
    /// </summary>
    public string? LyricsServerEndpoint { get; set; }

    /// <summary>가사 서버 공유 토큰(평문) — 파일엔 암호화본만 저장한다.</summary>
    [JsonIgnore]
    public string? LyricsServerToken { get; set; }

    /// <summary>가사 서버 토큰의 DPAPI 암호문(base64).</summary>
    [JsonPropertyName("lyricsServerTokenEnc")]
    public string? LyricsServerTokenEncrypted { get; set; }

    /// <summary>OpenRouter API 키(평문) — 파일엔 암호화본만 저장한다.</summary>
    [JsonIgnore]
    public string? OpenRouterApiKey { get; set; }

    /// <summary>OpenRouter 키의 DPAPI 암호문(base64).</summary>
    [JsonPropertyName("openRouterApiKeyEnc")]
    public string? OpenRouterApiKeyEncrypted { get; set; }

    /// <summary>
    /// 엔진 id별 API 키 조회(설정 UI가 선택된 엔진의 키를 따라 보여주는 데 쓴다).
    /// 키를 쓰지 않는 엔진(MyMemory 등)은 null.
    /// </summary>
    public string? GetTranslationApiKey(string engineId) => engineId?.ToLowerInvariant() switch
    {
        "deepl" => DeeplApiKey,
        "google" => GoogleApiKey,
        "libretranslate" => LibreTranslateApiKey,
        "openrouter" => OpenRouterApiKey,
        _ => null,
    };

    /// <summary>엔진 id별 API 키 저장(빈 문자열은 null로 정규화).</summary>
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

    /// <summary>DeepL target_lang. 최초 실행 시 시스템 언어로 기본 설정(미지원이면 EN-US).</summary>
    public string TargetLanguage { get; set; } = DefaultTargetLanguage();

    /// <summary>UI 표시 언어. "system"이면 시스템 언어를 따르고, 미지원이면 영어로 폴백.</summary>
    public string UiLanguage { get; set; } = "system";

    [JsonIgnore]
    public string EffectiveTargetLanguage =>
        string.IsNullOrWhiteSpace(TargetLanguage) ? DefaultTargetLanguage() : TargetLanguage.Trim().ToUpperInvariant();

    /// <summary>
    /// 시스템 UI 언어를 DeepL target_lang 코드로 매핑한 기본 번역 대상 언어.
    /// (설정 로드 시점에는 아직 UI 언어 오버라이드 전이라 CurrentUICulture = 시스템 언어)
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
            // DeepL이 target_lang으로 지원하는 언어면 2글자 코드 그대로 사용
            "KO" or "JA" or "DE" or "FR" or "ES" or "IT" or "NL" or "PL" or "RU" or "UK" or "TR"
                or "CS" or "ID" or "AR" or "VI" or "BG" or "DA" or "EL" or "ET" or "FI" or "HU"
                or "LT" or "LV" or "NB" or "RO" or "SK" or "SL" or "SV" => two,
            _ => "EN-US", // 미지원 시스템 언어 → 영어
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // null 필드(구 평문 키 등) 미기록
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
            // 손상된 설정은 기본값으로
        }
        return new AppSettings { _secretStore = store };
    }

    /// <summary>암호문 복호화 또는 구버전 평문 키 마이그레이션 → 평문 API 키 확정.</summary>
    private void ResolveSecrets()
    {
        if (!string.IsNullOrEmpty(DeeplApiKeyEncrypted))
            DeeplApiKey = _secretStore.Unprotect(DeeplApiKeyEncrypted);
        else if (!string.IsNullOrWhiteSpace(LegacyDeeplApiKey))
            DeeplApiKey = LegacyDeeplApiKey; // 구버전 평문 → 다음 Save에서 암호화
        LegacyDeeplApiKey = null;            // 평문 필드는 더 이상 보관/기록하지 않음

        GoogleApiKey = _secretStore.Unprotect(GoogleApiKeyEncrypted);
        LibreTranslateApiKey = _secretStore.Unprotect(LibreTranslateApiKeyEncrypted);
        OpenRouterApiKey = _secretStore.Unprotect(OpenRouterApiKeyEncrypted);
        LyricsServerToken = _secretStore.Unprotect(LyricsServerTokenEncrypted);
    }

    /// <summary>
    /// 평문을 이 PC의 DPAPI로 잠근다. <b>평문이 비었는데 기존 암호문이 있으면 그대로 둔다.</b>
    ///
    /// 예전에는 무조건 <c>Protect(평문)</c>을 썼는데, 복호에 실패해 평문이 null이면 암호문까지
    /// null이 되고 <c>WhenWritingNull</c> 때문에 키가 파일에서 아예 빠졌다 — 다른 PC·계정에서
    /// settings.json을 한 번 열어 보기만 해도 <b>키가 영영 사라졌다</b>. 복호 실패는 "값이 없다"가
    /// 아니라 "여기서는 못 읽는다"일 뿐이다.
    /// </summary>
    private string? Seal(string? plain, string? existingCipher) =>
        string.IsNullOrEmpty(plain) ? existingCipher : _secretStore.Protect(plain);

    /// <summary>파일에 쓰는 것과 같은 모양의 JSON — 백업이 이걸 담는다.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 백업이 <b>덮어쓰지 않는</b> 속성.
    ///
    /// 앞의 다섯은 기기마다 달라야 한다 — 익명 식별자는 기기별로 갈라야 하고, 창 좌표는 모니터
    /// 구성이 다르면 화면 밖이 된다.
    ///
    /// 나머지(비밀값 평문·암호문)는 <b>지우면 되돌릴 수 없어서</b> 지킨다. 백업 파일에는 암호문이
    /// 애초에 담기지 않고(다른 PC에서 못 푼다) 비밀번호 없이 내보내면 평문도 안 담긴다 — 그걸
    /// 그대로 반영하면 이 PC의 키가 평문·암호문 양쪽에서 사라진다. 실제로 들어온 값만
    /// <see cref="ApplyBackup"/> 끝에서 따로 덮어쓴다.
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
    /// 가져온 백업을 이 설정에 덮어쓴다. <b>지키는 것</b>은 <see cref="KeptOnImport"/>에 적혀 있다.
    ///
    /// 비밀값은 <b>백업에 실제로 들어 있을 때만</b> 바꾼다 — 비어 있는 칸은 "지우라"가 아니라
    /// "안 담겼다"는 뜻이다(비밀번호 없이 내보낸 백업에는 키가 아예 없다).
    ///
    /// 들어온 평문은 이어지는 <see cref="Save"/>가 <b>이 PC의 DPAPI로 다시 잠근다</b>.
    /// </summary>
    public void ApplyBackup(JsonElement settings, Musebase.Core.Settings.SettingsBackup.Secrets? secrets)
    {
        var incoming = settings.Deserialize<AppSettings>(JsonOptions)
            ?? throw new InvalidDataException("설정을 읽지 못했습니다.");

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
            // 평문 키는 파일에 쓰지 않고, 암호문만 저장(플랫폼 시크릿 저장소)
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
            // 저장 실패는 치명적이지 않음
        }
    }
}
