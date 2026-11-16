using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Musebase.Windows.Services;

public enum AudioOutputKind
{
    Speaker,
    Bluetooth,
    WiredOrUsb,
}

public sealed record AudioOutputDevice(
    string Id,
    string Name,
    AudioOutputKind Kind,
    bool IsActive,
    bool IsConnected = true,
    Guid ContainerId = default);

public readonly record struct AudioVolumeState(int Percent, bool IsMuted);

/// <summary>
/// 抽象底层音频端点枚举、切换与音量控制，便于核心监测逻辑与 UI 在无硬件环境下独立验证。
/// </summary>
public interface IAudioOutputBackend : IDisposable
{
    event Action? TopologyChanged;
    event Action<AudioVolumeState, Guid>? VolumeChanged;

    IReadOnlyList<AudioOutputDevice> EnumerateActiveRenderDevices(
        IReadOnlySet<Guid> connectedBluetoothContainers,
        IReadOnlySet<string> connectedBluetoothNames);

    AudioVolumeState GetVolumeState();
    bool SetVolumePercent(int percent, Guid eventContext);
    bool SetMuted(bool muted, Guid eventContext);
    bool TrySetDefaultEndpoint(string deviceId);
    Task<IReadOnlyList<PairedBluetoothAudioDevice>> QueryPairedBluetoothAudioDevicesAsync(CancellationToken ct);
}

public readonly record struct PairedBluetoothAudioDevice(
    string Id,
    string Name,
    bool IsConnected,
    Guid ContainerId);

/// <summary>
/// 监听 Windows 默认音频输出设备、蓝牙音频设备连接状态与主音量变化，
/// 并支持应用内一键切换输出端点、实时调节音量以及打开系统蓝牙/声音设置。
/// </summary>
public sealed class WindowsAudioOutputMonitor : IDisposable
{
    private readonly IAudioOutputBackend _backend;
    private readonly Guid _appVolumeContext = Guid.NewGuid();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private IReadOnlyList<PairedBluetoothAudioDevice> _pairedBt = Array.Empty<PairedBluetoothAudioDevice>();
    private string? _lastActiveSignature;
    private int _pairedQueryInFlight;
    private int _refreshQueued;
    private bool _disposed;

    public event Action? OutputStateChanged;
    public event Action<AudioVolumeState>? VolumeStateChanged;

    public WindowsAudioOutputMonitor(IAudioOutputBackend? backend = null)
    {
        _backend = backend ?? new CoreAudioOutputBackend();
        _backend.TopologyChanged += ScheduleAsyncRefresh;
        _backend.VolumeChanged += OnBackendVolumeChanged;
    }

    public void Start()
    {
        if (_disposed) return;
        Refresh();
        _ = RefreshPairedBluetoothAsync();
    }

    public AudioOutputDevice GetCurrentActiveOutput()
    {
        var active = GetActiveEndpoints().FirstOrDefault(d => d.IsActive);
        if (active is not null)
            return active;

        return new AudioOutputDevice(
            Id: "",
            Name: GetLocalSpeakerFallbackName(),
            Kind: AudioOutputKind.Speaker,
            IsActive: true);
    }

    public IReadOnlyList<AudioOutputDevice> GetAvailableOutputs()
    {
        var endpoints = GetActiveEndpoints();
        if (endpoints.Count == 0)
        {
            return
            [
                new AudioOutputDevice(
                    Id: "",
                    Name: GetLocalSpeakerFallbackName(),
                    Kind: AudioOutputKind.Speaker,
                    IsActive: true),
            ];
        }

        var inactive = new List<AudioOutputDevice>(endpoints.Count);
        AudioOutputDevice? active = null;
        foreach (var ep in endpoints)
        {
            if (ep.IsActive && active is null)
                active = ep;
            else
                inactive.Add(ep);
        }

        inactive.Sort(CompareInactiveOutputs);
        if (active is not null)
            inactive.Add(active);
        return inactive;
    }

    public IReadOnlyList<AudioOutputDevice> GetPairedDisconnectedBluetoothDevices()
    {
        IReadOnlyList<PairedBluetoothAudioDevice> paired;
        lock (_sync)
            paired = _pairedBt;

        if (paired.Count == 0)
            return Array.Empty<AudioOutputDevice>();

        var active = GetActiveEndpoints();
        var result = new List<AudioOutputDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bt in paired)
        {
            if (bt.IsConnected || string.IsNullOrWhiteSpace(bt.Name))
                continue;

            var representedByActive = active.Any(ep =>
                (bt.ContainerId != Guid.Empty && ep.ContainerId == bt.ContainerId)
                || ep.Name.Contains(bt.Name, StringComparison.OrdinalIgnoreCase));
            if (representedByActive || !seen.Add(bt.Name))
                continue;

            result.Add(new AudioOutputDevice(
                Id: bt.Id,
                Name: bt.Name,
                Kind: AudioOutputKind.Bluetooth,
                IsActive: false,
                IsConnected: false,
                ContainerId: bt.ContainerId));
        }

        return result;
    }

    public AudioVolumeState GetVolumeState() => _backend.GetVolumeState();

    public void SetVolumePercent(int percent)
    {
        if (_disposed) return;
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped > 0 && _backend.GetVolumeState().IsMuted)
            _backend.SetMuted(false, _appVolumeContext);
        _backend.SetVolumePercent(clamped, _appVolumeContext);
    }

    public void ToggleMute()
    {
        if (_disposed) return;
        var current = _backend.GetVolumeState();
        _backend.SetMuted(!current.IsMuted, _appVolumeContext);
    }

    public bool SwitchDefaultOutput(string deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId))
            return false;

        var ok = _backend.TrySetDefaultEndpoint(deviceId);
        if (ok)
        {
            Refresh();
            VolumeStateChanged?.Invoke(_backend.GetVolumeState());
        }
        return ok;
    }

    public void Refresh()
    {
        if (_disposed) return;

        var outputs = GetAvailableOutputs();
        var sig = string.Join("|", outputs.Select(o => $"{o.Id}:{(int)o.Kind}:{o.IsActive}:{o.Name}"));

        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(_lastActiveSignature, sig, StringComparison.Ordinal);
            _lastActiveSignature = sig;
        }

        if (changed)
            OutputStateChanged?.Invoke();
    }

    public async Task RefreshPairedBluetoothAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _pairedQueryInFlight, 1, 0) != 0)
            return;

        try
        {
            var devices = await _backend.QueryPairedBluetoothAudioDevicesAsync(_disposeCts.Token).ConfigureAwait(false);
            if (_disposed) return;

            bool changed;
            lock (_sync)
            {
                changed = !PairedListEquals(_pairedBt, devices);
                _pairedBt = devices;
            }

            if (changed)
                OutputStateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // 释放或退出时取消
        }
        catch (Exception ex)
        {
            Log.Write($"[audio] 刷新已配对蓝牙设备失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pairedQueryInFlight, 0);
        }
    }

    public static bool TryOpenSystemBluetoothSettings() => TryLaunchSettingsUri("ms-settings:bluetooth");

    public static bool TryOpenSystemSoundSettings() => TryLaunchSettingsUri("ms-settings:sound");

    internal static AudioOutputKind ClassifyEndpoint(
        string? enumeratorName,
        string? instanceId,
        uint? formFactor,
        Guid containerId,
        IReadOnlySet<Guid>? connectedBtContainers = null,
        IReadOnlySet<string>? connectedBtNames = null,
        string? interfaceName = null,
        string? friendlyName = null)
    {
        if (IsBluetoothIdentifier(enumeratorName)
            || IsBluetoothIdentifier(instanceId)
            || (containerId != Guid.Empty && connectedBtContainers?.Contains(containerId) == true)
            || MatchesConnectedBluetoothName(connectedBtNames, interfaceName, friendlyName))
        {
            return AudioOutputKind.Bluetooth;
        }

        // EndpointFormFactor: 3=Headphones, 5=Headset, 6=Handset
        if (formFactor is 3 or 5 or 6)
            return AudioOutputKind.WiredOrUsb;

        return AudioOutputKind.Speaker;
    }

    private IReadOnlyList<AudioOutputDevice> GetActiveEndpoints()
    {
        IReadOnlyList<PairedBluetoothAudioDevice> paired;
        lock (_sync)
            paired = _pairedBt;

        var btContainers = new HashSet<Guid>();
        var btNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bt in paired)
        {
            if (!bt.IsConnected) continue;
            if (bt.ContainerId != Guid.Empty) btContainers.Add(bt.ContainerId);
            if (!string.IsNullOrWhiteSpace(bt.Name)) btNames.Add(bt.Name);
        }

        var devices = _backend.EnumerateActiveRenderDevices(btContainers, btNames);
        if (devices.Count == 0)
            return devices;

        return devices
            .Where(d => d.IsActive || !IsVirtualAudioDeviceName(d.Name))
            .ToList();
    }

    internal static bool IsVirtualAudioDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("虚拟", StringComparison.OrdinalIgnoreCase)
            || name.Contains("virtual", StringComparison.OrdinalIgnoreCase);
    }

    private void OnBackendVolumeChanged(AudioVolumeState state, Guid context)
    {
        if (_disposed) return;
        VolumeStateChanged?.Invoke(state);
    }

    private void ScheduleAsyncRefresh()
    {
        if (_disposed || Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // 合并短时间内的硬件枚举与路由切换突发通知
                await Task.Delay(45, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
            }

            if (_disposed) return;
            Refresh();
            VolumeStateChanged?.Invoke(_backend.GetVolumeState());
            _ = RefreshPairedBluetoothAsync();
        });
    }

    private static int CompareInactiveOutputs(AudioOutputDevice a, AudioOutputDevice b)
    {
        static int KindRank(AudioOutputDevice d)
        {
            if (d.Kind == AudioOutputKind.Bluetooth)
            {
                var isHandsFree = d.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
                    || d.Name.Contains("AG Audio", StringComparison.OrdinalIgnoreCase);
                return isHandsFree ? 1 : 0;
            }
            return d.Kind == AudioOutputKind.WiredOrUsb ? 2 : 3;
        }

        var rankCmp = KindRank(a).CompareTo(KindRank(b));
        return rankCmp != 0 ? rankCmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsBluetoothIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
            || value.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || value.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
            || value.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesConnectedBluetoothName(
        IReadOnlySet<string>? connectedNames,
        string? interfaceName,
        string? friendlyName)
    {
        if (connectedNames is null || connectedNames.Count == 0)
            return false;

        foreach (var btName in connectedNames)
        {
            if (string.IsNullOrWhiteSpace(btName)) continue;
            if ((!string.IsNullOrWhiteSpace(interfaceName) && interfaceName.Contains(btName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(friendlyName) && friendlyName.Contains(btName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool PairedListEquals(
        IReadOnlyList<PairedBluetoothAudioDevice> a,
        IReadOnlyList<PairedBluetoothAudioDevice> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!Equals(a[i], b[i])) return false;
        }
        return true;
    }

    private static string GetLocalSpeakerFallbackName()
    {
        var machine = Environment.MachineName;
        return string.IsNullOrWhiteSpace(machine) ? "本机扬声器" : $"{machine} 扬声器";
    }

    private static bool TryLaunchSettingsUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[audio] 无法打开系统设置 ({uri}): {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _backend.TopologyChanged -= ScheduleAsyncRefresh;
        _backend.VolumeChanged -= OnBackendVolumeChanged;
        _backend.Dispose();
    }
}

/// <summary>
/// 基于 Windows Core Audio COM (`MMDeviceEnumerator` / `IAudioEndpointVolume` / `IPolicyConfig`)
/// 与 WinRT 已配对蓝牙设备的真实后端实现。
/// </summary>
internal sealed class CoreAudioOutputBackend : IAudioOutputBackend
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsCtxAll = 0x17;
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private static readonly PropertyKey PkeyDeviceDesc = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey PkeyDeviceEnumeratorName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey PkeyDeviceInstanceId = new(new("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    private static readonly PropertyKey PkeyDeviceContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey PkeyAudioEndpointFormFactor = new(new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private readonly object _lock = new();
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _notificationClient;
    private IAudioEndpointVolume? _volumeControl;
    private VolumeCallback? _volumeCallback;
    private string? _volumeDeviceId;
    private bool _disposed;

    public event Action? TopologyChanged;
    public event Action<AudioVolumeState, Guid>? VolumeChanged;

    public CoreAudioOutputBackend()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null) return;
            _enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
            if (_enumerator is not null)
            {
                _notificationClient = new NotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[audio] 初始化 CoreAudio 失败: {ex.Message}");
        }
    }

    public IReadOnlyList<AudioOutputDevice> EnumerateActiveRenderDevices(
        IReadOnlySet<Guid> connectedBluetoothContainers,
        IReadOnlySet<string> connectedBluetoothNames)
    {
        IMMDeviceEnumerator? enumerator;
        lock (_lock)
            enumerator = _enumerator;

        if (enumerator is null || _disposed)
            return Array.Empty<AudioOutputDevice>();

        var result = new List<AudioOutputDevice>();
        IMMDevice? defaultDev = null;
        IMMDeviceCollection? collection = null;
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out defaultDev) == 0 && defaultDev is not null)
            {
                defaultDev.GetId(out defaultId);
            }

            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out collection) != 0 || collection is null)
                return result;

            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                IPropertyStore? props = null;
                try
                {
                    if (collection.Item(i, out dev) != 0 || dev is null)
                        continue;

                    if (dev.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id))
                        continue;

                    string? friendly = null;
                    string? desc = null;
                    string? iface = null;
                    string? enumName = null;
                    string? instId = null;
                    Guid containerId = Guid.Empty;
                    uint? formFactor = null;

                    if (dev.OpenPropertyStore(0, out props) == 0 && props is not null)
                    {
                        friendly = ReadStringProp(props, PkeyDeviceFriendlyName);
                        desc = ReadStringProp(props, PkeyDeviceDesc);
                        iface = ReadStringProp(props, PkeyDeviceInterfaceFriendlyName);
                        enumName = ReadStringProp(props, PkeyDeviceEnumeratorName);
                        instId = ReadStringProp(props, PkeyDeviceInstanceId);
                        containerId = ReadGuidProp(props, PkeyDeviceContainerId) ?? Guid.Empty;
                        formFactor = ReadUIntProp(props, PkeyAudioEndpointFormFactor);
                    }

                    var displayName = !string.IsNullOrWhiteSpace(friendly)
                        ? friendly!
                        : !string.IsNullOrWhiteSpace(iface)
                            ? iface!
                            : !string.IsNullOrWhiteSpace(desc)
                                ? desc!
                                : "音频输出设备";

                    var kind = WindowsAudioOutputMonitor.ClassifyEndpoint(
                        enumName,
                        instId,
                        formFactor,
                        containerId,
                        connectedBluetoothContainers,
                        connectedBluetoothNames,
                        iface,
                        displayName);

                    var isActive = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                    result.Add(new AudioOutputDevice(
                        Id: id,
                        Name: displayName,
                        Kind: kind,
                        IsActive: isActive,
                        IsConnected: true,
                        ContainerId: containerId));
                }
                catch
                {
                    // 单个设备读取失败不影响其他音频端点枚举
                }
                finally
                {
                    ReleaseCom(props);
                    ReleaseCom(dev);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[audio] 枚举输出端点异常: {ex.Message}");
        }
        finally
        {
            ReleaseCom(collection);
            ReleaseCom(defaultDev);
        }

        return result;
    }

    public AudioVolumeState GetVolumeState()
    {
        lock (_lock)
        {
            var vol = EnsureVolumeControlLocked();
            if (vol is null) return new AudioVolumeState(0, IsMuted: false);

            try
            {
                vol.GetMasterVolumeLevelScalar(out var scalar);
                vol.GetMute(out var muted);
                var pct = Math.Clamp((int)Math.Round(scalar * 100f), 0, 100);
                return new AudioVolumeState(pct, muted);
            }
            catch
            {
                DetachVolumeControlLocked();
                return new AudioVolumeState(0, IsMuted: false);
            }
        }
    }

    public bool SetVolumePercent(int percent, Guid eventContext)
    {
        lock (_lock)
        {
            var vol = EnsureVolumeControlLocked();
            if (vol is null) return false;
            try
            {
                var level = Math.Clamp(percent, 0, 100) / 100f;
                return vol.SetMasterVolumeLevelScalar(level, ref eventContext) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool SetMuted(bool muted, Guid eventContext)
    {
        lock (_lock)
        {
            var vol = EnsureVolumeControlLocked();
            if (vol is null) return false;
            try
            {
                return vol.SetMute(muted, ref eventContext) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TrySetDefaultEndpoint(string deviceId)
    {
        IPolicyConfig? policy = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient);
            if (type is null) return false;
            policy = Activator.CreateInstance(type) as IPolicyConfig;
            if (policy is null) return false;

            var hrConsole = policy.SetDefaultEndpoint(deviceId, RoleConsole);
            var hrMultimedia = policy.SetDefaultEndpoint(deviceId, RoleMultimedia);
            _ = policy.SetDefaultEndpoint(deviceId, RoleCommunications);

            lock (_lock)
                DetachVolumeControlLocked();

            return hrMultimedia == 0 || hrConsole == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"[audio] 切换默认音频输出失败: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseCom(policy);
        }
    }

    public async Task<IReadOnlyList<PairedBluetoothAudioDevice>> QueryPairedBluetoothAudioDevicesAsync(CancellationToken ct)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);
        if (infos.Count == 0)
            return Array.Empty<PairedBluetoothAudioDevice>();

        var list = new List<PairedBluetoothAudioDevice>();
        foreach (var info in infos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var bt = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
                if (bt is null) continue;

                var isAudio = bt.ClassOfDevice?.MajorClass == BluetoothMajorClass.AudioVideo
                    || bt.ClassOfDevice?.ServiceCapabilities.HasFlag(BluetoothServiceCapabilities.AudioService) == true
                    || bt.ClassOfDevice?.ServiceCapabilities.HasFlag(BluetoothServiceCapabilities.RenderingService) == true;
                if (!isAudio) continue;

                var name = string.IsNullOrWhiteSpace(bt.Name) ? info.Name : bt.Name.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                Guid containerId = Guid.Empty;
                if (info.Properties.TryGetValue("System.Devices.ContainerId", out var contObj) && contObj is Guid g)
                    containerId = g;

                list.Add(new PairedBluetoothAudioDevice(
                    Id: info.Id,
                    Name: name,
                    IsConnected: bt.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    ContainerId: containerId));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个蓝牙设备详情读取失败时忽略
            }
        }

        return list;
    }

    private IAudioEndpointVolume? EnsureVolumeControlLocked()
    {
        if (_disposed || _enumerator is null) return null;

        IMMDevice? defDev = null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out defDev) != 0 || defDev is null)
            {
                DetachVolumeControlLocked();
                return null;
            }

            if (defDev.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id))
                return _volumeControl;

            if (_volumeControl is not null && string.Equals(id, _volumeDeviceId, StringComparison.OrdinalIgnoreCase))
                return _volumeControl;

            DetachVolumeControlLocked();

            var iid = IidAudioEndpointVolume;
            if (defDev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volObj) != 0
                || volObj is not IAudioEndpointVolume vol)
            {
                return null;
            }

            _volumeCallback = new VolumeCallback(this);
            vol.RegisterControlChangeNotify(_volumeCallback);
            _volumeControl = vol;
            _volumeDeviceId = id;
            return vol;
        }
        catch
        {
            DetachVolumeControlLocked();
            return null;
        }
        finally
        {
            ReleaseCom(defDev);
        }
    }

    private void DetachVolumeControlLocked()
    {
        if (_volumeControl is not null && _volumeCallback is not null)
        {
            try { _volumeControl.UnregisterControlChangeNotify(_volumeCallback); } catch { }
        }
        ReleaseCom(_volumeControl);
        _volumeControl = null;
        _volumeCallback = null;
        _volumeDeviceId = null;
    }

    private void RaiseTopologyChanged()
    {
        if (!_disposed)
            TopologyChanged?.Invoke();
    }

    private void RaiseVolumeChanged(AudioVolumeState state, Guid context)
    {
        if (!_disposed)
            VolumeChanged?.Invoke(state, context);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DetachVolumeControlLocked();
            if (_enumerator is not null && _notificationClient is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            }
            ReleaseCom(_enumerator);
            _enumerator = null;
            _notificationClient = null;
        }
    }

    private static string? ReadStringProp(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            // VT_LPWSTR = 31
            return pv.vt == 31 && pv.ptr != IntPtr.Zero ? Marshal.PtrToStringUni(pv.ptr)?.Trim() : null;
        }
        finally
        {
            _ = PropVariantClear(ref pv);
        }
    }

    private static uint? ReadUIntProp(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            // VT_UI4 = 19, VT_I4 = 3
            return pv.vt is 19 or 3 ? pv.uintVal : null;
        }
        finally
        {
            _ = PropVariantClear(ref pv);
        }
    }

    private static Guid? ReadGuidProp(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            // VT_CLSID = 72
            return pv.vt == 72 && pv.ptr != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(pv.ptr) : null;
        }
        finally
        {
            _ = PropVariantClear(ref pv);
        }
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try { Marshal.FinalReleaseComObject(obj); } catch { }
        }
    }

    private sealed class NotificationClient(CoreAudioOutputBackend owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => owner.RaiseTopologyChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => owner.RaiseTopologyChanged();
        public void OnDeviceRemoved(string pwstrDeviceId) => owner.RaiseTopologyChanged();
        public void OnDefaultDeviceChanged(int flow, int role, string pwstrDefaultDeviceId)
        {
            if (flow == DataFlowRender && role == RoleMultimedia)
                owner.RaiseTopologyChanged();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => owner.RaiseTopologyChanged();
    }

    private sealed class VolumeCallback(CoreAudioOutputBackend owner) : IAudioEndpointVolumeCallback
    {
        public void OnNotify(IntPtr pNotify)
        {
            if (pNotify == IntPtr.Zero) return;
            try
            {
                var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotify);
                var pct = Math.Clamp((int)Math.Round(data.MasterVolume * 100f), 0, 100);
                owner.RaiseVolumeChanged(new AudioVolumeState(pct, data.Muted != 0), data.EventContext);
            }
            catch
            {
                // 忽略回调结构体读取异常
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    // x64 下 PROPVARIANT 固定占 24 字节，Size 声明不足会导致 GetValue 越界覆盖栈上相邻变量。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public uint uintVal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection? ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice? ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore? ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        void OnNotify(IntPtr pNotify);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref long pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out int pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }
}
