using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// The craft a server will not clear, as it tells every game: sent whole whenever it changes, since
/// each game sweeps its own world and has to know what to leave standing in it.
///
/// <para>A craft on it is taken by <c>clear</c> only when it is asked for by its own name, in
/// full: the broom is for a world filling up with spent stages, and a station is in the way of
/// <c>clear *</c> exactly as often as somebody types it.</para>
/// </summary>
public static class ProtectedList
{
    /// <summary>The most names kept. Each is a craft somebody chose, and the list goes to every game that joins.</summary>
    public const int Most = 500;

    private const byte Form = 1;

    /// <summary>
    /// Whether a sweep by this name leaves this craft alone: it is on the list, and the name is not
    /// its own in full. A name with a wildcard in it is never a craft's own.
    /// </summary>
    public static bool Spares(IReadOnlySet<string> names, string key, string like) =>
        names.Contains(key) && !string.Equals(key, like.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <param name="serial">Which state of the list this is, counting up: a sweep says which it was made against.</param>
    public static byte[] Encode(IEnumerable<string> names, uint serial)
    {
        string[] all = [.. names.Take(Most)];
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        w.Write(Form);
        w.Write(serial);
        w.Write((ushort)all.Length);
        foreach (string name in all) w.Write(name);
        w.Flush();
        return m.ToArray();
    }

    /// <summary>The list read back, or null for one cut short or in a form this does not know.</summary>
    public static string[]? Decode(byte[] data, out uint serial)
    {
        serial = 0;
        try
        {
            using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
            if (r.ReadByte() != Form) return null;
            serial = r.ReadUInt32();
            string[] all = new string[Math.Min((int)r.ReadUInt16(), Most)];
            for (int i = 0; i < all.Length; i++) all[i] = Wire.Clip(r.ReadString(), Wire.KeyChars);
            return all;
        }
        catch (Exception e) when (e is EndOfStreamException or FormatException or DecoderFallbackException or IOException)
        {
            return null;
        }
    }
}
