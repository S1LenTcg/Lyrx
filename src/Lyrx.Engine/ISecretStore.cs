namespace Musebase.Engine;

/// <summary>
/// 로컬 비밀값(예: 번역 API 키) 보호 추상화. 플랫폼별 구현으로 교체한다.
/// - Windows: DPAPI(CurrentUser). Android: EncryptedSharedPreferences/Keystore.
///   macOS: Keychain. 저장 형식은 base64 등 이식 가능한 문자열.
/// 복호 불가/빈 값이면 null(기능 강등이지 오류가 아님).
/// </summary>
public interface ISecretStore
{
    /// <summary>평문 → 이식 가능한 암호문 문자열. 빈 값/실패면 null.</summary>
    string? Protect(string? plain);

    /// <summary>암호문 → 평문. 복호 실패면 null.</summary>
    string? Unprotect(string? cipher);
}
