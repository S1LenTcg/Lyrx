namespace Musebase.Engine;

/// <summary>
/// 엔진의 스레드 마샬링·주기 타이머 추상화. WPF 결합(Dispatcher/DispatcherTimer)을 제거해
/// 같은 오케스트레이션 로직을 Windows/Android/macOS/서버가 공유할 수 있게 한다.
/// - WPF: Dispatcher + DispatcherTimer로 구현(틱과 콜백을 UI 스레드에서 실행).
/// - Android: 메인 Looper(Handler)로 구현.
/// - 서버/헤드리스: SynchronizationContext 없이 스레드풀 타이머로 구현 가능.
/// </summary>
public interface IEngineDispatcher
{
    /// <summary>소비자(UI 등)의 선호 스레드에서 실행되도록 콜백을 게시한다.</summary>
    void Post(Action action);

    /// <summary>주기 타이머를 만든다(콜백은 Post와 같은 스레드에서 실행). 생성 직후엔 정지 상태.</summary>
    IEngineTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>시작/정지 가능한 주기 타이머.</summary>
public interface IEngineTimer
{
    void Start();
    void Stop();
}
