using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal enum UserNotificationState
{
    Unknown = 0, NotPresent = 1, Busy = 2, RunningDirect3DFullScreen = 3,
    PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7
}

[Flags]
internal enum NotificationMuteReason { None = 0, Game = 1, FullScreen = 2 }

internal static class GameModeDetector
{
    public static UserNotificationState ReadState()
    {
        if (!OperatingSystem.IsWindows()) return UserNotificationState.Unknown;
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 ? state : UserNotificationState.Unknown;
        }
        catch (DllNotFoundException) { return UserNotificationState.Unknown; }
        catch (EntryPointNotFoundException) { return UserNotificationState.Unknown; }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);
}

internal sealed class GameModePolicy
{
    public NotificationMuteReason Reason { get; private set; }
    public bool IsMuted => Reason != NotificationMuteReason.None;

    public bool Update(bool muteDuringGames, bool muteDuringFullScreen, UserNotificationState state)
    {
        var reason = NotificationMuteReason.None;
        if (muteDuringGames && state == UserNotificationState.RunningDirect3DFullScreen) reason |= NotificationMuteReason.Game;
        if (muteDuringFullScreen && state is UserNotificationState.Busy or UserNotificationState.RunningDirect3DFullScreen)
            reason |= NotificationMuteReason.FullScreen;
        if (reason == Reason) return false;
        Reason = reason;
        return true;
    }
}
