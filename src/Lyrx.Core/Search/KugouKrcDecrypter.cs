namespace Musebase.Core.Search;

/// <summary>
/// Kugou KRC 복호화. LyricsKit의 KugouKrcDecrypter.swift 포팅.
/// "krc1" 매직 확인 → 앞 4바이트(플래그) 제거 → 16바이트 순환키 XOR → zlib 해제.
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
