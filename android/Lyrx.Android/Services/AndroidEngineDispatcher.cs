using Android.OS;
using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// <see cref="IEngineDispatcher"/> 的 Android 实现：将回调与周期定时器统一封送到主线程 <see cref="Looper.MainLooper"/>。
/// </summary>
public sealed class AndroidEngineDispatcher : IEngineDispatcher
{
    private readonly Handler _handler = new(Looper.MainLooper!);

    public void Post(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper)
            action();
        else
            _handler.Post(action);
    }

    public IEngineTimer CreateTimer(TimeSpan interval, Action tick) =>
        new HandlerEngineTimer(_handler, Math.Max(16L, (long)interval.TotalMilliseconds), tick);

    private sealed class HandlerEngineTimer : IEngineTimer
    {
        private readonly Handler _handler;
        private readonly long _intervalMs;
        private readonly Action _tick;
        private readonly Java.Lang.Runnable _runnable;
        private bool _running;

        public HandlerEngineTimer(Handler handler, long intervalMs, Action tick)
        {
            _handler = handler;
            _intervalMs = intervalMs;
            _tick = tick;
            _runnable = new Java.Lang.Runnable(OnTick);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _handler.PostDelayed(_runnable, _intervalMs);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _handler.RemoveCallbacks(_runnable);
        }

        private void OnTick()
        {
            if (!_running) return;
            try
            {
                _tick();
            }
            finally
            {
                if (_running)
                    _handler.PostDelayed(_runnable, _intervalMs);
            }
        }
    }
}
