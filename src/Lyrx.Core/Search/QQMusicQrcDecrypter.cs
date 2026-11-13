namespace Musebase.Core.Search;

/// <summary>
/// QQ 音乐 QRC 복호화. LyricsKit의 QQMusicQrcDecrypter.swift(QrcDecoder/QrcDecodeHelper) 포팅.
/// 3중 DES(ddes KEY1 → des KEY2 → ddes KEY3, ECB, 8바이트 블록)로 hex 페이로드를 복호한 뒤
/// 앞 2바이트를 제외하고 zlib 해제한다. Swift Int(64bit) 산술을 그대로 재현하기 위해 long을 사용한다.
/// </summary>
internal static class QQMusicQrcDecrypter
{
    // 16바이트 문자열이지만 DES 키 스케줄은 앞 8바이트만 참조한다(원본과 동일).
    private static readonly byte[] Key1 = "!@#)(NHLiuy*$%^&"u8.ToArray();
    private static readonly byte[] Key2 = "123ZXC!@#)(*$%^&"u8.ToArray();
    private static readonly byte[] Key3 = "!@#)(*$%^&abcDEF"u8.ToArray();

    private const int Encrypt = 1;
    private const int Decrypt = 0;

    /// <summary>hex 문자열 → 복호·해제된 QRC 텍스트. 실패 시 null.</summary>
    public static string? Decode(string hex)
    {
        byte[] data;
        try { data = Convert.FromHexString(hex); }
        catch { return null; }
        if (data.Length == 0) return null;

        try
        {
            Ddes(data, Key1, data.Length);
            Des(data, Key2, data.Length);
            Ddes(data, Key3, data.Length);
        }
        catch
        {
            return null;
        }

        return ZlibInflate.InflateToString(data);
    }

    /// <summary>DES 암호화(ENCRYPT 스케줄)로 각 8바이트 블록을 in-place 변환.</summary>
    internal static void Des(byte[] data, byte[] key, int len) => Crypt(data, key, len, Encrypt);

    /// <summary>DES 복호화(DECRYPT 스케줄)로 각 8바이트 블록을 in-place 변환.</summary>
    internal static void Ddes(byte[] data, byte[] key, int len) => Crypt(data, key, len, Decrypt);

    private static void Crypt(byte[] data, byte[] key, int len, int mode)
    {
        var schedule = DesKeySetup(ToLongs(key), mode);
        for (var i = 0; i < len; i += 8)
        {
            var n = Math.Min(8, data.Length - i);
            var block = new long[8]; // <8 잔여 블록은 0패딩 (정상 QRC는 8바이트 정렬)
            for (var j = 0; j < n; j++) block[j] = data[i + j];
            DesCrypt(block, block, schedule);
            for (var j = 0; j < n; j++) data[i + j] = (byte)(block[j] & 0xFF);
        }
    }

    private static long[] ToLongs(byte[] bytes)
    {
        var result = new long[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) result[i] = bytes[i];
        return result;
    }

    // ---- DES 코어 (QrcDecodeHelper 포팅) ----

    private static long BitNum(long[] a, int b, int c) =>
        ((a[b / 32 * 4 + 3 - b % 32 / 8] >> (7 - b % 8)) & 0x01L) << c;

    private static long BitNumIntR(long a, int b, int c) =>
        ((a >> (31 - b)) & 0x00000001L) << c;

    private static long BitNumIntL(long a, int b, int c) =>
        ((a << b) & 0x80000000L) >> c;

    private static int SBoxBit(int a) =>
        (a & 0x20) | ((a & 0x1F) >> 1) | ((a & 0x01) << 4);

    private static void IP(long[] state, long[] input)
    {
        state[0] = BitNum(input, 57, 31) | BitNum(input, 49, 30) | BitNum(input, 41, 29) | BitNum(input, 33, 28) |
                   BitNum(input, 25, 27) | BitNum(input, 17, 26) | BitNum(input, 9, 25) | BitNum(input, 1, 24) |
                   BitNum(input, 59, 23) | BitNum(input, 51, 22) | BitNum(input, 43, 21) | BitNum(input, 35, 20) |
                   BitNum(input, 27, 19) | BitNum(input, 19, 18) | BitNum(input, 11, 17) | BitNum(input, 3, 16) |
                   BitNum(input, 61, 15) | BitNum(input, 53, 14) | BitNum(input, 45, 13) | BitNum(input, 37, 12) |
                   BitNum(input, 29, 11) | BitNum(input, 21, 10) | BitNum(input, 13, 9) | BitNum(input, 5, 8) |
                   BitNum(input, 63, 7) | BitNum(input, 55, 6) | BitNum(input, 47, 5) | BitNum(input, 39, 4) |
                   BitNum(input, 31, 3) | BitNum(input, 23, 2) | BitNum(input, 15, 1) | BitNum(input, 7, 0);

        state[1] = BitNum(input, 56, 31) | BitNum(input, 48, 30) | BitNum(input, 40, 29) | BitNum(input, 32, 28) |
                   BitNum(input, 24, 27) | BitNum(input, 16, 26) | BitNum(input, 8, 25) | BitNum(input, 0, 24) |
                   BitNum(input, 58, 23) | BitNum(input, 50, 22) | BitNum(input, 42, 21) | BitNum(input, 34, 20) |
                   BitNum(input, 26, 19) | BitNum(input, 18, 18) | BitNum(input, 10, 17) | BitNum(input, 2, 16) |
                   BitNum(input, 60, 15) | BitNum(input, 52, 14) | BitNum(input, 44, 13) | BitNum(input, 36, 12) |
                   BitNum(input, 28, 11) | BitNum(input, 20, 10) | BitNum(input, 12, 9) | BitNum(input, 4, 8) |
                   BitNum(input, 62, 7) | BitNum(input, 54, 6) | BitNum(input, 46, 5) | BitNum(input, 38, 4) |
                   BitNum(input, 30, 3) | BitNum(input, 22, 2) | BitNum(input, 14, 1) | BitNum(input, 6, 0);
    }

    private static void InvIP(long[] state, long[] ina)
    {
        ina[3] = BitNumIntR(state[1], 7, 7) | BitNumIntR(state[0], 7, 6) | BitNumIntR(state[1], 15, 5) | BitNumIntR(state[0], 15, 4) |
                 BitNumIntR(state[1], 23, 3) | BitNumIntR(state[0], 23, 2) | BitNumIntR(state[1], 31, 1) | BitNumIntR(state[0], 31, 0);
        ina[2] = BitNumIntR(state[1], 6, 7) | BitNumIntR(state[0], 6, 6) | BitNumIntR(state[1], 14, 5) | BitNumIntR(state[0], 14, 4) |
                 BitNumIntR(state[1], 22, 3) | BitNumIntR(state[0], 22, 2) | BitNumIntR(state[1], 30, 1) | BitNumIntR(state[0], 30, 0);
        ina[1] = BitNumIntR(state[1], 5, 7) | BitNumIntR(state[0], 5, 6) | BitNumIntR(state[1], 13, 5) | BitNumIntR(state[0], 13, 4) |
                 BitNumIntR(state[1], 21, 3) | BitNumIntR(state[0], 21, 2) | BitNumIntR(state[1], 29, 1) | BitNumIntR(state[0], 29, 0);
        ina[0] = BitNumIntR(state[1], 4, 7) | BitNumIntR(state[0], 4, 6) | BitNumIntR(state[1], 12, 5) | BitNumIntR(state[0], 12, 4) |
                 BitNumIntR(state[1], 20, 3) | BitNumIntR(state[0], 20, 2) | BitNumIntR(state[1], 28, 1) | BitNumIntR(state[0], 28, 0);
        ina[7] = BitNumIntR(state[1], 3, 7) | BitNumIntR(state[0], 3, 6) | BitNumIntR(state[1], 11, 5) | BitNumIntR(state[0], 11, 4) |
                 BitNumIntR(state[1], 19, 3) | BitNumIntR(state[0], 19, 2) | BitNumIntR(state[1], 27, 1) | BitNumIntR(state[0], 27, 0);
        ina[6] = BitNumIntR(state[1], 2, 7) | BitNumIntR(state[0], 2, 6) | BitNumIntR(state[1], 10, 5) | BitNumIntR(state[0], 10, 4) |
                 BitNumIntR(state[1], 18, 3) | BitNumIntR(state[0], 18, 2) | BitNumIntR(state[1], 26, 1) | BitNumIntR(state[0], 26, 0);
        ina[5] = BitNumIntR(state[1], 1, 7) | BitNumIntR(state[0], 1, 6) | BitNumIntR(state[1], 9, 5) | BitNumIntR(state[0], 9, 4) |
                 BitNumIntR(state[1], 17, 3) | BitNumIntR(state[0], 17, 2) | BitNumIntR(state[1], 25, 1) | BitNumIntR(state[0], 25, 0);
        ina[4] = BitNumIntR(state[1], 0, 7) | BitNumIntR(state[0], 0, 6) | BitNumIntR(state[1], 8, 5) | BitNumIntR(state[0], 8, 4) |
                 BitNumIntR(state[1], 16, 3) | BitNumIntR(state[0], 16, 2) | BitNumIntR(state[1], 24, 1) | BitNumIntR(state[0], 24, 0);
    }

    private static long F(long state, long[] key)
    {
        var lrgstate = new long[6];

        // Expansion Permutation
        var t1 = BitNumIntL(state, 31, 0) | ((state & 0xF0000000L) >> 1) | BitNumIntL(state, 4, 5) | BitNumIntL(state, 3, 6) |
                 ((state & 0x0F000000L) >> 3) | BitNumIntL(state, 8, 11) | BitNumIntL(state, 7, 12) | ((state & 0x00F00000L) >> 5) |
                 BitNumIntL(state, 12, 17) | BitNumIntL(state, 11, 18) | ((state & 0x000F0000L) >> 7) | BitNumIntL(state, 16, 23);

        var t2 = BitNumIntL(state, 15, 0) | ((state & 0x0000F000L) << 15) | BitNumIntL(state, 20, 5) | BitNumIntL(state, 19, 6) |
                 ((state & 0x00000F00L) << 13) | BitNumIntL(state, 24, 11) | BitNumIntL(state, 23, 12) | ((state & 0x000000F0L) << 11) |
                 BitNumIntL(state, 28, 17) | BitNumIntL(state, 27, 18) | ((state & 0x0000000FL) << 9) | BitNumIntL(state, 0, 23);

        lrgstate[0] = (t1 >> 24) & 0x000000FF;
        lrgstate[1] = (t1 >> 16) & 0x000000FF;
        lrgstate[2] = (t1 >> 8) & 0x000000FF;
        lrgstate[3] = (t2 >> 24) & 0x000000FF;
        lrgstate[4] = (t2 >> 16) & 0x000000FF;
        lrgstate[5] = (t2 >> 8) & 0x000000FF;

        // Key XOR
        for (var i = 0; i < 6; i++) lrgstate[i] ^= key[i];

        // S-Box Permutation
        var s = ((long)Sbox1[SBoxBit((int)(lrgstate[0] >> 2))] << 28) |
                ((long)Sbox2[SBoxBit((int)(((lrgstate[0] & 0x03) << 4) | (lrgstate[1] >> 4)))] << 24) |
                ((long)Sbox3[SBoxBit((int)(((lrgstate[1] & 0x0F) << 2) | (lrgstate[2] >> 6)))] << 20) |
                ((long)Sbox4[SBoxBit((int)(lrgstate[2] & 0x3F))] << 16) |
                ((long)Sbox5[SBoxBit((int)(lrgstate[3] >> 2))] << 12) |
                ((long)Sbox6[SBoxBit((int)(((lrgstate[3] & 0x03) << 4) | (lrgstate[4] >> 4)))] << 8) |
                ((long)Sbox7[SBoxBit((int)(((lrgstate[4] & 0x0F) << 2) | (lrgstate[5] >> 6)))] << 4) |
                (long)Sbox8[SBoxBit((int)(lrgstate[5] & 0x3F))];

        // P-Box Permutation
        return BitNumIntL(s, 15, 0) | BitNumIntL(s, 6, 1) | BitNumIntL(s, 19, 2) | BitNumIntL(s, 20, 3) |
               BitNumIntL(s, 28, 4) | BitNumIntL(s, 11, 5) | BitNumIntL(s, 27, 6) | BitNumIntL(s, 16, 7) |
               BitNumIntL(s, 0, 8) | BitNumIntL(s, 14, 9) | BitNumIntL(s, 22, 10) | BitNumIntL(s, 25, 11) |
               BitNumIntL(s, 4, 12) | BitNumIntL(s, 17, 13) | BitNumIntL(s, 30, 14) | BitNumIntL(s, 9, 15) |
               BitNumIntL(s, 1, 16) | BitNumIntL(s, 7, 17) | BitNumIntL(s, 23, 18) | BitNumIntL(s, 13, 19) |
               BitNumIntL(s, 31, 20) | BitNumIntL(s, 26, 21) | BitNumIntL(s, 2, 22) | BitNumIntL(s, 8, 23) |
               BitNumIntL(s, 18, 24) | BitNumIntL(s, 12, 25) | BitNumIntL(s, 29, 26) | BitNumIntL(s, 5, 27) |
               BitNumIntL(s, 21, 28) | BitNumIntL(s, 10, 29) | BitNumIntL(s, 3, 30) | BitNumIntL(s, 24, 31);
    }

    private static long[][] DesKeySetup(long[] key, int mode)
    {
        var schedule = new long[16][];
        long c = 0, d = 0;

        int[] keyRndShift = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
        int[] keyPermC = [56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35];
        int[] keyPermD = [62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3];
        int[] keyCompression =
        [
            13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3,
            25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36, 46, 54, 29, 39,
            50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31,
        ];

        for (var i = 0; i < 28; i++)
        {
            c |= BitNum(key, keyPermC[i], 31 - i);
            d |= BitNum(key, keyPermD[i], 31 - i);
        }

        for (var i = 0; i < 16; i++)
        {
            var shift = keyRndShift[i];
            c = ((c << shift) | (c >> (28 - shift))) & 0xFFFFFFF0L;
            d = ((d << shift) | (d >> (28 - shift))) & 0xFFFFFFF0L;

            var toGen = mode == Decrypt ? 15 - i : i;
            schedule[toGen] = new long[6];

            for (var j = 0; j < 48; j++)
            {
                if (j < 24)
                    schedule[toGen][j / 8] |= BitNumIntR(c, keyCompression[j], 7 - j % 8);
                else
                    schedule[toGen][j / 8] |= BitNumIntR(d, keyCompression[j] - 27, 7 - j % 8);
            }
        }

        return schedule;
    }

    private static void DesCrypt(long[] input, long[] output, long[][] key)
    {
        var state = new long[2];
        IP(state, input);

        for (var idx = 0; idx < 15; idx++)
        {
            var t = state[1];
            state[1] = F(state[1], key[idx]) ^ state[0];
            state[0] = t;
        }
        state[0] = F(state[1], key[15]) ^ state[0];

        InvIP(state, output);
    }

    // ---- S-Box 테이블 ----

    private static readonly int[] Sbox1 =
    [
        14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
        0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
        4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
        15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,
    ];

    private static readonly int[] Sbox2 =
    [
        15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
        3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
        0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
        13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,
    ];

    private static readonly int[] Sbox3 =
    [
        10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
        13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
        13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
        1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,
    ];

    private static readonly int[] Sbox4 =
    [
        7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
        13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
        10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
        3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,
    ];

    private static readonly int[] Sbox5 =
    [
        2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
        14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
        4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
        11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,
    ];

    private static readonly int[] Sbox6 =
    [
        12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
        10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
        9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
        4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,
    ];

    private static readonly int[] Sbox7 =
    [
        4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
        13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
        1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
        6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,
    ];

    private static readonly int[] Sbox8 =
    [
        13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
        1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
        7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
        2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
    ];
}
