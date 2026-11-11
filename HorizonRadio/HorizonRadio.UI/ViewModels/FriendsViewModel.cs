using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The Friends page: add a friend (show a code, or type theirs) and see who's online and what
/// they're listening to. Follows <see cref="Friends"/>, whose events arrive off the UI thread.
/// </summary>
public sealed partial class FriendsViewModel : ViewModelBase
{
    private readonly Core.Friends.Friends _friends;
    private readonly DispatcherTimer _clock;

    public ObservableCollection<FriendRow> Friends { get; } = new();

    [ObservableProperty] private string myName = "";
    [ObservableProperty] private string friendCode = "";
    [ObservableProperty] private string? inviteCode;
    [ObservableProperty] private string inviteExpires = "";
    [ObservableProperty] private string? status;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasFriends;

    public bool IsShowingCode => InviteCode != null;
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public FriendsViewModel(Core.Friends.Friends friends)
    {
        _friends = friends;
        _friends.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Refresh());
        _clock.Start();
        MyName = friends.MyName;
        Refresh();
    }

    /// <summary>Designer ctor.</summary>
    public FriendsViewModel() : this(new Core.Friends.Friends(SettingsStore.CreateInMemory(),
        _ => Task.FromException<IPeerNetwork>(new FileNotFoundException("designer"))))
    {
    }

    partial void OnInviteCodeChanged(string? value) => OnPropertyChanged(nameof(IsShowingCode));

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private void Refresh()
    {
        Status = _friends.Status;
        if (_friends.Invite is { } invite && invite.Expires > DateTimeOffset.UtcNow)
        {
            InviteCode = invite.Code.ToString();
            var minutes = Math.Max(1, (int)Math.Ceiling((invite.Expires - DateTimeOffset.UtcNow).TotalMinutes));
            InviteExpires = $"Good for {minutes} more minute{(minutes == 1 ? "" : "s")}, and for one try.";
        }
        else
        {
            InviteCode = null;
        }

        var rows = _friends.All.Select(f => new FriendRow(f, RemoveAsync)).ToList();
        HasFriends = rows.Count > 0;
        if (!rows.Select(r => r.Key).SequenceEqual(Friends.Select(r => r.Key)))
        {
            Friends.Clear();
            foreach (var row in rows) Friends.Add(row);
        }
    }

    [RelayCommand]
    private async Task ShowCodeAsync() => await RunAsync(async () =>
    {
        await _friends.InviteAsync();
        Refresh();
    });

    [RelayCommand]
    private void HideCode()
    {
        _friends.CancelInvite();
        Refresh();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!PairingCode.TryParse(FriendCode, out var code))
        {
            Status = "That isn't a friend code: it's 12 letters and numbers, like ABCD-EFGH-JKLM.";
            return;
        }
        await RunAsync(async () =>
        {
            Status = "Looking for your friend…";
            await _friends.AcceptAsync(code);
            FriendCode = "";
            Refresh();
        });
    }

    [RelayCommand]
    private void Rename()
    {
        _friends.Rename(MyName);
        MyName = _friends.MyName;
    }

    private async Task RemoveAsync(FriendRow row) => await RunAsync(async () =>
    {
        await _friends.RemoveAsync(row.UserId);
        Refresh();
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is PairingException or PeerNetworkException or FileNotFoundException or TimeoutException or IOException)
        {
            Status = ex is FileNotFoundException
                ? "The peer-to-peer helper isn't installed with this build, so friends can't connect."
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One friend in the list.</summary>
public sealed class FriendRow : ViewModelBase
{
    public FriendRow(FriendStatus friend, Func<FriendRow, Task> remove)
    {
        UserId = friend.UserId;
        Name = friend.Name;
        IsOnline = friend.IsOnline;
        State = Describe(friend);
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public string UserId { get; }
    public string Name { get; }
    public bool IsOnline { get; }
    public double IconOpacity => IsOnline ? 1.0 : 0.4;
    public string State { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    public string Key => $"{UserId}|{Name}|{State}";

    private static string Describe(FriendStatus f)
    {
        if (f.IsOnline)
        {
            return f.Listening is { } l
                ? $"Listening to {l.Title}{(string.IsNullOrWhiteSpace(l.Artist) ? "" : " · " + l.Artist)} ({l.Source})"
                : "Online";
        }
        return f.LastSeen is { } seen ? $"Offline · last seen {Ago(seen)}" : "Offline";
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return when.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }
}
