using System.Text;

namespace Musebase.Core.Meaning;

/// <summary>
/// 수집한 영어 원문들을 읽고 "이 곡이 무엇에 대한 노래인지"를 대상 언어로 써 준다.
///
/// 번역이 아니라 **요약**이라 <see cref="Translation.ITranslator"/>로는 안 된다 — DeepL에 긴 영어
/// bio를 넣으면 긴 한국어 문서가 나올 뿐 "의미"가 되지 않는다. 구현은 순수 HTTP + JSON이며
/// 엔진은 <see cref="MeaningWriterRegistry"/>로 갈아끼운다(번역 엔진과 같은 구조).
///
/// 실패는 예외가 아니라 <see cref="MeaningWriteResult"/>다 — 의미는 부가 기능이고, 없다고
/// 가사가 안 뜨면 안 된다. 다만 "일시적 실패"만은 구분해서 돌려준다(그쪽 설명 참고).
/// </summary>
public interface IMeaningWriter
{
    /// <summary>레지스트리 id(예: "gemini"). 어떤 엔진으로 만들었는지 기록해 둔다.</summary>
    string EngineId { get; }

    /// <summary>실제로 호출한 모델 이름(재생성 판단·기록용).</summary>
    string Model { get; }

    Task<MeaningWriteResult> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default);
}

/// <summary>
/// 엔진에 상관없이 같은 프롬프트를 쓴다 — 모델을 바꿔도 결과의 성격이 흔들리지 않게 하고,
/// 두 엔진의 출력을 나란히 비교할 수 있게 하기 위해서다.
/// </summary>
public static class MeaningPrompt
{
    /// <summary>원문이 아무리 길어도 이 길이까지만 넣는다(토큰 폭주 방지).</summary>
    public const int MaxSourceChars = 6000;

    /// <summary>
    /// 출력 상한. 3~5문장이면 충분하고, 넉넉히 잡아도 이 정도다.
    /// **반드시 요청에 실어야 한다** — 상한을 안 보내면 공급자가 모델 최대치를 예약하려 들어
    /// 잔액이 적은 계정에서 402로 거절당한다(OpenRouter 실측). 비용 폭주 방지이기도 하다.
    /// </summary>
    public const int MaxOutputTokens = 1200;

    /// <summary>
    /// 마지막 문장이 이 프롬프트의 핵심이다 — 자료가 부족할 때 모델이 지어내지 않고
    /// "부족하다"고 쓰게 만든다. 곡 해설은 그럴듯한 창작이 특히 쉬운 영역이다.
    /// </summary>
    public static string Build(
        string title, string artist, IReadOnlyList<MeaningSource> sources, string targetLang)
    {
        var language = LanguageName(targetLang);
        var sb = new StringBuilder();
        sb.Append("다음은 한 곡에 대해 여러 웹 자료에서 모은 설명이다.\n\n");
        sb.Append($"곡: {title}\n아티스트: {artist}\n\n");

        var budget = MaxSourceChars;
        foreach (var source in sources)
        {
            if (budget <= 0) break;
            var text = source.Text.Length > budget ? source.Text[..budget] : source.Text;
            budget -= text.Length;
            sb.Append($"[{source.Name}]\n{text}\n\n");
        }

        sb.Append($"""
            위 자료만 근거로, 이 곡이 무엇에 대한 노래인지 {language}로 3~5문장으로 써라.

            - 작곡 배경, 가사가 다루는 주제, 알려진 해석을 중심으로 쓴다.
            - 자료에 없는 내용은 절대 지어내지 않는다. 추측하지 않는다.
            - 자료가 부족해 이 곡이 무엇에 대한 노래인지 말할 수 없으면, 첫 줄에 정확히
              [자료부족] 이라고 쓰고 그 뒤에 이유를 한 문장으로만 쓴다. 억지로 채우지 않는다.
            - 차트 성적·수상 이력 같은 곡의 의미와 무관한 사실은 넣지 않는다.
            - 자료에 인용문이나 개인적 감상("내가 밴드에 들어온 뒤…", "정말 훌륭하다")이 섞여
              있으면 그것을 객관적 서술처럼 옮기지 않는다. 곡이 무엇에 대한 노래인지만 쓴다.
            - 출처 이름에 "AI 분석"이 붙은 자료는 기계가 가사를 해석한 것이라 사실 근거가 약하다.
              다른 자료와 어긋나면 다른 자료를 따르고, 그것만 있을 때는 단정하지 않는다.
            - 머리말 없이 본문만 쓴다.
            """);
        return sb.ToString();
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "ko" => "한국어",
        "ja" => "일본어",
        "en" => "영어",
        "zh" or "zh-hans" or "zh-hant" => "중국어",
        _ => code,
    };
}
