using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Musebase.Engine;

/// <summary>
/// <see cref="LyricsCoordinator"/>의 <see cref="PlaybackViewState"/>를 관찰 가능한 속성으로
/// 노출하는 바인딩 어댑터(MVVM). XAML 바인딩 UI(MAUI 등)가 이 VM의 <see cref="State"/>를 구독한다.
/// WPF 오버레이는 성능상 기존 per-frame 이벤트를 직접 쓰므로 이 VM을 요구하지 않는다(선택적 시임).
/// </summary>
public sealed class PlaybackViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LyricsCoordinator _coordinator;
    private PlaybackViewState _state = PlaybackViewState.Empty;

    public PlaybackViewModel(LyricsCoordinator coordinator)
    {
        _coordinator = coordinator;
        State = coordinator.CurrentState;
        _coordinator.StateChanged += OnStateChanged;
    }

    /// <summary>현재 표시 상태(변경 시 PropertyChanged 발생).</summary>
    public PlaybackViewState State
    {
        get => _state;
        private set
        {
            if (ReferenceEquals(_state, value)) return;
            _state = value;
            OnPropertyChanged();
        }
    }

    private void OnStateChanged(PlaybackViewState state) => State = state;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _coordinator.StateChanged -= OnStateChanged;
}
