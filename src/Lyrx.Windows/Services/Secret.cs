using System.Security.Cryptography;
using System.Text;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// 本地机密值（例如 DeepL API 密钥）保护。用 Windows DPAPI(CurrentUser) 加密。
/// - 只有同一 Windows 用户·同一台 PC 才能解密（文件被查看·备份·搬到别的 PC 后无法解密）。
/// - 局限：以同一用户身份运行的代码都能解密（本地应用机密的本质局限）。
/// <see cref="ISecretStore"/> 的 Windows 实现 — Android/macOS 各自替换为对应平台的存储。
/// </summary>
internal sealed class DpapiSecretStore : ISecretStore
{
    // 应用专属熵（额外盐） — 避免与其他应用的 DPAPI 数据混淆。
    // 注意：即使改名（→Musebase）之后，为了能解密已有的密文，entropy 字符串也绝不能改。
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LyricsX.DeepL.v1");

    /// <summary>明文 → base64 DPAPI 密文。失败/空值则返回 null。</summary>
    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>base64 DPAPI 密文 → 明文。解密失败（其他用户/PC 等）则返回 null。</summary>
    public string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(cipherBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
