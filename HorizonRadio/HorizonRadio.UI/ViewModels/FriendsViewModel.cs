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
using HorizonRadio.Core.Party;
using HorizonRadio.Core.Settings;

using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Peers;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The Friends page: add a friend (show a code, or type theirs), see who's online and what
/// they're listening to, and host or join a listening party. Follows <see cref="Friends"/> and
/// <see cref="ListeningParty"/>, whose events arrive off the UI thread.
/// </summary>
public sealed partial class FriendsViewModel : ViewModelBase
{
    private readonly Core.Friends.Friends _friends;
    private readonly ListeningParty? _party;
    private readonly IConfirmations _confirm;
    private readonly DispatcherTimer _clock;

    public ObservableCollection<FriendRow> Friends { get; } = new();

    [ObservableProperty] private string myName = "";
    [ObservableProperty] private string friendCode = "";
    [ObservableProperty] private string? inviteCode;
    [ObservableProperty] private string inviteExpires = "";
    [ObservableProperty] private string? status;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasFriends;
    [ObservableProperty] private PartyRole partyRole;
    [ObservableProperty] private string partyTitle = "";
    [ObservableProperty] private string partyDetail = "";
    [ObservableProperty] private string? partyProblem;

    public bool IsShowingCode => InviteCode != null;
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    public bool HasParty => _party != null;
    public bool CanStartParty => PartyRole == PartyRole.None;
    public bool IsHostingParty => PartyRole == PartyRole.Hosting;
    public bool IsInParty => PartyRole is PartyRole.Joining or PartyRole.Guest;
    public bool HasPartyDetail => !string.IsNullOrEmpty(PartyDetail);
    public bool HasPartyProblem => !string.IsNullOrEmpty(PartyProblem);

    public FriendsViewModel(Core.Friends.Friends friends, ListeningParty? party = null,
        IConfirmations? confirmations = null)
    {
        _friends = friends;
        _party = party;
        _confirm = confirmations ?? ProceedWithoutAsking.Instance;
        _friends.Changed += () => Dispatcher.UIThread.Post(Refresh);
        if (_party != null) _party.Changed += () => Dispatcher.UIThread.Post(Refresh);
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

    partial void OnPartyRoleChanged(PartyRole value)
    {
        OnPropertyChanged(nameof(CanStartParty));
        OnPropertyChanged(nameof(IsHostingParty));
        OnPropertyChanged(nameof(IsInParty));
    }

    partial void OnPartyDetailChanged(string value) => OnPropertyChanged(nameof(HasPartyDetail));

    partial void OnPartyProblemChanged(string? value) => OnPropertyChanged(nameof(HasPartyProblem));

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

        var party = _party?.View ?? PartyView.None;
        PartyRole = party.Role;
        PartyProblem = party.Problem;
        var song = party.Song is { } s ? $"{s.Title}{(string.IsNullOrWhiteSpace(s.Artist) ? "" : " · " + s.Artist)}" : null;
        (PartyTitle, PartyDetail) = party.Role switch
        {
            PartyRole.Hosting => ("You're hosting a party.",
                (party.Guests.Count == 0 ? "Nobody has joined yet." : "Listening along: " + string.Join(", ", party.Guests) + ".")
                + (song != null ? $" Playing {song}." : "")),
            PartyRole.Joining => ($"Joining {party.HostName}'s party…", ""),
            PartyRole.Guest => ($"You're listening along with {party.HostName}.",
                song == null ? "Nothing's playing right now." : party.Finding ? $"Finding {song}…" : $"Playing {song}."),
            _ => ("Host a party and your friends can join it to hear what you play, in time with you.", ""),
        };

        var joined = party.Role is PartyRole.Joining or PartyRole.Guest ? party.HostName : null;
        var rows = _friends.All.Select(f => new FriendRow(f, RemoveAsync, _party != null && f.IsHosting && f.Name != joined ? JoinAsync : null)).ToList();
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

    [RelayCommand]
    private async Task StartPartyAsync() => await RunAsync(async () =>
    {
        await _party!.StartHostingAsync();
        Refresh();
    });

    [RelayCommand]
    private async Task EndPartyAsync() => await RunAsync(async () =>
    {
        await _party!.StopHostingAsync();
        Refresh();
    });

    [RelayCommand]
    private async Task LeavePartyAsync() => await RunAsync(async () =>
    {
        await _party!.LeaveAsync();
        Refresh();
    });

    private async Task JoinAsync(FriendRow row) => await RunAsync(async () =>
    {
        await _party!.JoinAsync(row.UserId);
        Refresh();
    });

    private async Task RemoveAsync(FriendRow row)
    {
        if (!await _confirm.ConfirmAsync(new Confirmation(
                $"Remove {row.Name}?",
                "You stop seeing each other online, on all your PCs. To be friends again, one of you shows a code.",
                "Remove")))
            return;
        await RunAsync(async () =>
        {
            await _friends.RemoveAsync(row.UserId);
            Refresh();
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is PairingException or PartyException or PeerNetworkException or FileNotFoundException or TimeoutException or IOException)
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
    /// <param name="join">Joins their party; null when there's none to join.</param>
    public FriendRow(FriendStatus friend, Func<FriendRow, Task> remove, Func<FriendRow, Task>? join = null)
    {
        UserId = friend.UserId;
        Name = friend.Name;
        IsOnline = friend.IsOnline;
        State = Describe(friend);
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
        CanJoin = join != null;
        JoinCommand = new AsyncRelayCommand(() => join?.Invoke(this) ?? Task.CompletedTask);
    }

    public string UserId { get; }
    public string Name { get; }
    public bool IsOnline { get; }
    public double IconOpacity => IsOnline ? 1.0 : 0.4;
    public string State { get; }
    public bool CanJoin { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand JoinCommand { get; }

    public string Key => $"{UserId}|{Name}|{State}|{CanJoin}";

    private static string Describe(FriendStatus f)
    {
        if (f.IsOnline)
        {
            var listening = f.Listening is { } l
                ? $"Listening to {l.Title}{(string.IsNullOrWhiteSpace(l.Artist) ? "" : " · " + l.Artist)} ({l.Source})"
                : "Online";
            return f.IsHosting ? "Hosting a party · " + listening : listening;
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
