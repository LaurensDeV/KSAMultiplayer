using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// Logging in with Discord, once per machine, through one login service for every server: the
/// directory, which alone holds the Discord application's secret.
///
/// <para>A game makes a key of its own and logs in by sending the public half through the browser to
/// the service, which has Discord say who the player is and signs a <see cref="DiscordCert"/> binding
/// that key to that Discord user. The certificate is caught on this machine
/// (<see cref="DiscordCallback"/>) and kept with the key (<see cref="DiscordIdentity"/>). At every
/// join after, the game signs what that server gave it to sign, with the address it typed
/// (<see cref="Proof"/>), and hands both over. The server checks the certificate with the service's
/// key, the signature with the certificate's, and that the address leads to itself.</para>
///
/// <para>The private key never leaves the machine, so what a server is handed is good for that
/// server's join alone: a server that has one of another server's admins join it cannot use what
/// they sent, since it names this server's nonce and address and the other wants its own.</para>
/// </summary>
public static class Discord
{
    /// <summary>The login service every game opens and every server takes the key of.</summary>
    public const string Service = "https://servers.ksamp.gg/v1/discord";

    /// <summary>Where on this machine a game listens for the browser to come back. Fixed, because the service sends it there.</summary>
    public const int CallbackPort = 50180;

    /// <summary>The longest a Discord user id is: a snowflake, at most 20 digits.</summary>
    public const int IdChars = 24;

    /// <summary>The longest a join's nonce is: 16 bytes in base64url.</summary>
    public const int NonceChars = 32;

    /// <summary>The longest a certificate is, signature and all.</summary>
    public const int CertChars = 1000;

    /// <summary>The longest a proof's signature is: a P-256 signature in base64url is 86 characters.</summary>
    public const int SignatureChars = 128;

    /// <summary>How long a certificate is good for, s: the machine logs in again after this.</summary>
    public const long CertSeconds = 30L * 24 * 3600;

    /// <summary>Whether this could be a Discord user's id: digits, and only so many.</summary>
    public static bool IsSnowflake(string s) => s.Length is > 0 and <= 20 && s.All(char.IsAsciiDigit);

    /// <summary>A fresh join's nonce, or the random half of a state.</summary>
    public static string Nonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// What a game sends the service through the browser and has back: the public half of its key,
    /// for the certificate, and a random part only this game knows, so an answer some other page
    /// sent the browser to is not taken for this login's.
    /// </summary>
    public static string State(string publicKey, string mine) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new LoginState(mine, publicKey)));

    /// <param name="R">The game's own random part.</param>
    /// <param name="K">The game's public key, SubjectPublicKeyInfo in base64url.</param>
    public sealed record LoginState(string R, string K);

    public static LoginState? ReadState(string state)
    {
        if (state.Length is 0 or > 1024) return null;
        try
        {
            LoginState? s = JsonSerializer.Deserialize<LoginState>(Base64Url.DecodeFromChars(state));
            return s is { R.Length: > 0 and <= 64, K.Length: > 0 and <= 300 } && PublicKey(s.K) is { } key && Dispose(key) ? s : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }

    /// <summary>A P-256 public key from its base64url SubjectPublicKeyInfo, or null for anything else.</summary>
    public static ECDsa? PublicKey(string spki)
    {
        try
        {
            ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(spki), out _);
            if (key.KeySize == 256) return key;
            key.Dispose();
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { }
        return null;
    }

    /// <summary>The service's page that starts a login.</summary>
    public static string LoginUrl(string service, string state) => $"{service}/login?state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// What a game signs to prove at a join that it holds the key its certificate names: the nonce
    /// that server gave it and the address the player typed, so the signature is good for that
    /// join alone.
    /// </summary>
    public static byte[] Proof(string nonce, string aud) => Encoding.UTF8.GetBytes($"ksamp-discord-join\n{nonce}\n{Aud(aud)}");

    /// <summary>An address as a proof names it: as typed, without the spaces or the case.</summary>
    public static string Aud(string typed) => Clip(typed.Trim().ToLowerInvariant(), 255);

    /// <summary>
    /// The certificate in what the browser asked of the callback, or why there is none. The state
    /// has to be the one this game sent.
    /// </summary>
    public static (string? Cert, string? Error) ReadCallback(string target, string state)
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
        return query.TryGetValue("cert", out string? cert) && cert.Length is > 0 and <= CertChars
            ? (cert, null)
            : (null, "the login came back with nothing");
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
/// What the login service says of a machine's key: whose it is on Discord, until when. Signed with
/// ECDSA P-256, which every .NET has built in.
/// </summary>
/// <param name="Id">Their Discord user id.</param>
/// <param name="Name">What Discord shows them as.</param>
/// <param name="Key">The machine's public key, SubjectPublicKeyInfo in base64url.</param>
/// <param name="Exp">Unix seconds after which it is no good.</param>
public sealed record DiscordCert(string Id, string Name, string Key, long Exp)
{
    /// <summary><c>payload.signature</c>, each base64url.</summary>
    public string Sign(ECDsa by)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(this);
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(by.SignData(payload, HashAlgorithmName.SHA256))}";
    }

    /// <summary>The certificate, if it is signed by this key and well formed; its expiry is the caller's to check.</summary>
    public static DiscordCert? Verify(string cert, ECDsa by)
    {
        int dot = cert.IndexOf('.');
        if (dot <= 0 || cert.Length > Discord.CertChars) return null;
        try
        {
            byte[] payload = Base64Url.DecodeFromChars(cert.AsSpan(0, dot));
            byte[] signature = Base64Url.DecodeFromChars(cert.AsSpan(dot + 1));
            if (!by.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return null;
            DiscordCert? c = JsonSerializer.Deserialize<DiscordCert>(payload);
            return c is { Id.Length: > 0, Key.Length: > 0 } && Discord.IsSnowflake(c.Id) ? c : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The payload alone, read without checking the signature: what this machine says of its own certificate.</summary>
    public static DiscordCert? Read(string cert)
    {
        int dot = cert.IndexOf('.');
        if (dot <= 0) return null;
        try
        {
            return JsonSerializer.Deserialize<DiscordCert>(Base64Url.DecodeFromChars(cert.AsSpan(0, dot)));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a join's signature was made with this certificate's key.</summary>
    public bool Signed(string nonce, string aud, string signature)
    {
        if (Discord.PublicKey(Key) is not { } key) return false;
        using (key)
        {
            try
            {
                return key.VerifyData(Discord.Proof(nonce, aud), Base64Url.DecodeFromChars(signature), HashAlgorithmName.SHA256);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// This machine's Discord login: its key and the certificate the service signed for it, kept in a
/// file of their own. Nothing of KSA is here, so a test can keep one in a folder of its own.
///
/// <para>The private key is as good as the login for as long as the certificate runs, as a token
/// is as good as being an admin; where the file is, is the game's to say.</para>
/// </summary>
public sealed class DiscordIdentity(string path, Action<string>? warn = null)
{
    private sealed record Kept(string Private, string Cert);

    private ECDsa? _key;
    private string _cert = "";
    private bool _read;

    /// <summary>The certificate, or empty while this machine is not logged in or its login has run out.</summary>
    public string Cert
    {
        get
        {
            Read();
            return Who is { } c && c.Exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? _cert : "";
        }
    }

    /// <summary>Who this machine is logged in as, read from its own certificate, or null.</summary>
    public DiscordCert? Who
    {
        get
        {
            Read();
            return _cert.Length > 0 ? DiscordCert.Read(_cert) : null;
        }
    }

    /// <summary>A key for a login about to start: a fresh one each time, which the certificate that comes back is for.</summary>
    public (ECDsa Key, string Public) NewKey()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Keeps a key and the certificate that came back for it, and says whether it is on disk.</summary>
    public bool Keep(ECDsa key, string cert)
    {
        (_key, _cert, _read) = (key, cert, true);
        return Write(new Kept(Base64Url.EncodeToString(key.ExportPkcs8PrivateKey()), cert));
    }

    /// <summary>Forgets the login, as Log out does.</summary>
    public void Forget()
    {
        (_key, _cert, _read) = (null, "", true);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not delete {path}: {e.Message}");
        }
    }

    /// <summary>What to hand a server at a join: the certificate and the signature over its nonce and the address typed, or null while not logged in.</summary>
    public (string Cert, string Signature)? Prove(string nonce, string aud)
    {
        string cert = Cert;
        if (cert.Length == 0 || _key is null) return null;
        return (cert, Base64Url.EncodeToString(_key.SignData(Discord.Proof(nonce, aud), HashAlgorithmName.SHA256)));
    }

    private void Read()
    {
        if (_read) return;
        _read = true;
        try
        {
            if (!File.Exists(path)) return;
            if (JsonSerializer.Deserialize<Kept>(File.ReadAllText(path)) is not { Private.Length: > 0, Cert.Length: > 0 } kept) return;
            ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Base64Url.DecodeFromChars(kept.Private), out _);
            (_key, _cert) = (key, kept.Cert);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException or CryptographicException)
        {
            warn?.Invoke($"could not read {path}: {e.Message}; log in with Discord again");
        }
    }

    // Written beside and moved over, so a game killed mid-write keeps the login it had.
    private bool Write(Kept kept)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            File.WriteAllText(path + ".new", JsonSerializer.Serialize(kept));
            File.Move(path + ".new", path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not write {path}: {e.Message}; this login lasts until the game is closed");
            return false;
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

    /// <summary>The certificate the service sent, once it has.</summary>
    public string? Cert { get; private set; }

    /// <summary>Why there will be no certificate, once that is known.</summary>
    public string? Error { get; private set; }

    public bool Finished => Cert is not null || Error is not null;

    /// <summary>Listens at once. Throws <see cref="SocketException"/> when the port is taken, as by another login still waiting.</summary>
    /// <param name="publicKey">The public half of the key the certificate is to be for.</param>
    public DiscordCallback(string publicKey, int port = Discord.CallbackPort)
    {
        State = Discord.State(publicKey, Discord.Nonce());
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
                    Answer(stream, "404 Not Found", Look.Neutral, "Nothing here", "This is where KSA Multiplayer waits for a Discord login.");
                    continue;
                }
                (string? cert, string? error) = Discord.ReadCallback(parts[1], State);
                // An answer for another login is not this one's: this one goes on waiting for its own.
                if (error == Discord.NotThisLogin)
                {
                    Answer(stream, "400 Bad Request", Look.Failed, "Not this login", "This isn't the login the game is waiting for. Go back to the game and press Log in with Discord again.");
                    continue;
                }
                if (cert is not null) Answer(stream, "200 OK", Look.Done, "You're logged in", "Close this tab and go back to the game. This computer stays logged in for 30 days.");
                else Answer(stream, "200 OK", Look.Failed, "Not logged in", $"{Capital(error ?? "something went wrong")}. Close this tab and try again from the game.");
                (Cert, Error) = (cert, error);
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

    private enum Look { Done, Failed, Neutral }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // One page, everything in it: it is served from this machine with nothing else to load, and a
    // browser coming back from Discord should land somewhere that looks like it meant to.
    private static void Answer(Stream stream, string status, Look look, string title, string text)
    {
        (string accent, string mark) = look switch
        {
            Look.Done => ("#3ddc97", "<path d=\"M14 25l7 7 15-16\"/>"),
            Look.Failed => ("#ff6b6b", "<path d=\"M17 17l14 14M31 17L17 31\"/>"),
            _ => ("#8b93ff", "<path d=\"M24 15v11M24 32v1\"/>"),
        };
        string html = $$$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{WebUtility.HtmlEncode(title)}}} · KSA Multiplayer</title>
            <style>
            *{box-sizing:border-box;margin:0}
            html,body{height:100%}
            body{display:grid;place-items:center;padding:24px;color:#e8eaf6;
              font:16px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
              background:#070a1a radial-gradient(ellipse at 20% 0%,#1d2457 0%,transparent 55%),
                radial-gradient(ellipse at 90% 100%,#2a1550 0%,transparent 50%);
              background-color:#070a1a;overflow:hidden}
            body::before{content:"";position:fixed;inset:0;pointer-events:none;opacity:.7;
              background-image:radial-gradient(1px 1px at 12% 22%,#fff 50%,transparent 51%),
                radial-gradient(1px 1px at 72% 14%,#fff 50%,transparent 51%),
                radial-gradient(1.5px 1.5px at 38% 68%,#cfd6ff 50%,transparent 51%),
                radial-gradient(1px 1px at 86% 58%,#fff 50%,transparent 51%),
                radial-gradient(1px 1px at 55% 35%,#fff 50%,transparent 51%),
                radial-gradient(1.5px 1.5px at 22% 86%,#fff 50%,transparent 51%),
                radial-gradient(1px 1px at 64% 88%,#cfd6ff 50%,transparent 51%),
                radial-gradient(1px 1px at 93% 30%,#fff 50%,transparent 51%),
                radial-gradient(1px 1px at 5% 50%,#fff 50%,transparent 51%)}
            .card{position:relative;width:min(440px,100%);padding:40px 36px 32px;text-align:center;
              border-radius:20px;background:rgba(20,24,52,.72);border:1px solid rgba(255,255,255,.09);
              box-shadow:0 30px 80px -20px rgba(0,0,0,.7),0 0 0 1px rgba(88,101,242,.08) inset;
              backdrop-filter:blur(10px);animation:rise .5s cubic-bezier(.2,.8,.2,1) both}
            .badge{width:84px;height:84px;margin:0 auto 22px;border-radius:50%;display:grid;place-items:center;
              background:radial-gradient(circle,{{{accent}}}33 0%,{{{accent}}}11 60%,transparent 70%);
              box-shadow:0 0 0 1px {{{accent}}}55,0 0 40px {{{accent}}}40}
            .badge svg{width:48px;height:48px;fill:none;stroke:{{{accent}}};stroke-width:3.5;
              stroke-linecap:round;stroke-linejoin:round;stroke-dasharray:40;animation:draw .6s .25s ease-out both}
            h1{font-size:26px;font-weight:700;letter-spacing:-.01em;margin-bottom:10px}
            p{color:#aab0d6;font-size:15px}
            .brand{margin-top:28px;padding-top:18px;border-top:1px solid rgba(255,255,255,.07);
              display:flex;justify-content:center;align-items:center;gap:8px;
              font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:#7179a8}
            .brand b{color:#5865f2;font-weight:700}
            @keyframes rise{from{opacity:0;transform:translateY(14px) scale(.98)}}
            @keyframes draw{from{stroke-dashoffset:40}to{stroke-dashoffset:0}}
            @media (prefers-reduced-motion:reduce){.card,.badge svg{animation:none}}
            </style></head><body>
            <main class="card">
              <div class="badge"><svg viewBox="0 0 48 48" aria-hidden="true">{{{mark}}}</svg></div>
              <h1>{{{WebUtility.HtmlEncode(title)}}}</h1>
              <p>{{{WebUtility.HtmlEncode(text)}}}</p>
              <div class="brand">KSA Multiplayer <span aria-hidden="true">·</span> <b>Discord</b></div>
            </main></body></html>
            """;
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
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
