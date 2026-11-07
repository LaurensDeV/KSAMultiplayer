using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// SRV records, read by hand.
///
/// <para>One machine holds several worlds, each on a UDP port of its own, and an address record
/// cannot carry a port: that is the one thing an SRV record is for. <c>_ksamp._udp.name</c> says
/// which host and which port the world behind <c>name</c> is on, so a player types the name alone.</para>
///
/// <para>Hand-written because .NET resolves names and nothing else -- <see cref="Dns"/> answers A
/// and AAAA, and there is no SRV in the BCL at all. The library everyone reaches for instead is a
/// second assembly, and this mod ships one. What is here is the small half of RFC 1035 that a client
/// needs: ask one question, read the answers, and nothing of a resolver's own job.</para>
///
/// <para>Treated as a hint throughout. Every failure -- no resolver, no answer, a mangled packet, a
/// record pointing nowhere -- ends as "no SRV here", and the caller falls back to the name and the
/// default port, which is what it did before any of this existed.</para>
/// </summary>
public static class Srv
{
    /// <summary>The service as RFC 2782 names one: this protocol, over UDP.</summary>
    public const string Service = "_ksamp._udp";

    private const int SrvType = 33, InClass = 1, DnsPort = 53;

    // 1232 is what a resolver may send over UDP without fragmenting on a 1280-byte path, which is
    // the figure the DNS flag day settled on. An answer longer than that sets TC and wants TCP; a
    // handful of SRV records is nowhere near it, so one that would be truncated is one to give up on.
    private const int MostBytes = 1232;

    /// <param name="Target">The host to dial, without a trailing dot. Empty is RFC 2782's "." -- the
    /// record saying this service is deliberately not here, which is not the same as no record.</param>
    public readonly record struct Record(ushort Priority, ushort Weight, ushort Port, string Target);

    /// <summary>
    /// What a name resolves to for this service, or null where nothing says: no record, no resolver
    /// that answers, or an answer that cannot be read. An address typed as digits is nobody's name
    /// and is never asked about.
    /// </summary>
    public static async Task<(string Host, int Port)?> LookupAsync(string host, TimeSpan within,
                                                                  CancellationToken ct = default)
    {
        host = host.Trim().TrimEnd('.');
        if (host.Length == 0 || IPAddress.TryParse(host, out _)) return null;

        if (await Resolve($"{Service}.{host}", within, ct) is not { Length: > 0 } found) return null;
        return Pick(found, Random.Shared) is { Target.Length: > 0 } one ? (one.Target, one.Port) : null;
    }

    /// <summary>
    /// The SRV records held under a name, exactly as asked for -- <c>_service._protocol.host</c>,
    /// whatever the service. Null where nothing answered; empty where a resolver said there are none.
    /// </summary>
    public static async Task<Record[]?> Resolve(string name, TimeSpan within, CancellationToken ct = default)
    {
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
        byte[] question;
        try
        {
            question = Ask(name, id);
        }
        catch (ArgumentException)
        {
            return null;
        }

        foreach (IPEndPoint resolver in Resolvers())
        {
            // Each resolver gets the whole of the time rather than a share of it: asking two is
            // already the unusual path, and a player on a slow link is better served by one answer
            // late than by two that were cut off.
            if (await AskOne(resolver, question, id, within, ct) is { } answered) return answered;
        }
        return null;
    }

    private static async Task<Record[]?> AskOne(IPEndPoint resolver, byte[] question, ushort id,
                                                TimeSpan within, CancellationToken ct)
    {
        try
        {
            using Socket s = new(resolver.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(within);
            await s.SendToAsync(question, resolver, stop.Token);
            byte[] buffer = new byte[MostBytes];
            EndPoint from = new IPEndPoint(resolver.Address.AddressFamily == AddressFamily.InterNetworkV6
                                               ? IPAddress.IPv6Any : IPAddress.Any, 0);
            // Round again rather than out: a datagram from anywhere but the resolver, or one carrying
            // somebody else's id, is not an answer to this and must not end the wait for the real one.
            while (!stop.IsCancellationRequested)
            {
                SocketReceiveFromResult got = await s.ReceiveFromAsync(buffer, from, stop.Token);
                if (!got.RemoteEndPoint.Equals(resolver)) continue;
                if (!Read(buffer.AsSpan(0, got.ReceivedBytes), id, out Record[] found)) continue;
                // Answered, whatever it said. A name with no record of this service has none at any
                // resolver, so asking the next one is a second wait for the same nothing.
                return found;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return null;
    }

    /// <summary>
    /// The resolvers this machine was given, in the order it was given them. Loopback is left in:
    /// on Linux that is where systemd-resolved listens, and on a machine whose only resolver is
    /// 127.0.0.53 throwing it away would leave nothing to ask.
    /// </summary>
    public static IPEndPoint[] Resolvers()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == OperationalStatus.Up)
                        .SelectMany(n => n.GetIPProperties().DnsAddresses)
                        .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        .Distinct()
                        .Take(3)
                        .Select(a => new IPEndPoint(a, DnsPort))];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>One question about one name, with recursion asked for, as a datagram to a resolver.</summary>
    public static byte[] Ask(string name, ushort id)
    {
        List<byte> m =
        [
            (byte)(id >> 8), (byte)id,
            0x01, 0x00,             // a question, asked of a resolver that should go and find out
            0x00, 0x01,             // one question
            0x00, 0x00,             // and no answers, authorities or additionals in a question
            0x00, 0x00,
            0x00, 0x00,
        ];
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException($"label longer than a name allows: {label}", nameof(name));
            m.Add((byte)bytes.Length);
            m.AddRange(bytes);
        }
        m.Add(0);
        m.AddRange([0x00, SrvType, 0x00, InClass]);
        if (m.Count > MostBytes) throw new ArgumentException($"not a name: {name}", nameof(name));
        return [.. m];
    }

    /// <summary>
    /// The SRV records in a resolver's answer to the question <paramref name="id"/> was asked under.
    /// False where this is not that answer, or is too mangled to trust any of; true with none found
    /// is the resolver saying there is no such record, which is an answer in itself.
    /// </summary>
    public static bool Read(ReadOnlySpan<byte> m, ushort id, out Record[] found)
    {
        found = [];
        if (m.Length < 12) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(m) != id) return false;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(m[2..]);
        if ((flags & 0x8000) == 0) return false;        // somebody else's question, not an answer
        if ((flags & 0x0200) != 0) return false;        // cut short: what is here may not be all there is
        if ((flags & 0x000F) != 0) return true;         // answered with a refusal or no such name

        int questions = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        int at = 12;
        for (int i = 0; i < questions; i++)
        {
            if (!Name(m, ref at, out _)) return false;
            at += 4;
            if (at > m.Length) return false;
        }

        List<Record> records = [];
        for (int i = 0; i < answers; i++)
        {
            if (!Name(m, ref at, out _)) return false;
            if (at + 10 > m.Length) return false;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[at..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(m[(at + 2)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(m[(at + 8)..]);
            at += 10;
            if (at + length > m.Length) return false;
            int next = at + length;
            // Anything else in here is the resolver being helpful -- the CNAME it followed, the
            // addresses of the targets -- and is stepped over by its own length.
            if (type == SrvType && cls == InClass && length >= 7)
            {
                int rdata = at;
                ushort priority = BinaryPrimitives.ReadUInt16BigEndian(m[rdata..]);
                ushort weight = BinaryPrimitives.ReadUInt16BigEndian(m[(rdata + 2)..]);
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(m[(rdata + 4)..]);
                rdata += 6;
                if (!Name(m, ref rdata, out string target)) return false;
                if (port != 0) records.Add(new Record(priority, weight, port, target));
            }
            at = next;
        }
        found = [.. records];
        return true;
    }

    /// <summary>
    /// Which of several records to dial, as RFC 2782 has it: the lowest priority offered, and among
    /// those a draw weighted by how much of the load each target asked for. A set that is all zero
    /// weight is drawn evenly, which is what a zone that never thought about it means.
    /// </summary>
    public static Record? Pick(IReadOnlyList<Record> found, Random chance)
    {
        if (found.Count == 0) return null;
        ushort first = found.Min(r => r.Priority);
        Record[] level = [.. found.Where(r => r.Priority == first)];
        int whole = level.Sum(r => (int)r.Weight);
        if (whole == 0) return level[chance.Next(level.Length)];
        int roll = chance.Next(whole) + 1;
        int running = 0;
        foreach (Record r in level)
        {
            running += r.Weight;
            if (running >= roll) return r;
        }
        return level[^1];
    }

    /// <summary>
    /// A name from <paramref name="at"/>, following the pointers a packet compresses itself with and
    /// leaving <paramref name="at"/> after the name as it was written here, not after whatever it
    /// pointed at. False for a name that runs off the end or points round in a circle.
    /// </summary>
    private static bool Name(ReadOnlySpan<byte> m, ref int at, out string name)
    {
        name = "";
        StringBuilder built = new();
        int cursor = at, hops = 0;
        bool jumped = false;
        while (true)
        {
            if ((uint)cursor >= (uint)m.Length) return false;
            byte length = m[cursor];
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= m.Length || ++hops > 16) return false;
                int to = ((length & 0x3F) << 8) | m[cursor + 1];
                if (!jumped) (at, jumped) = (cursor + 2, true);
                cursor = to;
                continue;
            }
            if ((length & 0xC0) != 0) return false;     // a form of label this has never had to read
            cursor++;
            if (length == 0)
            {
                if (!jumped) at = cursor;
                break;
            }
            if (cursor + length > m.Length || built.Length + length + 1 > 255) return false;
            if (built.Length > 0) built.Append('.');
            built.Append(Encoding.ASCII.GetString(m.Slice(cursor, length)));
            cursor += length;
        }
        name = built.ToString();
        return true;
    }
}
