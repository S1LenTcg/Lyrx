namespace Musebase.Engine;

/// <summary>歌词搜索与显示状态的种类（本地化由消费者 UI 负责）。</summary>
public enum LyricsStatusKind
{
    NoTrack,
    HiddenByUser,
    Cache,
    Searching,
    Found,
    NotFound,
    Wrong,
    Manual,
    Edited,
}

/// <summary>
/// 引擎发布的结构化状态。传递种类+数据而不是字符串/本地化文案，
/// 从而把引擎与 UI（本地化）解耦。由消费者用 Loc 等生成文案。
/// </summary>
public sealed record LyricsStatus(
    LyricsStatusKind Kind,
    string? Track = null,
    string? Service = null,
    double? Quality = null);

/// <summary>
/// 目标语言翻译的显示状态（标注在歌词来源/质量旁边）。本地化由消费者 UI 负责。
/// UI 会像 "歌词: QQMusic (质量 1.04) · 翻译: 配额超限" 这样贴在来源文本旁边。
/// </summary>
public enum TranslationDisplayStatus
{
    None,        // 翻译引擎关闭 / 不需要（目标=中文等）—— 不标注
    Translating, // 翻译进行中
    Live,        // 本次由翻译 API 填充（正常翻译）
    Cache,       // 由缓存填充（离线 / 重新播放）
    Quota,       // 因配额超限、请求受限而失败
    Failed,      // 其他失败（认证/服务器/网络）
    Disabled,      // 用户关闭了 API 翻译 —— 仍有未填充的行（正在无翻译显示）
    DisabledCached, // 用户关闭了 API 翻译，但需要的行已由缓存/既有翻译全部填充（翻译正常显示）
}
