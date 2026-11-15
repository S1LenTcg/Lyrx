using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Settings;

/// <summary>
/// 为把设置迁移到另一台 PC 而生成 / 打开信封(envelope).
///
/// <b>不能直接复制设置文件。</b>API 密钥与令牌由 DPAPI(<c>CurrentUser</c>)锁定，
/// 在另一台 PC、另一个账户上会静默变成 null —— 在用户看来就是密钥凭空消失了。
/// 因此导出时 <b>先解开平台锁，再用人工设定的密码重新封装</b>，
/// 导入时解开，并用那台 PC 的锁重新锁定。
///
/// 封装使用 PBKDF2(SHA-256, 210,000 次) + AES-GCM.迭代次数与服务器管理员密码取同一个值
/// (<c>pbkdf2$210000$…</c>) —— 同一个存储里放两种强度，会让人分不清以哪个为准。
///
/// 本类 <b>不感知平台设置类型。</b>设置以 JSON 片段传入，只有密钥值单独传入 ——
/// 这样才能不被 Windows WPF 绑定、可以进行测试(平台一侧只做很薄的连接).
///
/// ⚠ 导出的文件 <b>只要知道密码，在任何 PC 上都能解开。</b>这正是目的，但放到云上时
/// 也就等于把相应风险一起搬了过去。
/// </summary>
public static class SettingsBackup
{
    /// <summary>文件格式编号。日后结构变化时据此区分。</summary>
    public const int FormatVersion = 1;

    private const string AppTag = "musebase";
    private const string KdfTag = "pbkdf2-sha256";

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;   // AES-GCM 标准
    private const int TagBytes = 16;
    private const int KeyBytes = 32;     // AES-256

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 要迁移的密钥值。名称与设置中的明文字段保持一致 —— 哪个字段去哪儿
    /// 必须一眼看清，这样日后新增条目时才不会遗漏。
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

    /// <summary>导入结果 —— 明文设置，以及用密码解出的密钥值(没有则为 null)。</summary>
    public readonly record struct Opened(JsonElement Settings, Secrets? Secrets);

    /// <summary>
    /// 生成信封。当 <paramref name="password"/> 为空时 <b>完全不装入密钥值</b> ——
    /// 与其把未加密的密钥漏进文件，不如干脆不带、直接导出。
    /// </summary>
    /// <param name="settingsJson">去掉密钥值后的完整设置(由平台序列化后传入)。</param>
    public static string Export(string settingsJson, Secrets secrets, string? password)
    {
        using var document = JsonDocument.Parse(settingsJson);

        // 用平台锁封装的字段(*Enc)在另一台 PC 上解不开 —— 装进去只会造成误解。
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
    /// 打开信封。<b>要么全部成功，要么什么都不做</b> —— 密码错误却把明文设置交出去，
    /// 会让调用方陷入半成品状态。所以先解出密钥值，失败就抛出。
    /// </summary>
    public static Opened Import(string backupJson, string? password)
    {
        var envelope = Parse(backupJson);

        if (envelope.App != AppTag)
            throw new InvalidDataException("这不是 Musebase 备份文件。");
        if (envelope.Version > FormatVersion)
            throw new InvalidDataException("该备份由更新的版本创建，请先更新应用。");
        if (envelope.Settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("备份中没有包含设置。");

        Secrets? secrets = null;
        if (envelope.Secrets is { } locked)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidDataException("该备份需要密码。");
            secrets = Open(locked, password!);
        }

        return new Opened(envelope.Settings, secrets);
    }

    /// <summary>该备份是否需要密码(供导入界面提前询问)。</summary>
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
                ?? throw new InvalidDataException("读取备份文件失败。");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("读取备份文件失败。");
        }
    }

    // ---- 封装 ----

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

        // 把标签接在密文之后当作一整块 —— 多设一个字段反而容易出现只搬一半的事故。
        var payload = new byte[cipher.Length + tag.Length];
        cipher.CopyTo(payload, 0);
        tag.CopyTo(payload, cipher.Length);

        return new SealedSecrets(KdfTag, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(payload));
    }

    private static Secrets Open(SealedSecrets locked, string password)
    {
        if (locked.Kdf != KdfTag) throw new InvalidDataException("未知的加锁方式。");

        byte[] salt, nonce, payload;
        try
        {
            salt = Convert.FromBase64String(locked.Salt);
            nonce = Convert.FromBase64String(locked.Nonce);
            payload = Convert.FromBase64String(locked.Cipher);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("备份已损坏。");
        }

        if (payload.Length < TagBytes || nonce.Length != NonceBytes)
            throw new InvalidDataException("备份已损坏。");

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
            // AES-GCM 在密码错误时会在认证阶段失败 —— 与数据损坏无从区分，也没有区分的必要。
            throw new InvalidDataException("密码不正确。");
        }

        try
        {
            return JsonSerializer.Deserialize<Secrets>(plain, Json)
                ?? throw new InvalidDataException("备份已损坏。");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("备份已损坏。");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Math.Clamp(iterations, 10_000, 1_000_000), HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>去掉 `*Enc`(平台锁密文)字段后的副本 —— 在另一台 PC 上解不开。</summary>
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

    // ---- 实际写入文件的结构 ----

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
