using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Jeffijoe.MessageFormat;

namespace Musebase.Windows.Services;

/// <summary>下拉框·翻译目标语言条目（文化代码 + 母语写法）。</summary>
public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// UI 文案查询服务。本项目只使用中文，因此只加载一份嵌入资源
/// <c>Musebase.Windows.i18n.zh-Hans.json</c>，不再做区域设置匹配与英语回退。
/// 参数/复数形式仍由 ICU MessageFormat 处理。
///
/// 保留 <see cref="Setting"/>、<see cref="CurrentCode"/>、<see cref="CultureChanged"/>
/// 这三个对外形状：调用方（启动流程、设置窗口）无需改动，将来若重新引入多语言，
/// 只需在这里恢复目录加载与匹配逻辑即可。
/// </summary>
public static class Loc
{
    /// <summary>历史设置值，配置文件里可能仍存着它；本项目会无视它，一律用中文。</summary>
    public const string SystemSetting = "system";

    /// <summary>唯一的 UI 语言代码。</summary>
    public const string UiLanguageCode = "zh-Hans";

    /// <summary>当前支持的 UI 语言（只有一个）。</summary>
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new[]
    {
        new LanguageOption(UiLanguageCode, "简体中文"),
    };

    private static readonly Dictionary<string, string> Catalog = LoadCatalog(UiLanguageCode) ?? new();
    private static readonly Dictionary<string, object?> EmptyArgs = new();

    private static Dictionary<string, string> _current = Catalog;
    private static CultureInfo _culture = SafeCulture(UiLanguageCode);
    private static MessageFormatter _formatter = new(true, SafeCulture(UiLanguageCode), null);

    /// <summary>当前使用的目录代码（固定为 <see cref="UiLanguageCode"/>）。</summary>
    public static string CurrentCode { get; private set; } = UiLanguageCode;

    /// <summary>用户设置值（本项目只看不用，保留以兼容旧配置）。</summary>
    public static string Setting { get; private set; } = UiLanguageCode;

    /// <summary>语言变化时触发（让已打开的窗口重新读取字符串）。</summary>
    public static event Action? CultureChanged;

    /// <summary>初始化。传入什么值都无所谓——本项目固定中文。</summary>
    public static void Initialize(string? setting) => SetLanguage(setting ?? UiLanguageCode);

    public static void SetLanguage(string setting)
    {
        // 只记录用户原来的设置值，实际目录恒为中文目录。
        Setting = string.IsNullOrWhiteSpace(setting) ? UiLanguageCode : setting;
        _current = Catalog;
        CurrentCode = UiLanguageCode;
        _culture = SafeCulture(UiLanguageCode);
        _formatter = new MessageFormatter(true, _culture, null);
        // 为对齐框架的文本/数字表示，只设置 UI 文化（保留格式文化）
        try { CultureInfo.CurrentUICulture = _culture; } catch { /* 忽略 */ }
        CultureChanged?.Invoke();
    }

    /// <summary>按键查询本地化字符串（无参数）。</summary>
    public static string T(string key) => Format(Lookup(key), null);

    /// <summary>按键查询本地化字符串 + ICU 参数。例如 T("k", ("value", 3)).</summary>
    public static string T(string key, params (string Name, object? Value)[] args)
        => Format(Lookup(key), args.Length == 0 ? null : args.ToDictionary(a => a.Name, a => a.Value));

    private static string Lookup(string key)
        => _current.TryGetValue(key, out var v) ? v
         : key; // 缺失时直接暴露键本身（让缺失显眼）

    private static string Format(string pattern, IReadOnlyDictionary<string, object?>? args)
    {
        // 既无参数也无花括号时不经过 ICU 解析器（避免撇号等被误解析）
        if (args is null && !pattern.Contains('{')) return pattern;
        try { return _formatter.FormatMessage(pattern, args ?? EmptyArgs, _culture); }
        catch { return pattern; }
    }

    private static CultureInfo SafeCulture(string code)
    {
        try { return CultureInfo.GetCultureInfo(code); }
        catch { return CultureInfo.GetCultureInfo("zh-CN"); }
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
