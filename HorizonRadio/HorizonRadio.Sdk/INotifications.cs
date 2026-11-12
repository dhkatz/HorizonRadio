namespace HorizonRadio.Sdk;

/// <summary>How a notification reads: news, a success, something to look at, or a failure.</summary>
public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Tells the user something now, outside any page: a toast in the window, a line in a headless
/// instance's log. For what needs their attention (a step only they can take), not for progress.
/// </summary>
public interface INotifications
{
    /// <param name="duration">How long it stays; null for the host's usual time. It can be
    /// dismissed sooner.</param>
    void Show(string title, string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);
}
