using System.Windows.Threading;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// <see cref="IEngineDispatcher"/> 的 WPF 实现。
/// 在 UI 线程（Dispatcher）上执行回调与定时器 tick，保证覆盖层刷新安全。
/// </summary>
public sealed class WpfEngineDispatcher : IEngineDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfEngineDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public IEngineTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send, _dispatcher) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return new DispatcherEngineTimer(timer);
    }

    private sealed class DispatcherEngineTimer : IEngineTimer
    {
        private readonly DispatcherTimer _timer;
        public DispatcherEngineTimer(DispatcherTimer timer) => _timer = timer;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
