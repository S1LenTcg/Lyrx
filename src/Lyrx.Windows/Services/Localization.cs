using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Jeffijoe.MessageFormat;

namespace Musebase.Windows.Services;

/// <summary>드롭다운·번역 대상 언어 항목 (문화권 코드 + 자국어 표기).</summary>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// UI 다국어(i18n) 조회 서비스.
/// - 로케일별 JSON 카탈로그(임베디드 리소스 <c>Musebase.Windows.i18n.&lt;code&gt;.json</c>)를 읽어 키→문자열 조회.
/// - 문화권 폴백: (설정 or 시스템) → 정확/중립 매칭 → 영어(en).
/// - 인자/복수형은 ICU MessageFormat으로 처리.
/// 카탈로그가 없는 지원 언어는 영어로 폴백된다(P2에서 Weblate 기계번역으로 채움).
/// </summary>
public static class Loc
{
    public const string SystemSetting = "system";

    /// <summary>번역 기여 안내(GitHub). i18n 카탈로그를 직접 편집·PR하거나 이슈로 제안.</summary>
    public const string ContributionUrl = "https://github.com/countnine/musebase/blob/master/TRANSLATING.md";

    /// <summary>지원 언어: 기본 10개 + 오픈소스에서 활발한 언어 9개.</summary>
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new[]
    {
        new LanguageOption("en", "English"),
        new LanguageOption("ko", "한국어"),
        new LanguageOption("ja", "日本語"),
        new LanguageOption("zh-Hans", "简体中文"),
        new LanguageOption("zh-Hant", "繁體中文"),
        new LanguageOption("es", "Español"),
        new LanguageOption("pt-BR", "Português (Brasil)"),
        new LanguageOption("fr", "Français"),
        new LanguageOption("de", "Deutsch"),
        new LanguageOption("ru", "Русский"),
        new LanguageOption("it", "Italiano"),
        new LanguageOption("pl", "Polski"),
        new LanguageOption("tr", "Türkçe"),
        new LanguageOption("nl", "Nederlands"),
        new LanguageOption("uk", "Українська"),
        new LanguageOption("cs", "Čeština"),
        new LanguageOption("vi", "Tiếng Việt"),
        new LanguageOption("id", "Bahasa Indonesia"),
        new LanguageOption("ar", "العربية"),
    };

    private static readonly Dictionary<string, string> Fallback = LoadCatalog("en") ?? new();
    private static readonly Dictionary<string, object?> EmptyArgs = new();

    private static Dictionary<string, string> _current = Fallback;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    private static MessageFormatter _formatter = new(true, CultureInfo.GetCultureInfo("en"), null);

    /// <summary>현재 해석된 카탈로그 코드(예: "ko", "zh-Hant", "en").</summary>
    public static string CurrentCode { get; private set; } = "en";

    /// <summary>사용자 설정 값("system" 또는 문화권 코드).</summary>
    public static string Setting { get; private set; } = SystemSetting;

    /// <summary>언어가 바뀌면 발생(열려 있는 창이 문자열을 다시 읽도록).</summary>
    public static event Action? CultureChanged;

    public static void Initialize(string? setting) => SetLanguage(setting ?? SystemSetting);

    public static void SetLanguage(string setting)
    {
        Setting = string.IsNullOrWhiteSpace(setting) ? SystemSetting : setting;
        var code = ResolveCode(Setting);
        _current = LoadCatalog(code) ?? Fallback;
        CurrentCode = code;
        _culture = SafeCulture(code);
        _formatter = new MessageFormatter(true, _culture, null);
        // 프레임워크 텍스트/숫자 표기를 맞추기 위해 UI 컬처만 설정(형식 컬처는 유지)
        try { CultureInfo.CurrentUICulture = _culture; } catch { /* 무시 */ }
        CultureChanged?.Invoke();
    }

    /// <summary>키로 현지화 문자열 조회(인자 없음).</summary>
    public static string T(string key) => Format(Lookup(key), null);

    /// <summary>키로 현지화 문자열 조회 + ICU 인자. 예: T("k", ("value", 3)).</summary>
    public static string T(string key, params (string Name, object? Value)[] args)
        => Format(Lookup(key), args.Length == 0 ? null : args.ToDictionary(a => a.Name, a => a.Value));

    private static string Lookup(string key)
        => _current.TryGetValue(key, out var v) ? v
         : Fallback.TryGetValue(key, out var f) ? f
         : key; // 최후: 키 자체 노출(누락을 눈에 띄게)

    private static string Format(string pattern, IReadOnlyDictionary<string, object?>? args)
    {
        // 인자도 없고 중괄호도 없으면 ICU 파서를 거치지 않는다(아포스트로피 등 오해석 방지)
        if (args is null && !pattern.Contains('{')) return pattern;
        try { return _formatter.FormatMessage(pattern, args ?? EmptyArgs, _culture); }
        catch { return pattern; }
    }

    // ---- 문화권 해석 ----

    private static string ResolveCode(string setting)
    {
        var culture = setting == SystemSetting ? CultureInfo.CurrentUICulture : SafeCulture(setting);
        return MatchSupported(culture);
    }

    private static string MatchSupported(CultureInfo culture)
    {
        var codes = SupportedLanguages.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 중국어: 간체/번체 스크립트 판별
        if (string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var name = culture.Name;
            var hant = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return hant ? "zh-Hant" : "zh-Hans";
        }

        if (codes.Contains(culture.Name)) return culture.Name;          // 정확 매칭(pt-BR 등)
        var two = culture.TwoLetterISOLanguageName;
        if (string.Equals(two, "pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR"; // 우리가 가진 유일한 pt 변형
        if (codes.Contains(two)) return two;                           // 중립 매칭(ko, ja 등)
        return "en";
    }

    private static CultureInfo SafeCulture(string code)
    {
        try { return CultureInfo.GetCultureInfo(code); }
        catch { return CultureInfo.InvariantCulture; }
    }

    private static Dictionary<string, string>? LoadCatalog(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"Musebase.Windows.i18n.{code}.json");
        if (stream is null) return null;
        try
        {
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }
}
