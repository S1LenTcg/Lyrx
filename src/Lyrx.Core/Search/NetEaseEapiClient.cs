using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Musebase.Core.Search;

/// <summary>
/// NetEase EAPI 클라이언트. LyricsKit의 NetEaseEapiClient.swift 포팅.
/// 요청 파라미터를 md5 서명 후 AES-ECB(PKCS7)로 암호화해 params 필드로 전송한다.
/// </summary>
internal sealed class NetEaseEapiClient
{
    private static readonly byte[] EapiKey = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
    private const string UserAgent =
        "Mozilla/5.0 (Linux; Android 9; PCT-AL10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 Mobile Safari/537.36";

    private readonly HttpClient _http;

    public NetEaseEapiClient(HttpClient http) => _http = http;

    public async Task<byte[]> PostAsync(string url, Dictionary<string, string> payload, CancellationToken ct)
    {
        var header = MakeHeader();
        var cookie = string.Join("; ", header.Select(kv => $"{kv.Key}={kv.Value}"));

        var payloadWithHeader = new Dictionary<string, string>(payload)
        {
            ["header"] = JsonSerializer.Serialize(header),
        };

        var encrypted = BuildEapiParams(url, payloadWithHeader);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("params", encrypted)]);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> MakeHeader()
    {
        var now = DateTimeOffset.UtcNow;
        return new Dictionary<string, string>
        {
            ["__csrf"] = "",
            ["appver"] = "8.0.0",
            ["buildver"] = now.ToUnixTimeSeconds().ToString(),
            ["channel"] = "",
            ["deviceId"] = "",
            ["mobilename"] = "",
            ["resolution"] = "1920x1080",
            ["os"] = "android",
            ["osver"] = "",
            ["requestId"] = $"{now.ToUnixTimeMilliseconds()}_{Random.Shared.Next(0, 1000):0000}",
            ["versioncode"] = "140",
            ["MUSIC_U"] = "",
        };
    }

    /// <summary>eapi 서명·암호화: hex(AES-ECB("{path}-36cd479b6b5-{json}-36cd479b6b5-{md5}"))</summary>
    internal static string BuildEapiParams(string url, Dictionary<string, string> payload)
    {
        var path = url
            .Replace("https://interface3.music.163.com/e", "/")
            .Replace("https://interface.music.163.com/e", "/");

        var json = JsonSerializer.Serialize(payload);
        var message = $"nobody{path}use{json}md5forencrypt";
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        var data = $"{path}-36cd479b6b5-{json}-36cd479b6b5-{digest}";

        return Convert.ToHexString(AesEncryptEcb(Encoding.UTF8.GetBytes(data)));
    }

    internal static byte[] AesEncryptEcb(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = EapiKey;
        return aes.EncryptEcb(data, PaddingMode.PKCS7);
    }

    internal static byte[] AesDecryptEcb(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = EapiKey;
        return aes.DecryptEcb(data, PaddingMode.PKCS7);
    }
}
