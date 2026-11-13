using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Musebase.Windows.Services;

internal sealed class TrayIcon : IDisposable
{
    private const int WmAppTray = 0x8000 + 42;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private readonly HwndSource _sink;
    private readonly IntPtr _hIcon;
    private readonly Action _onLeftClick;
    private readonly ContextMenu _menu;
    private NotifyIconData _data;
    private bool _disposed;

    public TrayIcon(string tooltip, ContextMenu menu, Action onLeftClick)
    {
        _menu = menu;
        _onLeftClick = onLeftClick;

        var parameters = new HwndSourceParameters("SpotifyLyricsTraySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };
        _sink = new HwndSource(parameters);
        _sink.AddHook(WndProc);

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        ushort iconIndex = 0;
        _hIcon = !string.IsNullOrEmpty(exePath)
            ? ExtractAssociatedIconW(IntPtr.Zero, exePath, ref iconIndex)
            : IntPtr.Zero;

        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _sink.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = _hIcon,
            szTip = TruncateTip(tooltip),
        };
        _ = Shell_NotifyIconW(NimAdd, ref _data);
    }

    public static ImageSource? LoadAppIcon()
    {
        try
        {
            return BitmapFrame.Create(new Uri("pack://application:,,,/assets/app.ico", UriKind.Absolute));
        }
        catch
        {
            return null;
        }
    }

    public void UpdateToolTip(string tooltip)
    {
        if (_disposed) return;
        _data.uFlags = NifTip;
        _data.szTip = TruncateTip(tooltip);
        _ = Shell_NotifyIconW(NimModify, ref _data);
    }

    private static string TruncateTip(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Lyrx" : (text.Length > 120 ? text[..120] : text);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmAppTray)
        {
            var eventId = (int)(lParam.ToInt64() & 0xFFFF);
            if (eventId is WmLButtonUp or WmLButtonDblClk)
            {
                _onLeftClick();
                handled = true;
            }
            else if (eventId is WmRButtonUp or WmContextMenu)
            {
                _ = SetForegroundWindow(_sink.Handle);
                _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                _menu.IsOpen = true;
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = Shell_NotifyIconW(NimDelete, ref _data);
        if (_hIcon != IntPtr.Zero) _ = DestroyIcon(_hIcon);
        _sink.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
