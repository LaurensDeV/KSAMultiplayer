using System.Text;
using System.Text.Json;

namespace KSAMultiplayer.Server;

/// <summary>
/// The clock, the warp and how late each player says they are, a line of JSON at a time, for a test
/// that samples a room through a warp: <c>tools/warp-trace.py</c>.
///
/// <para>Every fault in the clock lives in a transition, and a server read four times a second sees
/// a transition at 1000x as up to 250 universe-seconds in which anything may have happened. So a
/// change is written from both sides -- the server as it stood before the tick that changed it and
/// as it stands after, a tick apart -- and between any two lines the clock ran at one rate, which
/// is what lets a reader work out where it was at an instant in between.</para>
/// </summary>
/// <param name="to">Where the lines go. Flushed a line at a time: the reader is another process.</param>
/// <param name="everySeconds">How often a line is written while nothing changes.</param>
public sealed class ClockTrace(TextWriter to, double everySeconds = 0.25)
{
    /// <summary>How far the clock may be from where its own rate would have carried it before that is a jump, s.</summary>
    private const double JumpSeconds = 0.01;

    private readonly record struct Reading(double Now, double Wall, double Universe, double Rate, bool Known,
        byte Who, double Speed, double Wanted, Wire.WarpPhase Phase);

    private Reading _before;
    private double _next;

    private static Reading Read(GameServer s, double now, double wall)
    {
        (byte who, double speed, Wire.WarpPhase phase) = s.Warp;
        return new(now, wall, s.UniverseClock.Now(now), s.UniverseClock.Rate, s.UniverseClock.Known, who, speed, s.WarpWanted, phase);
    }

    /// <summary>The server as it stands, before a tick that may change it.</summary>
    /// <param name="now">The server's own clock, which is what its universe time is carried on.</param>
    /// <param name="wall">The time of day, which is the only clock a game and the server share.</param>
    public void Before(GameServer s, double now, double wall) => _before = Read(s, now, wall);

    /// <summary>The server after that tick: written with what it was before if the tick changed the clock or the warp, and on its own when a line is due.</summary>
    public void After(GameServer s, double now, double wall)
    {
        Reading after = Read(s, now, wall);
        double carried = _before.Universe + (_before.Rate * (after.Now - _before.Now));
        bool changed = after.Rate != _before.Rate || after.Known != _before.Known || after.Phase != _before.Phase
            || after.Speed != _before.Speed || after.Wanted != _before.Wanted || after.Who != _before.Who
            || Math.Abs(after.Universe - carried) > JumpSeconds;
        if (changed) Write(s, _before);
        else if (now < _next) return;
        Write(s, after);
        _next = now + everySeconds;
    }

    private void Write(GameServer s, Reading r)
    {
        using MemoryStream bytes = new();
        using (Utf8JsonWriter json = new(bytes))
        {
            json.WriteStartObject();
            json.WriteNumber("t", Math.Round(r.Wall, 4));
            json.WriteNumber("universe", Math.Round(r.Universe, 3));
            json.WriteNumber("rate", r.Rate);
            json.WriteBoolean("known", r.Known);
            json.WriteString("phase", r.Phase.ToString());
            json.WriteNumber("speed", Math.Round(r.Speed, 3));
            // What the warp is on its way to, which it is held below by what the room can fly.
            json.WriteNumber("wanted", Math.Round(r.Wanted, 3));
            json.WriteString("who", r.Phase == Wire.WarpPhase.None ? "" : s.Players.FirstOrDefault(p => p.Id == r.Who)?.Name ?? "");
            json.WriteStartArray("players");
            foreach (GameServer.Player p in s.Players.OrderBy(p => p.Id))
            {
                json.WriteStartObject();
                json.WriteString("name", p.Name);
                json.WriteBoolean("loaded", p.Loaded);
                json.WriteBoolean("here", p.Here);
                // Numbers off the wire, and JSON has no way to say one was not a number.
                if (double.IsFinite(p.Late)) json.WriteNumber("late", Math.Round(p.Late, 3));
                else json.WriteNull("late");
                if (double.IsFinite(p.Can)) json.WriteNumber("can", Math.Round(p.Can, 3));
                else json.WriteNull("can");
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        to.WriteLine(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length));
        to.Flush();
    }
}
