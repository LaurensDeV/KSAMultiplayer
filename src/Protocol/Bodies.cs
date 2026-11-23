using System.Text.Json;

namespace KSAMultiplayer;

/// <summary>
/// A world's system as a game in it computed it: what a viewer needs to put every body where the
/// game would, at any universe time, without the game. Written by the mod, kept by the server, read
/// by the web viewer, and never interpreted against anything of KSA's here -- which is why it carries
/// what the game had already worked out (each orbit's rotation, each body's frame and spin phase)
/// rather than the elements as authored, whose frames and units vary from body to body.
///
/// <para>Every body moves on a fixed two-body orbit around its parent, as patched conics, and
/// spins at a constant rate about its own inertial +Z. Positions add up in the ecliptic axes
/// (CCE) only: each body's inertial frame (CCI) is tilted, and <see cref="BodyDescription.Cci2Cce"/>
/// is what turns one into the other.</para>
/// </summary>
public sealed record SystemDescription(string System, string KsaBuild, BodyDescription[] Bodies)
{
    /// <summary>The most bodies one description may hold, and the most bytes it may come to.</summary>
    public const int MostBodies = 256, MostBytes = 512 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>A description read and checked whole, or null and why not. Anything not finite, any parent not described, any loop of parents is refused.</summary>
    public static SystemDescription? Decode(byte[] bytes, out string why)
    {
        why = "";
        if (bytes.Length > MostBytes)
        {
            why = $"{bytes.Length} bytes, past the {MostBytes} a description may be";
            return null;
        }
        SystemDescription? d;
        try
        {
            d = JsonSerializer.Deserialize<SystemDescription>(bytes, Json);
        }
        catch (JsonException e)
        {
            why = $"not a description: {e.Message}";
            return null;
        }
        if (d?.Bodies is null || d.System is null || d.KsaBuild is null)
        {
            why = "not a description";
            return null;
        }
        return d.Check(out why) ? d : null;
    }

    private bool Check(out string why)
    {
        why = "";
        if (Bodies.Length is 0 or > MostBodies)
        {
            why = $"{Bodies.Length} bodies, where 1 to {MostBodies} may be described";
            return false;
        }
        Dictionary<string, BodyDescription> byId = new(StringComparer.Ordinal);
        foreach (BodyDescription b in Bodies)
        {
            if (b is null || string.IsNullOrEmpty(b.Id) || b.Id.Length > Wire.KeyChars || !byId.TryAdd(b.Id, b))
            {
                why = $"a body with no name, too long a name, or a name twice: '{b?.Id}'";
                return false;
            }
            if (!b.Sane(out string what))
            {
                why = $"{b.Id}: {what}";
                return false;
            }
        }
        foreach (BodyDescription b in Bodies)
        {
            if (b.Parent.Length > 0 && !byId.ContainsKey(b.Parent))
            {
                why = $"{b.Id}'s parent {b.Parent} is not described";
                return false;
            }
            // Up the tree from every body, never further than there are bodies: a loop never ends.
            string at = b.Id;
            for (int steps = 0; byId[at].Parent.Length > 0; steps++)
            {
                if (steps > Bodies.Length)
                {
                    why = $"{b.Id} is its own ancestor";
                    return false;
                }
                at = byId[at].Parent;
            }
        }
        return true;
    }
}

/// <param name="Parent">Empty for a root, which stays where <see cref="Position"/> says.</param>
/// <param name="Kind">star, barycenter, planet, moon or minor: how to draw it, and nothing else.</param>
/// <param name="Radius">Mean radius, m; 0 for a barycentre.</param>
/// <param name="Mu">G times the body's own mass, m³/s²: what a craft around it orbits by.</param>
/// <param name="Atmosphere">How far above <paramref name="Radius"/> its atmosphere reaches, m; 0 for none.</param>
/// <param name="Position">Where a root is in the ecliptic frame, m; null for a body with an orbit.</param>
/// <param name="Cci2Cce">The body's inertial frame turned into ecliptic axes: constant, since nothing precesses.</param>
/// <param name="SpinRate">rad/s, negative for a retrograde spin; 0 for none.</param>
/// <param name="SpinPhase">rad: the body-fixed frame is turned SpinRate·t + SpinPhase about its inertial +Z.</param>
public sealed record BodyDescription(string Id, string Parent, string Kind, double Radius, double Mu, double Atmosphere,
                                     double Soi, Vec3? Position, BodyOrbit? Orbit, Quat Cci2Cce, double SpinRate, double SpinPhase)
{
    internal bool Sane(out string what)
    {
        what = "";
        bool finite = double.IsFinite(Radius) && double.IsFinite(Mu) && double.IsFinite(Atmosphere) && double.IsFinite(Soi)
                      && double.IsFinite(SpinRate) && double.IsFinite(SpinPhase) && Unit(Cci2Cce)
                      && (Position is not { } p || (double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)));
        if (!finite) what = "a number that is not finite, or a rotation that is not one";
        else if (Radius < 0 || (Radius == 0 && Kind != "barycenter")) what = $"radius {Radius}";
        else if (Mu < 0 || Atmosphere < 0 || Soi < 0) what = "a negative mass, atmosphere or sphere of influence";
        else if (Parent.Length == 0 && Orbit is not null) what = "an orbit with no parent to go round";
        else if (Parent.Length > 0 && Orbit is null) what = "a parent and no orbit";
        else if (Orbit is { } o && !o.Sane()) what = "an orbit that is not one";
        return what.Length == 0;
    }

    internal static bool Unit(Quat q) => q.IsFinite && Math.Abs((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W) - 1.0) < 1e-3;
}

/// <summary>
/// A two-body orbit as a viewer runs it: mean anomaly n·(t − TimeAtPeriapsis) from periapsis, in
/// the orbit's own plane, turned into the parent's ecliptic axes by <see cref="Orb2ParentCce"/>.
/// </summary>
/// <param name="Mu">What the orbit goes round by, m³/s², which is not always the parent's own: a star of a pair goes round their barycentre's.</param>
public sealed record BodyOrbit(double Periapsis, double Eccentricity, double TimeAtPeriapsis, double Period, double Mu, Quat Orb2ParentCce)
{
    internal bool Sane() =>
        double.IsFinite(Periapsis) && Periapsis > 0 && double.IsFinite(Eccentricity) && Eccentricity >= 0
        && double.IsFinite(TimeAtPeriapsis) && double.IsFinite(Mu) && Mu > 0 && BodyDescription.Unit(Orb2ParentCce)
        && (Eccentricity >= 1 || (double.IsFinite(Period) && Period > 0));
}
