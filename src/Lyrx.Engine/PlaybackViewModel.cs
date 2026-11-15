using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Musebase.Engine;

/// <summary>
/// 把 <see cref="LyricsCoordinator"/> 的 <see cref="PlaybackViewState"/> 暴露为可观察属性的
/// 绑定适配器（MVVM）。XAML 绑定界面（MAUI 等）订阅该 VM 的 <see cref="State"/>。
/// WPF 覆盖层出于性能直接使用原有的逐帧事件，不需要该 VM（可选接缝点）。
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

    /// <summary>当前显示状态（变更时触发 PropertyChanged）。</summary>
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
