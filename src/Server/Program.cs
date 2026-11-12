using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
                             whoever gave the server its world, or is in it)
      --mods <a,b,c>         the mods players must have (default: those of whoever
                             gave the server its world, which follows whoever last
                             handed one over)
      --snapshot-every <s>   seconds between fresh saves of the world (default 300)
      --warp-answer <s>      seconds everyone has to answer a warp (default 30)
      --keep-time            let universe time run with nobody playing
      --speed <x>            how fast universe time runs (default 1)
      --lapse-within <s>     seconds a craft stays its owner's while they are not near it (default 10)
      --advertise            list this server in the public directories so players can find it.
                             Off by default: being listed publishes the address this server is
                             reached at, which is a decision for whoever runs the machine
      --advertise-as <host>  the hostname to be listed under (default: the address a directory
                             sees the announcement arrive from)
      --directories <urls>   where to announce, separated by commas (default: the built-in list)
      --config <path>        the settings file (default: ksamp-server.toml beside the server)
      --trace <path>         write the clock, the warp and how late each player says they are to
                             this file, a line of JSON at a time, for a test that samples a room
                             through a warp. A flag only, and no setting
      --help                 this

    Or, instead of serving anything, stand between the games and a real server and hold their
    datagrams back, for testing this protocol over a wire that is not loopback:

      --relay <host:port>    the server to pass everything to. The games are pointed at this
                             process instead, and it serves no world of its own
      --latency <ms>         how long each datagram is held, each way (default 25). The round
                             trip a game sees is twice this, and so is the path to another game
      --jitter <ms>          how much that varies, give or take (default 0). This is also what
                             makes datagrams pass each other, so reordering is not a setting
                             of its own: it is what a delay that varies already means
      --drop <share>         the share of datagrams thrown away, 0 to 1 (default 0)

    Every option but --config and --trace can also be given as an environment variable -- KSAMP_PORT,
    KSAMP_NAME, KSAMP_PASSWORD and so on, the option's name in capitals with dashes as
    underscores -- and every one but --world, --config and --trace as a line in the settings file:
    port = 50100. A flag wins over the environment, and the environment over the file. With no
    file, the server writes one with every setting at its default.

    A variable set here overrides the file for good, so the console cannot change what it gives:
    use one for a setting that belongs to the machine, and the file for the rest.

    With no world, the first player to join gives the server theirs -- or set empty-world to a
    system and the server starts with an empty world of its own instead.

    Console commands, on standard input: players, say <text>, kick <name> [why],
    op <name>, deop <name>, admins, clear [name], reload, save, status, version, help, fresh,
    quit, settings, set <key> <value>.

    An admin can type those same commands into a game's chat with a / in front. op hands the player
    a token their game keeps and offers at every join after, so they stay an admin without a
    password to type or to lose; deop takes it back, from a name whether or not it is playing. The
    list is admins.json beside the world, and holds only each token's hash. fresh, quit, settings
    and set are the console's alone: a server nobody can start again, one whose password an admin
    has changed, or a world thrown away under everybody standing in it, is not something to hand
    out by accident.

    fresh throws the world away and puts an empty one in its place, the one empty-world names:
    every craft gone, universe time started over, and everybody handed the new world where they
    stand. There is no undoing it.

    settings reads the settings file again and set changes one line of it; either takes effect at
    once, except port, bind and data, which wait for a restart, and a setting a flag or the
    environment gives, which they go on overriding.

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

// A relay is not a server and takes none of the settings below: no file is read or written, and
// the real server's own options grow nothing an operator could set by accident.
if (opts.Remove("relay", out string? passTo))
{
    double Num(string name, double fallback) =>
        opts.Remove(name, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : fallback;

    double latency = Num("latency", 25.0);
    double jitter = Num("jitter", 0.0);
    double drop = Math.Clamp(Num("drop", 0.0), 0.0, 1.0);
    int front = (int)Num("port", 50200);
    string bindTo = opts.Remove("bind", out string? where) ? where : "0.0.0.0";
    if (opts.Count > 0)
    {
        Console.Error.WriteLine($"--relay takes none of --{string.Join(", --", opts.Keys)}; try --help");
        return 2;
    }

    string[] halves = passTo.Split(':');
    if (halves.Length is not 2
        || !int.TryParse(halves[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int upPort)
        || !IPAddress.TryParse(bindTo, out IPAddress? bindAt))
    {
        Console.Error.WriteLine("--relay wants host:port, and --bind an address");
        return 2;
    }
    IPAddress[] found = await Dns.GetHostAddressesAsync(halves[0]);
    if (found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is not { } upAt)
    {
        Console.Error.WriteLine($"no address for '{halves[0]}'");
        return 2;
    }

    // Windows naps in whatever its timer resolution is, which is 15.6 ms by default: a one
    // millisecond sleep then overshoots a 25 ms delay by a third, and the relay held 33 ms against
    // the 25 it was asked for. This is what the knob exists for, and what every game calls on its
    // way up.
    if (OperatingSystem.IsWindows()) Timers.Fine();

    using Relay relay = new(new IPEndPoint(upAt, upPort), front, bindAt, latency, jitter, drop);
    Console.WriteLine($"relaying :{relay.Port} to {upAt}:{upPort}, holding every datagram "
                      + $"{latency:0.#} ms each way{(jitter > 0 ? $" give or take {jitter:0.#}" : "")}"
                      + $"{(drop > 0 ? $", dropping {drop:P1}" : "")}");
    Console.WriteLine($"a game joins {bindAt}:{relay.Port} and sees a round trip of about {latency * 2:0.#} ms");
    double said = 0.0;
    while (true)
    {
        relay.Pump();
        // A millisecond at a time while anything is in hand, because the nap is what the delay
        // overshoots by: at a 5 ms cap this held 31 ms against the 25 it was asked for. Idle, it
        // waits longer, since there is nothing to be late for. Either way the figure it reports is
        // the one it measured, so a run never has to take the asked-for number on trust.
        double next = relay.NextDueIn();
        if (next > 0.0015) Thread.Sleep(next > 0.02 ? 5 : 1);
        // The clock first: counting is what starts the next interval, so asked every pass it
        // reported the delay of whatever had gone through since the pass before, which was mostly
        // nothing, and said 0 ms.
        if (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - said > 15.0 && relay.Counts() is var now && now.Passed + now.Dropped > 0)
        {
            said = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            Console.WriteLine($"{now.Games} game(s), {now.Passed} through, {now.Dropped} dropped, "
                              + $"{now.Waiting} in hand, held {now.MeanMs:0.#} ms on average");
        }
    }
}

// A test's instrument and not a setting: taken out here, before anything that reads the settings
// sees it, so the file an operator edits never grows a line for it.
string? tracePath = opts.Remove("trace", out string? traceTo) ? Path.GetFullPath(traceTo) : null;

string configPath = Path.GetFullPath(opts.Remove("config", out string? asked) ? asked : Settings.DefaultPath);

// The file as it stands, written first when there is none so there is one to edit. A server that
// cannot write beside itself -- a system install, say -- runs on its defaults and says where to
// point --config instead.
Dictionary<string, string> ReadFile()
{
    if (!File.Exists(configPath))
    {
        try
        {
            File.WriteAllText(configPath, Settings.Template());
            Console.WriteLine($"wrote the default settings to {configPath}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"no settings file, and none could be written at {configPath} ({e.Message}): give --config a path that can be");
            return [];
        }
    }
    return Settings.Read(File.ReadAllText(configPath));
}

string? Opt(string name) =>
    opts.TryGetValue(name, out string? v) ? v : Environment.GetEnvironmentVariable(Settings.EnvironmentName(name));

foreach (string key in opts.Keys)
{
    if (key != "world" && Settings.Find(key) is null)
    {
        Console.Error.WriteLine($"no option --{key}; try --help");
        return 2;
    }
}

ServerOptions options;
IPAddress? bind;
string dataFolder;
// What each setting came to and from where, as this server took them up: what set lists, and what
// a reread of the file is measured against.
Dictionary<string, (string Value, string From)> started;
try
{
    started = Settings.Resolve(opts, Environment.GetEnvironmentVariable, ReadFile());
    (options, bind, dataFolder) = Settings.Options(started);
}
catch (FormatException e)
{
    Console.Error.WriteLine($"bad setting: {e.Message}; try --help");
    return 2;
}

WorldStore store = new(Path.GetFullPath(dataFolder));
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
        Say($"world imported from {worldPath}: {Blob.Size(zip.Length)}{(system.Length > 0 ? $", in {system}" : "")}");
    }
    // Only when there is nothing: a world kept from the last run, or one just imported, is the world
    // this server has, and starting empty over the top of it would throw away what it was keeping.
    else if (options.EmptyWorld.Length > 0 && store.Load() is null && StockWorld.Zip(options.EmptyWorld) is { } empty)
    {
        store.Save(new Blob(BlobKind.World, "", 0, 0.0, empty), options.KsaBuild, [], options.EmptyWorld, timeKnown: false);
        Say($"starting with an empty world in {options.EmptyWorld}; the first player joins into it rather than giving theirs");
    }
    server = new GameServer(options, Now, store, bind);
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"could not start: {e.Message}");
    return 1;
}

string Version = typeof(GameServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
// So a directory's log says which build asked it anything.
Listings.Agent = $"ksamp-server/{Version}";
// Made after the socket, which is where the port it is really on comes from: a server started on
// port 0 picks one, and the one it announces has to be the one it answers on.
Announcer announcer = new(options, server.Port, Now, Path.Combine(dataFolder, "directories-seen.txt"));

while (server.Said.TryDequeue(out string? early)) Say(early);
Say($"ksamp-server {Version}, speaking protocol {Wire.Version}");
Say($"settings: {configPath}");
Say($"{options.Name}: listening on UDP {(bind?.ToString() ?? "*")}:{server.Port}, up to {options.MaxPlayers} players{(options.Password.Length > 0 ? ", with a password" : "")}");
Say(server.HasWorld ? "a world is ready to hand out" : "no world yet: the first player to join gives the server theirs");
// Said at every start whichever way it is set, so an operator reading the log at least learns that
// the setting is there. A list nobody opts into is an empty list.
Say(announcer.Standing());

// Shared for reading, because whatever reads it does so while this is still writing.
ClockTrace? trace = null;
if (tracePath is not null)
{
    try
    {
        trace = new ClockTrace(new StreamWriter(new FileStream(tracePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)));
        Say($"tracing the clock to {tracePath}");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"could not start: --trace {tracePath}: {e.Message}");
        return 1;
    }
}
// The time of day, which is the one clock this process and a game on the same machine both read.
double Wall() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

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
                // Where a player is from goes to the console and nowhere else. An admin is a player
                // like any other, and being able to run a command is not being told everybody's
                // address; whoever is at the console has them in the log already.
                Answer($"  {p.Id,3} {p.Name,-24} {(p.Loaded ? "playing" : p.WaitingForWorld ? "waiting for the world" : "loading")}"
                    + $"{(p.Admin ? ", admin" : "")}"
                    + $"{(p.Controlling.Length > 0 ? $", flying {p.Controlling}" : "")}"
                    + $"{(who is null ? $", from {p.At}" : "")}, KSA {p.KsaBuild}");
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
                + $"{(server.UniverseClock.Running ? "" : " (held)")}, world {(server.World is { } w ? Blob.Size(w.Data.Length) : "none")}"
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
            if (who is null)
            {
                Answer($"  {"fresh".PadRight(18)} throw the world away and start on an empty one");
                Answer($"  {"settings".PadRight(18)} read the settings file again");
                Answer($"  {"set <key> <value>".PadRight(18)} change a setting and keep it in the file; set alone lists them");
                Answer($"  {"quit".PadRight(18)} stop the server");
            }
            else Answer("  fresh, quit, settings and set are the console's alone");
            break;
        // The console's alone, as quit is: an admin who could set the password could lock every
        // other player out, and one who could set the data folder could point it anywhere.
        // The console's alone, with quit: it throws away the world everybody is standing in, and
        // there is no undoing it from a chat window.
        case "fresh" when who is null:
            Answer(server.Fresh("the operator started the world over"));
            break;
        case "fresh":
            Answer("fresh is the console's alone");
            break;
        case "settings" when who is null:
            Reapply(null);
            break;
        case "set" when who is null:
            Set(rest);
            break;
        case "settings" or "set":
            Answer("settings and set are the console's alone");
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

// The settings file read again, with one line of it changed first if asked, and what changed put
// to the running server. A flag or the environment still wins over the file, and port, bind and
// data stay as the server was started with until it is started again.
void Reapply((Settings.Setting Setting, string Value)? change)
{
    Dictionary<string, (string Value, string From)> next;
    ServerOptions nextOptions;
    try
    {
        if (change is { } c)
        {
            string text = File.Exists(configPath) ? File.ReadAllText(configPath) : Settings.Template();
            string changed = Settings.Write(text, c.Setting, c.Value);
            // Checked before it is written, so a bad value never reaches the file.
            Settings.Options(Settings.Resolve(opts, Environment.GetEnvironmentVariable, Settings.Read(changed)));
            File.WriteAllText(configPath, changed);
        }
        next = Settings.Resolve(opts, Environment.GetEnvironmentVariable, Settings.Read(File.ReadAllText(configPath)));
        (nextOptions, _, _) = Settings.Options(next);
    }
    catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
    {
        Say($"settings not changed: {e.Message}");
        return;
    }
    foreach (Settings.Setting s in Settings.All)
    {
        (string value, string from) = next[s.Key];
        if (from is not ("the settings file" or "its default"))
        {
            if (change?.Setting == s) Say($"{s.Key} is written to the file, but {from} still gives it as '{value}'");
            continue;
        }
        if (value == started[s.Key].Value) continue;
        Say(s.Live ? $"{s.Key} is now '{value}'" : $"{s.Key} is '{value}' in the file; a restart takes it up");
    }
    server.Reconfigure(nextOptions);
    announcer.Reconfigure(nextOptions);
    foreach ((string key, (string Value, string From) v) in next) if (Settings.Find(key)!.Live) started[key] = v;
}

void Set(string rest)
{
    string[] kv = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
    if (kv[0].Length == 0)
    {
        foreach (Settings.Setting s in Settings.All)
        {
            // The password is the one value not to put in a log, which is where the console goes.
            string shown = s.Key == "password" ? (started[s.Key].Value.Length > 0 ? "(set)" : "(none)") : $"'{started[s.Key].Value}'";
            Say($"  {s.Key.PadRight(15)} {shown}, from {started[s.Key].From}");
        }
        return;
    }
    if (Settings.Find(kv[0]) is not { } setting)
    {
        Say($"no setting '{kv[0]}'; set alone lists them");
        return;
    }
    Reapply((setting, kv.Length > 1 ? kv[1] : ""));
}

// Ticked often enough that a state is passed on within a frame of its arriving.
const int TickMs = 5;
while (!stop.IsCancellationRequested)
{
    try
    {
        // Before the commands as well as the tick: a reload moves the clock as surely as a warp does.
        trace?.Before(server, Now(), Wall());
        while (commands.TryDequeue(out string? c)) Command(c);
        while (server.Asked.TryDequeue(out (GameServer.Player Who, string Line) a)) Command(a.Line, a.Who);
        server.Tick();
        trace?.After(server, Now(), Wall());
        announcer.Tick();
        while (server.Said.TryDequeue(out string? s)) Say(s);
        while (announcer.Said.TryDequeue(out string? s)) Say(s);
    }
    catch (Exception e)
    {
        Say($"error: {e}");
    }
    try { await Task.Delay(TickMs, stop.Token); }
    catch (TaskCanceledException) { }
}

Say("shutting down");
announcer.Dispose();
while (announcer.Said.TryDequeue(out string? last)) Say(last);
server.Dispose();
return 0;

static class Timers
{
    // DllImport rather than LibraryImport: the generated marshalling for the latter is unsafe code,
    // and this project does not compile with /unsafe for one call that takes an integer.
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint BeginPeriod(uint ms);

    /// <summary>Asks Windows for a one-millisecond timer, so a sleep is about as long as it says.</summary>
    public static void Fine()
    {
        try { BeginPeriod(1); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
