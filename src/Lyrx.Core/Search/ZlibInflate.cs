using System.IO.Compression;
using System.Text;

namespace Musebase.Core.Search;

/// <summary>
/// KRC/QRC 载荷的解压。原始实现（LyricsKit）用 Apple Compression 的 .zlib（即 raw DEFLATE），
/// 以"去掉前 2 个字节后"解压；但无法确定实际数据是 zlib 封装还是 raw DEFLATE，
/// 于是按顺序尝试两种解释：(1) 把整个缓冲区当 zlib(RFC1950)，失败时 (2) 跳过前 2 字节按 raw DEFLATE。
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
