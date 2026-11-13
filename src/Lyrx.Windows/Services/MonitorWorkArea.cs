using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Musebase.Windows.Services;

/// <summary>
/// 창이 놓인 <b>그 모니터</b>의 작업영역(작업표시줄 제외).
///
/// <see cref="SystemParameters.WorkArea"/>는 <b>주 모니터만</b> 돌려준다. 듀얼 모니터에서
/// 보조 화면에 둔 창을 그 값으로 보정하면 창이 주 모니터로 끌려온다 — 그래서 네이티브로 묻는다.
/// 핸들이 아직 없거나 조회가 실패하면 주 모니터 작업영역으로 물러난다(예전 동작).
/// </summary>
public static class MonitorWorkArea
{
    private const int MonitorDefaultToNearest = 2;

    public static Rect For(Window window) => GetArea(window, fullMonitor: false);

    public static Rect MonitorFor(Window window) => GetArea(window, fullMonitor: true);

    private static Rect GetArea(Window window, bool fullMonitor)
    {
        var fallback = fullMonitor
            ? new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)
            : SystemParameters.WorkArea;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return fallback;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return fallback;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info)) return fallback;

            var rect = fullMonitor ? info.rcMonitor : info.rcWork;
            // 물리 픽셀을 WPF 좌표(DIP)로 바꾼다.
            var source = PresentationSource.FromVisual(window);
            var m = source?.CompositionTarget?.TransformFromDevice;
            var topLeft = new Point(rect.Left, rect.Top);
            var bottomRight = new Point(rect.Right, rect.Bottom);
            if (m is { } transform)
            {
                topLeft = transform.Transform(topLeft);
                bottomRight = transform.Transform(bottomRight);
            }

            return new Rect(topLeft, bottomRight);
        }
        catch (Exception)
        {
            return fallback;   // 조용한 강등 — 위치 보정이 창을 못 띄우면 안 된다
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
}
