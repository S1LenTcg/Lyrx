using System.Windows.Threading;
using Musebase.Engine;

namespace Musebase.Windows.Services;

/// <summary>
/// <see cref="IEngineDispatcher"/>의 WPF 구현.
/// 콜백·타이머 틱을 UI 스레드(Dispatcher)에서 실행해 오버레이 갱신을 안전하게 한다.
/// </summary>
public sealed class WpfEngineDispatcher : IEngineDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfEngineDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public IEngineTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = interval };
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
