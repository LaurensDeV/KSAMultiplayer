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
    /// <para>Enough that the grass a pad lays over the ground around it stops short of its
    /// neighbour's. That disc is the widest thing a pad draws by a long way -- 81.9 m to the mesh's
    /// rim and 82.5 m to the corners of the boxes that collide it, against 62.1 m for the circular
    /// road and 14.9 m for the concrete -- so two of them want 165 m between centres and this
    /// leaves them four metres apart. The least this may be is twice that disc over the footprint,
    /// 165 / 108.3; <see cref="Ring"/> turns whatever is set here into both distances that follow
    /// from it, neighbours in the ring ending up exactly this many footprints apart and the ring
    /// standing 1.152 times that from the site's own pad.</para>
    ///
    /// <para>1.2 was read off a picture, and put the centres 130 m apart: those discs overlapped by
    /// 35 m on every side. Nothing showed while the ground still sloped, the overlaps being buried
    /// along with everything else on the inward side. Levelled, the discs are exactly coplanar,
    /// their depth values are interpolated from different matrices and round against each other
    /// pixel by pixel, and the overlap came back as a patchwork of each showing through the other,
    /// worst close up. <see cref="Level"/> is why this number is what it is.</para>
    /// </summary>
    private const double Spacing = 1.56;

    /// <summary>
    /// How much room the site's own levelling is given to come back down to the planet, in metres.
    ///
    /// <para>A site's decal holds the ground at one height over a flat core and feathers back to
    /// whatever the planet makes of it by the decal's radius. Widening the core eats into that rim,
    /// and a rim given no room is a berm: Kourou's decal is 250 m across, so taking its core out to
    /// where the pads are and leaving its radius alone would drop 7.8 m over 18 m of ground. This is
    /// the width the rim keeps before the radius itself is widened to make room for it.</para>
    /// </summary>
    private const double Rim = 150.0;

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
        location.Key.Contains(Suffix, StringComparison.Ordinal);

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
    /// This mod's pads taken out of the editor's own list of places to launch from, once it has
    /// built it: a player picks the site they know by name, not one of eight numbered slabs.
    ///
    /// <para>Its list, never the world's. The celestial template's <c>Locations</c> are walked by
    /// <c>ConstraintSim.UpdateLaunchPads</c> on the physics worker while the hooks run, so taking
    /// pads out of that list and handing them back as the editor opened was a list modified under
    /// that enumerator: "Collection was modified", failing the physics of every craft in the bubble,
    /// every time somebody opened the VAB. The editor's own list is private and is reached by
    /// reflection, which is the cheaper price.</para>
    /// </summary>
    /// <param name="menu">
    /// Whichever of KSA's menus has just built a list of places to launch from. There are three --
    /// the vehicle editor, the launch menu for a vehicle already built, and the picker when a game
    /// is started -- and each keeps its list under its own name.
    /// </param>
    public static void HideFromEditor(object menu)
    {
        System.Collections.IList? offered =
            (AccessTools.Field(menu.GetType(), "_locationObjects")?.GetValue(menu)
             ?? AccessTools.Field(menu.GetType(), "_locations")?.GetValue(menu)) as System.Collections.IList;
        if (offered is null) return;
        for (int i = offered.Count - 1; i >= 0; i--)
        {
            if (offered[i] is not { } one) continue;
            if (AccessTools.Method(one.GetType(), "GetKey")?.Invoke(one, null) is string key
                && key.Contains(Suffix, StringComparison.Ordinal))
            {
                offered.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// The pads of one site, the one the game shipped first and then its ring, so a quiet site is
    /// launched from exactly where it always was.
    /// </summary>
    public static List<LandmarkReference> At(CelestialTemplate template, string siteId) =>
        [.. template.Locations.OfType<LandmarkReference>()
                    .Where(l => l.IsLaunchPad && (l.Key == siteId || l.Key.StartsWith(siteId + Suffix, StringComparison.Ordinal)))
                    .OrderBy(l => l.Key.Length)
                    .ThenBy(l => l.Key, StringComparer.Ordinal)];

    /// <summary>
    /// The pads of a site in the order this game tries them: as they are while this game is its own,
    /// so a world nobody else is in launches from the pad it always did, and turned by this player's
    /// id while it is in a world with others, so that no two games start at the same pad.
    ///
    /// <para>The site's own pad used to be put first whatever else was turned, which left the
    /// turning doing nothing: every game's first choice was the same slab, and two players launching
    /// at a quiet site -- before either's craft or state had crossed the wire for <see cref="Free"/>
    /// to count -- both stood on it. The pads of a site are the same concrete as each other, sharing
    /// the site's own <c>StaticObjectReference</c>, so there is nothing to prefer about any of
    /// them. An id past the number of pads wraps onto one an earlier player starts at, and from
    /// there the craft and states <see cref="Free"/> counts are what keeps them apart; this only
    /// covers the moment before the first of them arrives.</para>
    /// </summary>
    private static List<LandmarkReference> Ordered(List<LandmarkReference> pads)
    {
        if (pads.Count < 2 || Sync.Client is not { Joined: true } c) return pads;
        int from = c.Id % pads.Count;
        List<LandmarkReference> mine = new(pads.Count);
        for (int i = 0; i < pads.Count; i++) mine.Add(pads[(from + i) % pads.Count]);
        return mine;
    }

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
        int cut = asked.Key.IndexOf(Suffix, StringComparison.Ordinal);
        return Free(celestial, cut < 0 ? asked.Key : asked.Key[..cut]);
    }

    /// <summary>
    /// The first pad of a site with nothing standing on it, or null when every one is taken.
    ///
    /// <para>Taken is KSA's own test for whether a craft is on a pad: within the pad's footprint of
    /// its centre, measured as an angle at the body and turned back into metres. The footprint is
    /// 108 m, neighbours in the ring stand 169 m apart and the ring 195 m out from the site's own
    /// pad, so a craft standing on one pad is outside every other's.</para>
    /// </summary>
    public static LandmarkReference? Free(Celestial celestial, string siteId)
    {
        if (celestial.BodyTemplate is not { } template) return null;
        List<double3> craft = [.. World.Vehicles().Where(v => ReferenceEquals(v.Parent, celestial))
                                       .Select(v => World.DirectionOf(v, celestial))
                                       .Where(d => d is not null)
                                       .Select(d => d!.Value)];
        // And every craft the others have told us about, whether or not it stands here yet. Two
        // players launching within a second of each other both saw the same pad free, because a
        // craft does not exist in the other game until its state has crossed the wire and the
        // puppet has been built. The state arrives long before the craft does, and a pad with
        // somebody's state on it is a pad somebody is on.
        if (Sync.Client is { Joined: true } c)
        {
            double now = World.Universe;
            foreach (string key in c.Tracked)
            {
                if (c.TrackOf(key).At(now) is not { } s || s.Body != celestial.Id) continue;
                double3 where = new(s.PositionCcf.X, s.PositionCcf.Y, s.PositionCcf.Z);
                if (where.LengthSquared() > 0.0) craft.Add(where.Normalized());
            }
        }
        foreach (LandmarkReference pad in Ordered(At(template, siteId)))
        {
            if (pad.GetStaticObject() is not { } concrete) continue;
            if (!craft.Any(d => (pad.ForwardCcf - d).Length() * celestial.MeanRadius < concrete.FootprintRadius))
            {
                // Only when the answer changes. The editor asks this every frame it is open to know
                // whether to warn, and logging each one put two thousand lines in a session.
                if (!_said.TryGetValue(siteId, out string? was) || was != pad.Key)
                {
                    _said[siteId] = pad.Key;
                    Log.Info($"{pad.Key} is the free pad at {siteId}, with {craft.Count} craft on {celestial.Id}");
                }
                return pad;
            }
        }
        return null;
    }

    // The chosen body and site of whichever menu is asking, which each keeps to itself. Read rather
    // than guessed at: the list it offers is the one this mod filtered, so the name in it is a
    // site's and the pads of that site are what matter. By the menu's own type, because the vehicle
    // editor and the launch menu hold these under the same two names and both must be asked -- and
    // kept, because the editor asks every frame it is open.
    private static readonly Dictionary<Type, (System.Reflection.FieldInfo? Body, System.Reflection.FieldInfo? Site)> Asked = [];

    /// <summary>Which body and which launch site a menu is pointed at, as far as it will say.</summary>
    public static (Celestial? Body, string Site) Chosen(object menu)
    {
        Type kind = menu.GetType();
        if (!Asked.TryGetValue(kind, out (System.Reflection.FieldInfo? Body, System.Reflection.FieldInfo? Site) found))
        {
            Asked[kind] = found = (AccessTools.Field(kind, "_selectedCelestial"), AccessTools.Field(kind, "_selectedLocation"));
        }
        object? body = found.Body?.GetValue(menu);
        object? site = found.Site?.GetValue(menu);
        Celestial? where = body is null
            ? null
            : (AccessTools.Field(body.GetType(), "Celestial")?.GetValue(body)
               ?? AccessTools.Property(body.GetType(), "Celestial")?.GetValue(body)) as Celestial;
        string named = site is null ? "" : AccessTools.Method(site.GetType(), "GetKey")?.Invoke(site, null) as string ?? "";
        return (where, named);
    }

    /// <summary>
    /// Whether the site a menu is pointed at has a pad to put a craft on. True whenever it cannot be
    /// told: a launch is never refused over a question this mod could not answer.
    /// </summary>
    public static bool Room(object menu)
    {
        try
        {
            (Celestial? body, string site) = Chosen(menu);
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

    /// <summary>How far this mod's pads stand from the site they ring, in metres.</summary>
    private static double Ring(double footprint) =>
        footprint * Spacing / (2.0 * Math.Sin(Math.PI / (PerSite - 1)));

    /// <summary>
    /// The ground under this mod's pads, levelled with the ground under the site's own.
    ///
    /// <para>KSA levels a launch site with a terrain decal that holds the ground at one absolute
    /// height over a flat core and feathers back to the planet's own by the decal's radius. The core
    /// is 85 m at the Cape and 68 m at Vandenberg, and these pads stand 150 m out: on the feather,
    /// where some of the hillside still comes through. A pad is a rigid disc placed flush with the
    /// ground at its own centre, so the side of it facing the site is buried and the side facing
    /// away hangs over a drop -- 1.6 m and 2.8 m at the Cape, 5.8 m and 10.5 m at Vandenberg, where
    /// a kitten can walk in under a pad and be stuck there. Pads on a slope also stand at different
    /// heights from one another, the hillside under each one's centre being its own.</para>
    ///
    /// <para>Widening the core until the pads are inside it is the one change that answers all
    /// three: within the core the decal's weight is exactly one, so the ground under the whole
    /// complex is dead flat and every pad sits on it at the same height. The numbers are the site's
    /// own and this mod's own rather than anything measured and written down here, so a change to
    /// <see cref="Spacing"/> or <see cref="PerSite"/> carries the levelling with it.</para>
    ///
    /// <para>Called where the celestials exist and nothing has read the terrain yet. KSA copies a
    /// decal's radius into the parameters the GPU reads as the planets' render data is built, and
    /// fills that buffer once, in the planet renderer's constructor. Widened before it, every reader
    /// agrees and nothing has to be rebuilt; widened after, the ground the physics finds and the
    /// ground a player sees would be different ground.</para>
    /// </summary>
    public static void Level()
    {
        try
        {
            if (Universe.CurrentSystem is not { } system) return;
            foreach (Celestial celestial in system.All.OfType<Celestial>())
            {
                if (celestial.BodyTemplate is not { } template) continue;
                if (template.TerrainReference?.ProceduralModifiers?.Modifiers is not { } modifiers) continue;
                foreach (LandmarkReference site in Sites(template))
                {
                    if (site.GetStaticObject() is not { } pad || pad.FootprintRadius <= 0.0) continue;
                    // The whole complex: out to the ring, and a pad's own footprint past the far
                    // side of it, which reaches further than anything the pad draws.
                    double want = Ring(pad.FootprintRadius) + pad.FootprintRadius;
                    foreach (DecalModifierReference decal in modifiers.OfType<DecalModifierReference>())
                    {
                        if (decal.Poi is not { } where) continue;
                        // The decal that levels this site, found by where it is and not by what it
                        // is called: it carries its own copy of the site's position, and KSA's two
                        // names for the one place are not the same string.
                        if ((where.ForwardCcf - site.ForwardCcf).Length() * celestial.MeanRadius
                            > pad.FootprintRadius) continue;
                        Widen(decal, want, site.Key);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not level the ground at the launch sites: {e.GetBaseException().Message}");
        }
    }

    // Absolute and never relative: the core asked for is the same every time, so a second pass over
    // a decal already widened finds it wide enough and leaves it alone.
    private static void Widen(DecalModifierReference decal, double core, string siteId)
    {
        double had = (1.0 - decal.SmoothFactor.Value) * decal.Radius.Value;
        if (had >= core) return;
        double radius = Math.Max(decal.Radius.Value, core + Rim);
        decal.Radius.Value = radius;
        decal.SmoothFactor.Value = 1.0 - core / radius;
        Log.Info($"levelled the ground {core:F0} m out at {siteId}, from {had:F0} m, "
                 + $"inside a decal of {radius:F0} m");
    }

    /// <summary>
    /// The ground to keep ground clutter off at each of this celestial's launch sites: one circle
    /// around the whole complex rather than one around every pad, as a direction and an angle.
    ///
    /// <para>KSA keeps four of these for a whole planet -- <c>MAX_LAUNCH_PAD_EXCLUSION_ZONES</c>, in
    /// the clutter compute shader as much as in the struct handed to it -- and spends one on each of
    /// the first four launchpads in the celestial's list. Earth ships five launch sites, so the
    /// fifth has grass growing through it in a game with no mods at all, and the pads this mod adds
    /// never stood a chance. One circle per site is not an approximation of the eight it stands in
    /// for: a ring pad's own circle already reaches past the site's centre, so the eight together
    /// are exactly a circle of the ring plus a pad's clearance, which is what this gives.</para>
    /// </summary>
    public static IEnumerable<(double3 Where, double Angle)> Clearings(Celestial celestial)
    {
        if (celestial.BodyTemplate is not { } template) yield break;
        foreach (LandmarkReference site in Sites(template))
        {
            if (site.GetStaticObject() is not { } pad || pad.FootprintRadius <= 0.0) continue;
            double metres = Ring(pad.FootprintRadius) + pad.FootprintRadius
                            + LocationReference.CLUTTER_CLEARANCE_METERS;
            yield return (site.ForwardCcf, metres / celestial.MeanRadius);
        }
    }

    // The sites the game shipped, which are the ones this mod rings. Taken as an array because a
    // caller may add to the list it comes from.
    private static LandmarkReference[] Sites(CelestialTemplate template) =>
        [.. template.Locations.OfType<LandmarkReference>().Where(l => l.IsLaunchPad && !Ours(l))];

    private static void Spread(Celestial celestial, CelestialTemplate template)
    {
        // The sites the game shipped, taken before anything is added to the list that is being read.
        foreach (LandmarkReference site in Sites(template))
        {
            if (site.GetStaticObject() is not { } pad || pad.FootprintRadius <= 0.0) continue;
            // A ring around the pad the game shipped, rather than rows running off one side of it.
            // Only the ground by a site is any good: it is flattened by a terrain decal there and is
            // whatever the planet makes of it a few hundred metres out, and a pad is a rigid disc
            // that stands half buried in anything else. Seven in a ring sit 150 m from the site where
            // two rows of four threw the far one 411 m away.
            double chord = pad.FootprintRadius * Spacing;
            double ring = Ring(pad.FootprintRadius);
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
                    Key = $"{site.Key}{Suffix}{i + 1}",
                    IsLaunchPad = true,
                    // The site's own reference, shared rather than copied: it is already resolved by
                    // the time a world is current, and a new one made here would be tracked by no
                    // content scope and so never resolve to the concrete it names.
                    StaticObjectReference = site.StaticObjectReference,
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
                    Log.Warn($"{made.Key} has no concrete under it, so it is no pad: not adding it");
                    continue;
                }
                template.Locations.Add(made);
            }
            Log.Info($"laid out {PerSite - 1} more launchpads in a ring at {site.Key}, {ring:F0} m out and {chord:F0} m apart");
        }
    }
}
