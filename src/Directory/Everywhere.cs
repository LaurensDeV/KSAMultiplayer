namespace KSAMultiplayer.Directories;

/// <summary>One server and every directory that lists it.</summary>
/// <param name="Listed">The most recently seen of the accounts the directories gave of it.</param>
/// <param name="By">The directories that carry it, as each calls itself.</param>
public sealed record Carried(Listed Listed, string[] By);

/// <summary>
/// What every known directory lists, as one document: what a game's server list shows, for a
/// reader that cannot make it for itself.
///
/// <para>A web page is that reader. A browser reads another site's document only where that
/// site says it may, a hand-written directory on a static host says no such thing, and a page
/// cannot ask a server anything over UDP. So the asking is done here, where a directory is
/// already a program on the internet, and a page reads the one answer.</para>
/// </summary>
/// <param name="V">The shape of this document, 1.</param>
/// <param name="Directories">The directories that answered, as each calls itself.</param>
public sealed record Everything(int V, string[] Directories, Carried[] Servers);

/// <summary>
/// Reads the other directories and holds what they said, so that a thousand readers of the page
/// are one reader of each directory.
/// </summary>
public sealed class Everywhere(Roster roster, Ticker clock)
{
    /// <summary>The shape of the document this makes.</summary>
    public const int Version = 1;

    /// <summary>How long one gathering is handed out for, s: as long as a directory lets its own list be held.</summary>
    public const double HoldForSeconds = 20.0;

    /// <summary>How long the list of directories is kept, s. It changes when somebody edits a file, which is not often.</summary>
    private const double KnownForSeconds = 600.0;

    /// <summary>The most servers in the document, so that directories listing hundreds each do not make a page of thousands.</summary>
    public const int MostServers = 500;

    // What a stranger's directory may say in each field before it is cut: a page shows these as
    // text, and a megabyte of name is still a megabyte.
    private const int HostChars = 253, NameChars = 64, SystemChars = 64, ModsChars = 2048, BuildChars = 32;

    private readonly SemaphoreSlim _gathering = new(1, 1);
    private Everything? _held;
    private double _heldAt = double.NegativeInfinity;
    private string[] _known = [];
    private double _knownAt = double.NegativeInfinity;

    /// <summary>
    /// Everything listed anywhere, no older than <see cref="HoldForSeconds"/>. Never throws and
    /// never answers with nothing: this directory's own list is in it whatever the others do.
    /// </summary>
    public async Task<Everything> NowAsync(string name, CancellationToken stop = default)
    {
        if (_held is { } fresh && clock.Now - _heldAt < HoldForSeconds) return fresh;
        await _gathering.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            // Whoever waited behind the one that gathered takes what it found.
            if (_held is { } found && clock.Now - _heldAt < HoldForSeconds) return found;
            if (clock.Now - _knownAt >= KnownForSeconds)
            {
                (_known, _knownAt) = (await Listings.KnownAsync(null, stop).ConfigureAwait(false), clock.Now);
            }
            Listing?[] said = await Task.WhenAll(_known.Select(u => Listings.ReadAsync(u, stop))).ConfigureAwait(false);
            // This directory first, from its own memory: it may be one no list of directories
            // names, and where a list does name it, what comes back over HTTP is the same
            // account under the same name and adds nothing.
            (_held, _heldAt) = (Merge([roster.Snapshot(name, clock.Now, clock.Wall), .. said]), clock.Now);
            return _held;
        }
        finally
        {
            _gathering.Release();
        }
    }

    /// <summary>
    /// What was last gathered, at once, where anything has been: for a page, which is better
    /// shown with a list a minute old than held back while a slow directory is waited on. The
    /// gathering it is due is started and not waited for.
    /// </summary>
    public Task<Everything> SoonAsync(string name)
    {
        if (_held is not { } held) return NowAsync(name);
        if (clock.Now - _heldAt >= HoldForSeconds) _ = NowAsync(name);
        return Task.FromResult(held);
    }

    /// <summary>
    /// One row for each address, however many directories carry it. By address written out, as a
    /// game's own list does it: two names for one server are two rows here, because telling they
    /// are one takes asking them, and this asks nobody.
    /// </summary>
    /// <param name="said">What each directory answered, null for one that did not.</param>
    public static Everything Merge(IReadOnlyList<Listing?> said)
    {
        List<string> names = [];
        Dictionary<string, (Listed Listed, List<string> By)> rows = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        foreach (Listing? l in said)
        {
            if (l is null) continue;
            string name = l.Directory.Length > 0 ? l.Directory : "a directory";
            if (!names.Contains(name)) names.Add(name);
            foreach (Listed s in l.Servers ?? [])
            {
                if (s is not { Host.Length: > 0 } || s.Port is <= 0 or >= 65536) continue;
                string key = $"{s.Host}:{s.Port}";
                if (!rows.TryGetValue(key, out (Listed Listed, List<string> By) row))
                {
                    if (rows.Count >= MostServers) continue;
                    rows[key] = row = (Plain(s), []);
                    order.Add(key);
                }
                else if (s.Seen > row.Listed.Seen) rows[key] = row = (Plain(s), row.By);
                if (!row.By.Contains(name)) row.By.Add(name);
            }
        }
        Carried[] servers = [.. order
            .Select(k => rows[k])
            .OrderByDescending(r => r.Listed.Players)
            .ThenBy(r => r.Listed.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Listed.Host, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Carried(r.Listed, [.. r.By]))];
        return new Everything(Version, [.. names], servers);
    }

    private static Listed Plain(Listed s) => s with
    {
        Host = Wire.Clip(s.Host, HostChars),
        Name = Wire.Clip(s.Name, NameChars),
        Ksa = Wire.Clip(s.Ksa, BuildChars),
        System = Wire.Clip(s.System, SystemChars),
        Mods = Wire.Clip(s.Mods, ModsChars),
        // A word that is not the shape of an id is not passed on as one.
        Id = ModLink.IsId(s.Id ?? "") ? s.Id! : "",
    };
}
