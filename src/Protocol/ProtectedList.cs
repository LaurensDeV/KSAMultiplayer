using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// The craft a server will not clear, as it tells every game: sent whole whenever it changes, since
/// each game sweeps its own world and has to know what to leave standing in it.
///
/// <para>A craft on it is never taken by <c>clear</c>, not even by its own name in full: the broom
/// is for a world filling up with spent stages, and a name typed in full is as easily a typo away
/// from a station as <c>clear *</c> is. Taking one out is unprotecting it first, on purpose.</para>
/// </summary>
public static class ProtectedList
{
    /// <summary>The most names kept. Each is a craft somebody chose, and the list goes to every game that joins.</summary>
    public const int Most = 500;

    private const byte Form = 1;

    /// <summary>Whether a sweep leaves this craft alone, whatever it was swept by: it is on the list.</summary>
    public static bool Spares(IReadOnlySet<string> names, string key) => names.Contains(key);

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
