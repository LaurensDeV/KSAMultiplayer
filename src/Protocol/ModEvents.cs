using System.Text;

namespace KSAMultiplayer;

/// <summary>One thing a mod says once, as it travels.</summary>
/// <param name="From">Who said it, as the server writes it; 0 from a game, whose word for it is not taken.</param>
/// <param name="FromName">Their name then, as the server writes it: a number is handed to the next to join. Empty from a game.</param>
/// <param name="To">The one player it is for, or 0 for every other player in the world.</param>
/// <param name="Id">Its sender's own number for it, which <see cref="Wire.EventLost"/> names it by.</param>
/// <param name="Craft">The craft it is about by the name the world shares, or empty for none.</param>
public readonly record struct ModEvent(byte From, string FromName, byte To, uint Id, string Channel, string Craft, byte[] Data);

/// <summary>
/// What mods say once and in order (<see cref="BlobKind.ModEvent"/>): several to a transfer, since a
/// link carries four transfers at a time and a server passing on one for each sender would fill it.
/// Neither a game nor a server reads <see cref="ModEvent.Data"/>.
///
/// <para>The numbers are here, where both ends read them, because being told a transfer came is
/// not being told it was taken: a server drops what is over them after saying it has all of it, so
/// a game has to hold itself under the same ones.</para>
/// </summary>
public static class ModEvents
{
    /// <summary>The most one event's <see cref="ModEvent.Data"/> weighs.</summary>
    public const int EventBytes = 4096;

    /// <summary>
    /// The most a batch weighs. With a blob's eleven bytes in front that is eight pieces, which is
    /// what a link sends at once: a batch leaves whole, and does not sit half sent in one of the
    /// other end's four places while a craft and a log wait for theirs.
    /// </summary>
    public const int BatchBytes = 8192;

    /// <summary>What a server passes on of one player's events a second, and at once: see <see cref="Cost"/>.</summary>
    public const int BytesASecond = 16 * 1024, BurstBytes = 32 * 1024;

    /// <summary>
    /// What a game sends a second. Under the server's, since two clocks never count a second alike
    /// and nothing tells a game it went over but its events not arriving.
    /// </summary>
    public const int PaceBytesASecond = 15 * 1024;

    /// <summary>The most held for one player at a server, and unsent in one game.</summary>
    public const int WaitingBytes = 256 * 1024;

    private const byte Form = 1;

    // A name at its longest as it is written: twenty-four characters of three bytes and their length.
    private const int NameAtMost = (Wire.NameChars * 3) + 1;

    /// <summary>Whether an event is one a batch may hold. Compared and never clipped: a channel cut short is another channel.</summary>
    public static bool Fits(in ModEvent e) =>
        e.Channel.Length is > 0 and <= Wire.ChannelChars && e.Craft.Length <= Wire.KeyChars
        && e.FromName.Length <= Wire.NameChars && e.Data.Length <= EventBytes;

    /// <summary>What an event weighs in a batch.</summary>
    public static int Size(in ModEvent e) => 8 + Text(e.FromName) + Text(e.Channel) + Text(e.Craft) + e.Data.Length;

    /// <summary>
    /// What an event is counted as against <see cref="BytesASecond"/>: its weight as a server
    /// passes it on, with the sender's name at the longest a name can be. Counted as it arrived, an
    /// event of a dozen bytes from a long name would weigh seven times that in every other
    /// player's queue, and a game inside its allowance could fill them all.
    /// </summary>
    public static int Cost(in ModEvent e) => Size(e) - Text(e.FromName) + NameAtMost;

    private static int Text(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        return n + (n < 128 ? 1 : 2);
    }

    /// <summary>
    /// A batch of events from the front and in order, for as long as the next still fits in
    /// <paramref name="most"/> bytes; <paramref name="taken"/> is how many. One that does not fit
    /// ends it, and none after is taken in its place: the order is the point.
    /// </summary>
    public static byte[] Encode(IEnumerable<ModEvent> events, int most, out int taken)
    {
        taken = 0;
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        w.Write(Form);
        int total = 1;
        foreach (ModEvent e in events)
        {
            if (total + Size(e) > most) break;
            total += Size(e);
            w.Write(e.From);
            w.Write(e.FromName);
            w.Write(e.To);
            w.Write(e.Id);
            w.Write(e.Channel);
            w.Write(e.Craft);
            w.Write((ushort)e.Data.Length);
            w.Write(e.Data);
            taken++;
        }
        w.Flush();
        return m.ToArray();
    }

    /// <summary>
    /// The events of a batch, or null for one over <see cref="BatchBytes"/>, in a form this does
    /// not know, holding nothing, cut short inside an event, or holding one that does not
    /// <see cref="Fits"/>. Never a throw.
    /// </summary>
    public static ModEvent[]? Decode(byte[] data)
    {
        if (data.Length is 0 or > BatchBytes) return null;
        try
        {
            using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
            if (r.ReadByte() != Form) return null;
            List<ModEvent> events = [];
            while (r.BaseStream.Position < data.Length)
            {
                byte from = r.ReadByte();
                string fromName = r.ReadString();
                byte to = r.ReadByte();
                uint id = r.ReadUInt32();
                string channel = r.ReadString();
                string craft = r.ReadString();
                int n = r.ReadUInt16();
                if (n > EventBytes) return null;
                byte[] bytes = r.ReadBytes(n);
                if (bytes.Length != n) return null;
                ModEvent e = new(from, fromName, to, id, channel, craft, bytes);
                if (!Fits(e)) return null;
                events.Add(e);
            }
            return events.Count > 0 ? [.. events] : null;
        }
        // Whatever a reader makes of bytes from another machine: a throw here is a throw in the game's frame.
        catch (Exception)
        {
            return null;
        }
    }
}
