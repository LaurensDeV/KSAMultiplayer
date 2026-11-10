using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// Where every craft this game sends and every craft it is sent stood, written down each time the
/// mod touched it, for a run that wants to know whether another player's craft is where its owner
/// says it is and whether it got there smoothly.
///
/// <para>The bridge cannot answer that. It is asked a couple of times a second and a judder lives
/// inside a frame, so ten samples of a craft a metre out of place and shaking came back identical.
/// This is written from where the work is done -- the frame that sends a craft's state and the
/// physics step that pulls a puppet towards one -- and read afterwards, the two games' accounts
/// laid side by side on universe time, which is the one clock they share.</para>
///
/// <para>Off unless a run turns it on, and nothing in a game a player started ever does.</para>
/// </summary>
internal static class Motion
{
    // A recording left running is a few thousand lines a second: this many and it stops by itself.
    private const int MostLines = 600_000;

    private static readonly ConcurrentQueue<string> Lines = new();
    private static volatile bool _on;

    /// <summary>Whether anything is being written down. Read from the physics workers, once a craft a step.</summary>
    public static bool On => _on;

    public static void Begin()
    {
        Lines.Clear();
        _on = true;
    }

    /// <summary>Stops, and writes what was recorded to a file: a line of JSON for each craft each time it was touched.</summary>
    public static int End(string file)
    {
        _on = false;
        int written = 0;
        using StreamWriter to = new(file);
        while (Lines.TryDequeue(out string? line))
        {
            to.WriteLine(line);
            written++;
        }
        return written;
    }

    private static double Wall() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    private static string N(double x) => double.IsFinite(x) ? x.ToString("R", CultureInfo.InvariantCulture) : "null";

    private static string P(Vec3 v) => $"[{N(v.X)},{N(v.Y)},{N(v.Z)}]";

    private static void Add(string line)
    {
        if (Lines.Count >= MostLines) _on = false;
        else Lines.Enqueue(line);
    }

    /// <summary>A craft this game owns, as it would say it this frame: the truth another game's copy is held against.</summary>
    /// <param name="parts">How many parts it has, which is how a reader knows two games mean one shape by one name.</param>
    public static void Owned(Wire.State said, bool flown, int parts)
    {
        Add($"{{\"r\":\"o\",\"k\":{JsonSerializer.Serialize(said.Key)},\"w\":{N(Wall())},\"u\":{N(said.Universe)},\"b\":{JsonSerializer.Serialize(said.Body)},"
            + $"\"p\":{P(said.PositionCcf)},\"v\":{P(said.VelocityCcf)},\"flown\":{(flown ? 1 : 0)},"
            + $"\"rails\":{(said.Flags.HasFlag(Wire.StateFlags.Rails) ? 1 : 0)},\"land\":{(said.Flags.HasFlag(Wire.StateFlags.Landed) ? 1 : 0)},\"n\":{parts}}}");
    }

    /// <summary>Any craft in this world this frame, whoever's it is: what the distance between two craft is worked out from.</summary>
    public static void Standing(Wire.State at, int parts)
    {
        Add($"{{\"r\":\"w\",\"k\":{JsonSerializer.Serialize(at.Key)},\"w\":{N(Wall())},\"u\":{N(at.Universe)},\"b\":{JsonSerializer.Serialize(at.Body)},"
            + $"\"p\":{P(at.PositionCcf)},\"v\":{P(at.VelocityCcf)},\"n\":{parts}}}");
    }

    /// <summary>Another player's craft at one physics step: where it stood, where it was told to stand, and what was done about the difference.</summary>
    /// <param name="newest">The universe time of the newest state heard of it, which the target is past when the craft is being carried on.</param>
    public static void Puppet(string key, double universe, double dt, World.Pulled pulled, Wire.State target, double newest)
    {
        Add($"{{\"r\":\"p\",\"k\":{JsonSerializer.Serialize(key)},\"w\":{N(Wall())},\"u\":{N(universe)},\"dt\":{N(dt)},\"b\":{JsonSerializer.Serialize(target.Body)},"
            + $"\"p\":{P(pulled.At)},\"v\":{P(pulled.Moving)},\"tu\":{N(target.Universe)},\"tp\":{P(target.PositionCcf)},\"tv\":{P(target.VelocityCcf)},"
            + $"\"nu\":{N(newest)},\"off\":{N(pulled.Off)},\"snap\":{(pulled.Snap ? 1 : 0)},\"held\":{(pulled.Held ? 1 : 0)},\"rest\":{(pulled.Rest ? 1 : 0)},"
            + $"\"flown\":{(target.Flags.HasFlag(Wire.StateFlags.Controlled) ? 1 : 0)}}}");
    }
}
