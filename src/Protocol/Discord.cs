using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// Logging in with Discord, through one login service for every server: the directory, which alone
/// holds the Discord application's secret. A game opens the service's page in the browser, the
/// service has Discord say who the player is and hands back a signed <see cref="DiscordPass"/>,
/// caught on this machine (<see cref="DiscordCallback"/>), which the game gives the server it is in.
/// The server checks it with the service's public key and needs nothing of Discord's.
///
/// <para>A pass names the login it was asked for and the address the player typed, and a server
/// takes it only for the login it gave that player and an address that leads to itself. Otherwise
/// a server that had one of another server's admins log in on it could hand their pass on as its
/// own player's. The address is the one thing here a server cannot say for itself, as with
/// <see cref="TokenBook"/>.</para>
/// </summary>
public static class Discord
{
    /// <summary>The login service every game opens and every server takes the key of.</summary>
    public const string Service = "https://servers.ksamp.gg/v1/discord";

    /// <summary>Where on this machine a game listens for the browser to come back. Fixed, because the service sends it there.</summary>
    public const int CallbackPort = 50180;

    /// <summary>The longest a Discord user id is: a snowflake, at most 20 digits.</summary>
    public const int IdChars = 24;

    /// <summary>The longest a login's nonce is: 16 bytes in base64url.</summary>
    public const int NonceChars = 32;

    /// <summary>The longest a pass is, signature and all.</summary>
    public const int PassChars = 1000;

    /// <summary>How long a pass is good for, s: long enough to get back to the game, too short to keep.</summary>
    public const int PassSeconds = 300;

    /// <summary>Whether this could be a Discord user's id: digits, and only so many.</summary>
    public static bool IsSnowflake(string s) => s.Length is > 0 and <= 20 && s.All(char.IsAsciiDigit);

    /// <summary>A fresh login's nonce, or the random half of a state.</summary>
    public static string Nonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// What a game sends the service through the browser and has back: the address it typed and the
    /// server's nonce, for the pass, and a random part only this game knows, so an answer some other
    /// page sent the browser to is not taken for this login's.
    /// </summary>
    public static string State(string typed, string nonce, string mine) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new LoginState(mine, Clip(typed.Trim().ToLowerInvariant(), 255), nonce)));

    /// <param name="R">The game's own random part.</param>
    /// <param name="A">The server's address, as typed.</param>
    /// <param name="N">The server's nonce for this player.</param>
    public sealed record LoginState(string R, string A, string N);

    public static LoginState? ReadState(string state)
    {
        if (state.Length is 0 or > 1024) return null;
        try
        {
            LoginState? s = JsonSerializer.Deserialize<LoginState>(Base64Url.DecodeFromChars(state));
            return s is { R.Length: > 0 and <= 64, A.Length: > 0 and <= 255, N.Length: > 0 and <= NonceChars } ? s : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The service's page that starts a login.</summary>
    public static string LoginUrl(string service, string state) => $"{service}/login?state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// The pass in what the browser asked of the callback, or why there is none. The state has to be
    /// the one this game sent.
    /// </summary>
    public static (string? Pass, string? Error) ReadCallback(string target, string state)
    {
        Dictionary<string, string> query = Query(target);
        if (!query.TryGetValue("state", out string? said) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(said), Encoding.UTF8.GetBytes(state)))
        {
            return (null, NotThisLogin);
        }
        if (query.TryGetValue("error", out string? error))
        {
            return (null, error == "access_denied" ? "you cancelled the Discord login" : Clip(error, 200));
        }
        return query.TryGetValue("pass", out string? pass) && pass.Length is > 0 and <= PassChars
            ? (pass, null)
            : (null, "the login came back with no pass");
    }

    internal const string NotThisLogin = "the answer wasn't for this login";

    /// <summary>The query of a request target, unescaped.</summary>
    public static Dictionary<string, string> Query(string target)
    {
        int q = target.IndexOf('?');
        Dictionary<string, string> query = [];
        if (q < 0) return query;
        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            query[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return query;
    }

    private static string Clip(string s, int chars) => s.Length > chars ? s[..chars] : s;
}

/// <summary>
/// What the login service says of a player: who they are on Discord, which address they typed and
/// which login it answers, until when. Signed with ECDSA P-256, which every .NET has built in.
/// </summary>
/// <param name="Id">Their Discord user id.</param>
/// <param name="Name">What Discord shows them as.</param>
/// <param name="Aud">The server's address as the player typed it.</param>
/// <param name="N">The nonce the server gave this player.</param>
/// <param name="Exp">Unix seconds after which it is no good.</param>
public sealed record DiscordPass(string Id, string Name, string Aud, string N, long Exp)
{
    /// <summary><c>payload.signature</c>, each base64url.</summary>
    public string Sign(ECDsa key)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(this);
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(key.SignData(payload, HashAlgorithmName.SHA256))}";
    }

    /// <summary>The pass, if it is signed by this key and well formed; nothing is checked of what it says.</summary>
    public static DiscordPass? Verify(string pass, ECDsa key)
    {
        int dot = pass.IndexOf('.');
        if (dot <= 0 || pass.Length > Discord.PassChars) return null;
        try
        {
            byte[] payload = Base64Url.DecodeFromChars(pass.AsSpan(0, dot));
            byte[] signature = Base64Url.DecodeFromChars(pass.AsSpan(dot + 1));
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return null;
            DiscordPass? p = JsonSerializer.Deserialize<DiscordPass>(payload);
            return p is { Id.Length: > 0, Aud.Length: > 0, N.Length: > 0 } && Discord.IsSnowflake(p.Id) ? p : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Waits on this machine for the browser to come back from the login service, for one login, on a
/// thread of its own. A plain socket and not <c>HttpListener</c>, which on Windows needs a URL
/// reservation an ordinary user does not have; one request line is all there is to read.
/// </summary>
public sealed class DiscordCallback : IDisposable
{
    private readonly TcpListener _listener;
    private volatile bool _done;

    /// <summary>The state to send, which the answer has to carry back.</summary>
    public string State { get; }

    /// <summary>The pass the service sent, once it has; then handed to the server.</summary>
    public string? Pass { get; private set; }

    /// <summary>Why there will be no pass, once that is known.</summary>
    public string? Error { get; private set; }

    public bool Finished => Pass is not null || Error is not null;

    /// <summary>Listens at once. Throws <see cref="SocketException"/> when the port is taken, as by another login still waiting.</summary>
    /// <param name="typed">The server's address as the player gave it.</param>
    /// <param name="nonce">What the server gave this player to log in with.</param>
    public DiscordCallback(string typed, string nonce, int port = Discord.CallbackPort)
    {
        State = Discord.State(typed, nonce, Discord.Nonce());
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Thread t = new(Run) { IsBackground = true, Name = "KSAMultiplayer Discord login" };
        t.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private void Run()
    {
        // Until an answer for this login: a browser also asks for /favicon.ico, and anything else on
        // the machine may knock.
        while (!_done)
        {
            try
            {
                using TcpClient client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = client.SendTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                string? line = FirstLine(stream);
                string[] parts = line?.Split(' ') ?? [];
                if (parts.Length < 2 || parts[0] != "GET" || !parts[1].StartsWith("/ksamp", StringComparison.Ordinal))
                {
                    Answer(stream, "404 Not Found", "Nothing here.");
                    continue;
                }
                (string? pass, string? error) = Discord.ReadCallback(parts[1], State);
                // An answer for another login is not this one's: this one goes on waiting for its own.
                if (error == Discord.NotThisLogin)
                {
                    Answer(stream, "400 Bad Request", "This wasn't the login KSA Multiplayer was waiting for. Go back to the game and press the button again.");
                    continue;
                }
                Answer(stream, "200 OK", pass is not null
                    ? "Logged in. You can close this tab and go back to the game."
                    : $"Not logged in: {error}. You can close this tab.");
                (Pass, Error) = (pass, error);
                _done = true;
            }
            // One browser that went quiet is not the end of the login; a listener that was stopped is.
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (_done || e is ObjectDisposedException or InvalidOperationException) return;
            }
        }
    }

    private static string? FirstLine(Stream stream)
    {
        StringBuilder line = new();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
        {
            if (line.Length > 4096) return null;
            if (b != '\r') line.Append((char)b);
        }
        return line.ToString();
    }

    private static void Answer(Stream stream, string status, string text)
    {
        string html = $"<!doctype html><meta charset=utf-8><title>KSA Multiplayer</title><body style=\"font-family:sans-serif;padding:2em\"><p>{WebUtility.HtmlEncode(text)}</p>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(head);
        stream.Write(body);
    }

    /// <summary>Gives up waiting, as when the player leaves the server first.</summary>
    public void Dispose()
    {
        if (!Finished) Error ??= "the login was given up";
        _done = true;
        _listener.Stop();
    }
}
