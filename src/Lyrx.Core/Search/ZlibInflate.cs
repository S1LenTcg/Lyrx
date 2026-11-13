using System.IO.Compression;
using System.Text;

namespace Musebase.Core.Search;

/// <summary>
/// KRC/QRC 페이로드 압축 해제. 원본(LyricsKit)은 Apple Compression의 .zlib(=raw DEFLATE)로
/// "앞 2바이트 제거 후" 해제하지만, 실제 데이터가 zlib 래핑인지 raw DEFLATE인지 확정할 수 없어
/// 두 해석을 순서대로 시도한다: (1) 전체 버퍼를 zlib(RFC1950)로, 실패 시 (2) 앞 2바이트를 건너뛴 raw DEFLATE.
/// </summary>
internal static class ZlibInflate
{
    public static string? InflateToString(byte[] data)
    {
        if (TryInflate(data, 0, zlibWrapped: true, out var s)) return s;
        if (data.Length > 2 && TryInflate(data, 2, zlibWrapped: false, out s)) return s;
        return null;
    }

    private static bool TryInflate(byte[] data, int offset, bool zlibWrapped, out string? result)
    {
        result = null;
        try
        {
            using var input = new MemoryStream(data, offset, data.Length - offset);
            using Stream inflate = zlibWrapped
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            if (output.Length == 0) return false;
            result = Encoding.UTF8.GetString(output.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
