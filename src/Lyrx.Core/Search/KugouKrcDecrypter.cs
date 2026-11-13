namespace Musebase.Core.Search;

/// <summary>
/// Kugou KRC 解密。LyricsKit 的 KugouKrcDecrypter.swift 移植。
/// 校验 "krc1" 魔数 → 去掉前 4 字节（标志位）→ 16 字节循环密钥 XOR → zlib 解压。
/// </summary>
internal static class KugouKrcDecrypter
{
    private static readonly byte[] DecodeKey =
        [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];

    private static readonly byte[] FlagKey = "krc1"u8.ToArray();

    public static string? Decrypt(byte[] data)
    {
        if (data.Length <= 4 || !data.AsSpan(0, 4).SequenceEqual(FlagKey)) return null;

        var xored = new byte[data.Length - 4];
        for (var i = 0; i < xored.Length; i++)
            xored[i] = (byte)(data[i + 4] ^ DecodeKey[i & 0b1111]);

        return ZlibInflate.InflateToString(xored);
    }
}
