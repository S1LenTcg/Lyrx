using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Search;

/// <summary>
/// LRCLIB (lrclib.net) — 免认证的公开歌词 API。LyricsKit 的 LRCLIB.swift 移植。
/// </summary>
public sealed class LrclibProvider : LyricsProviderBase<LrclibProvider.Record>
{
    public override string ServiceName => "LRCLIB";

    private readonly HttpClient _http;

    public LrclibProvider(HttpClient? http = null) => _http = http ?? LyricsHttp.Client;

    protected override async Task<IReadOnlyList<Record>> SearchAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        var query = request.Term.IsKeyword
            ? $"q={Uri.EscapeDataString(request.Term.Keyword!)}"
            : $"track_name={Uri.EscapeDataString(request.Term.Title!)}&artist_name={Uri.EscapeDataString(request.Term.Artist!)}";

        var results = await _http.GetFromJsonAsync<List<Record>>(
            $"https://lrclib.net/api/search?{query}", ct).ConfigureAwait(false);
        return results ?? [];
    }

    protected override async Task<Lyrics?> FetchAsync(Record token, CancellationToken ct)
    {
        if (ParseRecord(token) is { } lyrics) return lyrics;

        // 搜索响应里没有 syncedLyrics 时查详情
        var fetched = await _http.GetFromJsonAsync<Record>(
            $"https://lrclib.net/api/get/{token.Id}", ct).ConfigureAwait(false);
        return fetched is null ? null : ParseRecord(fetched);
    }

    private static Lyrics? ParseRecord(Record record)
    {
        if (string.IsNullOrEmpty(record.SyncedLyrics)) return null;
        var lyrics = Lyrics.Parse(record.SyncedLyrics);
        if (lyrics is null) return null;

        lyrics.IdTags[Lyrics.TagTitle] = record.TrackName;
        lyrics.IdTags[Lyrics.TagArtist] = record.ArtistName;
        lyrics.IdTags[Lyrics.TagAlbum] = record.AlbumName;
        lyrics.IdTags[Lyrics.TagLength] = record.Duration.ToString("0.##", CultureInfo.InvariantCulture);
        lyrics.Metadata.ServiceToken = record.Id.ToString(CultureInfo.InvariantCulture);
        return lyrics;
    }

    public sealed record Record(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("trackName")] string TrackName,
        [property: JsonPropertyName("artistName")] string ArtistName,
        [property: JsonPropertyName("albumName")] string AlbumName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("instrumental")] bool Instrumental,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
}
