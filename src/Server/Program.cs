using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using KSAMultiplayer;
using KSAMultiplayer.Server;

const string Usage = """
    ksamp-server: a dedicated server for KSA Multiplayer.

    Usage: ksamp-server [options]

      --port <n>             UDP port to listen on (default 50100)
      --bind <address>       address to listen on (default every address)
      --name <text>          the server's name, shown to players
      --password <text>      a password players must give (default none)
      --max-players <n>      the most players at once (default 16)
      --data <dir>           where the world is kept between runs (default ./data)
      --world <path>         start from this KSA save: its folder, or a zip of it,
                             replacing any world kept in --data
      --ksa-build <build>    the KSA build players must run (default: that of
                             whoever gave the server its world)
      --snapshot-every <s>   seconds between fresh saves of the world (default 300)
      --warp-answer <s>      seconds everyone has to answer a warp (default 30)
      --keep-time            let universe time run with nobody playing
      --speed <x>            how fast universe time runs (default 1)
      --help                 this

    Every option can also be given as an environment variable: KSAMP_PORT, KSAMP_NAME,
    KSAMP_PASSWORD, and so on, the option's name in capitals with dashes as underscores.

    With no world, the first player to join gives the server theirs.

    Console commands, on standard input: players, say <text>, kick <name> [why],
    op <name>, deop <name>, admins, clear [name], reload, save, status, version, help, quit.

    An admin can type those same commands into a game's chat with a / in front. op hands the player
    a token their game keeps and offers at every join after, so they stay an admin without a
    password to type or to lose; deop takes it back, from a name whether or not it is playing. The
    list is admins.json beside the world, and holds only each token's hash. quit is the console's
    alone: a server nobody can start again is not something to hand out by accident.

    clear takes every craft nobody is flying out of every game and out of the world the server
    hands out -- the broom for a world filling with spent stages. A name narrows it, with * and ?
    for wildcards: clear Rocket_*. Once the cleared world has been saved, everybody is put back on
    it. reload does that on its own, with the world as it stands; it is also the only thing that
    puts universe time back, since a game's own time comes down by loading a world and no other way.
    Nobody reconnects for either.
    """;

Dictionary<string, string> opts = [];
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a is "--help" or "-h")
    {
        Console.WriteLine(Usage);
        return 0;
    }
    if (!a.StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"unexpected argument '{a}'; try --help");
        return 2;
    }
    string key = a[2..];
    if (key is "keep-time") opts[key] = "true";
    else if (i + 1 < args.Length) opts[key] = args[++i];
    else
    {
        Console.Error.WriteLine($"{a} needs a value; try --help");
        return 2;
    }
}

string? Opt(string name) =>
    opts.TryGetValue(name, out string? v) ? v : Environment.GetEnvironmentVariable("KSAMP_" + name.ToUpperInvariant().Replace('-', '_'));

int OptInt(string name, int fallback) => Opt(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
double OptDouble(string name, double fallback) => Opt(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

ServerOptions options;
IPAddress? bind;
try
{
    options = new ServerOptions
    {
        Port = OptInt("port", 50100),
        Name = Opt("name") ?? "KSA server",
        Password = Opt("password") ?? "",
        MaxPlayers = Math.Clamp(OptInt("max-players", 16), 1, 250),
        KsaBuild = Opt("ksa-build") ?? "",
        SnapshotEverySeconds = Math.Max(30.0, OptDouble("snapshot-every", 300.0)),
        WarpAnswerSeconds = Math.Clamp(OptDouble("warp-answer", 30.0), 1.0, 300.0),
        LapseWithin = Math.Clamp(OptDouble("lapse-within", 10.0), 1.0, 3600.0),
        KeepTime = Opt("keep-time") is "true" or "1" or "yes",
        Speed = Math.Clamp(OptDouble("speed", 1.0), 0.0, 100.0),
    };
    bind = Opt("bind") is { } b ? IPAddress.Parse(b) : null;
}
catch (FormatException e)
{
    Console.Error.WriteLine($"bad option: {e.Message}; try --help");
    return 2;
}

WorldStore store = new(Path.GetFullPath(Opt("data") ?? "data"));
Stopwatch watch = Stopwatch.StartNew();
double Now() => watch.Elapsed.TotalSeconds;

void Say(string what) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}");

GameServer server;
try
{
    if (Opt("world") is { } worldPath)
    {
        byte[] zip = WorldStore.Import(worldPath);
        string system = WorldStore.SystemOf(zip);
        store.Save(new Blob(BlobKind.World, "", 0, 0.0, zip), options.KsaBuild, [], system, timeKnown: false);
        Say($"world imported from {worldPath}: {zip.Length / 1024} KB{(system.Length > 0 ? $", in {system}" : "")}");
    }
    server = new GameServer(options, Now, store, bind);
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"could not start: {e.Message}");
    return 1;
}

string Version = typeof(GameServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";

while (server.Said.TryDequeue(out string? early)) Say(early);
Say($"ksamp-server {Version}, speaking protocol {Wire.Version}");
Say($"{options.Name}: listening on UDP {(bind?.ToString() ?? "*")}:{server.Port}, up to {options.MaxPlayers} players{(options.Password.Length > 0 ? ", with a password" : "")}");
Say(server.HasWorld ? "a world is ready to hand out" : "no world yet: the first player to join gives the server theirs");

CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c =>
{
    c.Cancel = true;
    stop.Cancel();
});

System.Collections.Concurrent.ConcurrentQueue<string> commands = new();
if (!Console.IsInputRedirected || Environment.GetEnvironmentVariable("KSAMP_STDIN") == "1")
{
    Thread reader = new(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { return; }
            if (line is null) return;
            if (line.Trim().Length > 0) commands.Enqueue(line.Trim());
        }
    }) { IsBackground = true, Name = "console" };
    reader.Start();
}

// What each command is for, in the order they are worth knowing. Beside the dispatcher rather than
// in it, so a command added without a line here is a command nobody is told about.
(string What, string Does)[] Commands =
[
    ("players", "who is here, what they are flying and where from"),
    ("say <text>", "a line from the server to everybody"),
    ("kick <name> [why]", "send a player away; nothing stops them coming back"),
    ("op <name>", "make a player who is here an admin: their game keeps a token and stays one"),
    ("deop <name>", "take that back, playing or not; the token stops working at once"),
    ("admins", "who is an admin"),
    ("clear [name]", "take every craft nobody is flying out of every game; clear Rocket_* narrows it"),
    ("reload", "hand the world out again, the only thing that puts universe time back; also reset"),
    ("save", "ask the longest-playing player for a fresh save of the world"),
    ("status", "players, craft, universe time, and what has crossed the wire"),
    ("version", "what this server is, what it speaks, and the KSA build it holds players to"),
];

// who is null for the console, and the player who typed it in a game otherwise: an admin asks for
// the same things, and hears the answer in their own chat rather than in a log they cannot see.
void Command(string line, GameServer.Player? who = null)
{
    void Answer(string text)
    {
        if (who is null) Say(text);
        else server.Tell(who, text);
    }

    string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
    string rest = parts.Length > 1 ? parts[1] : "";
    switch (parts[0].ToLowerInvariant())
    {
        case "players" or "list":
            if (server.Players.Count == 0) Answer("nobody is playing");
            foreach (GameServer.Player p in server.Players.OrderBy(p => p.Id))
            {
                Answer($"  {p.Id,3} {p.Name,-24} {(p.Loaded ? "playing" : p.WaitingForWorld ? "waiting for the world" : "loading")}"
                    + $"{(p.Admin ? ", admin" : "")}"
                    + $"{(p.Controlling.Length > 0 ? $", flying {p.Controlling}" : "")}, from {p.At}, KSA {p.KsaBuild}");
            }
            break;
        case "say":
            server.Announce(rest);
            Say($"<server> {rest}");
            break;
        case "kick":
            string[] k = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (k[0].Length == 0 || !server.Kick(k[0], k.Length > 1 ? k[1] : "")) Answer($"nobody called '{k[0]}'");
            break;
        case "op":
            if (rest.Length == 0) Answer("op <name>, of somebody who is playing");
            else if (server.Op(rest) is not { } opped) Answer($"nobody called '{rest}' is playing: a token has to reach a game, and is said once");
            else Answer($"{opped} is an admin, and their game keeps the token to stay one");
            break;
        case "deop":
            if (rest.Length == 0) Answer("deop <name>, playing or not");
            // Not the sentence the server logs for itself, or the console says it twice.
            else Answer(server.Deop(rest)
                ? $"{rest} is no admin now, and the token their game kept is no good"
                : $"{rest} was not an admin");
            break;
        case "admins":
            Answer(server.Admins.Names.Count == 0
                ? "nobody is an admin; op <name> makes one of somebody who is playing"
                : $"admins: {string.Join(", ", server.Admins.Names)}");
            break;
        case "reload" or "reset":
            int put = server.ReloadAll("the operator asked");
            Answer(put == 0
                ? server.HasWorld ? "nobody to hand the world to" : "there is no world to hand out"
                : $"handing the world to {put} player{(put == 1 ? "" : "s")} again; their games load it and come back in");
            break;
        case "clear" or "sweep":
            List<string> swept = server.Sweep(rest);
            // What the server knew of is only ever part of it: most of a world's debris was claimed
            // by nobody, so it is the games that find it, each in its own world.
            // What the sweep found here is logged by the server itself, detail and all; this is
            // only the console answering the operator who typed it.
            Answer($"sweeping every game by '{(rest.Length > 0 ? rest : "*")}', bar what is being flown"
                + $"{(swept.Count > 0 ? $" -- {swept.Count} known here" : "")}");
            break;
        case "save" or "snapshot":
            server.SnapshotNow();
            Answer(server.Players.Any(p => p.Loaded) ? "asked the longest-playing player for a save" : "nobody is in the world to save it");
            break;
        case "version":
            // The protocol, not just the release: it is the number that decides who can join, and the
            // one an operator needs when a player says they were refused by name.
            Answer($"ksamp-server {Version}, protocol {Wire.Version}, "
                + (server.RequiredBuild.Length > 0 ? $"for KSA {server.RequiredBuild}" : "any KSA build until a world says otherwise"));
            break;
        case "status":
            double u = server.UniverseClock.Now(Now());
            Answer($"{server.Players.Count} players, {server.CraftCount} craft known, universe time {(server.UniverseClock.Known ? $"{u:F1} s" : "not yet known")}"
                + $"{(server.UniverseClock.Running ? "" : " (held)")}, world {(server.World is { } w ? $"{w.Data.Length / 1024} KB" : "none")}"
                + $", {server.Socket.Received} datagrams in, {server.Socket.Sent} out");
            break;
        // One command a line. All of them on one was unreadable, and the three that needed saying
        // more than their name said it underneath, which left the rest looking like they needed none.
        case "help" or "?":
            // Said as it has to be typed: bare at the console, behind a / by an admin in a game.
            string slash = who is null ? "" : "/";
            foreach ((string what, string does) in Commands)
            {
                Answer($"  {slash}{what.PadRight(18)} {does}");
            }
            Answer(who is null
                ? $"  {"quit".PadRight(18)} stop the server"
                : "  quit is the console's alone");
            break;
        case "quit" or "exit" or "stop":
            // The console's alone: a server nobody can start again is not something to hand to an admin
            // who meant to leave the world rather than end it.
            if (who is not null) Answer("quit is the console's alone");
            else stop.Cancel();
            break;
        default:
            Answer($"no command '{parts[0]}'; try help");
            break;
    }
}

// Ticked often enough that a state is passed on within a frame of its arriving.
const int TickMs = 5;
while (!stop.IsCancellationRequested)
{
    try
    {
        while (commands.TryDequeue(out string? c)) Command(c);
        while (server.Asked.TryDequeue(out (GameServer.Player Who, string Line) a)) Command(a.Line, a.Who);
        server.Tick();
        while (server.Said.TryDequeue(out string? s)) Say(s);
    }
    catch (Exception e)
    {
        Say($"error: {e}");
    }
    try { await Task.Delay(TickMs, stop.Token); }
    catch (TaskCanceledException) { }
}

Say("shutting down");
server.Dispose();
return 0;
