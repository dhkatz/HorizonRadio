using System.Text.Json;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk.Peers;

namespace HorizonRadio.Core.Tests;

/// <summary>Adding friends and seeing what they listen to, on an in-memory peer network.</summary>
public sealed class FriendsTests : IAsyncDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private readonly MemoryPeerNetwork _net = new();
    private readonly List<IAsyncDisposable> _running = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in Enumerable.Reverse(_running)) await r.DisposeAsync();
    }

    private sealed record Person(Friends.Friends Friends, SettingsStore Settings, MemoryPeer Peer);

    // Someone's PC: their settings, friends, and a helper.
    private async Task<Person> NewPerson(string name, SettingsStore? settings = null)
    {
        settings ??= SettingsStore.CreateInMemory();
        settings.Set(IdentitySettings.Name, name);
        var peer = _net.Join();
        var friends = new Friends.Friends(settings, _ => Task.FromResult<IPeerNetwork>(peer),
            presenceInterval: TimeSpan.FromMilliseconds(100), joinTimeout: TimeSpan.FromSeconds(20)); // generous: a busy CI runner
        _running.Add(peer);
        _running.Add(friends);
        await friends.StartAsync();
        return new Person(friends, settings, peer);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow + Soon;
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static async Task BecomeFriends(Person a, Person b)
    {
        var code = await a.Friends.InviteAsync();
        await b.Friends.AcceptAsync(code);
        await Eventually(() => a.Friends.All.Any(f => f.IsOnline) && b.Friends.All.Any(f => f.IsOnline), "both to see each other online");
    }

    [Fact]
    public async Task Adding_a_friend_puts_each_on_the_others_list_online()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");

        var code = await sam.Friends.InviteAsync();
        Assert.Equal(code, sam.Friends.Invite?.Code);
        var added = await alex.Friends.AcceptAsync(code);
        Assert.Equal("Sam", added.Name);

        await Eventually(() => sam.Friends.All.SingleOrDefault()?.IsOnline == true, "Sam to see Alex online");
        Assert.Equal("Alex", sam.Friends.All.Single().Name);
        Assert.Null(sam.Friends.Invite); // used up
        Assert.Contains("Added Alex", sam.Friends.Status);
        Assert.Equal(SettingScope.Synced, sam.Settings.ScopeOf(Friends.Friends.RecordPrefix + sam.Friends.All.Single().UserId));
    }

    [Fact]
    public async Task Friends_see_what_each_other_is_listening_to()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");
        await BecomeFriends(sam, alex);

        sam.Friends.SetListening(new Listening("Melt", "ryo", "Spotify", "spotify:track:1"));
        await Eventually(() => alex.Friends.All.Single().Listening?.Title == "Melt", "Alex to see Sam's song");
        Assert.Equal("spotify:track:1", alex.Friends.All.Single().Listening!.ExternalId);

        sam.Friends.SetListening(null);
        await Eventually(() => alex.Friends.All.Single().Listening == null, "Alex to see Sam stopped");
    }

    [Fact]
    public async Task A_friend_going_offline_shows_and_coming_back_shows_too()
    {
        var samSettings = SettingsStore.CreateInMemory();
        var sam = await NewPerson("Sam", samSettings);
        var alex = await NewPerson("Alex");
        await BecomeFriends(sam, alex);

        await sam.Friends.DisposeAsync();
        sam.Peer.GoOffline();
        await Eventually(() => !alex.Friends.All.Single().IsOnline, "Alex to see Sam offline");
        Assert.NotNull(alex.Friends.All.Single().LastSeen);

        // Sam's PC starts again (same settings, so the same identity and friends).
        await NewPerson("Sam", samSettings);
        await Eventually(() => alex.Friends.All.Single().IsOnline, "Alex to see Sam back");
    }

    [Fact]
    public async Task Someone_claiming_to_be_a_friend_without_their_key_is_ignored()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");
        await BecomeFriends(sam, alex);
        Assert.True(sam.Settings.TryGetRaw(sam.Settings.KeysWithPrefix(Friends.Friends.RecordPrefix).Single(), out var alexRecord));
        var publicKey = alexRecord.GetProperty("publicKey").GetString();

        // A stranger sends Alex's public key with a signature it can't make.
        var stranger = _net.Join();
        stranger.SendUnchecked(sam.Peer, JsonSerializer.SerializeToElement(new
        {
            type = "friend.hello",
            name = "Alex",
            userKey = publicKey,
            proof = Convert.ToBase64String(new byte[64]),
        }));
        stranger.SendUnchecked(sam.Peer, JsonSerializer.SerializeToElement(new
        {
            type = "friend.status",
            listening = new { title = "Fake", artist = "Faker", source = "Nowhere" },
        }));
        await Task.Delay(300);
        Assert.Null(sam.Friends.All.Single().Listening);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_your_own_code_cant_add_you()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");
        var code = (await sam.Friends.InviteAsync()).ToString();
        var wrong = code[..5] + (code[5] == 'A' ? 'B' : 'A') + code[6..];
        Assert.True(PairingCode.TryParse(wrong, out var guess));

        var ex = await Assert.ThrowsAsync<PairingException>(() => alex.Friends.AcceptAsync(guess));
        Assert.Contains("didn't match", ex.Message);
        Assert.Empty(sam.Friends.All);
        Assert.Empty(alex.Friends.All);

        // Sam's own other PC (same identity) can't add Sam.
        var samLaptop = await NewPerson("Sam");
        samLaptop.Settings.Set(IdentitySettings.Key, sam.Settings.Get(IdentitySettings.Key));
        var again = await sam.Friends.InviteAsync();
        await Assert.ThrowsAsync<PairingException>(() => samLaptop.Friends.AcceptAsync(again));
        Assert.Empty(sam.Friends.All);
    }

    [Fact]
    public async Task Removing_a_friend_stops_listening_to_them()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");
        await BecomeFriends(sam, alex);
        var alexId = sam.Friends.All.Single().UserId;

        await sam.Friends.RemoveAsync(alexId);
        Assert.Empty(sam.Friends.All);
        alex.Friends.SetListening(new Listening("Song", "Artist", "Radio"));
        await Task.Delay(300);
        Assert.Empty(sam.Friends.All);
    }

    [Fact]
    public async Task Your_devices_share_one_identity_after_pairing()
    {
        // Pairing hands the identity over, so both PCs are the same person to friends.
        var desktopSettings = SettingsStore.CreateInMemory();
        var laptopSettings = SettingsStore.CreateInMemory();
        var desktopPeer = _net.Join();
        var laptopPeer = _net.Join();
        _running.Add(desktopPeer);
        _running.Add(laptopPeer);
        var desktop = new MyDevices(desktopSettings, _ => Task.FromResult<IPeerNetwork>(desktopPeer));
        var laptop = new MyDevices(laptopSettings, _ => Task.FromResult<IPeerNetwork>(laptopPeer));
        _running.Add(desktop);
        _running.Add(laptop);
        await desktop.StartAsync();
        await laptop.StartAsync();
        var mine = Identity.GetOrCreate(desktopSettings).UserId;

        await laptop.JoinAsync(await desktop.StartPairingAsync());

        Assert.Equal(mine, Identity.GetOrCreate(laptopSettings).UserId);
    }
}
