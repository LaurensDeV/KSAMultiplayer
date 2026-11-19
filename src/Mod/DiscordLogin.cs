using System.Net.Sockets;
using System.Security.Cryptography;

namespace KSAMultiplayer;

/// <summary>
/// This machine's Discord login, made once from the Multiplayer windows and proved to every server
/// joined after (<see cref="Discord"/>). Logging in opens the login service's page in the browser,
/// with the public half of a key made for it; the certificate that comes back is kept with the key
/// in the Windows account's application data, and lasts 30 days.
/// </summary>
internal static class DiscordLogin
{
    /// <summary>
    /// Where the login is kept: the Windows account's own application data rather than KSA's
    /// documents folder, which is one StarMap or Borea instance's, so every instance on the machine
    /// is logged in at once. Local and not roaming, so the key stays on this machine.
    /// </summary>
    private static readonly string KeptAt =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSAMultiplayer", "discord.json");

    private static readonly DiscordIdentity Identity = new(Moved(KeptAt), Log.Warn);

    // A login kept in the instance's documents folder is taken along, once, so nobody logs in again for the move.
    private static string Moved(string to)
    {
        string from = Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "discord.json");
        try
        {
            if (!File.Exists(to) && File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
                Log.Info($"moved the Discord login to {to}, where every KSA instance finds it");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not move the Discord login to {to}: {e.Message}");
        }
        return to;
    }

    private static DiscordCallback? _waiting;
    private static ECDsa? _key;
    private static string _public = "";

    // What was last proved to the server joined, so it is said once a join and again for a new login.
    private static string _proved = "";

    /// <summary>What became of the last login, for the windows: empty when there is nothing to say.</summary>
    public static string Said { get; private set; } = "";

    /// <summary>Whether the browser is out at Discord and this game is waiting for it to come back.</summary>
    public static bool Waiting => _waiting is not null;

    /// <summary>
    /// The page the login was opened on, for the player to copy into another browser: the one the
    /// system opens may not be the one they are logged in to Discord in. Good for as long as this
    /// login waits, since what comes back is checked against it.
    /// </summary>
    public static string Url { get; private set; } = "";

    /// <summary>Who this machine is logged in as, or null while it is not, or its login has run out.</summary>
    public static DiscordCert? Who => Identity.Cert.Length > 0 ? Identity.Who : null;

    public static void Start()
    {
        Stop();
        (_key, _public) = Identity.NewKey();
        try
        {
            _waiting = new DiscordCallback(_public);
        }
        catch (SocketException e)
        {
            Stop();
            Said = $"couldn't listen for the login on port {Discord.CallbackPort}: {e.Message}";
            Log.Warn(Said);
            return;
        }
        Url = Discord.LoginUrl(Discord.Service, _waiting.State);
        Said = "";
        try
        {
            // Always the one service, never one a server names: a page a server could choose is a
            // page made to look like Discord's.
            using global::System.Diagnostics.Process? opened = global::System.Diagnostics.Process.Start(
                new global::System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
            Log.Info("opened the Discord login in the browser");
        }
        // Still waiting: the link can be copied into a browser by hand.
        catch (Exception e)
        {
            Said = $"couldn't open the browser: {e.Message}; copy the link instead";
            Log.Warn(Said);
        }
    }

    /// <summary>Once a frame, in a server or not: keeps the certificate as soon as the browser has brought it back.</summary>
    public static void Update()
    {
        if (_waiting is not { Finished: true } done) return;
        if (done.Cert is { } cert && _key is { } key)
        {
            // For the key this login was started with, or it proves nothing anybody could check.
            if (DiscordCert.Read(cert)?.Key == _public)
            {
                Identity.Keep(key, cert);
                _key = null;
                Said = "";
                Log.Info($"logged in with Discord as {Identity.Who?.Name}");
            }
            else Said = "the login came back for another key: try again";
        }
        else Said = done.Error ?? "";
        Stop();
    }

    /// <summary>
    /// Once a frame while joined: proves this machine's login to the server, once a join and again
    /// after logging in afresh, as soon as the server has said it takes Discord logins.
    /// </summary>
    /// <param name="typed">The server's address as the player gave it, which the proof names.</param>
    public static void Prove(Client c, string typed)
    {
        if (!c.Joined || c.DiscordNonce.Length == 0 || c.DiscordAsking) return;
        string cert = Identity.Cert;
        string now = $"{c.DiscordNonce}|{cert}";
        if (cert.Length == 0 || now == _proved) return;
        if (Identity.Prove(c.DiscordNonce, typed) is not { } proof) return;
        _proved = now;
        c.ProveDiscord(proof.Cert, typed, proof.Signature);
    }

    /// <summary>Forgets this machine's login, as Log out does.</summary>
    public static void LogOut()
    {
        Stop();
        Identity.Forget();
        Said = "";
        Log.Info("logged out of Discord");
    }

    /// <summary>Gives up on a login still out at Discord.</summary>
    public static void Stop()
    {
        _waiting?.Dispose();
        (_waiting, Url) = (null, "");
    }

    /// <summary>For a server joined afresh: what was proved to the last one is nothing to this one.</summary>
    public static void Left() => _proved = "";
}
