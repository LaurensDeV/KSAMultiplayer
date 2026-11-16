using System.Text;

namespace KSAMultiplayer;

/// <summary>A world a server keeps under a name, as a list of them says it: everything but the world itself.</summary>
/// <param name="Universe">The universe time it was saved at, or zero for one copied in by hand, which does not say.</param>
/// <param name="By">Who asked for it to be kept; empty for one copied in by hand.</param>
/// <param name="Auto">Whether the server kept it of its own accord: the world that was there before a save was loaded over it.</param>
public sealed record SavedWorld(string Name, DateTime SavedUtc, double Universe, int Bytes, string System, string KsaBuild, string By, bool Auto);

/// <summary>
/// The worlds a server keeps under names, as it tells an admin's game: sent whole whenever it
/// changes, so a window showing it never has to ask.
///
/// <para>It goes as a <see cref="Blob"/> rather than a datagram of its own because it has to arrive
/// and may not fit in one, and because a game from before there were any reads a blob of a kind it
/// does not know as nothing at all.</para>
/// </summary>
public static class SaveList
{
    public const int NameChars = 48;

    /// <summary>The most a server keeps. Each is a whole world, and the list is an admin's to fill.</summary>
    public const int Most = 100;

    private const byte Form = 1;

    // A name is a file's name on the server, on whichever system that runs: nothing a path is made
    // of, and none of the names Windows keeps for its devices.
    // No '#': two of them end a label in the game's windows, so "Good##anything" would be listed as "Good".
    private const string Marks = " -_.()'+,&!";
    private static readonly string[] Devices = ["con", "prn", "aux", "nul"];

    /// <summary>
    /// A name a save may be kept under, tidied, or null and why not. The same answer in a game as
    /// on the server, so a window can say what is wrong with a name before anything is asked.
    /// </summary>
    public static string? Name(string asked, out string why)
    {
        string name = asked.Trim().Trim('"').Trim();
        why = "";
        if (name.Length == 0) why = "a save needs a name";
        else if (name.Length > NameChars) why = $"save names can be at most {NameChars} characters";
        else if (name.FirstOrDefault(c => !char.IsLetterOrDigit(c) && !Marks.Contains(c)) is var bad && bad != default) why = $"save names can't contain '{bad}'";
        else if (name[0] == '.' || name[^1] == '.') why = "save names can't start or end with a dot";
        else if (Device(name)) why = $"'{name}' is a reserved name on Windows";
        return why.Length == 0 ? name : null;
    }

    private static bool Device(string name)
    {
        string stem = name.Split('.')[0].TrimEnd().ToLowerInvariant();
        return Devices.Contains(stem)
               || (stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    /// <param name="serial">
    /// Which telling this is, counting up. Two lists sent a moment apart are two transfers, and the
    /// first can finish arriving last: a game keeps a list only if it is newer than the one it has.
    /// </param>
    public static byte[] Encode(IEnumerable<SavedWorld> saves, uint serial = 0)
    {
        SavedWorld[] all = [.. saves];
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        w.Write(Form);
        w.Write(serial);
        w.Write((ushort)all.Length);
        foreach (SavedWorld s in all)
        {
            w.Write(s.Name);
            w.Write(s.SavedUtc.ToUniversalTime().Ticks);
            w.Write(s.Universe);
            w.Write(s.Bytes);
            w.Write(s.System);
            w.Write(s.KsaBuild);
            w.Write(s.By);
            w.Write(s.Auto);
        }
        w.Flush();
        return m.ToArray();
    }

    /// <summary>The list read back, or null for one cut short or in a form this does not know.</summary>
    public static SavedWorld[]? Decode(byte[] data) => Decode(data, out _);

    public static SavedWorld[]? Decode(byte[] data, out uint serial)
    {
        serial = 0;
        try
        {
            using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
            if (r.ReadByte() != Form) return null;
            serial = r.ReadUInt32();
            SavedWorld[] all = new SavedWorld[r.ReadUInt16()];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = new SavedWorld(Wire.Clip(r.ReadString(), NameChars), new DateTime(r.ReadInt64(), DateTimeKind.Utc), r.ReadDouble(), r.ReadInt32(),
                                        Wire.Clip(r.ReadString(), Wire.NameChars), Wire.Clip(r.ReadString(), 64), Wire.Clip(r.ReadString(), Wire.NameChars), r.ReadBoolean());
            }
            return all;
        }
        catch (Exception e) when (e is EndOfStreamException or FormatException or ArgumentOutOfRangeException or DecoderFallbackException or IOException)
        {
            return null;
        }
    }
}
