namespace Musebase.Engine;

/// <summary>
/// 引擎的线程封送与周期定时器抽象。去掉 WPF 耦合(Dispatcher/DispatcherTimer)后，
/// 同一套编排逻辑可由 Windows/Android/macOS/服务器共享。
/// - WPF: 用 Dispatcher + DispatcherTimer 实现(ticks 与回调在 UI 线程上执行)。
/// - Android: 用主 Looper(Handler) 实现。
/// - 服务器/无界面: 不需要 SynchronizationContext，可用线程池定时器实现。
/// </summary>
public interface IEngineDispatcher
{
    /// <summary>把回调投递到消费方(UI 等)的首选线程上执行。</summary>
    void Post(Action action);

    /// <summary>创建周期定时器(回调与 Post 在同一个线程上执行)。创建后处于停止状态。</summary>
    IEngineTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>可启动/停止的周期定时器。</summary>
public interface IEngineTimer
{
    void Start();
    void Stop();
}
