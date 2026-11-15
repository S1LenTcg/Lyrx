using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;

namespace Musebase.Android.Services;

/// <summary>
/// 监听系统当前连接的蓝牙音频输出设备（A2DP / LE Audio / 助听器 / 活跃蓝牙路由），
/// 并在设备连接、断开、重命名或切换输出路由时通知界面更新底部设备名称。
/// </summary>
public sealed class BluetoothAudioMonitor : IDisposable
{
    private const string ActionA2dpActiveDeviceChanged = "android.bluetooth.a2dp.profile.action.ACTIVE_DEVICE_CHANGED";

    private readonly Context _context;
    private readonly Handler _handler;
    private readonly AudioManager? _audioManager;
    private readonly MediaRouter? _mediaRouter;
    private readonly BluetoothManager? _bluetoothManager;
    private readonly AudioAttributes _mediaAudioAttributes;

    private readonly DeviceCallback _audioCallback;
    private readonly RouteCallback _routeCallback;
    private readonly BtBroadcastReceiver _receiver;
    private readonly ProfileListener _profileListener;
    private readonly Java.Lang.Runnable _delayedRefreshRunnable;

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
        _handler = new Handler(Looper.MainLooper!);
        _audioManager = _context.GetSystemService(Context.AudioService) as AudioManager;
        _mediaRouter = _context.GetSystemService(Context.MediaRouterService) as MediaRouter;
        _bluetoothManager = _context.GetSystemService(Context.BluetoothService) as BluetoothManager;

        _mediaAudioAttributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;

        _audioCallback = new DeviceCallback(this);
        _routeCallback = new RouteCallback(this);
        _receiver = new BtBroadcastReceiver(this);
        _profileListener = new ProfileListener(this);
        _delayedRefreshRunnable = new Java.Lang.Runnable(Refresh);
    }

    public static bool HasBluetoothConnectPermission(Context context) =>
        !OperatingSystem.IsAndroidVersionAtLeast(31)
        || context.CheckSelfPermission(global::Android.Manifest.Permission.BluetoothConnect) == Permission.Granted;

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        try
        {
            _audioManager?.RegisterAudioDeviceCallback(_audioCallback, _handler);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 注册 AudioDeviceCallback 失败: {ex.Message}");
        }

        try
        {
            _mediaRouter?.AddCallback(
                MediaRouteType.LiveAudio,
                _routeCallback,
                MediaRouterCallbackFlags.UnfilteredEvents);
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

    private void RegisterReceivers()
    {
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
                filter.AddAction(BluetoothDevice.ActionAliasChanged);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                filter.AddAction(BluetoothLeAudio.ActionLeAudioConnectionStateChanged);

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                _context.RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
            else
                _context.RegisterReceiver(_receiver, filter);
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"[bluetooth] 注册蓝牙广播监听失败: {ex.Message}");
        }
    }

    private void EnsureBluetoothProxies()
    {
        if (_proxiesRequested || !HasBluetoothConnectPermission(_context)) return;

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
            TryRequestProxy(adapter, ProfileType.HearingAid);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            TryRequestProxy(adapter, ProfileType.LeAudio);
    }

    private void TryRequestProxy(BluetoothAdapter adapter, ProfileType profile)
    {
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

        AudioDeviceInfo[] outputDevices;
        try
        {
            outputDevices = _audioManager.GetDevices(GetDevicesTargets.Outputs) ?? Array.Empty<AudioDeviceInfo>();
        }
        catch
        {
            return (false, null);
        }

        var bluetoothDevice = FindActiveBluetoothOutput(outputDevices);
        if (bluetoothDevice is null)
            return (false, null);

        var name = ResolveDeviceDisplayName(bluetoothDevice);
        return (true, string.IsNullOrWhiteSpace(name) ? "蓝牙音频设备" : name);
    }

    private AudioDeviceInfo? FindActiveBluetoothOutput(AudioDeviceInfo[] outputDevices)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            try
            {
                var routed = _audioManager!.GetAudioDevicesForAttributes(_mediaAudioAttributes);
                if (routed is { Count: > 0 })
                {
                    AudioDeviceInfo? routedBt = null;
                    foreach (var d in routed)
                    {
                        if (IsBluetoothOutputType(d.Type))
                        {
                            routedBt = d;
                            break;
                        }
                    }

                    if (routedBt is null)
                        return null;

                    return MatchOutputDevice(outputDevices, routedBt) ?? routedBt;
                }
            }
            catch
            {
                // 部分 ROM 对 GetAudioDevicesForAttributes 兼容不佳时回退到输出设备扫描
            }
        }

        AudioDeviceInfo? mediaBt = null;
        AudioDeviceInfo? scoBt = null;
        var hasWired = false;

        foreach (var d in outputDevices)
        {
            if (!d.IsSink) continue;

            if (IsMediaBluetoothType(d.Type))
                mediaBt ??= d;
            else if (d.Type == AudioDeviceType.BluetoothSco)
                scoBt ??= d;
            else if (IsWiredOutputType(d.Type))
                hasWired = true;
        }

        var selectedRoute = GetSelectedLiveAudioRoute();
        var routeIsBluetooth = selectedRoute?.DeviceType == MediaRouterDeviceType.Bluetooth;

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

    private static AudioDeviceInfo? MatchOutputDevice(AudioDeviceInfo[] outputs, AudioDeviceInfo target)
    {
        var targetAddr = OperatingSystem.IsAndroidVersionAtLeast(28) ? target.Address : null;
        AudioDeviceInfo? byType = null;
        foreach (var d in outputs)
        {
            if (!d.IsSink || d.Type != target.Type) continue;
            if (!string.IsNullOrWhiteSpace(targetAddr)
                && OperatingSystem.IsAndroidVersionAtLeast(28)
                && string.Equals(d.Address, targetAddr, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
            byType ??= d;
        }
        return byType;
    }

    private string? ResolveDeviceDisplayName(AudioDeviceInfo device)
    {
        var productName = CleanCandidateName(device.ProductNameFormatted?.ToString() ?? device.ProductName);

        if (HasBluetoothConnectPermission(_context))
        {
            var btName = ResolveViaBluetoothStack(device, productName);
            if (!string.IsNullOrWhiteSpace(btName))
                return btName;
        }

        if (!string.IsNullOrWhiteSpace(productName))
            return productName;

        var route = GetSelectedLiveAudioRoute();
        if (route is not null && route.DeviceType == MediaRouterDeviceType.Bluetooth)
        {
            var routeName = CleanCandidateName(route.GetName(_context) ?? route.Name);
            if (!string.IsNullOrWhiteSpace(routeName))
                return routeName;
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

            var address = OperatingSystem.IsAndroidVersionAtLeast(28) ? device.Address : null;
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
                        if (string.Equals(bt.Name?.Trim(), productName, StringComparison.OrdinalIgnoreCase))
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
                return (_leAudioProxy as BluetoothLeAudio)?.ConnectedDevices;
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(29) && type == AudioDeviceType.HearingAid)
                return (_hearingAidProxy as BluetoothHearingAid)?.ConnectedDevices;

            if (type == AudioDeviceType.BluetoothA2dp)
                return (_a2dpProxy as BluetoothA2dp)?.ConnectedDevices;
        }
        catch
        {
            // 忽略跨进程代理异常
        }
        return null;
    }

    private static BluetoothDevice? MatchBluetoothDevice(
        IList<BluetoothDevice> candidates,
        string? address,
        string? productName)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            foreach (var bt in candidates)
            {
                if (string.Equals(bt.Address, address, StringComparison.OrdinalIgnoreCase))
                    return bt;
            }
        }

        if (!string.IsNullOrWhiteSpace(productName))
        {
            foreach (var bt in candidates)
            {
                if (string.Equals(bt.Name?.Trim(), productName, StringComparison.OrdinalIgnoreCase))
                    return bt;
            }
        }

        return null;
    }

    private static string? ReadBluetoothDeviceName(BluetoothDevice? btDevice)
    {
        if (btDevice is null) return null;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var alias = btDevice.Alias;
            if (!string.IsNullOrWhiteSpace(alias))
                return alias.Trim();
        }
        var name = btDevice.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

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
        if (profile == ProfileType.A2dp)
            _a2dpProxy = proxy;
        else if (OperatingSystem.IsAndroidVersionAtLeast(29) && profile == ProfileType.HearingAid)
            _hearingAidProxy = proxy;
        else if (OperatingSystem.IsAndroidVersionAtLeast(33) && profile == ProfileType.LeAudio)
            _leAudioProxy = proxy;
        Refresh();
    }

    private void OnProfileDisconnected(ProfileType profile)
    {
        if (profile == ProfileType.A2dp)
            _a2dpProxy = null;
        else if (OperatingSystem.IsAndroidVersionAtLeast(29) && profile == ProfileType.HearingAid)
            _hearingAidProxy = null;
        else if (OperatingSystem.IsAndroidVersionAtLeast(33) && profile == ProfileType.LeAudio)
            _leAudioProxy = null;
        Refresh();
    }

    private void CloseProxies()
    {
        var adapter = _bluetoothManager?.Adapter;
        if (adapter is not null)
        {
            TryCloseProxy(adapter, ProfileType.A2dp, ref _a2dpProxy);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                TryCloseProxy(adapter, ProfileType.HearingAid, ref _hearingAidProxy);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                TryCloseProxy(adapter, ProfileType.LeAudio, ref _leAudioProxy);
        }
        _proxiesRequested = false;
    }

    private static void TryCloseProxy(BluetoothAdapter adapter, ProfileType profile, ref IBluetoothProfile? proxy)
    {
        if (proxy is null) return;
        try { adapter.CloseProfileProxy(profile, proxy); } catch { }
        proxy = null;
    }

    private void ScheduleDelayedRefresh()
    {
        if (_disposed) return;
        _handler.RemoveCallbacks(_delayedRefreshRunnable);
        _handler.PostDelayed(_delayedRefreshRunnable, 250);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _handler.RemoveCallbacks(_delayedRefreshRunnable);
        try { _audioManager?.UnregisterAudioDeviceCallback(_audioCallback); } catch { }
        try { _mediaRouter?.RemoveCallback(_routeCallback); } catch { }
        try { _context.UnregisterReceiver(_receiver); } catch { }
        CloseProxies();

        _audioCallback.Dispose();
        _routeCallback.Dispose();
        _receiver.Dispose();
        _profileListener.Dispose();
        _delayedRefreshRunnable.Dispose();
        _mediaAudioAttributes.Dispose();
    }

    private sealed class DeviceCallback(BluetoothAudioMonitor owner) : AudioDeviceCallback
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) =>
            owner.Refresh();

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) =>
            owner.Refresh();
    }

    private sealed class RouteCallback(BluetoothAudioMonitor owner) : MediaRouter.SimpleCallback
    {
        public override void OnRouteSelected(MediaRouter? router, MediaRouteType type, MediaRouter.RouteInfo? info) =>
            owner.Refresh();

        public override void OnRouteUnselected(MediaRouter? router, MediaRouteType type, MediaRouter.RouteInfo? info) =>
            owner.Refresh();

        public override void OnRouteChanged(MediaRouter? router, MediaRouter.RouteInfo? info) =>
            owner.Refresh();

        public override void OnRouteAdded(MediaRouter? router, MediaRouter.RouteInfo? info) =>
            owner.Refresh();

        public override void OnRouteRemoved(MediaRouter? router, MediaRouter.RouteInfo? info) =>
            owner.Refresh();
    }

    private sealed class BtBroadcastReceiver(BluetoothAudioMonitor owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (string.Equals(intent?.Action, BluetoothAdapter.ActionStateChanged, StringComparison.Ordinal))
            {
                var state = intent?.GetIntExtra(BluetoothAdapter.ExtraState, (int)global::Android.Bluetooth.State.Off);
                if (state == (int)global::Android.Bluetooth.State.Off)
                    owner.CloseProxies();
            }
            owner.Refresh();
            owner.ScheduleDelayedRefresh();
        }
    }

    private sealed class ProfileListener(BluetoothAudioMonitor owner) : Java.Lang.Object, IBluetoothProfileServiceListener
    {
        public void OnServiceConnected(ProfileType profile, IBluetoothProfile? proxy) =>
            owner.OnProfileConnected(profile, proxy);

        public void OnServiceDisconnected(ProfileType profile) =>
            owner.OnProfileDisconnected(profile);
    }
}
