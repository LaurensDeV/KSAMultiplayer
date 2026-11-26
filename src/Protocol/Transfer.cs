using System.IO.Compression;
using System.Text;

namespace KSAMultiplayer;

/// <summary>What a transfer carries: a whole world, one craft's definition, or a list the server keeps and a game needs whole.</summary>
public enum BlobKind : byte
{
    /// <summary>A save folder, zipped: what a player joining is put in.</summary>
    World = 1,

    /// <summary>One craft as KSA saves it, gzipped XML: what every other game builds it from.</summary>
    Vehicle = 2,

    /// <summary>The worlds a server keeps under names, for an admin's game: see <see cref="SaveList"/>.</summary>
    Saves = 3,

    /// <summary>The craft a server will not clear, for every game: see <see cref="ProtectedList"/>.</summary>
    Protected = 4,

    /// <summary>The server's log, gzipped text, for the operator who asked: see <see cref="LogText"/>.</summary>
    Log = 5,

    /// <summary>The kittens that have died and whose game they died in, for every game: see <see cref="MemorialList"/>.</summary>
    Memorial = 6,
}

/// <summary>
/// Something too big for a datagram, with what it is said in front of it so a receiver knows what it
/// has once every piece has come.
/// </summary>
/// <param name="Key">A craft's Id; nothing for a world.</param>
/// <param name="Owner">The player the craft is flown by, as the server says it.</param>
/// <param name="Universe">The universe time it was taken at.</param>
public sealed record Blob(BlobKind Kind, string Key, byte Owner, double Universe, byte[] Data)
{
    /// <summary>
    /// The most a world unpacks to, and the most files in it; and the most a craft's definition
    /// unpacks to. A save is tens of megabytes and a craft tens of kilobytes, and what comes from
    /// another machine says what it likes: a few hundred kilobytes can be written to unpack to
    /// gigabytes, in the game of everybody it is sent to.
    /// </summary>
    public const long WorldMostBytes = 512L * 1024 * 1024;
    public const int WorldMostFiles = 10_000;
    public const int VehicleMostBytes = 64 * 1024 * 1024;

    public byte[] Encode()
    {
        byte[] key = Encoding.UTF8.GetBytes(Wire.Clip(Key, Wire.KeyChars));
        using MemoryStream m = new();
        using BinaryWriter w = new(m);
        w.Write((byte)Kind);
        w.Write((byte)Math.Min(key.Length, 255));
        w.Write(key, 0, Math.Min(key.Length, 255));
        w.Write(Owner);
        w.Write(Universe);
        w.Write(Data);
        w.Flush();
        return m.ToArray();
    }

    public static Blob? Decode(byte[] bytes)
    {
        try
        {
            using BinaryReader r = new(new MemoryStream(bytes));
            BlobKind kind = (BlobKind)r.ReadByte();
            if (kind is not (BlobKind.World or BlobKind.Vehicle or BlobKind.Saves or BlobKind.Protected or BlobKind.Log or BlobKind.Memorial)) return null;
            string key = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
            byte owner = r.ReadByte();
            double universe = r.ReadDouble();
            byte[] data = r.ReadBytes(bytes.Length - (int)r.BaseStream.Position);
            return ClockFollow.SaneUniverse(universe) ? new Blob(kind, key, owner, universe, data) : null;
        }
        catch (Exception e) when (e is EndOfStreamException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why a zip is not a world to hand out, or null for one that is: read from what the zip says of
    /// itself, with nothing unpacked.
    /// </summary>
    public static string? WorldFault(byte[] packed)
    {
        try
        {
            using ZipArchive zip = new(new MemoryStream(packed), ZipArchiveMode.Read);
            if (zip.Entries.Count > WorldMostFiles) return $"it holds {zip.Entries.Count} files";
            long bytes = 0;
            bool any = false;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (!SafeName(entry.FullName)) return $"it holds '{Wire.Clip(entry.FullName, 64)}', which is not a name a save has";
                bytes += entry.Length;
                any = true;
            }
            if (bytes > WorldMostBytes) return $"it unpacks to {bytes / (1024 * 1024)} MB";
            return any ? null : "it holds no files";
        }
        catch (InvalidDataException)
        {
            return "it is not a zip";
        }
    }

    /// <summary>Files packed into one zip, each under its own name.</summary>
    /// <summary>
    /// A blob's size for a log line. Bytes under a kilobyte, because the empty worlds a server
    /// carries are a few hundred of them and "0 KB" reads as nothing at all.
    /// </summary>
    public static string Size(int bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024} KB";

    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> files)
    {
        using MemoryStream into = new();
        using (ZipArchive zip = new(into, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in files)
            {
                using Stream entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(data);
            }
        }
        return into.ToArray();
    }

    /// <summary>
    /// Whether two zips hold the same files. Not the same bytes: a zip stamps each file with when
    /// it was packed, so the stock empty world packed twice a few seconds apart differed.
    /// </summary>
    public static bool SameFiles(byte[] one, byte[] other)
    {
        try
        {
            List<(string Name, byte[] Data)> a = Unzip(one), b = Unzip(other);
            return a.Count == b.Count
                   && a.OrderBy(f => f.Name, StringComparer.Ordinal).Zip(b.OrderBy(f => f.Name, StringComparer.Ordinal))
                       .All(p => p.First.Name == p.Second.Name && p.First.Data.AsSpan().SequenceEqual(p.Second.Data));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The files a zip holds. A name that would land outside the folder it is unpacked into is refused: the zip comes from another machine.</summary>
    public static List<(string Name, byte[] Data)> Unzip(byte[] packed)
    {
        List<(string, byte[])> files = [];
        using ZipArchive zip = new(new MemoryStream(packed), ZipArchiveMode.Read);
        if (zip.Entries.Count > WorldMostFiles) throw new InvalidDataException($"a world holds {zip.Entries.Count} files, which is more than a save has");
        long left = WorldMostBytes;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!SafeName(entry.FullName)) throw new InvalidDataException($"a world holds '{entry.FullName}', which is not a name a save has");
            using Stream from = entry.Open();
            byte[] data = ReadAtMost(from, left, "a world");
            left -= data.Length;
            files.Add((entry.FullName, data));
        }
        return files;
    }

    // What a stream holds, counted as it is unpacked: what a zip says of its own sizes is not to be believed.
    private static byte[] ReadAtMost(Stream from, long most, string what)
    {
        using MemoryStream into = new();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (into.Length + n > most) throw new InvalidDataException($"{what} unpacks to more than it may");
            into.Write(buffer, 0, n);
        }
        return into.ToArray();
    }

    /// <summary>Whether a name is a plain relative path, with nothing in it that climbs out of the folder it is under.</summary>
    public static bool SafeName(string name) =>
        name.Length > 0 && !Path.IsPathRooted(name) && !name.Contains(':') && !name.Contains('\\')
        && name.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>Every file under a folder, zipped by its path relative to it.</summary>
    public static byte[] ZipFolder(string folder) =>
        Zip(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Select(path => (Path.GetRelativePath(folder, path).Replace('\\', '/'), File.ReadAllBytes(path))));

    /// <summary>A zip written out under a folder, which is emptied first.</summary>
    public static void UnzipInto(byte[] packed, string folder)
    {
        List<(string Name, byte[] Data)> files = Unzip(packed);
        string under = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        // What the file system makes of a name is asked of it, whatever SafeName made of it.
        foreach ((string name, _) in files)
        {
            if (!Path.GetFullPath(Path.Combine(folder, name)).StartsWith(under, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"a world holds '{name}', which lands outside its folder");
            }
        }
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        foreach ((string name, byte[] data) in files)
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }

    public static byte[] Gzip(byte[] data)
    {
        using MemoryStream into = new();
        using (GZipStream gz = new(into, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return into.ToArray();
    }

    /// <summary>A craft's definition unpacked, or an <see cref="InvalidDataException"/> for one bigger than a craft is.</summary>
    public static byte[] Gunzip(byte[] data)
    {
        using GZipStream gz = new(new MemoryStream(data), CompressionMode.Decompress);
        return ReadAtMost(gz, VehicleMostBytes, "a craft");
    }
}

/// <summary>
/// Every transfer between this end and one other: cut into pieces that each go whole in a datagram,
/// sent at a pace a home line's upload carries, the lost ones asked for again, and put back together.
/// </summary>
public sealed class Link
{
    /// <summary>The most bytes in one piece: with its header, under the 1,280 any path carries whole.</summary>
    public const int ChunkBytes = 1100;

    public const double BytesPerSecond = 400_000;

    // The receiver asks again for what is missing this often, and for all of it after so long with no piece.
    private const double NeedEverySeconds = 0.25, QuietSeconds = 1.0;

    // A sender that has sent everything and heard nothing for this long sends it all again, and gives
    // up after so long with nothing sent that was due or asked for and nothing heard: counted from
    // when it started, a world of 50 MB could never arrive at 400 KB a second.
    private const double ResendAfterSeconds = 2.0, GiveUpSeconds = 120.0;

    /// <summary>The most pieces a craft's definition comes in: 4.5 MB, many times any craft's.</summary>
    public const ushort VehiclePieces = 4096;

    /// <summary>
    /// The most pieces a transfer starting now may say it has. A server holds a player's to
    /// <see cref="VehiclePieces"/> unless it has asked that player for a world: every piece is kept
    /// until the last has come, and four transfers of 65,535 are 288 MB from one player.
    /// </summary>
    public ushort MostPieces { get; set; } = ushort.MaxValue;

    /// <summary>
    /// The most things one link may be sending here at once.
    ///
    /// <para>A transfer's pieces are held in an array as long as the sender says the transfer is, so
    /// one datagram of forty bytes claiming 65,535 pieces asked for half a megabyte -- and a fresh
    /// transfer number in the next one asked for another, with nothing let go of for two minutes. A
    /// few hundred of them filled a server's whole allowance; it was killed, restarted, and met the
    /// next few hundred. A link carries a world and a few craft at a time, never four at once.</para>
    /// </summary>
    private const int MostIncoming = 4;

    private readonly Dictionary<uint, Outgoing> _out = [];

    // How many bytes the line may be given now: one pace for the whole link, never one each, so a
    // burst of transfers is the same few hundred kilobytes a second as one. Forty definitions of a
    // craft coming apart, each at its own pace, put ten times their size on the wire and lost it
    // at the player's router.
    private double _allowance = 8.0 * ChunkBytes, _allowedAt = double.NaN;
    private readonly Dictionary<uint, Incoming> _in = [];
    private readonly Queue<uint> _finished = new();
    private readonly HashSet<uint> _finishedSet = [];
    private readonly Queue<uint> _gotQueue = new();
    private readonly HashSet<uint> _got = [];
    private uint _next;

    /// <summary>How many transfers are going out or coming in.</summary>
    public int Busy => _out.Count + _in.Count(i => !i.Value.Complete);

    /// <summary>The share of the newest transfer coming in that has come, while one is.</summary>
    public double? Arriving => _in.Values.Where(i => !i.Complete).OrderByDescending(i => i.Count).FirstOrDefault() is { } c ? (double)c.Have / c.Count : null;

    /// <summary>
    /// Whether the other end has said it has the whole of what <see cref="Send"/> numbered so:
    /// false while it is still going and for one given up on, which went nowhere. The newest 256 are
    /// remembered.
    /// </summary>
    public bool Arrived(uint id) => _got.Contains(id);

    /// <summary>Whether <see cref="Send"/>'s transfer of this number is still going out.</summary>
    public bool Going(uint id) => _out.ContainsKey(id);

    /// <summary>
    /// Starts sending something; its pieces go out from <see cref="Pump"/>, at most
    /// <see cref="MostIncoming"/> transfers at once, which is all the other end takes, and the rest
    /// in turn.
    /// </summary>
    /// <param name="first">Ahead of everything not yet begun: a world, or a list the other end acts on.
    /// A save asked for that waits behind its own game's craft is a save the server gives up on and
    /// asks of somebody else, whose world may not hold them.</param>
    /// <param name="replaces">What it is the newest of, such as a craft's name: one of the same that
    /// has not begun is dropped, since only the last word about a craft is any use.</param>
    public uint Send(byte[] data, double now, bool first = false, string? replaces = null)
    {
        if (replaces is not null)
        {
            foreach (Outgoing waiting in _out.Values.Where(o => !o.Started && o.Replaces == replaces).ToArray()) _out.Remove(waiting.Id);
        }
        uint id = ++_next;
        _out[id] = new Outgoing(id, data, first, replaces);
        return id;
    }

    /// <summary>The pieces due and the pieces to ask for again, said through <paramref name="say"/>.</summary>
    public void Pump(double now, Action<Wire.Message> say)
    {
        _allowance = Math.Min(_allowance + (double.IsNaN(_allowedAt) ? 0.0 : (now - _allowedAt) * BytesPerSecond), 64.0 * ChunkBytes);
        _allowedAt = now;
        foreach (Outgoing o in _out.Values.Where(o => o.Started).ToArray())
        {
            if (now - o.ActiveAt > GiveUpSeconds) _out.Remove(o.Id);
        }
        // Begun in turn, the first ones first and otherwise in the order asked: one not begun has
        // put nothing on the wire, and is not given up on for waiting.
        int going = _out.Values.Count(o => o.Started);
        foreach (Outgoing o in _out.Values.Where(o => !o.Started).OrderByDescending(o => o.First).ThenBy(o => o.Id).ToArray())
        {
            if (going >= MostIncoming) break;
            o.Start(now);
            going++;
        }
        foreach (Outgoing o in _out.Values.Where(o => o.Started).OrderBy(o => o.Id).ToArray())
        {
            foreach (Wire.Chunk c in o.Next(now, ref _allowance)) say(c);
        }
        foreach (Incoming i in _in.Values.ToArray())
        {
            if (i.Complete) continue;
            if (now - i.LastPieceAt > GiveUpSeconds)
            {
                _in.Remove(i.Id);
                continue;
            }
            if (now < i.NeedNext) continue;
            ushort[] missing = i.Missing(all: now - i.LastPieceAt > QuietSeconds);
            if (missing.Length > 0) say(new Wire.Need(i.Id, missing));
            i.NeedNext = now + NeedEverySeconds;
        }
    }

    /// <summary>
    /// A transfer message handled: the whole of something once its last piece has come, said once,
    /// and anything to answer said through <paramref name="say"/>. Null for anything else.
    /// </summary>
    public byte[]? Handle(Wire.Message message, double now, Action<Wire.Message> say)
    {
        switch (message)
        {
            case Wire.Chunk chunk:
                if (_finishedSet.Contains(chunk.Transfer))
                {
                    // Every piece came and the sender has not heard so.
                    say(new Wire.Got(chunk.Transfer));
                    return null;
                }
                if (!_in.TryGetValue(chunk.Transfer, out Incoming? incoming))
                {
                    // Asked of the piece before anything is held for the transfer: a piece that
                    // could never belong to one buys no room at all.
                    if (chunk.Count == 0 || chunk.Count > MostPieces || !Incoming.Could(chunk) || _in.Count >= MostIncoming) return null;
                    _in[chunk.Transfer] = incoming = new Incoming(chunk.Transfer, chunk.Count, now);
                }
                incoming.Add(chunk, now);
                if (!incoming.Complete) return null;
                _in.Remove(chunk.Transfer);
                Finished(chunk.Transfer);
                say(new Wire.Got(chunk.Transfer));
                return incoming.Assemble();
            case Wire.Need need when _out.TryGetValue(need.Transfer, out Outgoing? o):
                o.Need(need.Missing, now);
                return null;
            case Wire.Got got:
                if (_out.Remove(got.Transfer) && _got.Add(got.Transfer))
                {
                    _gotQueue.Enqueue(got.Transfer);
                    while (_gotQueue.Count > 256) _got.Remove(_gotQueue.Dequeue());
                }
                return null;
        }
        return null;
    }

    private void Finished(uint id)
    {
        _finished.Enqueue(id);
        _finishedSet.Add(id);
        while (_finished.Count > 256) _finishedSet.Remove(_finished.Dequeue());
    }

    private sealed class Outgoing
    {
        private readonly byte[] _data;
        private readonly Queue<ushort> _due = new();
        private readonly HashSet<ushort> _queued = [];
        private double _heardAt;
        private bool _unasked;

        public Outgoing(uint id, byte[] data, bool first, string? replaces)
        {
            int count = (data.Length + ChunkBytes - 1) / ChunkBytes;
            if (count > ushort.MaxValue) throw new InvalidDataException($"{data.Length} bytes is too big to send");
            (Id, _data, First, Replaces) = (id, data, first, replaces);
            Count = (ushort)Math.Max(1, count);
        }

        public uint Id { get; }
        public ushort Count { get; }
        public bool First { get; }
        public string? Replaces { get; }

        /// <summary>Whether a piece of it may have gone: until then it is only waiting its turn.</summary>
        public bool Started { get; private set; }

        public void Start(double now)
        {
            Started = true;
            (ActiveAt, _heardAt) = (now, now);
            for (ushort i = 0; i < Count; i++) Queue(i);
        }

        /// <summary>When a piece last went that was due or asked for, or the other end was last heard: not the sending of it all again to an end that says nothing.</summary>
        public double ActiveAt { get; private set; }

        public List<Wire.Chunk> Next(double now, ref double allowance)
        {
            List<Wire.Chunk> chunks = [];
            if (_due.Count == 0 && now - _heardAt > ResendAfterSeconds)
            {
                // Nothing heard: the first pieces may all have been lost, and the receiver knows of nothing to ask for.
                for (ushort i = 0; i < Count; i++) Queue(i);
                _heardAt = now;
                _unasked = true;
            }
            while (allowance >= ChunkBytes && _due.TryDequeue(out ushort i))
            {
                _queued.Remove(i);
                int from = i * ChunkBytes;
                chunks.Add(new Wire.Chunk(Id, i, Count, _data[from..Math.Min(from + ChunkBytes, _data.Length)]));
                allowance -= ChunkBytes;
            }
            if (chunks.Count > 0 && !_unasked) ActiveAt = now;
            return chunks;
        }

        public void Need(IEnumerable<ushort> missing, double now)
        {
            (_heardAt, ActiveAt, _unasked) = (now, now, false);
            foreach (ushort i in missing) if (i < Count) Queue(i);
        }

        private void Queue(ushort i)
        {
            if (_queued.Add(i)) _due.Enqueue(i);
        }
    }

    private sealed class Incoming(uint id, ushort count, double now)
    {
        private readonly byte[]?[] _pieces = new byte[]?[count];

        public uint Id { get; } = id;
        public ushort Count { get; } = count;
        public int Have { get; private set; }
        public bool Complete => Have == Count;
        public double LastPieceAt { get; private set; } = now;
        public double NeedNext { get; set; } = now + NeedEverySeconds;
        private int _highest = -1;

        /// <summary>
        /// Whether a piece could belong to a transfer at all: everything <see cref="Add"/> refuses it
        /// for that can be seen without one. Asked before a transfer is held open for it.
        /// </summary>
        public static bool Could(Wire.Chunk chunk) =>
            chunk.Index < chunk.Count
            && chunk.Data.Length <= ChunkBytes
            && (chunk.Index == chunk.Count - 1 || chunk.Data.Length == ChunkBytes);

        public void Add(Wire.Chunk chunk, double now)
        {
            if (chunk.Count != Count || chunk.Index >= Count || _pieces[chunk.Index] is not null) return;
            if (chunk.Data.Length > ChunkBytes || (chunk.Index < Count - 1 && chunk.Data.Length != ChunkBytes)) return;
            _pieces[chunk.Index] = chunk.Data;
            Have++;
            LastPieceAt = now;
            _highest = Math.Max(_highest, chunk.Index);
        }

        // Those under the highest that has come, which were sent and lost, or every missing one for a sending gone quiet.
        public ushort[] Missing(bool all)
        {
            List<ushort> missing = [];
            int below = all ? Count : _highest;
            for (int i = 0; i < below && missing.Count < Wire.MostNeeded; i++) if (_pieces[i] is null) missing.Add((ushort)i);
            return [.. missing];
        }

        public byte[] Assemble()
        {
            byte[] whole = new byte[_pieces.Sum(p => p!.Length)];
            int at = 0;
            foreach (byte[]? piece in _pieces)
            {
                piece!.CopyTo(whole, at);
                at += piece.Length;
            }
            return whole;
        }
    }
}
