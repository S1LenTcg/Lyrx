using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// 基于 AndroidKeyStore（AES-256-GCM）的 <see cref="ISecretStore"/> 实现，
/// 用硬件/系统密钥库加密翻译 API 密钥，替代 Windows 端的 DPAPI。
/// </summary>
public sealed class AndroidSecretStore : ISecretStore
{
    private const string KeyStoreType = "AndroidKeyStore";
    private const string KeyAlias = "app.lyrx.android.api_secrets";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLengthBytes = 12;
    private const int TagLengthBits = 128;

    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var key = GetOrCreateKey();
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, key);
            var iv = cipher.GetIV() ?? Array.Empty<byte>();
            var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(plain)) ?? Array.Empty<byte>();
            if (iv.Length != IvLengthBytes || encrypted.Length == 0) return null;

            var payload = new byte[iv.Length + encrypted.Length];
            Buffer.BlockCopy(iv, 0, payload, 0, iv.Length);
            Buffer.BlockCopy(encrypted, 0, payload, iv.Length, encrypted.Length);
            return Convert.ToBase64String(payload);
        }
        catch
        {
            return null;
        }
    }

    public string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return null;
        try
        {
            var payload = Convert.FromBase64String(cipherText);
            if (payload.Length <= IvLengthBytes) return null;

            var iv = new byte[IvLengthBytes];
            var encrypted = new byte[payload.Length - IvLengthBytes];
            Buffer.BlockCopy(payload, 0, iv, 0, IvLengthBytes);
            Buffer.BlockCopy(payload, IvLengthBytes, encrypted, 0, encrypted.Length);

            var key = GetExistingKey();
            if (key is null) return null;

            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(TagLengthBits, iv));
            var plainBytes = cipher.DoFinal(encrypted);
            return plainBytes is { Length: > 0 } ? Encoding.UTF8.GetString(plainBytes) : null;
        }
        catch
        {
            return null;
        }
    }

    private static IKey? GetExistingKey()
    {
        var ks = KeyStore.GetInstance(KeyStoreType)!;
        ks.Load(null);
        return ks.GetKey(KeyAlias, null);
    }

    private static IKey GetOrCreateKey()
    {
        if (GetExistingKey() is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreType)!;
        var spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        generator.Init(spec);
        return generator.GenerateKey()!;
    }
}
