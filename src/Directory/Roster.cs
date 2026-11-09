using System.Net;
using System.Net.Sockets;

namespace KSAMultiplayer.Directories;

/// <summary>One server this directory has been told about, and what it has managed to see of it.</summary>
public sealed class Entry
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>The address the announcement arrived from, which is the one thing about it the sender did not choose.</summary>
    public required IPAddress From { get; init; }

    /// <summary>When this was last announced, and when a probe of it last answered. Zero for never.</summary>
    public double Announced { get; set; }
    public double Answered { get; set; }

    /// <summary>When this is next worth asking about.</summary>
    public double Ask { get; set; }

    /// <summary>What it last said of itself, or null until it has said anything.</summary>
    public ServerFacts? Facts { get; set; }

    /// <summary>Why it is not listed, in words its operator can act on. Empty once it is.</summary>
    public string Trouble { get; set; } = "";

    public string Where => Port == Wire.DefaultPort ? Host : $"{Host}:{Port}";
}

/// <summary>
/// Every server this directory knows, in memory and nowhere else.
///
/// <para>Nothing here is worth keeping across a restart. An entry is listed only while a probe of it
/// answered in the last few minutes, so a directory that comes back empty is refilled by the next
/// round of heartbeats and is wrong for one heartbeat interval. A database would buy the right to be
/// wrong for longer: it would bring back rows for servers that stopped while it was down, which is
/// the one thing a list of servers must not do.</para>
/// </summary>
public sealed class Roster
{
    /// <summary>How recently a probe must have answered for a server to be in the list, s. Three missed heartbeats at sixty seconds.</summary>
    public const double ListedForSeconds = 180.0;

    /// <summary>How recently a server must have announced itself to stay in the list, s. A withdrawal can be lost; five missed heartbeats cannot all be.</summary>
    public const double AnnouncedWithinSeconds = 300.0;

    /// <summary>How long an entry nobody announces and nothing answers is kept, s, so its operator can still be told why it is not listed.</summary>
    public const double ForgetAfterSeconds = 1800.0;

    /// <summary>How often a listed server is asked again, s.</summary>
    public const double AskEverySeconds = 45.0;

    /// <summary>How often one that is not answering is asked again, s. Slower, so a mistyped port costs this directory little.</summary>
    public const double AskSilentEverySeconds = 120.0;

    /// <summary>How long an entry already being asked about is left alone, s. Longer than a name takes to resolve plus a probe takes to give up, so one round of asking is never started twice.</summary>
    public const double AskingForSeconds = 15.0;

    public const int MostEntries = 500;

    /// <summary>The most servers one address may have listed. A machine running a handful is ordinary; a machine running fifty is filling the list, not using it.</summary>
    public const int MostPerAddress = 4;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];

    public static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    /// <summary>
    /// Whether an address is one this directory will list: a public IPv4 address and nothing else.
    ///
    /// <para>Refused at the announcement rather than filtered out later, because an entry nobody
    /// can reach is not one whose operator should be left wondering why nobody joins. An address on
    /// the loopback or a private network is the commonest thing a server list carries wrongly, since
    /// nothing about announcing one fails.</para>
    /// </summary>
    public static bool Reachable(IPAddress ip)
    {
        // IPv4 alone, because the protocol this lists is IPv4 alone: the client's socket and its
        // resolution are both InterNetwork.
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,                       // this network, private, loopback
            100 => b[1] is < 64 or > 127,                  // carrier grade NAT, where nothing can be reached
            169 => b[1] != 254,                            // link local
            172 => b[1] is < 16 or > 31,                   // private
            192 => !(b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2)),  // private, and the documentation ranges
            198 => b[1] is not (18 or 19) && !(b[1] == 51 && b[2] == 100),  // benchmarking, documentation
            203 => !(b[1] == 0 && b[2] == 113),            // documentation
            >= 224 => false,                               // multicast and above
            _ => true,
        };
    }

    /// <summary>
    /// Takes an announcement. The entry is made but not listed: nothing is listed until a probe of it
    /// has answered, which is the only claim about a server that its operator cannot simply make.
    /// </summary>
    public Noted Announce(string host, int port, IPAddress from, double now)
    {
        if (port is < 1 or > 65535) return new Noted(false, from.ToString(), Say: $"{port} is not a port");
        if (!Reachable(from))
        {
            // Said plainly, because this is what an operator behind carrier-grade NAT or a server
            // announcing over a private network will hit, and "not listed" alone sends them to
            // check a firewall that is fine.
            return new Noted(false, from.ToString(),
                             Say: $"this announcement came from {from}, which is not an address players on the internet can reach: "
                                + "a server behind carrier-grade NAT or on a private network cannot be listed");
        }
        host = Wire.Clip(host, 100);
        // No name given means be listed at whatever address this arrived from, which is the case
        // that cannot be got wrong.
        if (host.Length == 0) host = from.ToString();
        string key = Key(host, port);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Entry? had))
            {
                had.Announced = now;
                return new Noted(true, from.ToString(), Listed(had, now), had.Trouble.Length > 0 ? had.Trouble : Where(had, now));
            }
            if (_entries.Count >= MostEntries)
            {
                return new Noted(false, from.ToString(), Say: $"this directory is full at {MostEntries} servers");
            }
            int mine = _entries.Values.Count(e => e.From.Equals(from));
            if (mine >= MostPerAddress)
            {
                return new Noted(false, from.ToString(), Say: $"{from} already has {mine} servers listed here, which is as many as one address may");
            }
            _entries[key] = new Entry { Host = host, Port = port, From = from, Announced = now, Ask = now };
            return new Noted(true, from.ToString(), false, "being asked about now; it is listed once it answers");
        }
    }

    /// <summary>Takes a server out at once. Only the address that put it in may, so a withdrawal is not a way to unlist somebody else's server.</summary>
    public bool Withdraw(string host, int port, IPAddress from)
    {
        host = Wire.Clip(host, 100);
        if (host.Length == 0) host = from.ToString();
        lock (_lock)
        {
            if (!_entries.TryGetValue(Key(host, port), out Entry? had) || !had.From.Equals(from)) return false;
            _entries.Remove(Key(host, port));
            return true;
        }
    }

    /// <summary>Whether an entry is in the list as it stands: answering lately, and still saying it wants to be.</summary>
    private static bool Listed(Entry e, double now) =>
        e.Facts is not null && now - e.Answered < ListedForSeconds && now - e.Announced < AnnouncedWithinSeconds;

    private static string Where(Entry e, double now) =>
        Listed(e, now) ? "listed"
        : e.Facts is null ? "not listed: it has not answered yet"
        : now - e.Answered >= ListedForSeconds ? "not listed: it stopped answering"
        : "not listed: it stopped announcing itself";

    /// <summary>
    /// The list, as a directory serves it. Expiry is this comparison and nothing else: no sweep to
    /// schedule, no time-to-live to get wrong, and an entry is never listed for a moment longer than
    /// it has earned.
    /// </summary>
    public Listing Snapshot(string name, double now, DateTimeOffset wall)
    {
        lock (_lock)
        {
            Listed[] servers = [.. _entries.Values
                .Where(e => Listed(e, now))
                .OrderByDescending(e => e.Facts!.Players)
                .ThenBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Listed(
                    e.Host, e.Port, e.Facts!.ServerName, Wire.Version, e.Facts.KsaBuild,
                    e.Facts.Players, e.Facts.MaxPlayers, e.Facts.Password, e.Facts.System, e.Facts.Mods,
                    wall.ToUnixTimeSeconds() - (long)Math.Max(0.0, now - e.Answered)))];
            return new Listing(Listings.Version, name, servers);
        }
    }

    /// <summary>What is worth asking about now, and when each should next be asked.</summary>
    public Entry[] Due(double now)
    {
        lock (_lock)
        {
            return [.. _entries.Values.Where(e => now >= e.Ask)];
        }
    }

    /// <summary>
    /// Marks an entry as being asked about now, without saying anything about how it is doing: a
    /// name that takes three seconds to resolve must not be started again on every round in the
    /// meantime, and a listed server must not have its cadence slowed to a silent one's for it.
    /// </summary>
    public void Asking(string key, double now)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Entry? e)) e.Ask = now + AskingForSeconds;
        }
    }

    /// <summary>A probe answered. The entry is listed from here until it stops.</summary>
    public void Heard(string key, ServerFacts facts, double now)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? e)) return;
            (e.Facts, e.Answered, e.Trouble, e.Ask) = (facts, now, "", now + AskEverySeconds);
        }
    }

    /// <summary>A probe did not answer, or could not be sent. The entry stays, so its operator keeps being told why.</summary>
    public void Quiet(string key, string why, double now)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? e)) return;
            (e.Trouble, e.Ask) = (why, now + AskSilentEverySeconds);
        }
    }

    /// <summary>Forgets what has neither answered nor announced for long enough that nobody is waiting to be told about it.</summary>
    public int Forget(double now)
    {
        lock (_lock)
        {
            string[] gone = [.. _entries
                .Where(kv => now - kv.Value.Announced > ForgetAfterSeconds && now - kv.Value.Answered > ForgetAfterSeconds)
                .Select(kv => kv.Key)];
            foreach (string k in gone) _entries.Remove(k);
            return gone.Length;
        }
    }

    /// <summary>For the health endpoint and the log: how many are known and how many of those are listed.</summary>
    public (int Known, int Listed) Count(double now)
    {
        lock (_lock)
        {
            return (_entries.Count, _entries.Values.Count(e => Listed(e, now)));
        }
    }
}
