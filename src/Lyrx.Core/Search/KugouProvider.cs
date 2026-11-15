using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Search;

/// <summary>
/// Kugou（酷狗音乐）提供者。LyricsKit 的 Kugou.swift 移植。
/// 搜索(mobilecdn) → 候选(krcs, 访问密钥) → 下载(lyrics, base64 KRC) → XOR·zlib 解密 → KRC 解析。
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
        // 1) 查询加密歌词候选（访问密钥）
        var candUrl = "http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=" +
                      $"&hash={Uri.EscapeDataString(token.Hash)}&album_audio_id={token.AlbumAudioId}";
        var candidates = await _http.GetFromJsonAsync<CandidatesResponse>(candUrl, ct).ConfigureAwait(false);
        var candidate = candidates?.Candidates?.FirstOrDefault();
        if (candidate is null) return null;

        // 2) 下载 KRC（content = base64）
        var dlUrl = $"http://lyrics.kugou.com/download?id={Uri.EscapeDataString(candidate.Id)}" +
                    $"&accesskey={Uri.EscapeDataString(candidate.Accesskey)}&fmt=krc&charset=utf8&client=pc&ver=1";
        var download = await _http.GetFromJsonAsync<DownloadResponse>(dlUrl, ct).ConfigureAwait(false);
        if (download?.Content is not { Length: > 0 } base64) return null;

        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(base64); }
        catch { return null; }

        // krc 解密失败时（例如 fmt=lrc 回退）改用明文 LRC 重试
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

    // ---- 响应模型 ----

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
