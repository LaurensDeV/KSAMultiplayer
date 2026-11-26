using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// The kittens that have died in a server's world and whose game they died in, as it tells every
/// game: sent whole whenever it changes, since each game draws its own roster.
///
/// <para>The player is the server's word, not the game's: it is whoever's game said the kitten
/// died, by the name the server knows that game under. KSA keeps a kitten's death in the save and
/// nothing of who flew it, and the save comes from whichever game the server asked last, which is
/// as often somebody else's as the killer's.</para>
/// </summary>
public static class MemorialList
{
    /// <summary>The most deaths kept: a world that loses more than this has the oldest forgotten first.</summary>
    public const int Most = 1000;

    /// <summary>The longest a cause of death is said: KSA's names for them are a word or two.</summary>
    public const int CauseChars = 32;

    private const byte Form = 1;

    /// <summary>One kitten's death.</summary>
    /// <param name="Kitten">The kitten's name, which is what KSA's roster knows it by.</param>
    /// <param name="By">The player whose game it died in.</param>
    /// <param name="Cause">KSA's cause of death, by its name in the save.</param>
    /// <param name="Craft">What it was aboard, or its own name for a kitten on EVA; nothing where its game did not know.</param>
    /// <param name="Universe">The universe time it died at, in the game that said so.</param>
    public readonly record struct Death(string Kitten, string By, string Cause, string Craft, double Universe);

    /// <param name="serial">Which state of the list this is, counting up, so a game never takes an older one over a newer.</param>
    public static byte[] Encode(IEnumerable<Death> deaths, uint serial)
    {
        Death[] all = [.. deaths.Take(Most)];
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        w.Write(Form);
        w.Write(serial);
        w.Write((ushort)all.Length);
        foreach (Death d in all)
        {
            w.Write(d.Kitten);
            w.Write(d.By);
            w.Write(d.Cause);
            w.Write(d.Craft);
            w.Write(d.Universe);
        }
        w.Flush();
        return m.ToArray();
    }

    /// <summary>The list read back, or null for one cut short or in a form this does not know.</summary>
    public static Death[]? Decode(byte[] data, out uint serial)
    {
        serial = 0;
        try
        {
            using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
            if (r.ReadByte() != Form) return null;
            serial = r.ReadUInt32();
            Death[] all = new Death[Math.Min((int)r.ReadUInt16(), Most)];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = new Death(Wire.Clip(r.ReadString(), Wire.KeyChars), Wire.Clip(r.ReadString(), Wire.NameChars * 2),
                                   Wire.Clip(r.ReadString(), CauseChars), Wire.Clip(r.ReadString(), Wire.KeyChars), r.ReadDouble());
            }
            return all;
        }
        catch (Exception e) when (e is EndOfStreamException or FormatException or DecoderFallbackException or IOException)
        {
            return null;
        }
    }
}
