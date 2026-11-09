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
///
/// <para>Nothing here reaches the network until <c>advertise</c> is on. That includes reading the
/// shared list of directories: a server nobody opted in asks nothing of anybody, because a tarball
/// that fetched a list on the quiet would be making a choice on behalf of whoever downloaded
/// it.</para>
/// </summary>
public sealed class Announcer : IDisposable
{
    /// <summary>How often this says the server is still here, s. A directory lists it for three of these after the last answer.</summary>
    public const double EverySeconds = 60.0;

    /// <summary>How long after starting the first announcement goes out, s, so the socket is up and answering before a directory asks it anything.</summary>
    private const double FirstAfterSeconds = 3.0;

    /// <summary>How long a withdrawal is waited for on the way out, s. Well inside the twenty seconds a stopping container is given.</summary>
    private const double WithdrawWithinSeconds = 4.0;

    /// <summary>
    /// How often the shared list of directories is read again, s.
    ///
    /// <para>An hour, so a directory retired from that list stops being asked within the hour rather
    /// than at the next release. A server runs for weeks and the list changes perhaps twice a year,
    /// so anything shorter is a request made to learn nothing.</para>
    /// </summary>
    private const double LearnEverySeconds = 3600.0;

    private readonly Func<double> _clock;
    private readonly int _port;
    private readonly string? _cache;
    private readonly Lock _lock = new();

    // What was last said about each directory, so the same line is not written every minute. Also
    // the set this has actually announced to, which is what decides who is owed a withdrawal.
    private readonly Dictionary<string, string> _said = [];

    private ServerOptions _options;
    private string[] _known = Listings.BuiltIn;
    private double _next;
    private double _learnNext;
    private bool _learning;
    private bool _stopped;

    /// <summary><paramref name="cache"/> keeps the last list read, so a restart with no internet still knows the directories it knew yesterday. Null to keep nothing.</summary>
    public Announcer(ServerOptions options, int port, Func<double> clock, string? cache = null)
    {
        (_options, _port, _clock, _cache) = (options, port, clock, cache);
        _next = clock() + FirstAfterSeconds;
        _learnNext = clock();
    }

    /// <summary>Drained by whoever runs the loop, as the server's own is: nothing here writes to the console from a thread of its own.</summary>
    public ConcurrentQueue<string> Said { get; } = new();

    /// <summary>Where this announces to: what the operator named, or the shared list as it was last read.</summary>
    public string[] Where()
    {
        if (Named(_options) is { } named) return named;
        lock (_lock) return _known;
    }

    /// <summary>The directories an operator named, or null where they named none and the shared list stands in for them.</summary>
    private static string[]? Named(ServerOptions options) =>
        options.Directories.Length > 0
            ? [.. options.Directories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

    /// <summary>What a server says of itself at startup, listed or not, so an operator reading the log learns the setting exists.</summary>
    public string Standing() =>
        !_options.Advertise
            ? $"not listed publicly (advertise = false in {Settings.FileName})"
            : $"listing itself with {string.Join(", ", Where())}"
            + (_options.AdvertiseAs.Length > 0 ? $" as {_options.AdvertiseAs}" : "");

    public void Tick()
    {
        if (_stopped || !_options.Advertise) return;
        double now = _clock();
        Learn(now);
        if (now < _next) return;
        _next = now + EverySeconds;
        ServerOptions options = _options;
        foreach (string url in Where()) _ = Say(url, options);
    }

    /// <summary>
    /// Reads the shared list of directories again, so that retiring one is a commented-out line in
    /// a file rather than a release everybody has to install.
    ///
    /// <para>Called from <see cref="Tick"/> alone, which is past the <c>advertise</c> check, so a
    /// server nobody opted in never asks. An operator who named their own directories is not asked
    /// either: there is nothing to look up, and they did not ask to be told otherwise.</para>
    /// </summary>
    private void Learn(double now)
    {
        if (_learning || now < _learnNext || Named(_options) is not null) return;
        _learning = true;
        _learnNext = now + LearnEverySeconds;
        _ = Learned();
    }

    private async Task Learned()
    {
        try
        {
            string[] said = await Listings.KnownAsync(_cache).ConfigureAwait(false);
            string[] gone;
            lock (_lock)
            {
                if (said.SequenceEqual(_known, StringComparer.OrdinalIgnoreCase)) return;
                // Only the ones this has actually spoken to. A directory it never announced to has
                // nothing to withdraw, and asking would be a request made for nothing.
                gone = [.. _known.Except(said, StringComparer.OrdinalIgnoreCase).Where(_said.ContainsKey)];
                foreach (string url in gone) _said.Remove(url);
                _known = said;
            }
            Said.Enqueue($"the directory list changed: now {string.Join(", ", said)}");
            if (gone.Length == 0) return;
            Said.Enqueue($"no longer listing itself with {string.Join(", ", gone)}");
            string? name = _options.AdvertiseAs;
            foreach (string url in gone) _ = Listings.WithdrawAsync(url, _port, name ?? "");
        }
        catch (Exception)
        {
            // KnownAsync answers with the cache and then with the built-in list rather than
            // throwing, so there is nothing here to report and nothing to retry sooner than the
            // next hour's reading.
        }
        finally
        {
            _learning = false;
        }
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
        string[] before = Where();
        _options = next;
        lock (_lock) _said.Clear();
        if (was.Advertise && !next.Advertise)
        {
            Withdraw(was, before);
            Said.Enqueue($"no longer listed publicly (advertise = false in {Settings.FileName})");
            return;
        }
        if (!was.Advertise && next.Advertise)
        {
            _next = _clock();
            Said.Enqueue(Standing());
            return;
        }
        // Still advertising, but somewhere else or under another name: gone from where it was.
        if (was.Advertise && (was.Directories != next.Directories || was.AdvertiseAs != next.AdvertiseAs))
        {
            Withdraw(was, before);
            _next = _clock();
            Said.Enqueue(Standing());
        }
    }

    private void Withdraw(ServerOptions options, string[] where)
    {
        if (!options.Advertise) return;
        try
        {
            Task.WhenAll(where.Select(u => Listings.WithdrawAsync(u, _port, options.AdvertiseAs)))
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
        Withdraw(_options, Where());
    }
}
