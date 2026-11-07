using System.IO.Compression;
using System.Text;

namespace KSAMultiplayer;

/// <summary>What a transfer carries: a whole world, or one craft's definition.</summary>
public enum BlobKind : byte
{
    /// <summary>A save folder, zipped: what a player joining is put in.</summary>
    World = 1,

    /// <summary>One craft as KSA saves it, gzipped XML: what every other game builds it from.</summary>
    Vehicle = 2,
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
            if (kind is not (BlobKind.World or BlobKind.Vehicle)) return null;
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

    /// <summary>The files a zip holds. A name that would land outside the folder it is unpacked into is refused: the zip comes from another machine.</summary>
    public static List<(string Name, byte[] Data)> Unzip(byte[] packed)
    {
        List<(string, byte[])> files = [];
        using ZipArchive zip = new(new MemoryStream(packed), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (!SafeName(entry.FullName)) throw new InvalidDataException($"a world holds '{entry.FullName}', which is not a name a save has");
            using Stream from = entry.Open();
            using MemoryStream data = new();
            from.CopyTo(data);
            files.Add((entry.FullName, data.ToArray()));
        }
        return files;
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

    public static byte[] Gunzip(byte[] data)
    {
        using GZipStream gz = new(new MemoryStream(data), CompressionMode.Decompress);
        using MemoryStream into = new();
        gz.CopyTo(into);
        return into.ToArray();
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

    // A sender that has sent everything and heard nothing for this long sends it all again, and gives up after so long.
    private const double ResendAfterSeconds = 2.0, GiveUpSeconds = 120.0;

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
    private readonly Dictionary<uint, Incoming> _in = [];
    private readonly Queue<uint> _finished = new();
    private readonly HashSet<uint> _finishedSet = [];
    private uint _next;

    /// <summary>How many transfers are going out or coming in.</summary>
    public int Busy => _out.Count + _in.Count(i => !i.Value.Complete);

    /// <summary>The share of the newest transfer coming in that has come, while one is.</summary>
    public double? Arriving => _in.Values.Where(i => !i.Complete).OrderByDescending(i => i.Count).FirstOrDefault() is { } c ? (double)c.Have / c.Count : null;

    /// <summary>Starts sending something; its pieces go out from <see cref="Pump"/>.</summary>
    public uint Send(byte[] data, double now)
    {
        uint id = ++_next;
        _out[id] = new Outgoing(id, data, now);
        return id;
    }

    /// <summary>The pieces due and the pieces to ask for again, said through <paramref name="say"/>.</summary>
    public void Pump(double now, Action<Wire.Message> say)
    {
        foreach (Outgoing o in _out.Values.ToArray())
        {
            if (now - o.Started > GiveUpSeconds)
            {
                _out.Remove(o.Id);
                continue;
            }
            foreach (Wire.Chunk c in o.Next(now)) say(c);
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
                    if (chunk.Count == 0 || !Incoming.Could(chunk) || _in.Count >= MostIncoming) return null;
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
                _out.Remove(got.Transfer);
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
        private double _allowance, _at, _heardAt;

        public Outgoing(uint id, byte[] data, double now)
        {
            int count = (data.Length + ChunkBytes - 1) / ChunkBytes;
            if (count > ushort.MaxValue) throw new InvalidDataException($"{data.Length} bytes is too big to send");
            // A first burst goes at once, so a small craft is not held for the pace to allow it.
            (Id, _data, Started, _at, _heardAt, _allowance) = (id, data, now, now, now, 8.0 * ChunkBytes);
            Count = (ushort)Math.Max(1, count);
            for (ushort i = 0; i < Count; i++) Queue(i);
        }

        public uint Id { get; }
        public ushort Count { get; }
        public double Started { get; }

        public List<Wire.Chunk> Next(double now)
        {
            List<Wire.Chunk> chunks = [];
            _allowance = Math.Min(_allowance + ((now - _at) * BytesPerSecond), 64.0 * ChunkBytes);
            _at = now;
            if (_due.Count == 0 && now - _heardAt > ResendAfterSeconds)
            {
                // Nothing heard: the first pieces may all have been lost, and the receiver knows of nothing to ask for.
                for (ushort i = 0; i < Count; i++) Queue(i);
                _heardAt = now;
            }
            while (_allowance >= ChunkBytes && _due.TryDequeue(out ushort i))
            {
                _queued.Remove(i);
                int from = i * ChunkBytes;
                chunks.Add(new Wire.Chunk(Id, i, Count, _data[from..Math.Min(from + ChunkBytes, _data.Length)]));
                _allowance -= ChunkBytes;
            }
            return chunks;
        }

        public void Need(IEnumerable<ushort> missing, double now)
        {
            _heardAt = now;
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

        public byte[] Assemble() => [.. _pieces.SelectMany(p => p!)];
    }
}
