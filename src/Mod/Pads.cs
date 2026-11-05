using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// More launchpads at every launch site, so a room of players is not queueing for one square of
/// concrete.
///
/// <para>A pad in KSA is a <see cref="LandmarkReference"/> in its celestial's
/// <c>BodyTemplate.Locations</c> with <c>IsLaunchPad</c> set and a <c>StaticObject</c> behind it: the
/// static object is the concrete, its collider and its ground offset, and <c>ConstraintSim</c> walks
/// the same list to build the poses a craft rests on. So a pad added here is a real one, with the
/// same structure under it as the one the game shipped, and nothing has to be drawn or collided by
/// this mod at all.</para>
///
/// <para>They are not offered in the editor's list: <see cref="Hooks"/> takes them back out of it, so
/// a player still picks the site they know by name and the mod puts them on whichever of its pads is
/// free. The list is the celestial template's, which every game builds the same way from the same
/// content, and the pads are derived from the site's own position, so every game lays them out
/// identically without a word crossing the wire.</para>
/// </summary>
internal static class Pads
{
    /// <summary>Pads per site, counting the one the game already had.</summary>
    public const int PerSite = 8;

    /// <summary>
    /// How close neighbouring pads sit, as a multiple of the pad's own footprint.
    ///
    /// <para>Just over one. The footprint is the clearance KSA keeps around a pad, not the concrete:
    /// measured off a picture of the eight, the apron is about 118 m across against a footprint
    /// radius of 108 m, so a little more than one footprint between centres leaves the aprons
    /// nearly touching.</para>
    /// </summary>
    private const double Spacing = 1.2;

    /// <summary>What this mod's pads are called: the site's own id and a number, from 2 up.</summary>
    public const string Suffix = " MP";

    private static readonly HashSet<string> Added = [];

    private static object? _laidOutFor;

    // The last pad named in the log for each site, so a steady answer is said once.
    private static readonly Dictionary<string, string> _said = [];

    // KSA resolves a location's static object once, as the celestial loads, through a method of its
    // own that nothing outside the assembly can reach. A pad added after that has a name for its
    // concrete and no concrete: no model, no collider, and no ground for a craft to stand on, while
    // the ground marker IsLaunchPad asks for is drawn all the same. So it is called here by hand.
    private static readonly System.Reflection.MethodInfo? Resolve =
        AccessTools.Method(typeof(LocationReference), "ResolveStaticObject");

    public static bool Ours(LocationReference location) =>
        location.Id.Contains(Suffix, StringComparison.Ordinal);

    /// <summary>
    /// Laid out once per celestial template, idempotently: a world loaded again finds them already
    /// there. Called where a world has just become current, before anything walks the locations.
    /// </summary>
    public static void LayOut()
    {
        try
        {
            if (Universe.CurrentSystem is not { } system) return;
            // Once per world: the templates are the system's and last as long as it does.
            if (ReferenceEquals(_laidOutFor, system)) return;
            _laidOutFor = system;
            foreach (Celestial celestial in system.All.OfType<Celestial>())
            {
                if (celestial.BodyTemplate is not { } template || !Added.Add(template.Id)) continue;
                Spread(celestial, template);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not lay out the extra launchpads: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// This mod's pads taken out of every celestial's locations and handed back, so that the editor
    /// builds its list of places to launch from without them: a player picks the site they know by
    /// name, not one of eight numbered slabs. Only the template's own list is touched, which is
    /// public; the editor's list of them is private and is left to build itself as it always did.
    /// </summary>
    public static List<(CelestialTemplate Template, LocationReference Pad)> Hide()
    {
        List<(CelestialTemplate, LocationReference)> held = [];
        if (Universe.CurrentSystem is not { } system) return held;
        foreach (Celestial celestial in system.All.OfType<Celestial>())
        {
            if (celestial.BodyTemplate is not { } template) continue;
            foreach (LocationReference pad in template.Locations.Where(Ours).ToArray())
            {
                template.Locations.Remove(pad);
                held.Add((template, pad));
            }
        }
        return held;
    }

    public static void Show(List<(CelestialTemplate Template, LocationReference Pad)> held)
    {
        foreach ((CelestialTemplate template, LocationReference pad) in held) template.Locations.Add(pad);
    }

    /// <summary>
    /// The pads of one site, the one the game shipped first and then its ring, so a quiet site is
    /// launched from exactly where it always was.
    /// </summary>
    public static List<LandmarkReference> At(CelestialTemplate template, string siteId) =>
        [.. template.Locations.OfType<LandmarkReference>()
                    .Where(l => l.IsLaunchPad && (l.Id == siteId || l.Id.StartsWith(siteId + Suffix, StringComparison.Ordinal)))
                    .OrderBy(l => l.Id.Length)
                    .ThenBy(l => l.Id, StringComparer.Ordinal)];

    /// <summary>
    /// A free pad of whichever site sits at these coordinates, given in degrees as KSA hands them to
    /// the code that places a launched craft. Null when the coordinates are nobody's pad, or when
    /// every pad of that site is taken.
    /// </summary>
    public static LandmarkReference? FreeFor(Celestial celestial, double latitudeDeg, double longitudeDeg)
    {
        if (celestial.BodyTemplate is not { } template) return null;
        double lat = latitudeDeg * Math.PI / 180.0;
        double lon = longitudeDeg * Math.PI / 180.0;
        LandmarkReference? asked = template.Locations.OfType<LandmarkReference>()
            .Where(l => l.IsLaunchPad)
            .FirstOrDefault(l => Math.Abs(l.Latitude.GetRadians() - lat) < 1e-9
                                 && Math.Abs(l.Longitude.GetRadians() - lon) < 1e-9);
        if (asked is null) return null;
        // One of this mod's own would mean the editor was given one to offer, which it is not; the
        // site's name is the part before the suffix either way.
        int cut = asked.Id.IndexOf(Suffix, StringComparison.Ordinal);
        return Free(celestial, cut < 0 ? asked.Id : asked.Id[..cut]);
    }

    /// <summary>
    /// The first pad of a site with nothing standing on it, or null when every one is taken.
    ///
    /// <para>Taken is KSA's own test for whether a craft is on a pad: within the pad's footprint of
    /// its centre, measured as an angle at the body and turned back into metres. The footprint is
    /// 108 m and the pads are 130 m apart, so no craft can hold two of them at once.</para>
    /// </summary>
    public static LandmarkReference? Free(Celestial celestial, string siteId)
    {
        if (celestial.BodyTemplate is not { } template) return null;
        double3[] craft = [.. World.Vehicles().Where(v => ReferenceEquals(v.Parent, celestial))
                                   .Select(v => World.DirectionOf(v, celestial))
                                   .Where(d => d is not null)
                                   .Select(d => d!.Value)];
        foreach (LandmarkReference pad in At(template, siteId))
        {
            if (pad.GetStaticObject() is not { } concrete) continue;
            if (!craft.Any(d => (pad.ForwardCcf - d).Length() * celestial.MeanRadius < concrete.FootprintRadius))
            {
                // Only when the answer changes. The editor asks this every frame it is open to know
                // whether to warn, and logging each one put two thousand lines in a session.
                if (!_said.TryGetValue(siteId, out string? was) || was != pad.Id)
                {
                    _said[siteId] = pad.Id;
                    Log.Info($"{pad.Id} is the free pad at {siteId}, with {craft.Length} craft on {celestial.Id}");
                }
                return pad;
            }
        }
        return null;
    }

    // The editor's chosen body and site, which it keeps to itself. Read rather than guessed at: the
    // list it offers is the one this mod filtered, so the name in it is a site's and the pads of
    // that site are what matter.
    private static readonly System.Reflection.FieldInfo? ChosenBody = AccessTools.Field(typeof(VehicleEditor), "_selectedCelestial");
    private static readonly System.Reflection.FieldInfo? ChosenSite = AccessTools.Field(typeof(VehicleEditor), "_selectedLocation");

    /// <summary>Which body and which launch site the editor is pointed at, as far as it will say.</summary>
    public static (Celestial? Body, string Site) Chosen(VehicleEditor editor)
    {
        object? body = ChosenBody?.GetValue(editor);
        object? site = ChosenSite?.GetValue(editor);
        Celestial? where = body is null
            ? null
            : (AccessTools.Field(body.GetType(), "Celestial")?.GetValue(body)
               ?? AccessTools.Property(body.GetType(), "Celestial")?.GetValue(body)) as Celestial;
        string named = site is null ? "" : AccessTools.Method(site.GetType(), "GetKey")?.Invoke(site, null) as string ?? "";
        return (where, named);
    }

    /// <summary>
    /// Whether the site the editor is pointed at has a pad to put a craft on. True whenever it
    /// cannot be told: a launch is never refused over a question this mod could not answer.
    /// </summary>
    public static bool Room(VehicleEditor editor)
    {
        try
        {
            (Celestial? body, string site) = Chosen(editor);
            return body is null || site.Length == 0 || Free(body, site) is not null;
        }
        catch (Exception e)
        {
            Log.Warn($"could not tell whether a pad is free: {e.GetBaseException().Message}");
            return true;
        }
    }

    /// <summary>The site the editor is pointed at, when every one of its pads is taken.</summary>
    public static string Full =>
        Program.Editor is { } editor && !Room(editor) ? Chosen(editor).Site : "";

    /// <summary>Forgotten when a world goes, since the next one builds its templates afresh.</summary>
    public static void Forget()
    {
        Added.Clear();
        _laidOutFor = null;
    }

    private static void Spread(Celestial celestial, CelestialTemplate template)
    {
        // The sites the game shipped, taken before anything is added to the list that is being read.
        LandmarkReference[] sites = [.. template.Locations.OfType<LandmarkReference>()
                                                          .Where(l => l.IsLaunchPad && !Ours(l))];
        foreach (LandmarkReference site in sites)
        {
            if (site.GetStaticObject() is not { } pad || pad.FootprintRadius <= 0.0) continue;
            // A ring around the pad the game shipped, rather than rows running off one side of it.
            // Only the ground by a site is any good: it is flattened by a terrain decal there and is
            // whatever the planet makes of it a few hundred metres out, and a pad is a rigid disc
            // that stands half buried in anything else. Seven in a ring sit 150 m from the site where
            // two rows of four threw the far one 411 m away.
            double chord = pad.FootprintRadius * Spacing;
            double ring = chord / (2.0 * Math.Sin(Math.PI / (PerSite - 1)));
            double lat = site.Latitude.GetRadians();
            double lon = site.Longitude.GetRadians();
            for (int i = 1; i < PerSite; i++)
            {
                double turn = 2.0 * Math.PI * (i - 1) / (PerSite - 1);
                // Metres to radians at this body: a ring is a distance and a landmark is an angle.
                double north = ring * Math.Cos(turn) / celestial.MeanRadius;
                double east = ring * Math.Sin(turn) / celestial.MeanRadius;
                LandmarkReference made = new()
                {
                    Id = $"{site.Id}{Suffix}{i + 1}",
                    IsLaunchPad = true,
                    StaticObjectId = site.StaticObjectId,
                    // Longitude closes up towards the poles, so a step east is wider in longitude the
                    // further the site is from the equator.
                    Latitude = new RadianReference(lat + north),
                    Longitude = new RadianReference(lon + (east / Math.Max(0.01, Math.Cos(lat)))),
                };
                // Without this a landmark has no ForwardCcf, and a pad is a direction before it is
                // anything else: KSA divides by it the moment it draws the ground marker that
                // IsLaunchPad asks for, and the game goes down on the first frame after a load.
                made.OnDataLoad(Mod.Empty);
                Resolve?.Invoke(made, null);
                if (made.GetStaticObject() is null)
                {
                    Log.Warn($"{made.Id} has no concrete under it, so it is no pad: not adding it");
                    continue;
                }
                template.Locations.Add(made);
            }
            Log.Info($"laid out {PerSite - 1} more launchpads in a ring at {site.Id}, {ring:F0} m out and {chord:F0} m apart");
        }
    }
}
