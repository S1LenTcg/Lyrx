namespace Musebase.Engine;

/// <summary>
/// 本地秘密值（例如翻译 API 密钥）的保护抽象。按平台替换实现。
/// - Windows: DPAPI(CurrentUser)。Android: EncryptedSharedPreferences/Keystore。
///   macOS: Keychain。存储格式为 base64 等可移植字符串。
/// 无法解密或为空值时返回 null（属功能降级，不算错误）。
/// </summary>
public interface ISecretStore
{
    /// <summary>明文 → 可移植的密文字符串。空值或失败时返回 null。</summary>
    string? Protect(string? plain);

    /// <summary>密文 → 明文。解密失败时返回 null。</summary>
    string? Unprotect(string? cipher);
}
