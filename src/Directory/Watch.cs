using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace KSAMultiplayer.Directories;

/// <summary>
/// Asks every server this directory has been told about whether it is really there, in the protocol
/// it would be joined in, and lists only what answers.
///
/// <para>This is the whole reason a directory is worth more than a text file. A server's account of
/// itself is unchecked, so it is free to be wrong about its address, its name and how many people
/// are on it. Nothing here is taken from an announcement except an address to try.</para>
///
/// <para>A stated name has to resolve to the address its announcement came from. Not because that
/// proves anything about reachability -- the probe does that -- but because without it this
/// directory would send UDP to any host and port anybody named, which is a scanner with a public
/// front door. With it, a server can only ever point this at itself.</para>
///
/// <para>The exception is a server whose announcement never left this machine, which has no address
/// of its own to be compared against: the host turns such a request back before it is given one.
/// That server is held to its name instead, and the name has to resolve to something a player could
/// reach. Nothing is lost by it, because an announcement from an address no player can reach is one
/// only this machine could have sent.</para>
/// </summary>
public sealed class Watch(Roster roster, Ticker clock, ILogger<Watch> log) : BackgroundService
{
    /// <summary>How often the loop comes round. Short, because probes answer in milliseconds and the point is to not sit on an answer.</summary>
    private static readonly TimeSpan Round = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Resolving = TimeSpan.FromSeconds(3.0);

    /// <summary>How many names are being resolved at once. DNS is the slow part and a directory that blocks on it stops answering nothing else.</summary>
    private const int ResolvingAtOnce = 16;

    private readonly ConcurrentQueue<(IPEndPoint At, string Key, string Trouble)> _resolved = new();
    private readonly SemaphoreSlim _room = new(ResolvingAtOnce);
    private readonly HashSet<string> _resolving = [];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using Probes probes = new(() => clock.Now);
        // By endpoint, and several keys to one: two names for one socket are one server listed
        // twice, and both entries have to hear the same answer or one of them reads as dead.
        Dictionary<IPEndPoint, HashSet<string>> asked = [];
        double forget = 0.0;

        log.LogInformation("watching: a server is listed while a probe of it has answered within {Listed:F0}s and it has announced itself within {Announced:F0}s",
                           Roster.ListedForSeconds, Roster.AnnouncedWithinSeconds);

        while (!stop.IsCancellationRequested)
        {
            double now = clock.Now;

            // Names that have resolved since the last round, or failed to.
            while (_resolved.TryDequeue(out (IPEndPoint At, string Key, string Trouble) r))
            {
                lock (_resolving) _resolving.Remove(r.Key);
                if (r.Trouble.Length > 0)
                {
                    roster.Quiet(r.Key, r.Trouble, now);
                    continue;
                }
                if (!asked.TryGetValue(r.At, out HashSet<string>? sharing)) asked[r.At] = sharing = [];
                sharing.Add(r.Key);
                probes.AskAgain(r.At);
            }

            probes.Update();

            // Whatever has settled since the last round, by the address it settled at.
            foreach ((IPEndPoint at, HashSet<string> keys) in asked.ToArray())
            {
                if (probes.Answered.TryGetValue(at, out ServerFacts? facts))
                {
                    foreach (string key in keys) roster.Heard(key, facts, now);
                }
                else if (probes.Elsewhere.TryGetValue(at, out ushort version))
                {
                    string why = $"it answered at {at} in protocol {version}; this directory lists protocol {Wire.Version}, so update the server";
                    foreach (string key in keys) roster.Quiet(key, why, now);
                }
                else if (probes.Silent.Contains(at))
                {
                    string why = $"nothing answered at {at}: check that the UDP port is open on the firewall and forwarded on the router";
                    foreach (string key in keys) roster.Quiet(key, why, now);
                }
                else continue;
                asked.Remove(at);
            }

            foreach (Entry e in roster.Due(now)) Start(e, now);

            if (now > forget)
            {
                forget = now + Roster.ForgetAfterSeconds;
                int gone = roster.Forget(now);
                (int known, int listed) = roster.Count(now);
                log.LogInformation("{Listed} of {Known} servers listed, {Gone} forgotten", listed, known, gone);
            }

            try
            {
                await Task.Delay(Round, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Works out where to send a probe, off this loop's thread. Pushes the answer back rather than
    /// touching the roster or the probes from here: one thread owns both.
    /// </summary>
    private void Start(Entry e, double now)
    {
        string key = Roster.Key(e.Host, e.Port);
        lock (_resolving)
        {
            if (!_resolving.Add(key)) return;
        }
        // Pushed forward at once, so a name that takes three seconds to resolve is not started again
        // on every round in the meantime.
        roster.Asking(key, now);

        _ = Task.Run(async () =>
        {
            try
            {
                await _room.WaitAsync().ConfigureAwait(false);
                try
                {
                    _resolved.Enqueue(await Where(e).ConfigureAwait(false));
                }
                finally
                {
                    _room.Release();
                }
            }
            catch (Exception bad)
            {
                // Nothing thrown on this task may end the loop that reads its answers.
                _resolved.Enqueue((new IPEndPoint(IPAddress.None, 0), key, $"could not be looked up: {bad.Message}"));
            }
        });
    }

    private static async Task<(IPEndPoint At, string Key, string Trouble)> Where(Entry e)
    {
        string key = Roster.Key(e.Host, e.Port);
        IPEndPoint nowhere = new(IPAddress.None, 0);

        if (IPAddress.TryParse(e.Host, out IPAddress? literal))
        {
            if (!Roster.Reachable(literal)) return (nowhere, key, $"{literal} is not an address players can reach");
            // An address rather than a name: it has to be the one the announcement came from, which
            // for an entry made without a name it always is. A server on this machine had no
            // address of its own to compare, so there is nothing to hold it to but the one it gave.
            if (!e.FromHere && !literal.Equals(e.From))
            {
                return (nowhere, key, $"it asked to be listed at {literal} but announced itself from {e.From}: a server is listed at its own address");
            }
            return (new IPEndPoint(literal, e.Port), key, "");
        }

        using CancellationTokenSource giveUp = new(Resolving);
        IPAddress[] found;
        try
        {
            found = await Dns.GetHostAddressesAsync(e.Host, AddressFamily.InterNetwork, giveUp.Token).ConfigureAwait(false);
        }
        catch (Exception bad) when (bad is SocketException or OperationCanceledException or ArgumentException)
        {
            return (nowhere, key, $"{e.Host} does not resolve to an IPv4 address");
        }
        if (found.Length == 0) return (nowhere, key, $"{e.Host} does not resolve to an IPv4 address");
        if (e.FromHere)
        {
            if (found.FirstOrDefault(Roster.Reachable) is not { } usable)
            {
                return (nowhere, key, $"{e.Host} resolves only to {string.Join(", ", found.Select(ip => ip.ToString()))}, which no player can reach");
            }
            return (new IPEndPoint(usable, e.Port), key, "");
        }
        if (!found.Any(ip => ip.Equals(e.From)))
        {
            return (nowhere, key,
                    $"{e.Host} resolves to {string.Join(", ", found.Select(ip => ip.ToString()))} and announced itself from {e.From}: "
                  + "keep an address record on the name pointing at the server, or leave the name out and be listed at the address alone");
        }
        return (new IPEndPoint(e.From, e.Port), key, "");
    }
}
