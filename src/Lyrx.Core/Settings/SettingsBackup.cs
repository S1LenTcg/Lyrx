using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Settings;

/// <summary>
/// 설정을 다른 PC로 옮기기 위한 봉투(envelope) 만들기·풀기.
///
/// <b>설정 파일을 그냥 복사해서는 안 된다.</b> API 키·토큰은 DPAPI(<c>CurrentUser</c>)로 잠겨 있어
/// 다른 PC·계정에서는 조용히 null이 된다 — 사용자에게는 키가 그냥 사라진 것으로 보인다.
/// 그래서 내보낼 때 <b>플랫폼 잠금을 풀고 사람이 정한 비밀번호로 다시 봉인</b>하고,
/// 가져올 때 풀어서 그 PC의 잠금으로 다시 잠근다.
///
/// 봉인은 PBKDF2(SHA-256, 210,000회) + AES-GCM이다. 반복 횟수는 서버 관리자 비밀번호와 같은 값을
/// 쓴다(<c>pbkdf2$210000$…</c>) — 한 저장소 안에서 세기를 두 가지로 두면 어느 쪽이 기준인지 흐려진다.
///
/// 이 클래스는 <b>플랫폼 설정 타입을 모른다.</b> 설정은 JSON 조각으로 받고 비밀값만 따로 받는다 —
/// 그래야 Windows WPF에 묶이지 않고 시험할 수 있다(플랫폼 쪽은 얇은 연결만 한다).
///
/// ⚠ 내보낸 파일은 <b>비밀번호만 알면 어느 PC에서나 풀린다.</b> 그게 목적이지만, 클라우드에 두면
/// 그만큼 위험도 함께 옮겨 간다.
/// </summary>
public static class SettingsBackup
{
    /// <summary>파일 형식 번호. 나중에 모양이 바뀌면 이걸 보고 가른다.</summary>
    public const int FormatVersion = 1;

    private const string AppTag = "musebase";
    private const string KdfTag = "pbkdf2-sha256";

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;   // AES-GCM 표준
    private const int TagBytes = 16;
    private const int KeyBytes = 32;     // AES-256

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 옮길 비밀값. 이름은 설정의 평문 프로퍼티와 같게 둔다 — 어느 칸이 어디로 가는지
    /// 한눈에 보여야 나중에 항목이 늘 때 빠뜨리지 않는다.
    /// </summary>
    public sealed record Secrets(
        string? DeeplApiKey = null,
        string? GoogleApiKey = null,
        string? LibreTranslateApiKey = null,
        string? OpenRouterApiKey = null,
        string? LyricsServerToken = null)
    {
        public bool Any =>
            !string.IsNullOrEmpty(DeeplApiKey) || !string.IsNullOrEmpty(GoogleApiKey)
            || !string.IsNullOrEmpty(LibreTranslateApiKey) || !string.IsNullOrEmpty(OpenRouterApiKey)
            || !string.IsNullOrEmpty(LyricsServerToken);
    }

    /// <summary>가져오기 결과 — 평문 설정과, 비밀번호로 풀어낸 비밀값(없으면 null).</summary>
    public readonly record struct Opened(JsonElement Settings, Secrets? Secrets);

    /// <summary>
    /// 봉투를 만든다. <paramref name="password"/>가 비면 비밀값을 <b>아예 담지 않는다</b> —
    /// 잠그지 않은 키를 파일에 흘리는 것보다 빼고 내보내는 편이 낫다.
    /// </summary>
    /// <param name="settingsJson">비밀값을 뺀 설정 전체(플랫폼이 직렬화해 넘긴다).</param>
    public static string Export(string settingsJson, Secrets secrets, string? password)
    {
        using var document = JsonDocument.Parse(settingsJson);

        // 플랫폼 잠금으로 봉인된 필드(*Enc)는 다른 PC에서 풀 수 없다 — 담아 봐야 오해만 만든다.
        var settings = WithoutEncryptedFields(document.RootElement);

        SealedSecrets? locked = null;
        if (!string.IsNullOrEmpty(password) && secrets.Any) locked = Seal(secrets, password!);

        return JsonSerializer.Serialize(new Envelope
        {
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            Settings = settings,
            Secrets = locked,
        }, Json);
    }

    /// <summary>
    /// 봉투를 푼다. <b>전부 되거나 아무것도 안 되거나</b>다 — 비밀번호가 틀렸는데 평문 설정만
    /// 넘겨주면 호출자가 어중간한 상태를 만들게 된다. 그래서 비밀값을 먼저 풀고 실패하면 던진다.
    /// </summary>
    public static Opened Import(string backupJson, string? password)
    {
        var envelope = Parse(backupJson);

        if (envelope.App != AppTag)
            throw new InvalidDataException("Musebase 백업 파일이 아닙니다.");
        if (envelope.Version > FormatVersion)
            throw new InvalidDataException("더 새 버전에서 만든 백업입니다. 앱을 먼저 업데이트하세요.");
        if (envelope.Settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("백업에 설정이 들어 있지 않습니다.");

        Secrets? secrets = null;
        if (envelope.Secrets is { } locked)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidDataException("이 백업에는 비밀번호가 필요합니다.");
            secrets = Open(locked, password!);
        }

        return new Opened(envelope.Settings, secrets);
    }

    /// <summary>이 백업이 비밀번호를 요구하는가(가져오기 화면이 미리 물어보려고).</summary>
    public static bool NeedsPassword(string backupJson)
    {
        try
        {
            return Parse(backupJson).Secrets is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static Envelope Parse(string backupJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(backupJson, Json)
                ?? throw new InvalidDataException("백업 파일을 읽지 못했습니다.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("백업 파일을 읽지 못했습니다.");
        }
    }

    // ---- 봉인 ----

    private static SealedSecrets Seal(Secrets secrets, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, Iterations);

        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets, Json);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);

        // 태그를 암호문 뒤에 붙여 한 덩어리로 둔다 — 필드를 하나 더 두면 한쪽만 옮기는 사고가 난다.
        var payload = new byte[cipher.Length + tag.Length];
        cipher.CopyTo(payload, 0);
        tag.CopyTo(payload, cipher.Length);

        return new SealedSecrets(KdfTag, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(payload));
    }

    private static Secrets Open(SealedSecrets locked, string password)
    {
        if (locked.Kdf != KdfTag) throw new InvalidDataException("모르는 잠금 방식입니다.");

        byte[] salt, nonce, payload;
        try
        {
            salt = Convert.FromBase64String(locked.Salt);
            nonce = Convert.FromBase64String(locked.Nonce);
            payload = Convert.FromBase64String(locked.Cipher);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("백업이 손상됐습니다.");
        }

        if (payload.Length < TagBytes || nonce.Length != NonceBytes)
            throw new InvalidDataException("백업이 손상됐습니다.");

        var key = DeriveKey(password, salt, locked.Iterations);
        var cipher = payload.AsSpan(0, payload.Length - TagBytes);
        var tag = payload.AsSpan(payload.Length - TagBytes);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            // AES-GCM은 비밀번호가 틀리면 인증에서 걸린다 — 손상과 구별할 길이 없고, 구별할 이유도 없다.
            throw new InvalidDataException("비밀번호가 맞지 않습니다.");
        }

        try
        {
            return JsonSerializer.Deserialize<Secrets>(plain, Json)
                ?? throw new InvalidDataException("백업이 손상됐습니다.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("백업이 손상됐습니다.");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Math.Clamp(iterations, 10_000, 1_000_000), HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>`*Enc`(플랫폼 잠금 암호문) 필드를 뺀 사본 — 다른 PC에서는 풀 수 없다.</summary>
    private static JsonElement WithoutEncryptedFields(JsonElement settings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in settings.EnumerateObject())
            {
                if (property.Name.EndsWith("Enc", StringComparison.Ordinal)) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    // ---- 파일에 실제로 쓰이는 모양 ----

    private sealed record Envelope
    {
        public int Version { get; init; } = FormatVersion;
        public string App { get; init; } = AppTag;
        public string? ExportedAt { get; init; }
        public JsonElement Settings { get; init; }
        public SealedSecrets? Secrets { get; init; }
    }

    private sealed record SealedSecrets(string Kdf, int Iterations, string Salt, string Nonce, string Cipher);
}
