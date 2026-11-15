using Android.App;
using Android.Content;
using Android.Service.Notification;

namespace Musebase.Android.Services;

/// <summary>
/// 声明为系统通知监听服务，使应用有权通过 <see cref="global::Android.Media.Session.MediaSessionManager"/>
/// 读取当前正在播放的音乐会话、元数据与播放进度。
/// </summary>
[Service(
    Name = "app.lyrx.android.MediaListenerService",
    Label = "@string/media_listener_label",
    Exported = true,
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE")]
[IntentFilter(["android.service.notification.NotificationListenerService"])]
public sealed class MediaListenerService : NotificationListenerService
{
    public static event Action<bool>? ListenerConnectionChanged;
    public static bool IsConnected { get; private set; }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        IsConnected = true;
        AndroidLog.Write("[media] NotificationListenerService 已连接");
        ListenerConnectionChanged?.Invoke(true);
    }

    public override void OnListenerDisconnected()
    {
        base.OnListenerDisconnected();
        IsConnected = false;
        AndroidLog.Write("[media] NotificationListenerService 已断开");
        ListenerConnectionChanged?.Invoke(false);
    }
}
