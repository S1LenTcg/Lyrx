using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Musebase.Windows.Services;

/// <summary>
/// 窗口所在<b>那块显示器</b>的工作区（不含任务栏）。
///
/// <see cref="SystemParameters.WorkArea"/> 只返回<b>主显示器</b>的数值。在多显示器环境下，
/// 若用它来校正放在副屏上的窗口，窗口会被拉到主显示器上 —— 所以改为直接向系统原生查询。
/// 若句柄尚不存在或查询失败，则退回到主显示器的工作区（旧行为）。
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
            // 把物理像素换算成 WPF 坐标（DIP）。
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
            return fallback;   // 静默降级 —— 位置校正绝不能导致窗口无法显示
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
