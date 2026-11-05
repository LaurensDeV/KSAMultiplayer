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
    /// <summary>Pads per site, counting the one the game already had: two rows of four.</summary>
    public const int PerSite = 8;

    private const int Columns = 4;

    /// <summary>
    /// How far apart their centres sit, as a multiple of the pad's own footprint.
    ///
    /// <para>Just over one. The footprint is the clearance KSA keeps around a pad, not the concrete:
    /// measured off a picture of the eight, the apron is about 118 m across against a footprint
    /// radius of 108 m. So a little more than one footprint between centres leaves the aprons nearly
    /// touching, and keeps all eight on the flat ground by the site -- at 2.5 they were spread over
    /// 800 m and the far ones stood half buried in the hillside.</para>
    /// </summary>
    private const double Spacing = 1.2;

    /// <summary>What this mod's pads are called: the site's own id and a number, from 2 up.</summary>
    public const string Suffix = " MP";

    private static readonly HashSet<string> Added = [];

    private static object? _laidOutFor;

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
            // Metres to radians at this body: a pad's spacing is a distance, and a landmark is an angle.
            double step = pad.FootprintRadius * Spacing / celestial.MeanRadius;
            for (int i = 1; i < PerSite; i++)
            {
                // The shipped pad is the first of the first row; the rest fill out the two rows beside
                // and behind it, east along a row and north between them.
                double east = i % Columns * step;
                double north = i / Columns * step;
                LandmarkReference made = new()
                {
                    Id = $"{site.Id}{Suffix}{i + 1}",
                    IsLaunchPad = true,
                    StaticObjectId = site.StaticObjectId,
                    // Longitude closes up towards the poles, so a step east is wider in longitude the
                    // further the site is from the equator.
                    Latitude = new RadianReference(site.Latitude.GetRadians() + north),
                    Longitude = new RadianReference(site.Longitude.GetRadians() + (east / Math.Max(0.01, Math.Cos(site.Latitude.GetRadians())))),
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
            Log.Info($"laid out {PerSite - 1} more launchpads at {site.Id}, {pad.FootprintRadius * Spacing:F0} m apart");
        }
    }
}
