using System;
using Avalonia.Threading;
using HorizonRadio.Sdk;
using ShadUI;

namespace HorizonRadio.UI.Services;

/// <summary>The window's notifications: a toast, shown on the UI thread, dismissed by a click.</summary>
internal sealed class ToastNotifications(ToastManager toasts) : INotifications
{
    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            var toast = toasts.CreateToast(title)
                .WithContent(message)
                .WithDelay((int)(duration ?? TimeSpan.FromSeconds(8)).TotalSeconds)
                .DismissOnClick();
            switch (kind)
            {
                case NotificationKind.Success: toast.ShowSuccess(); break;
                case NotificationKind.Warning: toast.ShowWarning(); break;
                case NotificationKind.Error: toast.ShowError(); break;
                default: toast.ShowInfo(); break;
            }
        });
}
