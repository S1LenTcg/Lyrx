using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace Musebase.Windows.Services;

internal sealed record ShareTempPayload(string Title, string Description, string BaseName, BitmapSource Bitmap);

/// <summary>
/// Windows 端歌词卡片的图片加载、PNG 导出、剪贴板复制与系统分享面板集成工具。
/// </summary>
internal static class WindowsShareService
{
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    public static string GetShareCacheDir()
    {
        var dir = Path.Combine(AppSettings.DataDirectory, "share-cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SanitizeFileName(string? raw, int maxLength = 48)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Lyrx_Lyrics";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = new char[Math.Min(raw.Length, maxLength)];
        var len = 0;
        foreach (var ch in raw.Trim())
        {
            if (Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch)) continue;
            chars[len++] = char.IsWhiteSpace(ch) ? '_' : ch;
            if (len >= maxLength) break;
        }
        return len > 0 ? new string(chars, 0, len) : "Lyrx_Lyrics";
    }

    public static BitmapSource LoadBackgroundImage(string filePath, int maxDimension = 1600)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("找不到所选的图片文件。", filePath);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0)
            throw new InvalidDataException("所选图片文件为空。");

        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("图片中没有可读取的图像帧。");

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            throw new InvalidDataException("图片尺寸无效。");

        BitmapSource result = frame;

        var orientation = ReadExifOrientation(frame);
        var transformGroup = new TransformGroup();
        switch (orientation)
        {
            case 2:
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 3:
                transformGroup.Children.Add(new RotateTransform(180));
                break;
            case 4:
                transformGroup.Children.Add(new ScaleTransform(1, -1));
                break;
            case 5:
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                transformGroup.Children.Add(new RotateTransform(90));
                break;
            case 6:
                transformGroup.Children.Add(new RotateTransform(90));
                break;
            case 7:
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                transformGroup.Children.Add(new RotateTransform(270));
                break;
            case 8:
                transformGroup.Children.Add(new RotateTransform(270));
                break;
        }

        var orientedW = orientation is 5 or 6 or 7 or 8 ? result.PixelHeight : result.PixelWidth;
        var orientedH = orientation is 5 or 6 or 7 or 8 ? result.PixelWidth : result.PixelHeight;
        var longest = Math.Max(orientedW, orientedH);
        if (longest > maxDimension && maxDimension > 0)
        {
            var scale = (double)maxDimension / longest;
            transformGroup.Children.Add(new ScaleTransform(scale, scale));
        }

        if (transformGroup.Children.Count > 0)
            result = new TransformedBitmap(result, transformGroup);

        if (result.Format != PixelFormats.Pbgra32)
            result = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);

        result.Freeze();
        return result;
    }

    public static BitmapSource DecodeCoverBytes(byte[] bytes, int maxDimension = 640)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("无法解码封面图片。");

        BitmapSource result = decoder.Frames[0];
        var longest = Math.Max(result.PixelWidth, result.PixelHeight);
        if (longest > maxDimension && maxDimension > 0)
        {
            var scale = (double)maxDimension / longest;
            result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
        }
        if (result.Format != PixelFormats.Pbgra32)
            result = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        result.Freeze();
        return result;
    }

    public static void SavePngToFile(BitmapSource bitmap, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = LyricShareCardRenderer.EncodePng(bitmap);
        File.WriteAllBytes(filePath, bytes);
    }

    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static void CopyImageToClipboard(BitmapSource bitmap, IntPtr ownerHwnd = default)
    {
        var pngBytes = LyricShareCardRenderer.EncodePng(bitmap);
        var dibV5Bytes = BuildDibV5Bytes(bitmap);
        var cfPng = RegisterClipboardFormat("PNG");
        var cfImagePng = RegisterClipboardFormat("image/png");
        var hwnd = ownerHwnd;
        if (hwnd == IntPtr.Zero && Application.Current?.MainWindow is { } mainWin)
            hwnd = new WindowInteropHelper(mainWin).Handle;

        Exception? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(hwnd))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        var emptyErr = Marshal.GetLastWin32Error();
                        throw emptyErr == 5
                            ? new UnauthorizedAccessException("拒绝访问。")
                            : new ExternalException($"清空系统剪贴板失败 (Win32={emptyErr})。", emptyErr);
                    }
                    // 将无损透明 PNG 流置于剪贴板枚举优先级首位，随后提供 image/png 与带 AlphaMask 的 CF_DIBV5 兜底
                    if (cfPng != 0) WriteClipboardFormat(cfPng, pngBytes);
                    if (cfImagePng != 0) WriteClipboardFormat(cfImagePng, pngBytes);
                    WriteClipboardFormat(CfDibV5, dibV5Bytes);
                    return;
                }
                catch (Exception ex) when (ex is ExternalException or UnauthorizedAccessException)
                {
                    lastError = ex;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                lastError = err == 5
                    ? new UnauthorizedAccessException("拒绝访问。")
                    : new ExternalException($"打开系统剪贴板失败 (Win32={err})。", err);
            }
            Thread.Sleep(30);
        }

        throw lastError ?? new InvalidOperationException("写入系统剪贴板失败。");
    }

    private static byte[] BuildDibV5Bytes(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var w = Math.Max(1, bgra.PixelWidth);
        var h = Math.Max(1, bgra.PixelHeight);
        var stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        // BITMAPV5HEADER (124 bytes, BI_BITFIELDS = 3, bottom-up positive Height + explicit AlphaMask)
        const int headerSize = 124;
        var output = new byte[headerSize + pixels.Length];
        using (var ms = new MemoryStream(output, writable: true))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((uint)headerSize);        // bV5Size
            bw.Write(w);                       // bV5Width
            bw.Write(h);                       // bV5Height (positive => bottom-up)
            bw.Write((ushort)1);               // bV5Planes
            bw.Write((ushort)32);              // bV5BitCount
            bw.Write((uint)3);                 // bV5Compression = BI_BITFIELDS
            bw.Write((uint)pixels.Length);     // bV5SizeImage
            bw.Write(3780);                    // bV5XPelsPerMeter (~96 DPI)
            bw.Write(3780);                    // bV5YPelsPerMeter (~96 DPI)
            bw.Write((uint)0);                 // bV5ClrUsed
            bw.Write((uint)0);                 // bV5ClrImportant
            bw.Write(0x00FF0000u);             // bV5RedMask
            bw.Write(0x0000FF00u);             // bV5GreenMask
            bw.Write(0x000000FFu);             // bV5BlueMask
            bw.Write(0xFF000000u);             // bV5AlphaMask
            bw.Write(0x73524742u);             // bV5CSType = 'sRGB'
            for (var i = 0; i < 9; i++) bw.Write(0); // bV5Endpoints (36 bytes)
            bw.Write((uint)0);                 // bV5GammaRed
            bw.Write((uint)0);                 // bV5GammaGreen
            bw.Write((uint)0);                 // bV5GammaBlue
            bw.Write((uint)4);                 // bV5Intent = LCS_GM_IMAGES
            bw.Write((uint)0);                 // bV5ProfileData
            bw.Write((uint)0);                 // bV5ProfileSize
            bw.Write((uint)0);                 // bV5Reserved
        }

        // Bottom-up DIB rows (last image row at offset 0 of pixel payload)
        for (var y = 0; y < h; y++)
        {
            var srcOff = (h - 1 - y) * stride;
            var dstOff = headerSize + y * stride;
            System.Buffer.BlockCopy(pixels, srcOff, output, dstOff, stride);
        }

        return output;
    }

    private static void WriteClipboardFormat(uint format, byte[] bytes)
    {
        var hMem = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)bytes.Length);
        if (hMem == IntPtr.Zero)
            throw new OutOfMemoryException("无法分配剪贴板内存。");

        var locked = false;
        var transferred = false;
        try
        {
            var ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("无法锁定剪贴板内存。");
            locked = true;
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            locked = false;

            if (SetClipboardData(format, hMem) == IntPtr.Zero)
            {
                var setErr = Marshal.GetLastWin32Error();
                if (setErr == 5)
                    throw new UnauthorizedAccessException("拒绝访问。");
                throw new ExternalException($"写入剪贴板格式 {format} 失败 (Win32={setErr})。", setErr);
            }
            transferred = true;
        }
        finally
        {
            if (locked) GlobalUnlock(hMem);
            if (!transferred) GlobalFree(hMem);
        }
    }

    public static string SaveTempSharePng(BitmapSource bitmap, string baseName)
    {
        var dir = GetShareCacheDir();
        PruneOldCacheFiles(dir);

        var safeName = SanitizeFileName(baseName);
        var fileName = $"{safeName}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
        var fullPath = Path.Combine(dir, fileName);
        SavePngToFile(bitmap, fullPath);
        return fullPath;
    }

    public static bool TryShowSystemShare(
        Window owner,
        ShareTempPayload payload,
        out string? errorMessage)
    {
        errorMessage = null;
        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero)
        {
            errorMessage = "窗口尚未完成初始化，无法打开系统分享。";
            return false;
        }

        DataTransferManager? dtm = null;
        IDataTransferManagerInterop? interop = null;
        var abiPtr = IntPtr.Zero;
        try
        {
            interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            abiPtr = interop.GetForWindow(hwnd, ref iid);
            if (abiPtr == IntPtr.Zero)
            {
                errorMessage = "当前系统未能提供分享会话，请使用「复制图片」或「保存图片」。";
                return false;
            }

            dtm = WinRT.MarshalInspectable<DataTransferManager>.FromAbi(abiPtr);
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 初始化系统分享失败: {ex.Message}");
            errorMessage = "当前 Windows 环境不支持系统分享面板，可使用「复制图片」或「保存图片」。";
            return false;
        }
        finally
        {
            if (abiPtr != IntPtr.Zero)
                Marshal.Release(abiPtr);
        }

        void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= OnDataRequested;
            var deferral = args.Request.GetDeferral();
            _ = HandleDataRequestedAsync(args.Request, payload, deferral);
        }

        dtm.DataRequested += OnDataRequested;
        try
        {
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            dtm.DataRequested -= OnDataRequested;
            Log.Write($"[share] 打开系统分享面板失败: {ex.Message}");
            errorMessage = $"无法打开系统分享面板（{ex.Message}），可使用「复制图片」或「保存图片」。";
            return false;
        }
    }

    private static async Task HandleDataRequestedAsync(
        DataRequest request,
        ShareTempPayload payload,
        DataRequestDeferral deferral)
    {
        try
        {
            var tempPath = SaveTempSharePng(payload.Bitmap, payload.BaseName);
            var storageFile = await StorageFile.GetFileFromPathAsync(tempPath);

            request.Data.Properties.Title = payload.Title;
            if (!string.IsNullOrWhiteSpace(payload.Description))
                request.Data.Properties.Description = payload.Description;
            request.Data.SetStorageItems([storageFile], readOnly: true);
            request.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(storageFile));
        }
        catch (Exception ex)
        {
            Log.Write($"[share] 准备系统分享数据失败: {ex.Message}");
            try { request.FailWithDisplayText($"生成分享图片失败：{ex.Message}"); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static ushort ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata meta) return 1;
            object? val = null;
            if (meta.ContainsQuery("/app1/ifd/{ushort=274}"))
                val = meta.GetQuery("/app1/ifd/{ushort=274}");
            else if (meta.ContainsQuery("/ifd/{ushort=274}"))
                val = meta.GetQuery("/ifd/{ushort=274}");

            return val switch
            {
                ushort u => u,
                short s when s > 0 => (ushort)s,
                int i when i > 0 => (ushort)i,
                _ => 1,
            };
        }
        catch
        {
            return 1;
        }
    }

    private static void PruneOldCacheFiles(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 忽略临时文件清理异常
        }
    }
}
