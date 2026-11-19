using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>What a login service signs passes with, and the addresses it saw this server ask from.</summary>
public sealed record LoginKey(ECDsa Key, IPAddress[] Seen);

/// <summary>Where a server learns the key a pass is checked against, and the address a pass names leads.</summary>
public interface ILoginService
{
    Task<LoginKey> Key(CancellationToken cancel);

    /// <summary>Where an address a player typed leads: a port given or a service record followed, then the name looked up.</summary>
    Task<IPAddress[]> Resolve(string typed, CancellationToken cancel);
}

/// <summary>
/// The login service over HTTPS. Asked once over IPv4 and once over IPv6, since the answer says
/// where this server was seen from, and a player may reach it by either.
/// </summary>
public sealed class LoginService(string url) : ILoginService
{
    public string Url => url;

    public async Task<LoginKey> Key(CancellationToken cancel)
    {
        Task<(byte[]? Key, IPAddress? You)> v4 = Ask(AddressFamily.InterNetwork, cancel), v6 = Ask(AddressFamily.InterNetworkV6, cancel);
        (byte[]? Key, IPAddress? You)[] both = await Task.WhenAll(v4, v6);
        byte[] spki = both.Select(b => b.Key).FirstOrDefault(k => k is not null)
                      ?? throw new HttpRequestException($"{url}/key gave no key");
        ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return new LoginKey(key, [.. both.Select(b => b.You).OfType<IPAddress>()]);
    }

    private async Task<(byte[]?, IPAddress?)> Ask(AddressFamily family, CancellationToken cancel)
    {
        try
        {
            using SocketsHttpHandler handler = new()
            {
                ConnectCallback = async (context, ct) =>
                {
                    Socket socket = new(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        IPAddress[] at = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, ct);
                        await socket.ConnectAsync(at, context.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Listings.Agent);
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync($"{url}/key", cancel));
            byte[]? key = doc.RootElement.TryGetProperty("key", out JsonElement k) && k.GetString() is { } b64 ? Convert.FromBase64String(b64) : null;
            IPAddress? you = doc.RootElement.TryGetProperty("you", out JsonElement y) && IPAddress.TryParse(y.GetString(), out IPAddress? ip) ? ip : null;
            return (key, you);
        }
        // A machine with no IPv6, or none of either, is told so by the family that answered.
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException or FormatException)
        {
            return (null, null);
        }
    }

    public Task<IPAddress[]> Resolve(string typed, CancellationToken cancel) => Addresses.Of(typed, cancel);
}

/// <summary>What an address a player typed comes to, and whether it is this machine.</summary>
public static class Addresses
{
    public static async Task<IPAddress[]> Of(string typed, CancellationToken cancel)
    {
        (string host, int? port) = Split(typed);
        if (IPAddress.TryParse(host, out IPAddress? literal)) return [literal];
        // As the game does: a name without a port may have a service record saying where it is.
        if (port is null && await Srv.LookupAsync(host, TimeSpan.FromSeconds(2), cancel) is { } found) host = found.Host;
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancel);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    /// <summary>A host and the port typed after it, if one was.</summary>
    public static (string Host, int? Port) Split(string typed)
    {
        string a = typed.Trim();
        if (a.StartsWith('[') && a.IndexOf(']') is int close and > 0)
        {
            string rest = a[(close + 1)..];
            return (a[1..close], rest.StartsWith(':') && int.TryParse(rest[1..], out int p6) ? p6 : null);
        }
        int colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':') == colon && int.TryParse(a[(colon + 1)..], out int port)) return (a[..colon], port);
        return (a, null);
    }

    /// <summary>Every address this machine answers at: its own interfaces, and where the world sees it from.</summary>
    public static HashSet<IPAddress> Mine(IEnumerable<IPAddress> seen)
    {
        HashSet<IPAddress> mine = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        try
        {
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses) mine.Add(Plain(u.Address));
            }
        }
        catch (NetworkInformationException) { }
        foreach (IPAddress ip in seen) mine.Add(Plain(ip));
        return mine;
    }

    public static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
