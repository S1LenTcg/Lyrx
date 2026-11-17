using System.IO;
using System.Security.Cryptography;

namespace Musebase.Windows.Services;

internal static class NetEasePluginInstaller
{
    public const string DefaultPluginDirectory = @"C:\betterncm\plugins";
    private const string PackageName = "InfLink-rs.plugin";
    private const string PackageSha256 = "7D0B7B863B072A99B88EB614C788099315E690920889AACA22685D3663661468";

    public static bool Install(string directory)
    {
        var package = Path.Combine(AppContext.BaseDirectory, "integrations", "netease", PackageName);
        if (!File.Exists(package))
            throw new FileNotFoundException("找不到随附的 InfLink-rs 插件。请完整解压 Lyrx 发布包。");

        using var source = File.OpenRead(package);
        if (!HasBundledChecksum(source))
            throw new InvalidDataException("随附的 InfLink-rs 插件校验失败。请重新下载 Lyrx 发布包。");
        source.Position = 0;

        var destination = Path.Combine(directory, PackageName);
        if (File.Exists(destination))
        {
            CheckExistingPackage(destination);
            return false;
        }

        var staged = Path.Combine(directory, $".inflink-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var copy = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                source.CopyTo(copy);
            try
            {
                File.Move(staged, destination);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                CheckExistingPackage(destination);
                return false;
            }
        }
        finally
        {
            try { File.Delete(staged); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool HasBundledChecksum(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream)) == PackageSha256;

    private static void CheckExistingPackage(string destination)
    {
        using var existing = File.OpenRead(destination);
        if (!HasBundledChecksum(existing))
            throw new IOException("这个目录已有不同版本的 InfLink-rs。请先在 BetterNCM 中管理现有插件。");
    }
}
