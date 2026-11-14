using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>
/// Who may run commands from the web viewer: whoever gave its password, for as long as they keep
/// using it. Its own password and not the in-game admins' tokens, since a token belongs to a game
/// and is said to it once, and a browser is not a game.
/// </summary>
public sealed class WebAdmin(Func<DateTime> clock)
{
    /// <summary>How long a session stands with nothing asked of it.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromHours(12.0);

    /// <summary>How many logins one address may try in <see cref="TriesWindow"/>, right or wrong.</summary>
    public const int Tries = 5;
    public static readonly TimeSpan TriesWindow = TimeSpan.FromMinutes(1.0);

    // A hash and never the password, so what is compared is always the same length: compared
    // byte for byte in constant time, a guess learns nothing from how long it took to be refused.
    private volatile byte[] _hash = [];
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Since, int Count)> _tried = new(StringComparer.Ordinal);

    public WebAdmin() : this(() => DateTime.UtcNow) { }

    /// <summary>Whether there is a password at all; without one nobody is let in, whatever they say.</summary>
    public bool On => _hash.Length > 0;

    /// <summary>The password, from the settings. A different one ends every session there is.</summary>
    public void SetPassword(string password)
    {
        byte[] next = password.Length > 0 ? SHA256.HashData(Encoding.UTF8.GetBytes(password)) : [];
        if (next.Length == _hash.Length && CryptographicOperations.FixedTimeEquals(next, _hash)) return;
        _hash = next;
        _sessions.Clear();
    }

    /// <summary>A session for whoever gave the right password, or null; an address over its tries is refused before the password is looked at.</summary>
    public string? Login(string password, string from)
    {
        DateTime now = clock();
        (DateTime since, int count) = _tried.AddOrUpdate(from, (now, 1), (_, t) => now - t.Since > TriesWindow ? (now, 1) : (t.Since, t.Count + 1));
        if (count > Tries || !On) return null;
        byte[] hash = _hash;
        byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        if (!CryptographicOperations.FixedTimeEquals(given, hash)) return null;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = now;
        return token;
    }

    /// <summary>Whether a session stands, and if so that it was used just now.</summary>
    public bool Valid(string? token)
    {
        if (!On || string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out DateTime last)) return false;
        DateTime now = clock();
        if (now - last > Idle)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = now;
        return true;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    /// <summary>Whether an address has used up its tries, for saying so rather than "wrong password".</summary>
    public bool Spent(string from) =>
        _tried.TryGetValue(from, out (DateTime Since, int Count) t) && clock() - t.Since <= TriesWindow && t.Count > Tries;
}
