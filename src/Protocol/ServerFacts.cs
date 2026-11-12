namespace KSAMultiplayer;

/// <summary>
/// What a server says of itself to anybody who asks without joining: everything a row of a list of
/// servers is drawn from, and nothing a stranger could not learn by being refused.
///
/// <para>Taken from what a server answers an address it has not heard from before: the
/// <see cref="Wire.Facts"/> a list of servers asks for, or the welcome a cookieless hello gets on
/// the way to joining. A record rather than a tuple because the same things are read by a window,
/// written into a listing and compared against this game, and a tuple that wide stops saying which
/// field is which at the third call site.</para>
/// </summary>
/// <param name="Players">How many are on it now, and <paramref name="MaxPlayers"/> the most it takes.</param>
/// <param name="KsaBuild">The build it wants, empty when it wants whichever it is given.</param>
/// <param name="Protocol">The <see cref="Wire.Version"/> it speaks, which need not be this one: a server may be asked what it is by anybody, and joined only by a game of its own version.</param>
public sealed record ServerFacts(
    string ServerName,
    string System,
    bool HasWorld,
    string Mods,
    int Players,
    int MaxPlayers,
    bool Password,
    string KsaBuild,
    ushort Protocol = Wire.Version)
{
    // A welcome is read in this version or not at all.
    public static ServerFacts Of(Wire.Welcome w) =>
        new(w.ServerName, w.System, w.HasWorld, w.Mods, w.Players, w.MaxPlayers,
            w.Flags.HasFlag(Wire.WelcomeFlags.Password), w.KsaBuild);

    public static ServerFacts Of(Wire.Facts f) =>
        new(f.ServerName, f.System, f.HasWorld, f.Mods, f.Players, f.MaxPlayers,
            f.Flags.HasFlag(Wire.WelcomeFlags.Password), f.KsaBuild, f.Speaks);

    /// <summary>Whether the server has as many players as it takes. Unknown of a server that said no maximum, which is no reason to keep a player out of it.</summary>
    public bool Full => MaxPlayers > 0 && Players >= MaxPlayers;
}
