using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KSAMultiplayer.Server;
using Xunit;

namespace KSAMultiplayer.Tests;

/// <summary>
/// The web viewer, as a browser meets it: a real Kestrel on a free port over loopback, beside a real
/// server and real clients, the way the other tests use real UDP.
/// </summary>
public sealed class WebTests : IAsyncLifetime
{
    private Rig _rig = null!;
    private WebHost _web = null!;
    private HttpClient _http = null!;
    private CookieContainer _cookies = null!;

    /// <summary>What a stand-in for the console answers, so a command from the web is seen arriving and answered.</summary>
    private readonly List<string> _ran = [];

    private static double Wall() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    public async Task InitializeAsync() => await Start(new ServerOptions { Password = "hunter2", WebAdminPassword = "letmein" });

    private async Task Start(ServerOptions options)
    {
        _rig = new Rig(options);
        _web = await WebHost.StartAsync(_rig.Server.Options with { WebBind = "127.0.0.1" }, 0, Wall);
        _rig.Server.Viewed = _web.Feed.Event;
        _rig.Ticked += () =>
        {
            _web.Tick(_rig.Server, _rig.Now());
            while (_web.Commands.TryDequeue(out WebCommand? c))
            {
                if (WebHost.NotForTheWeb(c.Line) is { } verb)
                {
                    c.Done.TrySetResult([$"{verb} is the console's alone"]);
                    continue;
                }
                _ran.Add(c.Line);
                c.Done.TrySetResult([$"ran {c.Line}"]);
            }
        };
        _cookies = new CookieContainer();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = new Uri($"http://127.0.0.1:{_web.At.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _web.DisposeAsync();
        _rig.Dispose();
    }

    // Ticks on a thread of its own while a request is waited on, since the request waits on a tick.
    private async Task<T> Ticking<T>(Func<Task<T>> asked)
    {
        using CancellationTokenSource stop = new();
        Task ticking = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _rig.Tick();
                Thread.Sleep(2);
            }
        });
        try
        {
            return await asked();
        }
        finally
        {
            await stop.CancelAsync();
            await ticking;
        }
    }

    private async Task<JsonElement> View()
    {
        _rig.For(0.3);
        return await _http.GetFromJsonAsync<JsonElement>("api/v1/view");
    }

    private Client Seed(string name = "Alice", double universe = 5000)
    {
        Client a = _rig.Join(name, password: "hunter2", token: "secret-token-of-alice");
        _rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, universe, Rig.FakeWorld()));
        _rig.Until(() => _rig.Server.HasWorld && _rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        return a;
    }

    [Fact]
    public async Task TheViewShowsWhoIsHereAndWhatTheyFly()
    {
        JsonElement empty = await View();
        Assert.Equal(0, empty.GetProperty("players").GetArrayLength());
        Assert.Equal(16, empty.GetProperty("server").GetProperty("maxPlayers").GetInt32());
        Assert.True(empty.GetProperty("server").GetProperty("password").GetBoolean());

        Client a = Seed();
        a.Claim("Rocket", controlling: true);
        _rig.For(0.2);
        a.SendState(Rig.State("Rocket", 5000.5) with { Flags = Wire.StateFlags.Controlled | Wire.StateFlags.Rails });
        _rig.Until(() => _rig.Server.OwnerOf("Rocket") == a.Id, what: "Alice to hold her rocket");

        JsonElement view = await View();
        JsonElement alice = view.GetProperty("players")[0];
        Assert.Equal("Alice", alice.GetProperty("name").GetString());
        Assert.True(alice.GetProperty("loaded").GetBoolean());
        JsonElement rocket = view.GetProperty("crafts").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "Rocket");
        Assert.Equal("Alice", rocket.GetProperty("ownerName").GetString());
        Assert.True(rocket.GetProperty("flags").GetProperty("rails").GetBoolean());
        Assert.Equal(6_371_000.0, rocket.GetProperty("posCcf").GetProperty("x").GetDouble());
        Assert.True(view.GetProperty("clock").GetProperty("known").GetBoolean());
    }

    [Fact]
    public async Task TheViewNeverCarriesAPasswordATokenOrAnAddress()
    {
        Client a = Seed();
        string all = (await View()).GetRawText() + await _http.GetStringAsync("api/v1/server") + await _http.GetStringAsync("api/v1/chat");
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("letmein", all);
        Assert.DoesNotContain("secret-token-of-alice", all);
        Assert.DoesNotContain("127.0.0.1", all);
        Assert.DoesNotContain(a.Socket.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), all);
    }

    [Fact]
    public async Task ChatArrivesOverTheSocketAsItIsSaid()
    {
        Client a = Seed();
        using ClientWebSocket ws = new();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_web.At.Port}/api/v1/live"), CancellationToken.None);

        List<JsonElement> heard = [];
        long lastSeq = 0;
        bool ordered = true;
        Task listening = Task.Run(async () =>
        {
            byte[] buffer = new byte[1 << 20];
            while (ws.State == WebSocketState.Open)
            {
                int n = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, n, buffer.Length - n), CancellationToken.None);
                    n += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) return;
                JsonElement m = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, n)).RootElement.Clone();
                lock (heard)
                {
                    heard.Add(m);
                    if (m.GetProperty("type").GetString() == "delta")
                    {
                        long seq = m.GetProperty("seq").GetInt64();
                        ordered &= seq > lastSeq;
                        lastSeq = seq;
                    }
                }
            }
        });

        a.SayChat("hello from orbit");
        _rig.Until(() =>
        {
            lock (heard) return heard.Any(m => m.GetProperty("type").GetString() == "event"
                && m.GetProperty("event").GetProperty("kind").GetString() == "chat"
                && m.GetProperty("event").GetProperty("text").GetString() == "hello from orbit"
                && m.GetProperty("event").GetProperty("who").GetString() == "Alice");
        }, what: "the chat line over the socket");
        _rig.For(0.6);
        lock (heard)
        {
            Assert.True(heard.Count(m => m.GetProperty("type").GetString() == "delta") >= 2);
            Assert.True(ordered, "deltas came out of order");
        }
        // And it is kept, for a page that opens after it was said.
        JsonElement chat = await _http.GetFromJsonAsync<JsonElement>("api/v1/chat?after=0");
        Assert.Contains(chat.EnumerateArray(), c => c.GetProperty("text").GetString() == "hello from orbit");
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await listening;
    }

    private Task<HttpResponseMessage> Post(string path, object body, bool guarded = true)
    {
        HttpRequestMessage r = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (guarded) r.Headers.Add("X-KSAMP", "1");
        return _http.SendAsync(r);
    }

    [Fact]
    public async Task AWrongPasswordGetsNothingAndFiveTriesAMinuteIsAll()
    {
        HttpResponseMessage wrong = await Post("api/v1/admin/login", new { password = "guess" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        HttpResponseMessage command = await Post("api/v1/admin/command", new { text = "status" });
        Assert.Equal(HttpStatusCode.Unauthorized, command.StatusCode);

        for (int i = 0; i < 4; i++) await Post("api/v1/admin/login", new { password = "guess" });
        // The right password too, once the address has spent its tries.
        HttpResponseMessage spent = await Post("api/v1/admin/login", new { password = "letmein" });
        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task ACommandFromTheWebIsRunAndAnsweredAndNeedsItsHeader()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post("api/v1/admin/login", new { password = "letmein" })).StatusCode);

        // A form on another site can send the cookie but not the header.
        HttpResponseMessage unguarded = await Post("api/v1/admin/command", new { text = "status" }, guarded: false);
        Assert.Equal(HttpStatusCode.BadRequest, unguarded.StatusCode);

        JsonElement ran = await Ticking(async () => await (await Post("api/v1/admin/command", new { text = "status" })).Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("ran status", ran.GetProperty("lines")[0].GetString());
        Assert.Equal(["status"], _ran);

        JsonElement refused = await Ticking(async () => await (await Post("api/v1/admin/command", new { text = "set web-admin-password stolen" })).Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("set is the console's alone", refused.GetProperty("lines")[0].GetString());
        Assert.Equal(["status"], _ran);

        await Post("api/v1/admin/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("api/v1/admin/command", new { text = "status" })).StatusCode);
    }

    [Fact]
    public async Task ANewPasswordEndsEverySession()
    {
        await Post("api/v1/admin/login", new { password = "letmein" });
        _web.Reconfigure(_rig.Server.Options with { WebAdminPassword = "changed" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("api/v1/admin/command", new { text = "status" })).StatusCode);
    }

    [Fact]
    public void NothingThatCouldCarryAPasswordIsForTheWeb()
    {
        foreach (string line in (string[])["set password x", "SET web-admin-password y", "settings", "fresh", "quit", "op Bob", "deop Bob"])
        {
            Assert.NotNull(WebHost.NotForTheWeb(line));
        }
        foreach (string line in (string[])["status", "players", "say hello", "kick Bob griefing", "save", "clear Rocket_*"])
        {
            Assert.Null(WebHost.NotForTheWeb(line));
        }
    }
}

/// <summary>The parts of the viewer that need no sockets: sessions, the craft rates, and a reader that cannot keep up.</summary>
public class WebPartsTests
{
    [Fact]
    public void ASessionLapsesAfterTwelveIdleHours()
    {
        DateTime now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        WebAdmin admin = new(() => now);
        admin.SetPassword("letmein");
        string token = admin.Login("letmein", "10.0.0.1")!;
        now += TimeSpan.FromHours(11.0);
        Assert.True(admin.Valid(token));
        now += TimeSpan.FromHours(11.0);
        Assert.True(admin.Valid(token), "used an hour ago, so still standing");
        now += WebAdmin.Idle + TimeSpan.FromMinutes(1.0);
        Assert.False(admin.Valid(token));
        Assert.Null(admin.Login("anything", "10.0.0.2"));
        admin.SetPassword("");
        Assert.Null(admin.Login("", "10.0.0.3"));
        Assert.False(admin.On);
    }

    private static ViewCraft Craft(string key, uint seq, bool controlled) =>
        new(key, 1, "Alice", "Earth", seq, 0, Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, Quat.Identity, Vec3.Zero, 0f,
            new ViewFlags(false, false, false, false, controlled, false), 0);

    private static ServerView ViewOf(params ViewCraft[] crafts) =>
        new(1, 0, new ViewServer("", "", "", [], 0, 16, true, false, Wire.Version), new ViewClock(0, 1, 0, true),
            new ViewWarp("None", "", 1, []), [], crafts, null);

    [Fact]
    public void AFlownCraftIsSentFiveTimesASecondAndAParkedOneOnce()
    {
        Dictionary<string, (uint Seq, double At)> sent = [];
        int flown = 0, parked = 0;
        for (int i = 0; i <= 50; i++)
        {
            // Both say something new every round, as a flown craft does; the rate is what holds them.
            (ViewCraft[] due, _) = ViewFeed.Due(sent, ViewOf(Craft("Ship", (uint)i, true), Craft("Debris", (uint)i, false)), i * ViewFeed.EverySeconds);
            flown += due.Count(c => c.Key == "Ship");
            parked += due.Count(c => c.Key == "Debris");
        }
        Assert.Equal(51, flown);
        Assert.InRange(parked, 10, 12);

        // A craft that said nothing new is not sent at all, and one that is gone is said to be.
        (ViewCraft[] quiet, _) = ViewFeed.Due(sent, ViewOf(Craft("Ship", 50, true), Craft("Debris", 50, false)), 100);
        Assert.Empty(quiet);
        (_, string[] removed) = ViewFeed.Due(sent, ViewOf(Craft("Ship", 50, true)), 101);
        Assert.Equal(["Debris"], removed);
    }

    /// <summary>A socket whose far end never takes anything, so whatever is sent to it waits for ever.</summary>
    private sealed class Stalled : WebSocket
    {
        private readonly TaskCompletionSource _never = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public bool Aborted { get; private set; }
        public override void Abort() { Aborted = true; _never.TrySetCanceled(); }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            await _never.Task.WaitAsync(c);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool end, CancellationToken c) => _never.Task.WaitAsync(c);
    }

    [Fact]
    public async Task AReaderThatCannotKeepUpIsDroppedAndNothingWaitsForIt()
    {
        ViewFeed feed = new(() => 0, behind: 8);
        Stalled ws = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task serving = feed.Serve(ws, stop.Token);
        while (feed.Sockets == 0) await Task.Delay(1);

        System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            feed.Event(new ViewEvent("chat", "Alice", $"line {i}"));
            feed.Pump();
        }
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(1), $"fifty rounds took {took.Elapsed}");
        Assert.Equal(0, feed.Sockets);
        Assert.True(ws.Aborted);
        await serving;
    }
}

/// <summary>What the viewer costs the tick thread, which is the one thread every player waits on.</summary>
public class WebCostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public WebCostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void BuildingTheViewOfSixteenPlayersAndTwoHundredCraftIsWithinATick()
    {
        using Rig rig = new(new ServerOptions { MaxPlayers = 16 });
        Client a = rig.Join("Alice");
        rig.Until(() => a.Joined && a.SnapshotWanted is not null, what: "the server to ask for a world");
        a.SnapshotWanted = null;
        a.Upload(new Blob(BlobKind.World, "", a.Id, 5000, Rig.FakeWorld()));
        rig.Until(() => rig.Server.Players.First(p => p.Id == a.Id).Loaded, what: "the world to arrive");
        List<Client> rest = [.. Enumerable.Range(2, 15).Select(i => rig.Join($"Player {i}"))];
        rig.Until(() => rest.All(c => c.Joined), seconds: 20, what: "fifteen more to join");

        for (int i = 0; i < 200; i++) a.Claim($"Craft {i}", controlling: false, made: true);
        rig.For(0.5);
        for (int i = 0; i < 200; i++) a.SendState(Rig.State($"Craft {i}", 5000 + i, i));
        rig.Until(() => rig.Server.BuildView(rig.Now(), 0).Crafts.Length == 200, seconds: 10, what: "two hundred craft to be said");

        double wall = 0;
        for (int i = 0; i < 20; i++) rig.Server.BuildView(rig.Now(), wall++);
        System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
        const int builds = 200;
        for (int i = 0; i < builds; i++) rig.Server.BuildView(rig.Now(), wall++);
        double each = took.Elapsed.TotalMilliseconds / builds;
        _out.WriteLine($"a view of {rig.Server.Players.Count} players and 200 craft: {each:F3} ms to build, once every {ViewFeed.EverySeconds} s");
        // Once every forty ticks, so what a tick pays on average is a fortieth of this. The bound is
        // the plan's own, for the one tick that builds it, with room for a slow machine.
        Assert.True(each < 0.2 * 5, $"{each:F3} ms a view");
    }
}
