using System.Net.Sockets;

namespace KSAMultiplayer;

/// <summary>
/// A player logging in with Discord from the Multiplayer window: the browser opened on the
/// login service's page, the pass it signs caught on this machine, and handed to the server, which
/// checks it and says who it was (<see cref="Client.Discord"/>).
/// </summary>
internal static class DiscordLogin
{
    private static DiscordCallback? _waiting;

    /// <summary>What became of the last login on this machine's side, for the window: empty when there is nothing to say.</summary>
    public static string Said { get; private set; } = "";

    /// <summary>Whether the browser is out at Discord and this game is waiting for it to come back.</summary>
    public static bool Waiting => _waiting is not null;

    /// <summary>
    /// The page the login was opened on, for the player to copy into another browser: the one the
    /// system opens may not be the one they are logged in to Discord in. Good for as long as this
    /// login waits, since what comes back is checked against it.
    /// </summary>
    public static string Url { get; private set; } = "";

    /// <param name="typed">The server's address as the player gave it, which the pass names.</param>
    public static void Start(Client c, string typed)
    {
        Stop();
        if (c.DiscordNonce.Length == 0 || typed.Length == 0) return;
        try
        {
            _waiting = new DiscordCallback(typed, c.DiscordNonce);
        }
        catch (SocketException e)
        {
            Said = $"couldn't listen for the login on port {Discord.CallbackPort}: {e.Message}";
            Log.Warn(Said);
            return;
        }
        Url = Discord.LoginUrl(Discord.Service, _waiting.State);
        try
        {
            // Always the one service, never one a server names: a page a server could choose is a
            // page made to look like Discord's.
            using global::System.Diagnostics.Process? opened = global::System.Diagnostics.Process.Start(
                new global::System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });
            Said = "";
            Log.Info("opened the Discord login in the browser");
        }
        // Still waiting: the link can be copied into a browser by hand.
        catch (Exception e)
        {
            Said = $"couldn't open the browser: {e.Message}; copy the link instead";
            Log.Warn(Said);
        }
    }

    /// <summary>Once a frame while joined: hands the pass over as soon as the browser has brought it back.</summary>
    public static void Update(Client c)
    {
        if (_waiting is not { Finished: true } done) return;
        if (done.Pass is { } pass)
        {
            c.LogInWithDiscord(pass);
            Said = "";
        }
        else Said = done.Error ?? "";
        Stop();
    }

    /// <summary>Gives up on a login still out at Discord, as when the player leaves the server.</summary>
    public static void Stop()
    {
        _waiting?.Dispose();
        (_waiting, Url) = (null, "");
    }

    /// <summary>Forgets what the last login said, for a server joined afresh.</summary>
    public static void Forget()
    {
        Stop();
        Said = "";
    }
}
