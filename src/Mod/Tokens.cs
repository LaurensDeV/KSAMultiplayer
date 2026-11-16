using System.Net;

namespace KSAMultiplayer;

/// <summary>
/// What each server gave this machine when it made this player an admin, kept so that being an admin
/// survives a restart with nothing to type. <see cref="TokenBook"/> is how they are filed and found;
/// this is only where.
///
/// <para>Beside the panel's settings rather than in them: a token arrives while the game is running
/// and the settings are written when the game ends, and neither write should lose the other's.</para>
///
/// <para>KSA's documents folder is one StarMap or Borea instance's, so an instance made afresh has
/// no tokens and its player is an admin nowhere until opped again or until the file is copied over.</para>
/// </summary>
internal static class Tokens
{
    private static readonly TokenBook Book = new(
        Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "tokens.json"), Log.Warn);

    /// <inheritdoc cref="TokenBook.Joining"/>
    public static string Joining(string typed, IPEndPoint at) => Book.Joining(typed, at);

    /// <inheritdoc cref="TokenBook.Keep"/>
    public static bool Keep(string token) => Book.Keep(token);

    /// <inheritdoc cref="TokenBook.Forget"/>
    public static void Forget() => Book.Forget();
}
