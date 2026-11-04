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
    save, status, help, quit.
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

while (server.Said.TryDequeue(out string? early)) Say(early);
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

void Command(string line)
{
    string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
    string rest = parts.Length > 1 ? parts[1] : "";
    switch (parts[0].ToLowerInvariant())
    {
        case "players" or "list":
            if (server.Players.Count == 0) Say("nobody is playing");
            foreach (GameServer.Player p in server.Players.OrderBy(p => p.Id))
            {
                Say($"  {p.Id,3} {p.Name,-24} {(p.Loaded ? "playing" : p.WaitingForWorld ? "waiting for the world" : "loading")}"
                    + $"{(p.Controlling.Length > 0 ? $", flying {p.Controlling}" : "")}, from {p.At}, KSA {p.KsaBuild}");
            }
            break;
        case "say":
            server.Announce(rest);
            Say($"<server> {rest}");
            break;
        case "kick":
            string[] k = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (k[0].Length == 0 || !server.Kick(k[0], k.Length > 1 ? k[1] : "")) Say($"nobody called '{k[0]}'");
            break;
        case "save" or "snapshot":
            server.SnapshotNow();
            Say(server.Players.Any(p => p.Loaded) ? "asked the longest-playing player for a save" : "nobody is in the world to save it");
            break;
        case "status":
            double u = server.UniverseClock.Now(Now());
            Say($"{server.Players.Count} players, {server.CraftCount} craft known, universe time {(server.UniverseClock.Known ? $"{u:F1} s" : "not yet known")}"
                + $"{(server.UniverseClock.Running ? "" : " (held)")}, world {(server.World is { } w ? $"{w.Data.Length / 1024} KB" : "none")}"
                + $", {server.Socket.Received} datagrams in, {server.Socket.Sent} out");
            break;
        case "help" or "?":
            Say("players, say <text>, kick <name> [why], save, status, quit");
            break;
        case "quit" or "exit" or "stop":
            stop.Cancel();
            break;
        default:
            Say($"no command '{parts[0]}'; try help");
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
