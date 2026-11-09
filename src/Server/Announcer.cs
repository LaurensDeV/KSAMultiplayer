using System.Collections.Concurrent;

namespace KSAMultiplayer.Server;

/// <summary>
/// Says this server is here, to the directories it was told to, for as long as it is told to.
///
/// <para>Off unless an operator turns it on. Announcing publishes the address the server is reached
/// at, and a great many of these are somebody's house: a tarball that called home by default would
/// be making that choice on behalf of whoever downloaded it.</para>
///
/// <para>What a directory says back goes into the log whatever it says, including that the server
/// was not listed and why. The failure worth designing against is the quiet one -- an address
/// published, nothing able to reach it, and nobody told -- because it arrives at its operator as
/// players saying multiplayer is broken.</para>
/// </summary>
public sealed class Announcer : IDisposable
{
    /// <summary>How often this says the server is still here, s. A directory lists it for three of these after the last answer.</summary>
    public const double EverySeconds = 60.0;

    /// <summary>How long after starting the first announcement goes out, s, so the socket is up and answering before a directory asks it anything.</summary>
    private const double FirstAfterSeconds = 3.0;

    /// <summary>How long a withdrawal is waited for on the way out, s. Well inside the twenty seconds a stopping container is given.</summary>
    private const double WithdrawWithinSeconds = 4.0;

    private readonly Func<double> _clock;
    private readonly int _port;
    private readonly Lock _lock = new();

    // What was last said about each directory, so the same line is not written every minute.
    private readonly Dictionary<string, string> _said = [];

    private ServerOptions _options;
    private double _next;
    private bool _stopped;

    public Announcer(ServerOptions options, int port, Func<double> clock)
    {
        (_options, _port, _clock) = (options, port, clock);
        _next = clock() + FirstAfterSeconds;
    }

    /// <summary>Drained by whoever runs the loop, as the server's own is: nothing here writes to the console from a thread of its own.</summary>
    public ConcurrentQueue<string> Said { get; } = new();

    /// <summary>Where this announces to: what the operator named, or the built-in list when they named nothing.</summary>
    public static string[] Where(ServerOptions options) =>
        options.Directories.Length > 0
            ? [.. options.Directories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : Listings.BuiltIn;

    /// <summary>What a server says of itself at startup, listed or not, so an operator reading the log learns the setting exists.</summary>
    public static string Standing(ServerOptions options) =>
        !options.Advertise
            ? $"not listed publicly (advertise = false in {Settings.FileName})"
            : $"listing itself with {string.Join(", ", Where(options))}"
            + (options.AdvertiseAs.Length > 0 ? $" as {options.AdvertiseAs}" : "");

    public void Tick()
    {
        if (_stopped || !_options.Advertise) return;
        double now = _clock();
        if (now < _next) return;
        _next = now + EverySeconds;
        ServerOptions options = _options;
        foreach (string url in Where(options)) _ = Say(url, options);
    }

    private async Task Say(string url, ServerOptions options)
    {
        Noted? noted = await Listings.AnnounceAsync(url, _port, options.AdvertiseAs).ConfigureAwait(false);
        string word = noted is null ? $"could not reach the directory at {url}"
            : noted.Listed ? $"listed at {url}"
            : $"not listed at {url}: {(noted.Say.Length > 0 ? noted.Say : "the directory did not say why")}";
        // Said once, and again only when it changes. A line a minute saying the same thing is a log
        // nobody reads, and the line that matters is the one where something became true.
        lock (_lock)
        {
            if (_said.TryGetValue(url, out string? before) && before == word) return;
            _said[url] = word;
        }
        Said.Enqueue(word);
    }

    /// <summary>
    /// Takes up a change to the settings. Turning advertising off withdraws at once rather than
    /// leaving the server listed until its entry goes stale, which is the whole reason the setting is
    /// one a running server takes up.
    /// </summary>
    public void Reconfigure(ServerOptions next)
    {
        ServerOptions was = _options;
        _options = next;
        lock (_lock) _said.Clear();
        if (was.Advertise && !next.Advertise)
        {
            Withdraw(was);
            Said.Enqueue($"no longer listed publicly (advertise = false in {Settings.FileName})");
            return;
        }
        if (!was.Advertise && next.Advertise)
        {
            _next = _clock();
            Said.Enqueue(Standing(next));
            return;
        }
        // Still advertising, but somewhere else or under another name: gone from where it was.
        if (was.Advertise && (was.Directories != next.Directories || was.AdvertiseAs != next.AdvertiseAs))
        {
            Withdraw(was);
            _next = _clock();
            Said.Enqueue(Standing(next));
        }
    }

    private void Withdraw(ServerOptions options)
    {
        if (!options.Advertise) return;
        try
        {
            Task.WhenAll(Where(options).Select(u => Listings.WithdrawAsync(u, _port, options.AdvertiseAs)))
                .Wait(TimeSpan.FromSeconds(WithdrawWithinSeconds));
        }
        catch (Exception)
        {
            // A directory that cannot be reached on the way out drops the server in three minutes
            // anyway. Nothing about stopping is worth failing over.
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        Withdraw(_options);
    }
}
