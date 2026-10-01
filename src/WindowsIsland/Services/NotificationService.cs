using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsIsland.Services;

internal enum NotificationAccess { NeedsRegistration, Waiting, Allowed, Denied, Unavailable }

internal sealed class NotificationService(DispatcherQueue dispatcher) : IDisposable
{
    private readonly NotificationTracker _tracker = new();
    private UserNotificationListener? _listener;
    private bool _busy, _disposed, _subscribed;
    public NotificationAccess Access { get; private set; } = NotificationAccess.Waiting;
    public event Action<IslandNotification>? Received;
    public event Action<NotificationAccess>? AccessChanged;

    // Called on the UI thread so Windows can present its own consent dialog.
    public async Task InitializeAsync(bool requestAccess = true)
    {
        if (_disposed || _busy) return;
        _busy = true;
        try
        {
            try { _ = Package.Current.Id; }
            catch (InvalidOperationException) { SetAccess(NotificationAccess.NeedsRegistration); return; }
            _listener ??= UserNotificationListener.Current;
            var status = _listener.GetAccessStatus();
            if (requestAccess && status == UserNotificationListenerAccessStatus.Unspecified)
                status = await _listener.RequestAccessAsync();
            if (_disposed) return;
            SetAccess(Map(status));
            if (Access != NotificationAccess.Allowed) return;
            // Seed the baseline before subscribing; old Action Center items must not pop up.
            _tracker.Reset();
            await ReadAsync();
            if (_disposed || _subscribed) return;
            try
            {
                _listener.NotificationChanged += OnNotificationChanged;
                _subscribed = true;
            }
            catch (Exception)
            {
                // Some desktop hosts do not deliver foreground events. The 1s poll remains active.
            }
        }
        catch (Exception) { if (!_disposed) SetAccess(NotificationAccess.Unavailable); }
        finally { _busy = false; }
    }

    public async Task PollAsync()
    {
        if (_disposed || _busy || _listener is null) return;
        _busy = true;
        try
        {
            SetAccess(Map(_listener.GetAccessStatus()));
            if (Access == NotificationAccess.Allowed) await ReadAsync();
        }
        catch (Exception) { if (!_disposed) SetAccess(NotificationAccess.Unavailable); }
        finally { _busy = false; }
    }

    private async Task ReadAsync()
    {
        var notifications = await _listener!.GetNotificationsAsync(NotificationKinds.Toast);
        if (_disposed) return;
        // Access may have been revoked while the OS query was outstanding.
        SetAccess(Map(_listener.GetAccessStatus()));
        if (Access != NotificationAccess.Allowed) return;
        var snapshot = new List<IslandNotification>();
        foreach (var notification in notifications)
        {
            try
            {
                var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)
                    ?? notification.Notification.Visual.Bindings.FirstOrDefault();
                var text = binding?.GetTextElements().Select(item => item.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
                string app = notification.AppInfo?.DisplayInfo?.DisplayName ?? "通知";
                snapshot.Add(new IslandNotification(notification.Id, notification.CreationTime,
                    app, text.FirstOrDefault() ?? app, string.Join("\n", text.Skip(1))));
            }
            catch (Exception)
            {
                // A removed or malformed notification must not block the other notifications.
            }
        }
        foreach (var notification in _tracker.Update(snapshot))
        {
            if (_disposed) break;
            Received?.Invoke(notification);
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (!_disposed) dispatcher.TryEnqueue(async () => await PollAsync());
    }

    private void SetAccess(NotificationAccess access)
    {
        if (Access == access) return;
        Access = access;
        if (access != NotificationAccess.Allowed) _tracker.Reset();
        AccessChanged?.Invoke(access);
    }

    private static NotificationAccess Map(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => NotificationAccess.Allowed,
        UserNotificationListenerAccessStatus.Denied => NotificationAccess.Denied,
        _ => NotificationAccess.Waiting
    };

    public void Dispose()
    {
        _disposed = true;
        if (_subscribed && _listener is not null) _listener.NotificationChanged -= OnNotificationChanged;
        Received = null; AccessChanged = null;
        _tracker.Reset();
    }
}
