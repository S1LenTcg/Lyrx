using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Runtime;

namespace Musebase.Android.Services;

public enum AudioOutputKind
{
    PhoneSpeaker,
    Bluetooth,
    WiredOrUsb,
}

public sealed record AudioOutputEntry(
    string Name,
    AudioOutputKind Kind,
    bool IsActive);

/// <summary>
/// 监听系统当前连接的蓝牙音频输出设备（A2DP / LE Audio / 助听器 / 活跃蓝牙路由），
/// 并在设备连接、断开、重命名或切换输出路由时通知界面更新底部设备名称。
/// </summary>
public sealed class BluetoothAudioMonitor : IDisposable
{
    private const string ActionA2dpActiveDeviceChanged = "android.bluetooth.a2dp.profile.action.ACTIVE_DEVICE_CHANGED";

    private readonly Context _context;
    private readonly Handler? _handler;
    private readonly AudioManager? _audioManager;
    private readonly MediaRouter? _mediaRouter;
    private readonly BluetoothManager? _bluetoothManager;
    private readonly AudioAttributes? _mediaAudioAttributes;

    private readonly DeviceCallback? _audioCallback;
    private readonly RouteCallback? _routeCallback;
    private readonly BtBroadcastReceiver? _receiver;
    private readonly ProfileListener? _profileListener;
    private readonly Java.Lang.Runnable? _delayedRefreshRunnable;

    private bool _started;
    private bool _disposed;
    private bool _proxiesRequested;

    private IBluetoothProfile? _a2dpProxy;
    private IBluetoothProfile? _hearingAidProxy;
    private IBluetoothProfile? _leAudioProxy;

    public bool IsBluetoothAudioConnected { get; private set; }
    public string? ConnectedDeviceName { get; private set; }

    public event Action<string?>? ConnectedDeviceChanged;

    public BluetoothAudioMonitor(Context context)
    {
        _context = context.ApplicationContext ?? context;

        try
        {
            var looper = Looper.MainLooper;
            _handler = looper is not null ? new Handler(looper) : null;
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 初始化 Handler 失败: {ex.Message}");
        }

        try { _audioManager = _context.GetSystemService(Context.AudioService) as AudioManager; }
        catch (Exception ex) { AndroidLog.Write($"[bluetooth] 获取 AudioManager 失败: {ex.Message}"); }

        try { _mediaRouter = _context.GetSystemService(Context.MediaRouterService) as MediaRouter; }
        catch (Exception ex) { AndroidLog.Write($"[bluetooth] 获取 MediaRouter 失败: {ex.Message}"); }

        try { _bluetoothManager = _context.GetSystemService(Context.BluetoothService) as BluetoothManager; }
        catch (Exception ex) { AndroidLog.Write($"[bluetooth] 获取 BluetoothManager 失败: {ex.Message}"); }

        try
        {
            _mediaAudioAttributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                ?.SetContentType(AudioContentType.Music)
                ?.Build();
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 初始化 AudioAttributes 失败: {ex.Message}");
        }

        try
        {
            _audioCallback = new DeviceCallback(this);
            _routeCallback = new RouteCallback(this);
            _receiver = new BtBroadcastReceiver(this);
            _profileListener = new ProfileListener(this);
            _delayedRefreshRunnable = new Java.Lang.Runnable(Refresh);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 初始化蓝牙监听对象失败: {ex.Message}");
        }
    }

    public static bool HasBluetoothConnectPermission(Context context)
    {
        try
        {
            return !OperatingSystem.IsAndroidVersionAtLeast(31)
                || context.CheckSelfPermission(global::Android.Manifest.Permission.BluetoothConnect) == Permission.Granted;
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        try
        {
            if (_audioCallback is not null)
                _audioManager?.RegisterAudioDeviceCallback(_audioCallback, _handler);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 注册 AudioDeviceCallback 失败: {ex.Message}");
        }

        try
        {
            if (_routeCallback is not null)
            {
                _mediaRouter?.AddCallback(
                    MediaRouteType.LiveAudio,
                    _routeCallback,
                    MediaRouterCallbackFlags.UnfilteredEvents);
            }
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 注册 MediaRouter 回调失败: {ex.Message}");
        }

        RegisterReceivers();
        EnsureBluetoothProxies();
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;

        try
        {
            EnsureBluetoothProxies();
            var (connected, name) = ResolveConnectedBluetoothAudio();

            var stateChanged = IsBluetoothAudioConnected != connected
                || !string.Equals(ConnectedDeviceName, name, StringComparison.Ordinal);
            IsBluetoothAudioConnected = connected;
            ConnectedDeviceName = name;

            if (stateChanged)
            {
                AndroidLog.Write($"[bluetooth] 音频输出设备: {(connected ? name ?? "(未知蓝牙设备)" : "(未连接)")}");
                ConnectedDeviceChanged?.Invoke(name);
            }
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 刷新蓝牙音频状态失败: {ex.Message}");
        }
    }

    public (int Current, int Min, int Max) GetMediaVolumeState()
    {
        if (_audioManager is null)
            return (0, 0, 15);

        try
        {
            var max = Math.Max(1, _audioManager.GetStreamMaxVolume(global::Android.Media.Stream.Music));
            var min = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? GetStreamMinVolumeApi28(_audioManager)
                : 0;
            if (min < 0 || min >= max) min = 0;
            var current = Math.Clamp(_audioManager.GetStreamVolume(global::Android.Media.Stream.Music), min, max);
            return (current, min, max);
        }
        catch
        {
            return (0, 0, 15);
        }
    }

    public void SetMediaVolume(int level)
    {
        if (_audioManager is null) return;
        try
        {
            var (_, min, max) = GetMediaVolumeState();
            var clamped = Math.Clamp(level, min, max);
            _audioManager.SetStreamVolume(global::Android.Media.Stream.Music, clamped, 0);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[audio] 设置媒体音量失败: {ex.Message}");
        }
    }

    public IReadOnlyList<AudioOutputEntry> GetAvailableOutputs()
    {
        var inactive = new List<AudioOutputEntry>();
        AudioOutputEntry? activeEntry = null;

        try
        {
            var outputDevices = _audioManager?.GetDevices(GetDevicesTargets.Outputs)
                ?? Array.Empty<AudioDeviceInfo>();
            var activeBt = FindActiveBluetoothOutput(outputDevices);
            var activeWired = activeBt is null ? FindActiveWiredOutput(outputDevices) : null;

            var btNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeBtName = activeBt is not null
                ? ResolveDeviceDisplayName(activeBt) ?? ConnectedDeviceName ?? "蓝牙音频设备"
                : null;

            if (activeBtName is not null && btNames.Add(activeBtName))
                activeEntry = new AudioOutputEntry(activeBtName, AudioOutputKind.Bluetooth, IsActive: true);

            foreach (var d in outputDevices)
            {
                if (d is null || !d.IsSink || !IsMediaBluetoothType(d.Type)) continue;
                var btName = ResolveDeviceDisplayName(d) ?? "蓝牙音频设备";
                if (btNames.Add(btName))
                    inactive.Add(new AudioOutputEntry(btName, AudioOutputKind.Bluetooth, IsActive: false));
            }

            if (HasBluetoothConnectPermission(_context))
            {
                CollectConnectedProxyDeviceNames(_a2dpProxy as BluetoothA2dp is { } a2dp ? a2dp.ConnectedDevices : null, btNames, inactive);
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    CollectConnectedProxyDeviceNames(GetHearingAidConnectedDevicesApi29(_hearingAidProxy), btNames, inactive);
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    CollectConnectedProxyDeviceNames(GetLeAudioConnectedDevicesApi33(_leAudioProxy), btNames, inactive);
            }

            var wiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeWiredName = activeWired is not null ? ResolveWiredDisplayName(activeWired) : null;
            if (activeWiredName is not null && wiredNames.Add(activeWiredName) && activeEntry is null)
                activeEntry = new AudioOutputEntry(activeWiredName, AudioOutputKind.WiredOrUsb, IsActive: true);

            foreach (var d in outputDevices)
            {
                if (d is null || !d.IsSink || !IsWiredOrUsbOutputType(d.Type)) continue;
                var wName = ResolveWiredDisplayName(d);
                if (wiredNames.Add(wName))
                    inactive.Add(new AudioOutputEntry(wName, AudioOutputKind.WiredOrUsb, IsActive: false));
            }

            var speakerEntry = new AudioOutputEntry("手机扬声器", AudioOutputKind.PhoneSpeaker, IsActive: activeEntry is null);
            if (activeEntry is null)
                activeEntry = speakerEntry;
            else
                inactive.Insert(0, speakerEntry);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[audio] 枚举播放设备失败: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(ConnectedDeviceName))
            {
                inactive.Add(new AudioOutputEntry("手机扬声器", AudioOutputKind.PhoneSpeaker, IsActive: false));
                activeEntry ??= new AudioOutputEntry(ConnectedDeviceName!, AudioOutputKind.Bluetooth, IsActive: true);
            }
            else
            {
                activeEntry ??= new AudioOutputEntry("手机扬声器", AudioOutputKind.PhoneSpeaker, IsActive: true);
            }
        }

        if (activeEntry is not null)
            inactive.Add(activeEntry);
        return inactive;
    }

    public static bool TryOpenSystemOutputSwitcher(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(34) && TryShowSystemOutputSwitcherApi34(context))
            return true;

        if (OperatingSystem.IsAndroidVersionAtLeast(29) && TryOpenVolumePanelApi29(context))
            return true;

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private static int GetStreamMinVolumeApi28(AudioManager audioManager) =>
        audioManager.GetStreamMinVolume(global::Android.Media.Stream.Music);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android34.0")]
    private static bool TryShowSystemOutputSwitcherApi34(Context context)
    {
        try
        {
            var router2 = MediaRouter2.GetInstance(context);
            return router2?.ShowSystemOutputSwitcher() == true;
        }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static bool TryOpenVolumePanelApi29(Context context)
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.Panel.ActionVolume);
            if (context is not global::Android.App.Activity)
                intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private AudioDeviceInfo? FindActiveWiredOutput(AudioDeviceInfo[] outputDevices)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && _mediaAudioAttributes is not null)
            {
                var wired = FindRoutedWiredApi33();
                if (wired is not null) return wired;
            }
            foreach (var d in outputDevices)
            {
                if (d is not null && d.IsSink && IsWiredOrUsbOutputType(d.Type))
                    return d;
            }
        }
        catch { }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private AudioDeviceInfo? FindRoutedWiredApi33()
    {
        try
        {
            if (_audioManager is null || _mediaAudioAttributes is null) return null;
            var routed = _audioManager.GetAudioDevicesForAttributes(_mediaAudioAttributes);
            if (routed is not { Count: > 0 }) return null;
            foreach (var d in routed)
            {
                if (d is not null && IsWiredOrUsbOutputType(d.Type))
                    return d;
            }
        }
        catch { }
        return null;
    }

    private static string ResolveWiredDisplayName(AudioDeviceInfo device)
    {
        try
        {
            var cleaned = CleanCandidateName(device.ProductNameFormatted?.ToString() ?? device.ProductName);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
            return device.Type is AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice
                ? "USB 音频设备"
                : "有线耳机";
        }
        catch
        {
            return "有线耳机";
        }
    }

    private static void CollectConnectedProxyDeviceNames(
        IList<BluetoothDevice>? devices,
        HashSet<string> seenNames,
        List<AudioOutputEntry> sink)
    {
        if (devices is null) return;
        foreach (var bt in devices)
        {
            var name = ReadBluetoothDeviceName(bt);
            if (!string.IsNullOrWhiteSpace(name) && seenNames.Add(name))
                sink.Add(new AudioOutputEntry(name, AudioOutputKind.Bluetooth, IsActive: false));
        }
    }

    private static bool IsWiredOrUsbOutputType(AudioDeviceType type) =>
        IsWiredOutputType(type) || type == AudioDeviceType.UsbDevice;

    private void RegisterReceivers()
    {
        if (_receiver is null) return;
        try
        {
            var filter = new IntentFilter();
            filter.AddAction(BluetoothA2dp.ActionConnectionStateChanged);
            filter.AddAction(BluetoothA2dp.ActionPlayingStateChanged);
            filter.AddAction(ActionA2dpActiveDeviceChanged);
            filter.AddAction(BluetoothAdapter.ActionStateChanged);
            filter.AddAction(BluetoothDevice.ActionNameChanged);
            filter.AddAction(BluetoothDevice.ActionAclConnected);
            filter.AddAction(BluetoothDevice.ActionAclDisconnected);
            filter.AddAction(AudioManager.ActionAudioBecomingNoisy);
            filter.AddAction(AudioManager.ActionScoAudioStateUpdated);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                AddAliasActionApi30(filter);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                AddLeAudioActionApi33(filter);

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                RegisterReceiverApi33(_context, _receiver, filter);
            else
                _context.RegisterReceiver(_receiver, filter);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 注册蓝牙广播监听失败: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static void AddAliasActionApi30(IntentFilter filter) =>
        filter.AddAction(BluetoothDevice.ActionAliasChanged);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static void AddLeAudioActionApi33(IntentFilter filter) =>
        filter.AddAction(BluetoothLeAudio.ActionLeAudioConnectionStateChanged);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static void RegisterReceiverApi33(Context context, BroadcastReceiver receiver, IntentFilter filter) =>
        context.RegisterReceiver(receiver, filter, ReceiverFlags.Exported);

    private void EnsureBluetoothProxies()
    {
        if (_proxiesRequested || _profileListener is null || !HasBluetoothConnectPermission(_context)) return;

        BluetoothAdapter? adapter;
        try
        {
            adapter = _bluetoothManager?.Adapter;
            if (adapter is null || !adapter.IsEnabled) return;
        }
        catch
        {
            return;
        }

        _proxiesRequested = true;
        TryRequestProxy(adapter, ProfileType.A2dp);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            TryRequestHearingAidProxyApi29(adapter);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            TryRequestLeAudioProxyApi33(adapter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private void TryRequestHearingAidProxyApi29(BluetoothAdapter adapter) =>
        TryRequestProxy(adapter, ProfileType.HearingAid);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private void TryRequestLeAudioProxyApi33(BluetoothAdapter adapter) =>
        TryRequestProxy(adapter, ProfileType.LeAudio);

    private void TryRequestProxy(BluetoothAdapter adapter, ProfileType profile)
    {
        if (_profileListener is null) return;
        try
        {
            adapter.GetProfileProxy(_context, _profileListener, profile);
        }
        catch
        {
            // 部分系统裁剪或蓝牙未就绪时忽略
        }
    }

    private (bool Connected, string? DeviceName) ResolveConnectedBluetoothAudio()
    {
        if (_audioManager is null)
            return (false, null);

        try
        {
            var outputDevices = _audioManager.GetDevices(GetDevicesTargets.Outputs) ?? Array.Empty<AudioDeviceInfo>();
            var bluetoothDevice = FindActiveBluetoothOutput(outputDevices);
            if (bluetoothDevice is null)
                return (false, null);

            var name = ResolveDeviceDisplayName(bluetoothDevice);
            return (true, string.IsNullOrWhiteSpace(name) ? "蓝牙音频设备" : name);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 解析蓝牙输出设备异常: {ex.Message}");
            return (false, null);
        }
    }

    private AudioDeviceInfo? FindActiveBluetoothOutput(AudioDeviceInfo[] outputDevices)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _mediaAudioAttributes is not null)
        {
            var (handled, device) = TryFindRoutedBluetoothApi33(outputDevices);
            if (handled)
                return device;
        }

        AudioDeviceInfo? mediaBt = null;
        AudioDeviceInfo? scoBt = null;
        var hasWired = false;

        try
        {
            foreach (var d in outputDevices)
            {
                if (d is null || !d.IsSink) continue;

                if (IsMediaBluetoothType(d.Type))
                    mediaBt ??= d;
                else if (d.Type == AudioDeviceType.BluetoothSco)
                    scoBt ??= d;
                else if (IsWiredOutputType(d.Type))
                    hasWired = true;
            }
        }
        catch
        {
            return null;
        }

        var selectedRoute = GetSelectedLiveAudioRoute();
        var routeIsBluetooth = false;
        try
        {
            routeIsBluetooth = selectedRoute?.DeviceType == MediaRouterDeviceType.Bluetooth;
        }
        catch
        {
            routeIsBluetooth = false;
        }

        if (mediaBt is not null)
        {
            if (hasWired && !routeIsBluetooth && !IsLegacyA2dpOn())
                return null;
            return mediaBt;
        }

        if (scoBt is not null && routeIsBluetooth)
            return scoBt;

        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private (bool Handled, AudioDeviceInfo? Device) TryFindRoutedBluetoothApi33(AudioDeviceInfo[] outputDevices)
    {
        try
        {
            if (_audioManager is null || _mediaAudioAttributes is null)
                return (false, null);

            var routed = _audioManager.GetAudioDevicesForAttributes(_mediaAudioAttributes);
            if (routed is not { Count: > 0 })
                return (false, null);

            AudioDeviceInfo? routedBt = null;
            foreach (var d in routed)
            {
                if (d is not null && IsBluetoothOutputType(d.Type))
                {
                    routedBt = d;
                    break;
                }
            }

            if (routedBt is null)
                return (true, null);

            return (true, MatchOutputDevice(outputDevices, routedBt) ?? routedBt);
        }
        catch
        {
            // 部分 ROM 对 GetAudioDevicesForAttributes 兼容不佳时回退到输出设备扫描
            return (false, null);
        }
    }

    private static AudioDeviceInfo? MatchOutputDevice(AudioDeviceInfo[] outputs, AudioDeviceInfo target)
    {
        try
        {
            var targetAddr = TryGetDeviceAddressApi28(target);
            var targetType = target.Type;
            AudioDeviceInfo? byType = null;
            foreach (var d in outputs)
            {
                if (d is null || !d.IsSink || d.Type != targetType) continue;
                var candidateAddr = TryGetDeviceAddressApi28(d);
                if (!string.IsNullOrWhiteSpace(targetAddr)
                    && string.Equals(candidateAddr, targetAddr, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
                byType ??= d;
            }
            return byType;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetDeviceAddressApi28(AudioDeviceInfo device)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return null;
        return ReadDeviceAddressApi28(device);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private static string? ReadDeviceAddressApi28(AudioDeviceInfo device) => device.Address;

    private string? ResolveDeviceDisplayName(AudioDeviceInfo device)
    {
        string? productName = null;
        try
        {
            productName = CleanCandidateName(device.ProductNameFormatted?.ToString() ?? device.ProductName);
        }
        catch
        {
            productName = null;
        }

        if (HasBluetoothConnectPermission(_context))
        {
            var btName = ResolveViaBluetoothStack(device, productName);
            if (!string.IsNullOrWhiteSpace(btName))
                return btName;
        }

        if (!string.IsNullOrWhiteSpace(productName))
            return productName;

        try
        {
            var route = GetSelectedLiveAudioRoute();
            if (route is not null && route.DeviceType == MediaRouterDeviceType.Bluetooth)
            {
                var routeName = CleanCandidateName(route.GetName(_context) ?? route.Name);
                if (!string.IsNullOrWhiteSpace(routeName))
                    return routeName;
            }
        }
        catch
        {
            // 部分系统在读取 MediaRouter 路由名时可能抛出异常，忽略即可
        }

        return null;
    }

    private string? ResolveViaBluetoothStack(AudioDeviceInfo device, string? productName)
    {
        try
        {
            var adapter = _bluetoothManager?.Adapter;
            if (adapter is null || !adapter.IsEnabled)
                return null;

            var address = TryGetDeviceAddressApi28(device);
            if (!string.IsNullOrWhiteSpace(address) && BluetoothAdapter.CheckBluetoothAddress(address))
            {
                var remote = adapter.GetRemoteDevice(address);
                var directName = ReadBluetoothDeviceName(remote);
                if (!string.IsNullOrWhiteSpace(directName))
                    return directName;
            }

            var proxyDevices = GetConnectedDevicesForAudioType(device.Type);
            if (proxyDevices is { Count: > 0 })
            {
                var matched = MatchBluetoothDevice(proxyDevices, address, productName);
                var proxyName = ReadBluetoothDeviceName(matched ?? proxyDevices[0]);
                if (!string.IsNullOrWhiteSpace(proxyName))
                    return proxyName;
            }

            if (!string.IsNullOrWhiteSpace(productName))
            {
                var bonded = adapter.BondedDevices;
                if (bonded is not null)
                {
                    foreach (var bt in bonded)
                    {
                        if (string.Equals(bt?.Name?.Trim(), productName, StringComparison.OrdinalIgnoreCase))
                        {
                            var aliasOrName = ReadBluetoothDeviceName(bt);
                            if (!string.IsNullOrWhiteSpace(aliasOrName))
                                return aliasOrName;
                        }
                    }
                }
            }
        }
        catch
        {
            // 权限撤销或系统蓝牙服务暂不可用时静默回退
        }

        return null;
    }

    private IList<BluetoothDevice>? GetConnectedDevicesForAudioType(AudioDeviceType type)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && (type == AudioDeviceType.BleHeadset
                    || type == AudioDeviceType.BleSpeaker
                    || type == AudioDeviceType.BleBroadcast))
            {
                return GetLeAudioConnectedDevicesApi33(_leAudioProxy);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(29) && type == AudioDeviceType.HearingAid)
                return GetHearingAidConnectedDevicesApi29(_hearingAidProxy);

            if (type == AudioDeviceType.BluetoothA2dp)
                return (_a2dpProxy as BluetoothA2dp)?.ConnectedDevices;
        }
        catch
        {
            // 忽略跨进程代理异常
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static IList<BluetoothDevice>? GetLeAudioConnectedDevicesApi33(IBluetoothProfile? proxy) =>
        (proxy as BluetoothLeAudio)?.ConnectedDevices;

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static IList<BluetoothDevice>? GetHearingAidConnectedDevicesApi29(IBluetoothProfile? proxy) =>
        (proxy as BluetoothHearingAid)?.ConnectedDevices;

    private static BluetoothDevice? MatchBluetoothDevice(
        IList<BluetoothDevice> candidates,
        string? address,
        string? productName)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            foreach (var bt in candidates)
            {
                if (bt is not null && string.Equals(bt.Address, address, StringComparison.OrdinalIgnoreCase))
                    return bt;
            }
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            foreach (var bt in candidates)
            {
                if (bt is not null && string.Equals(bt.Name?.Trim(), productName, StringComparison.OrdinalIgnoreCase))
                    return bt;
            }
        }

        return null;
    }

    private static string? ReadBluetoothDeviceName(BluetoothDevice? btDevice)
    {
        if (btDevice is null) return null;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var alias = ReadBluetoothAliasApi30(btDevice);
                if (!string.IsNullOrWhiteSpace(alias))
                    return alias.Trim();
            }
            var name = btDevice.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static string? ReadBluetoothAliasApi30(BluetoothDevice btDevice) => btDevice.Alias;

    private MediaRouter.RouteInfo? GetSelectedLiveAudioRoute()
    {
        try
        {
            return _mediaRouter?.GetSelectedRoute(MediaRouteType.LiveAudio);
        }
        catch
        {
            return null;
        }
    }

    private bool IsLegacyA2dpOn()
    {
        try
        {
#pragma warning disable CA1422, CS0618
            return _audioManager?.BluetoothA2dpOn == true;
#pragma warning restore CA1422, CS0618
        }
        catch
        {
            return false;
        }
    }

    private static string? CleanCandidateName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (string.Equals(trimmed, Build.Model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Build.Product, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Phone", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Speaker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Bluetooth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "蓝牙", StringComparison.Ordinal))
        {
            return null;
        }
        return trimmed;
    }

    private static bool IsMediaBluetoothType(AudioDeviceType type) =>
        type == AudioDeviceType.BluetoothA2dp
        || (OperatingSystem.IsAndroidVersionAtLeast(28) && type == AudioDeviceType.HearingAid)
        || (OperatingSystem.IsAndroidVersionAtLeast(31)
            && (type == AudioDeviceType.BleHeadset || type == AudioDeviceType.BleSpeaker))
        || (OperatingSystem.IsAndroidVersionAtLeast(33) && type == AudioDeviceType.BleBroadcast);

    private static bool IsBluetoothOutputType(AudioDeviceType type) =>
        IsMediaBluetoothType(type) || type == AudioDeviceType.BluetoothSco;

    private static bool IsWiredOutputType(AudioDeviceType type) =>
        type == AudioDeviceType.WiredHeadphones
        || type == AudioDeviceType.WiredHeadset
        || type == AudioDeviceType.UsbHeadset;

    private void OnProfileConnected(ProfileType profile, IBluetoothProfile? proxy)
    {
        try
        {
            if (profile == ProfileType.A2dp)
                _a2dpProxy = proxy;
            else if (OperatingSystem.IsAndroidVersionAtLeast(29) && IsHearingAidProfileApi29(profile))
                _hearingAidProxy = proxy;
            else if (OperatingSystem.IsAndroidVersionAtLeast(33) && IsLeAudioProfileApi33(profile))
                _leAudioProxy = proxy;
            Refresh();
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] OnProfileConnected 异常: {ex.Message}");
        }
    }

    private void OnProfileDisconnected(ProfileType profile)
    {
        try
        {
            if (profile == ProfileType.A2dp)
                _a2dpProxy = null;
            else if (OperatingSystem.IsAndroidVersionAtLeast(29) && IsHearingAidProfileApi29(profile))
                _hearingAidProxy = null;
            else if (OperatingSystem.IsAndroidVersionAtLeast(33) && IsLeAudioProfileApi33(profile))
                _leAudioProxy = null;
            Refresh();
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] OnProfileDisconnected 异常: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static bool IsHearingAidProfileApi29(ProfileType profile) => profile == ProfileType.HearingAid;

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static bool IsLeAudioProfileApi33(ProfileType profile) => profile == ProfileType.LeAudio;

    private void CloseProxies()
    {
        try
        {
            var adapter = _bluetoothManager?.Adapter;
            if (adapter is not null)
            {
                TryCloseProxy(adapter, ProfileType.A2dp, ref _a2dpProxy);
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    CloseHearingAidProxyApi29(adapter);
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    CloseLeAudioProxyApi33(adapter);
            }
        }
        catch
        {
            // 忽略蓝牙服务已断开的异常
        }
        _proxiesRequested = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private void CloseHearingAidProxyApi29(BluetoothAdapter adapter) =>
        TryCloseProxy(adapter, ProfileType.HearingAid, ref _hearingAidProxy);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [global::System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private void CloseLeAudioProxyApi33(BluetoothAdapter adapter) =>
        TryCloseProxy(adapter, ProfileType.LeAudio, ref _leAudioProxy);

    private static void TryCloseProxy(BluetoothAdapter adapter, ProfileType profile, ref IBluetoothProfile? proxy)
    {
        if (proxy is null) return;
        try { adapter.CloseProfileProxy(profile, proxy); } catch { }
        proxy = null;
    }

    private void ScheduleDelayedRefresh()
    {
        if (_disposed || _handler is null || _delayedRefreshRunnable is null) return;
        try
        {
            _handler.RemoveCallbacks(_delayedRefreshRunnable);
            _handler.PostDelayed(_delayedRefreshRunnable, 250);
        }
        catch
        {
            // 忽略 Handler 已退出等异常
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_delayedRefreshRunnable is not null)
                _handler?.RemoveCallbacks(_delayedRefreshRunnable);
        }
        catch { }
        try
        {
            if (_audioCallback is not null)
                _audioManager?.UnregisterAudioDeviceCallback(_audioCallback);
        }
        catch { }
        try
        {
            if (_routeCallback is not null)
                _mediaRouter?.RemoveCallback(_routeCallback);
        }
        catch { }
        try
        {
            if (_receiver is not null)
                _context.UnregisterReceiver(_receiver);
        }
        catch { }
        CloseProxies();

        try { _audioCallback?.Dispose(); } catch { }
        try { _routeCallback?.Dispose(); } catch { }
        try { _receiver?.Dispose(); } catch { }
        try { _profileListener?.Dispose(); } catch { }
        try { _delayedRefreshRunnable?.Dispose(); } catch { }
        try { _mediaAudioAttributes?.Dispose(); } catch { }
    }

    private sealed class DeviceCallback : AudioDeviceCallback
    {
        private readonly WeakReference<BluetoothAudioMonitor>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DeviceCallback))]
        public DeviceCallback()
        {
        }

        public DeviceCallback(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public DeviceCallback(BluetoothAudioMonitor owner) =>
            _owner = new WeakReference<BluetoothAudioMonitor>(owner);

        private BluetoothAudioMonitor? ResolveOwner() =>
            _owner is not null && _owner.TryGetTarget(out var target) ? target : null;

        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }
    }

    private sealed class RouteCallback : MediaRouter.SimpleCallback
    {
        private readonly WeakReference<BluetoothAudioMonitor>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RouteCallback))]
        public RouteCallback()
        {
        }

        public RouteCallback(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public RouteCallback(BluetoothAudioMonitor owner) =>
            _owner = new WeakReference<BluetoothAudioMonitor>(owner);

        private BluetoothAudioMonitor? ResolveOwner() =>
            _owner is not null && _owner.TryGetTarget(out var target) ? target : null;

        public override void OnRouteSelected(MediaRouter? router, MediaRouteType type, MediaRouter.RouteInfo? info)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }

        public override void OnRouteUnselected(MediaRouter? router, MediaRouteType type, MediaRouter.RouteInfo? info)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }

        public override void OnRouteChanged(MediaRouter? router, MediaRouter.RouteInfo? info)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }

        public override void OnRouteAdded(MediaRouter? router, MediaRouter.RouteInfo? info)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }

        public override void OnRouteRemoved(MediaRouter? router, MediaRouter.RouteInfo? info)
        {
            try { ResolveOwner()?.Refresh(); } catch { }
        }
    }

    private sealed class BtBroadcastReceiver : BroadcastReceiver
    {
        private readonly WeakReference<BluetoothAudioMonitor>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(BtBroadcastReceiver))]
        public BtBroadcastReceiver()
        {
        }

        public BtBroadcastReceiver(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public BtBroadcastReceiver(BluetoothAudioMonitor owner) =>
            _owner = new WeakReference<BluetoothAudioMonitor>(owner);

        public override void OnReceive(Context? context, Intent? intent)
        {
            try
            {
                if (_owner is null || !_owner.TryGetTarget(out var owner)) return;
                if (string.Equals(intent?.Action, BluetoothAdapter.ActionStateChanged, StringComparison.Ordinal))
                {
                    var state = intent?.GetIntExtra(BluetoothAdapter.ExtraState, (int)global::Android.Bluetooth.State.Off);
                    if (state == (int)global::Android.Bluetooth.State.Off)
                        owner.CloseProxies();
                }
                owner.Refresh();
                owner.ScheduleDelayedRefresh();
            }
            catch
            {
                // 广播回调内吞掉异常以免系统崩溃
            }
        }
    }

    private sealed class ProfileListener : Java.Lang.Object, IBluetoothProfileServiceListener
    {
        private readonly WeakReference<BluetoothAudioMonitor>? _owner;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ProfileListener))]
        public ProfileListener()
        {
        }

        public ProfileListener(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public ProfileListener(BluetoothAudioMonitor owner) =>
            _owner = new WeakReference<BluetoothAudioMonitor>(owner);

        public void OnServiceConnected(ProfileType profile, IBluetoothProfile? proxy)
        {
            try
            {
                if (_owner is not null && _owner.TryGetTarget(out var owner))
                    owner.OnProfileConnected(profile, proxy);
            }
            catch { }
        }

        public void OnServiceDisconnected(ProfileType profile)
        {
            try
            {
                if (_owner is not null && _owner.TryGetTarget(out var owner))
                    owner.OnProfileDisconnected(profile);
            }
            catch { }
        }
    }
}
