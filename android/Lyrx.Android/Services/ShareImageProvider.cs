using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Android.App;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using AEnvironment = Android.OS.Environment;
using AUri = Android.Net.Uri;
using IoPath = System.IO.Path;

namespace Musebase.Android.Services;

/// <summary>
/// 轻量级私有文件 <see cref="ContentProvider"/>，用于将生成的歌词分享卡片 PNG
/// 通过 <c>content://</c> URI 安全授予系统分享面板与第三方应用读取权限。
/// </summary>
[ContentProvider(
    ["app.lyrx.android.shareprovider"],
    Name = "app.lyrx.android.ShareImageProvider",
    Exported = false,
    GrantUriPermissions = true)]
public sealed class ShareImageProvider : ContentProvider
{
    public const string Authority = "app.lyrx.android.shareprovider";

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ShareImageProvider))]
    public ShareImageProvider()
    {
    }

    public ShareImageProvider(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }

    public override bool OnCreate() => true;

    public override string? GetType(AUri uri) => ResolveShareFile(uri) is not null ? "image/png" : null;

    public override ICursor? Query(
        AUri uri,
        string[]? projection,
        string? selection,
        string[]? selectionArgs,
        string? sortOrder)
    {
        var path = ResolveShareFile(uri);
        if (path is null || !File.Exists(path)) return null;

        var info = new FileInfo(path);
        var cols = projection is { Length: > 0 }
            ? projection
            : [IOpenableColumns.DisplayName, IOpenableColumns.Size];
        var cursor = new MatrixCursor(cols, 1);
        var row = new Java.Lang.Object[cols.Length];
        for (var i = 0; i < cols.Length; i++)
        {
            row[i] = cols[i] switch
            {
                IOpenableColumns.DisplayName => new Java.Lang.String(info.Name),
                IOpenableColumns.Size => Java.Lang.Long.ValueOf(info.Length),
                _ => new Java.Lang.String(string.Empty),
            };
        }
        cursor.AddRow(row);
        return cursor;
    }

    public override ParcelFileDescriptor? OpenFile(AUri uri, string mode)
    {
        var path = ResolveShareFile(uri);
        if (path is null || !File.Exists(path))
            throw new Java.IO.FileNotFoundException($"无法找到分享图片: {uri}");

        return ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly);
    }

    public override int Delete(AUri uri, string? selection, string[]? selectionArgs) => 0;

    public override AUri? Insert(AUri uri, ContentValues? values) => null;

    public override int Update(AUri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;

    private string? ResolveShareFile(AUri uri)
    {
        var ctx = Context;
        if (ctx is null) return null;
        var fileName = IoPath.GetFileName(uri.LastPathSegment);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return null;

        var baseDir = IoPath.GetFullPath(ShareImageHelper.GetShareCacheDir(ctx));
        var fullPath = IoPath.GetFullPath(IoPath.Combine(baseDir, fileName));
        if (!fullPath.StartsWith(baseDir + IoPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return fullPath;
    }
}

internal static class ShareImageHelper
{
    public static string GetShareCacheDir(Context ctx)
    {
        var dir = IoPath.Combine(ctx.CacheDir!.AbsolutePath, "lyric-share");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static AUri SaveTempSharePng(Context ctx, Bitmap bitmap, string baseName, bool forClipboard = false)
    {
        var dir = GetShareCacheDir(ctx);
        PruneOldCacheFiles(dir);

        var safeName = SanitizeFileName(baseName);
        var prefix = forClipboard ? "clipboard-" : "share-";
        var fileName = $"{prefix}{Guid.NewGuid():N}_{safeName}.png";
        var fullPath = IoPath.Combine(dir, fileName);

        try
        {
            using var fs = File.Open(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            WritePng(bitmap, fs);
        }
        catch
        {
            try { File.Delete(fullPath); } catch { }
            throw;
        }

        using var uri = new AUri.Builder();
        uri.Scheme("content");
        uri.Authority(ShareImageProvider.Authority);
        uri.AppendPath("cards");
        uri.AppendPath(fileName);
        return uri.Build() ?? throw new IOException("无法生成分享链接");
    }

    public static string SaveToGallery(Context ctx, Bitmap bitmap, string baseName)
    {
        var safeName = SanitizeFileName(baseName);
        var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            return SaveToGalleryApi29(ctx, bitmap, fileName);

        return SaveToGalleryLegacy(ctx, bitmap, fileName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static string SaveToGalleryApi29(Context ctx, Bitmap bitmap, string fileName)
    {
        var resolver = ctx.ContentResolver ?? throw new InvalidOperationException("ContentResolver 不可用");
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, "image/png");
        values.Put(MediaStore.IMediaColumns.RelativePath, $"{AEnvironment.DirectoryPictures}/Lyrx");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values)
            ?? throw new IOException("无法在系统相册中创建图片记录");

        try
        {
            using (var stream = resolver.OpenOutputStream(uri) ?? throw new IOException("无法打开相册输出流"))
            {
                WritePng(bitmap, stream);
            }

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
            return "Pictures/Lyrx";
        }
        catch
        {
            try { resolver.Delete(uri, null, null); } catch { }
            throw;
        }
    }

    private static string SaveToGalleryLegacy(Context ctx, Bitmap bitmap, string fileName)
    {
        if (ctx.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage)
            != global::Android.Content.PM.Permission.Granted)
            throw new UnauthorizedAccessException("需要存储权限才能保存到系统相册。");

#pragma warning disable CA1422
        var baseDir = AEnvironment.GetExternalStoragePublicDirectory(AEnvironment.DirectoryPictures)?.AbsolutePath;
#pragma warning restore CA1422

        if (string.IsNullOrEmpty(baseDir)) throw new IOException("找不到系统相册目录。");
        var outDir = IoPath.Combine(baseDir, "Lyrx");
        Directory.CreateDirectory(outDir);
        var fullPath = IoPath.Combine(outDir, fileName);

        using (var fs = File.Open(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try
            {
                WritePng(bitmap, fs);
            }
            catch
            {
                fs.Close();
                try { File.Delete(fullPath); } catch { }
                throw;
            }
        }

        MediaScannerConnection.ScanFile(ctx, [fullPath], ["image/png"], null);
        return "Pictures/Lyrx";
    }

    private static void PruneOldCacheFiles(string dir)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                var cutoff = IoPath.GetFileName(file).StartsWith("clipboard-", StringComparison.Ordinal)
                    ? now.AddDays(-1)
                    : now.AddHours(-1);
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 忽略临时文件清理异常
        }
    }

    private static void WritePng(Bitmap bitmap, System.IO.Stream stream)
    {
        if (!bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream))
            throw new IOException("PNG 编码失败");
        stream.Flush();
    }

    private static string SanitizeFileName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Lyrx_Lyrics";
        var invalid = IoPath.GetInvalidFileNameChars();
        var clean = new char[raw.Length];
        var len = 0;
        foreach (var ch in raw)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch)) continue;
            clean[len++] = char.IsWhiteSpace(ch) ? '_' : ch;
            if (len >= 32) break;
        }
        return len > 0 ? new string(clean, 0, len) : "Lyrx_Lyrics";
    }
}
