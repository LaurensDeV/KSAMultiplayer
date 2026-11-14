using System.Security.Cryptography;

namespace KSAMultiplayer.Server;

/// <summary>
/// The system the web viewer draws, as a game in the world described it. Asked for only while a
/// viewer wants it, taken only from a game that was asked and runs the build and system the world
/// does, and kept -- the first good one for each system and build -- beside the world, so a viewer
/// shows the bodies with nobody online and nobody is asked twice.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Whether to ask for a description at all: set while a web viewer runs. Without one, nothing here does anything.</summary>
    public bool WantBodies { get; set; }

    private readonly Dictionary<(string System, string Build), (byte[] Json, string Etag)?> _bodies = [];

    // How long a game has to answer before somebody else is asked: a game that left, or one too old
    // to know the question, never will.
    private const double DescribeWithinSeconds = 60.0;
    private double _bodiesAskedAt;

    /// <summary>The description of this world's system as JSON and a tag that changes with it, or null when none has been given.</summary>
    public (byte[] Json, string Etag)? Bodies => _worldSystem.Length > 0 && RequiredBuild.Length > 0 ? BodiesFor(_worldSystem, RequiredBuild) : null;

    private string BodiesPath(string system, string build)
    {
        static string Safe(string s) => new([.. s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return Path.Combine(_store?.Folder ?? "", $"bodies-{Safe(system)}-{Safe(build)}.json");
    }

    private (byte[] Json, string Etag)? BodiesFor(string system, string build)
    {
        if (_bodies.TryGetValue((system, build), out var known)) return known;
        (byte[], string)? found = null;
        try
        {
            if (_store is not null && File.Exists(BodiesPath(system, build)))
            {
                byte[] json = File.ReadAllBytes(BodiesPath(system, build));
                // Checked again, since a file can be edited: one that no longer reads is as good as none.
                if (SystemDescription.Decode(json, out _) is { } d && d.System == system && d.KsaBuild == build) found = (json, Tag(json));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _bodies[(system, build)] = found;
        return found;
    }

    private static string Tag(byte[] json) => "\"" + Convert.ToHexString(SHA256.HashData(json))[..16] + "\"";

    // A game the description could come from: in the world, and running what the world runs, so
    // what it computed is what every other game in the world computes too.
    private void AskForBodies(Player p)
    {
        if (!WantBodies || !p.Loaded || p.BodiesAsked || _worldSystem.Length == 0 || RequiredBuild.Length == 0) return;
        if (p.System != _worldSystem || p.KsaBuild != RequiredBuild || Bodies is not null) return;
        // One game asked at a time is enough: a second answer would only be refused.
        if (_players.Values.Any(q => q.BodiesAsked)) return;
        p.BodiesAsked = true;
        _bodiesAskedAt = _clock();
        _udp.Send(new Wire.Describe(), p.At);
        Log($"asked {p.Name} to describe {_worldSystem} for the web viewer");
    }

    private void OnBodies(Player p, Blob blob)
    {
        if (!p.BodiesAsked)
        {
            Log($"{p.Name} described a system nobody asked for: left where it came from");
            return;
        }
        p.BodiesAsked = false;
        if (SystemDescription.Decode(blob.Data, out string why) is not { } d)
        {
            Log($"{p.Name}'s description of the system was refused: {why}");
            return;
        }
        if (d.System != _worldSystem || d.KsaBuild != RequiredBuild || p.System != _worldSystem || p.KsaBuild != RequiredBuild)
        {
            Log($"{p.Name} described {d.System} on {d.KsaBuild}, and the world is {_worldSystem} on {RequiredBuild}: refused");
            return;
        }
        // The first good one stands: a second game describing it otherwise is a game to doubt,
        // not a reason to redraw everybody's planets.
        if (BodiesFor(d.System, d.KsaBuild) is not null) return;
        byte[] json = d.Encode();
        try
        {
            if (_store is not null)
            {
                Directory.CreateDirectory(_store.Folder);
                File.WriteAllBytes(BodiesPath(d.System, d.KsaBuild), json);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"could not keep the description of {d.System}: {e.Message}");
        }
        _bodies[(d.System, d.KsaBuild)] = (json, Tag(json));
        Log($"{p.Name} described {d.System}: {d.Bodies.Length} bodies, for the web viewer");
    }

    /// <summary>From the tick thread now and then while a viewer runs with no description: whoever is in the world may be asked.</summary>
    public void AskForBodiesNow()
    {
        if (!WantBodies || Bodies is not null) return;
        if (_clock() - _bodiesAskedAt > DescribeWithinSeconds)
        {
            foreach (Player q in _players.Values) q.BodiesAsked = false;
        }
        foreach (Player p in _players.Values) AskForBodies(p);
    }
}
