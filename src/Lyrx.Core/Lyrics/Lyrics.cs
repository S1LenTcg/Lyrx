using System.Globalization;
using System.Text.RegularExpressions;

namespace Musebase.Core;

/// <summary>
/// 동기화 가사 문서. LyricsKit의 Lyrics 클래스 포팅.
/// LRC(+확장: 다중 타임태그, 【】번역, [tt]/[tr] 첨부 라인) 파싱/직렬화와
/// 재생 위치 → 현재/다음 라인 조회를 제공한다.
/// </summary>
public sealed class Lyrics
{
    public List<LyricsLine> Lines { get; } = new();
    public Dictionary<string, string> IdTags { get; } = new();
    public LyricsMetadata Metadata { get; } = new();

    // 표준 ID 태그 키
    public const string TagTitle = "ti";
    public const string TagAlbum = "al";
    public const string TagArtist = "ar";
    public const string TagAuthor = "au";
    public const string TagLrcBy = "by";
    public const string TagOffset = "offset";
    public const string TagLength = "length";

    public Lyrics() { }

    public Lyrics(IEnumerable<LyricsLine> lines, IDictionary<string, string>? idTags = null)
    {
        Lines.AddRange(lines);
        if (idTags is not null)
        {
            foreach (var (k, v) in idTags) IdTags[k] = v;
        }
    }

    /// <summary>LRC 텍스트를 파싱한다. 유효한 가사 라인이 없으면 null.</summary>
    public static Lyrics? Parse(string text)
    {
        // .NET 정규식의 ^/$ 멀티라인 앵커는 \n 기준이므로 CRLF/CR을 정규화한다
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var lyrics = new Lyrics();

        foreach (Match m in LrcRegex.Id3Tag().Matches(text))
        {
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (value.Length > 0) lyrics.IdTags[key] = value;
        }

        foreach (Match m in LrcRegex.LyricsLine().Matches(text))
        {
            var timeTags = LrcRegex.ResolveTimeTags(m.Groups[1].Value);
            var content = m.Groups[2].Value;

            var attachments = new LineAttachments();
            if (m.Groups[3].Success && m.Groups[3].Value.Length > 0)
                attachments[LineAttachments.TranslationTag()] = m.Groups[3].Value;

            foreach (var t in timeTags)
                lyrics.Lines.Add(new LyricsLine(content, t, new LineAttachments(attachments)));
        }

        if (lyrics.Lines.Count == 0) return null;
        lyrics.Lines.Sort((a, b) => a.Position.CompareTo(b.Position));

        var attachmentTags = new HashSet<string>();
        foreach (Match m in LrcRegex.LineAttachment().Matches(text))
        {
            var timeTags = LrcRegex.ResolveTimeTags(m.Groups[1].Value);
            var tag = m.Groups[2].Value;
            var value = m.Groups[3].Value;

            foreach (var t in timeTags)
            {
                var (found, index) = lyrics.FindLineIndex(t);
                if (found) lyrics.Lines[index].Attachments[tag] = value;
            }
            attachmentTags.Add(tag);
        }
        lyrics.Metadata.AttachmentTags = attachmentTags;

        return lyrics;
    }

    /// <summary>[offset:] 태그 (밀리초)</summary>
    public int Offset
    {
        get => IdTags.TryGetValue(TagOffset, out var v) && int.TryParse(v, out var i) ? i : 0;
        set => IdTags[TagOffset] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Offset의 초 단위 표현</summary>
    public double TimeDelay
    {
        get => Offset / 1000.0;
        set => Offset = (int)(value * 1000);
    }

    /// <summary>[length:] 태그 (초). "mm:ss" 또는 "ss.x" 형식.</summary>
    public double? Length
    {
        get
        {
            if (!IdTags.TryGetValue(TagLength, out var len)) return null;
            var m = LrcRegex.Base60Time().Match(len);
            if (!m.Success) return null;
            var min = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return min * 60 + sec;
        }
    }

    /// <summary>
    /// 재생 위치에 해당하는 (현재 라인, 다음 라인) 인덱스.
    /// 비활성(enabled=false) 라인은 건너뛴다. LyricsKit의 subscript 포팅.
    /// </summary>
    public (int? CurrentIndex, int? NextIndex) LineIndexesAt(double position)
    {
        var (found, i) = FindLineIndex(position);
        var index = found ? i + 1 : i;

        int? current = null;
        for (var j = index - 1; j >= 0; j--)
        {
            if (Lines[j].Enabled) { current = j; break; }
        }
        int? next = null;
        for (var j = index; j < Lines.Count; j++)
        {
            if (Lines[j].Enabled) { next = j; break; }
        }
        return (current, next);
    }

    /// <summary>이진 탐색: 정확히 일치하면 (true, index), 아니면 (false, 삽입 위치)</summary>
    internal (bool Found, int Index) FindLineIndex(double position)
    {
        int left = 0, right = Lines.Count - 1;
        while (left <= right)
        {
            var mid = (left + right) / 2;
            if (Lines[mid].Position < position) left = mid + 1;
            else if (position < Lines[mid].Position) right = mid - 1;
            else return (true, mid);
        }
        return (false, left);
    }

    /// <summary>필터 조건에 맞지 않는 라인을 비활성화한다 (광고/크레딧 제거용).</summary>
    public void Filtrate(Func<LyricsLine, bool> isIncluded)
    {
        foreach (var line in Lines)
        {
            if (!isIncluded(line)) line.Enabled = false;
        }
    }

    /// <summary>LyricsX 확장 LRC 직렬화 (첨부 라인 포함)</summary>
    public override string ToString()
    {
        var components = IdTags.Select(kv => $"[{kv.Key}:{kv.Value}]")
            .Concat(Lines.Select(l => l.ToString()));
        return string.Join('\n', components);
    }

    /// <summary>
    /// 표준 LRC 직렬화 (번역은 【】 인라인).
    /// preferredLang 지정 시 해당 언어 번역(tr:{lang})을 우선하고 없으면 generic(tr)으로 폴백 —
    /// 화면 표시(기계번역 tr:{target} 우선)와 동일한 우선순위로 내보낸다.
    /// </summary>
    public string ToLegacyString(string? preferredLang = null)
    {
        var components = IdTags.Select(kv => $"[{kv.Key}:{kv.Value}]")
            .Concat(Lines.Select(l =>
            {
                var tr = string.IsNullOrEmpty(preferredLang)
                    ? l.Attachments.Translation()
                    : l.Attachments.Translation(preferredLang, null);
                return $"[{l.TimeTag}]{l.Content}" + (tr is not null ? $"【{tr}】" : "");
            }));
        return string.Join('\n', components);
    }
}

/// <summary>
/// 파싱 외 부가정보(출처, 검색 품질 등). M1-U4 랭킹에서 확장한다.
/// </summary>
public sealed class LyricsMetadata
{
    public IReadOnlySet<string> AttachmentTags { get; internal set; } = new HashSet<string>();

    /// <summary>제공자 식별자 (예: "LRCLIB", "NetEase")</summary>
    public string? ServiceName { get; set; }

    /// <summary>제공자 내부 곡 식별 토큰 (재취득/캐시 키용)</summary>
    public string? ServiceToken { get; set; }

    /// <summary>앨범아트 URL</summary>
    public Uri? ArtworkUrl { get; set; }

    /// <summary>검색 당시 요청 — 랭킹 계산에 사용</summary>
    public Search.LyricsSearchRequest? Request { get; set; }

    /// <summary>품질 점수 캐시 (LyricsQuality.Quality가 채운다)</summary>
    public double? Quality { get; internal set; }
}
