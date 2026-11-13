using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Search;

/// <summary>
/// Kugou(酷狗音乐) 제공자. LyricsKit의 Kugou.swift 포팅.
/// 검색(mobilecdn) → 후보(krcs, 접근키) → 다운로드(lyrics, base64 KRC) → XOR·zlib 복호 → KRC 파싱.
/// </summary>
public sealed class KugouProvider : LyricsProviderBase<KugouProvider.SongInfo>
{
    public override string ServiceName => "Kugou";

    private readonly HttpClient _http;

    public KugouProvider(HttpClient? http = null) => _http = http ?? LyricsHttp.Client;

    protected override async Task<IReadOnlyList<SongInfo>> SearchAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        var keyword = Uri.EscapeDataString(request.Term.ToString());
        var url = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={keyword}&page=1&pagesize=20&showtype=1";
        var resp = await _http.GetFromJsonAsync<SearchResponse>(url, ct).ConfigureAwait(false);
        return resp?.Data?.Info ?? [];
    }

    protected override async Task<Lyrics?> FetchAsync(SongInfo token, CancellationToken ct)
    {
        // 1) 암호화 가사 후보(접근키) 조회
        var candUrl = "http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=" +
                      $"&hash={Uri.EscapeDataString(token.Hash)}&album_audio_id={token.AlbumAudioId}";
        var candidates = await _http.GetFromJsonAsync<CandidatesResponse>(candUrl, ct).ConfigureAwait(false);
        var candidate = candidates?.Candidates?.FirstOrDefault();
        if (candidate is null) return null;

        // 2) KRC 다운로드 (content = base64)
        var dlUrl = $"http://lyrics.kugou.com/download?id={Uri.EscapeDataString(candidate.Id)}" +
                    $"&accesskey={Uri.EscapeDataString(candidate.Accesskey)}&fmt=krc&charset=utf8&client=pc&ver=1";
        var download = await _http.GetFromJsonAsync<DownloadResponse>(dlUrl, ct).ConfigureAwait(false);
        if (download?.Content is not { Length: > 0 } base64) return null;

        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(base64); }
        catch { return null; }

        // krc 복호 실패 시(예: fmt=lrc 폴백) 평문 LRC로 재시도
        var decrypted = KugouKrcDecrypter.Decrypt(encrypted);
        var lyrics = decrypted is not null
            ? KrcParser.Parse(decrypted)
            : Lyrics.Parse(System.Text.Encoding.UTF8.GetString(encrypted));
        if (lyrics is null) return null;

        lyrics.IdTags[Lyrics.TagTitle] = candidate.Song;
        lyrics.IdTags[Lyrics.TagArtist] = candidate.Singer;
        lyrics.IdTags[Lyrics.TagLrcBy] = "Kugou";
        lyrics.IdTags[Lyrics.TagLength] = (candidate.Duration / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
        if (token.TransParam?.UnionCover is { } cover)
        {
            var coverUrl = cover.Replace("{size}", "480");
            if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri)) lyrics.Metadata.ArtworkUrl = uri;
        }
        lyrics.Metadata.ServiceToken = $"{candidate.Id},{candidate.Accesskey}";
        return lyrics;
    }

    // ---- 응답 모델 ----

    internal sealed record SearchResponse([property: JsonPropertyName("data")] SearchData? Data);

    internal sealed record SearchData([property: JsonPropertyName("info")] List<SongInfo>? Info);

    public sealed record SongInfo(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("album_audio_id")] long AlbumAudioId,
        [property: JsonPropertyName("trans_param")] TransParamInfo? TransParam);

    public sealed record TransParamInfo(
        [property: JsonPropertyName("union_cover")] string? UnionCover);

    internal sealed record CandidatesResponse(
        [property: JsonPropertyName("candidates")] List<Candidate>? Candidates);

    internal sealed record Candidate(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("accesskey")] string Accesskey,
        [property: JsonPropertyName("song")] string Song,
        [property: JsonPropertyName("singer")] string Singer,
        [property: JsonPropertyName("duration")] int Duration);

    internal sealed record DownloadResponse(
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("fmt")] string? Fmt);
}
