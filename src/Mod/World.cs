using System.Xml.Serialization;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// Every contact with KSA but the hooks and the panel, kept here so an update to the game has one
/// place to break in. Nothing here throws to its caller where a failure can be reported instead.
/// </summary>
internal static class World
{
    /// <summary>The save a world is handed over in: the server's world written here and loaded, and this world saved here to send.</summary>
    public const string HandoverSave = "Multiplayer";

    public static Vehicle? Controlled => Program.ControlledVehicle is { IsDisposed: false } v ? v : null;

    public static bool InFlight => Controlled is not null;

    // KSA's own warp steps, read off the game rather than written down here, so the mod steps the
    // ladder the keys would. The field is private; a build that moves it falls back to the steps
    // the game has had, which is wrong only in how far one press goes.
    private static IReadOnlyList<double>? _warpSteps;

    public static IReadOnlyList<double> WarpSteps
    {
        get
        {
            if (_warpSteps is not null) return _warpSteps;
            try
            {
                if (AccessTools.Field(typeof(KSA.Universe), "_simulationQuickSpeeds")?.GetValue(null) is List<double> { Count: > 1 } steps)
                {
                    return _warpSteps = [.. steps.Order()];
                }
                Log.Warn("the game's warp steps could not be read; the mod's own are used");
            }
            catch (Exception e)
            {
                Log.Warn($"could not read the game's warp steps: {e.GetBaseException().Message}");
            }
            return _warpSteps = [1.0, 2.0, 5.0, 10.0, 50.0, 100.0, 1000.0, 10000.0];
        }
    }

    /// <summary>A warp key pressed, for a test with no hand on the keyboard.</summary>
    public static void WarpKey(int direction)
    {
        if (direction > 0) KSA.Universe.IncreaseSimulationSpeed();
        else if (direction < 0) KSA.Universe.DecreaseSimulationSpeed();
        else KSA.Universe.ResetSimulationSpeed();
    }

    /// <summary>
    /// The warp step above or below a speed, or the speed itself at either end of the ladder. The
    /// step is taken from the nearest rung, so a speed nudged a hair off one still counts as on it.
    /// </summary>
    public static double WarpStepFrom(double speed, int direction)
    {
        IReadOnlyList<double> steps = WarpSteps;
        int at = 0;
        for (int i = 1; i < steps.Count; i++)
        {
            if (Math.Abs(steps[i] - speed) < Math.Abs(steps[at] - speed)) at = i;
        }
        return steps[Math.Clamp(at + Math.Sign(direction), 0, steps.Count - 1)];
    }

    /// <summary>
    /// Whether this game is looking at a craft at all: one it flies, or another player's it watches.
    /// An observer controls nothing, and everything the mod draws has to go on being drawn for one.
    /// </summary>
    public static bool AtACraft => Controlled is not null || Watched is not null;

    public static double Universe => KSA.Universe.GetElapsedSeconds();

    public static double Speed => KSA.Universe.SimulationSpeed;

    /// <summary>
    /// Whether a key is held, asked of the window rather than of ImGui.
    ///
    /// <para>KSA hands a key to the game only while ImGui does not want the keyboard, and gives ImGui
    /// keys only when one of its windows has the focus, so a window that wants a key without taking
    /// the focus first -- a scoreboard, a chat line that opens on Enter -- never sees one. The window
    /// itself always knows.</para>
    /// </summary>
    public static bool KeyDown(GlfwKey key)
    {
        try { return Glfw.PInvoke.GetKey(Program.GetWindow(), (int)key) != 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether the map is the view. KSA binds Tab there, to take and release control of a craft from
    /// the map, and nowhere else: the player list keeps out of the map so that binding is left alone.
    /// </summary>
    public static bool InMapView
    {
        get
        {
            try { return Program.GetCameraMode() == CameraMode.Map; }
            catch (Exception) { return false; }
        }
    }

    public static string KsaBuild => typeof(Vehicle).Assembly.GetName().Version?.ToString() ?? "";

    /// <summary>The celestial system being played, such as Sol entire or Earth and its Moon alone.</summary>
    public static string System => KSA.Universe.CurrentSystem?.Id ?? "";

    public static string Mods =>
        ModLibrary.Manifest is { } manifest ? string.Join(',', manifest.Mods.Where(m => m.Enabled).Select(m => m.Id)) : "";

    /// <summary>The longest step KSA takes a frame, s: a frame slower than this loses the rest.</summary>
    public static double StepCap => 1.0 / Math.Max(1f, GameSettings.Current.Simulation.MinTargetFrameRate);

    /// <summary>
    /// Whether every craft in this world is one KSA works out in closed form rather than stepping
    /// through: on rails, or sitting on something. A step of an hour costs what a step of a frame
    /// costs for those, and costs an hour of stepping for anything else, so this is what says
    /// whether a world may close a long gap in a moment or must crawl back.
    /// </summary>
    public static bool Analytic
    {
        get
        {
            try
            {
                foreach (Vehicle v in Vehicles())
                {
                    if (!v.Situation.IsOnRails() && !v.Situation.HasAnyContact()) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Whether this game's window has the player's attention. Windows throttles the window that is
    /// not in front, so what an unfocused game manages says nothing about the machine behind it: it
    /// is an absent player rather than a slow one, and a world does not slow down for somebody who
    /// is not looking at it.
    /// </summary>
    public static bool Focused
    {
        get
        {
            try { return Program.GetWindow() is { IsFocused: true }; }
            catch (Exception) { return true; }
        }
    }

    /// <summary>
    /// How much of the speed it was asked for this game is managing, 0 to 1, as KSA measures its own
    /// solver against its frame budget. One means there was room to spare.
    /// </summary>
    public static double Achieved
    {
        get
        {
            try
            {
                double f = KSA.Universe.GetAchivedSpeedFraction();
                return double.IsFinite(f) && f > 0.0 ? Math.Min(f, 1.0) : 1.0;
            }
            catch (Exception) { return 1.0; }
        }
    }

    public static void SetSpeed(double speed)
    {
        if (KSA.Universe.IsAutoWarpActive) KSA.Universe.AutoWarpStop(resetSimulationSpeed: false);
        if (Math.Abs(KSA.Universe.SimulationSpeed - speed) > 1e-9) KSA.Universe.SetSimulationSpeed(speed, alert: false);
    }

    /// <summary>Every live craft in the world, kittens on EVA among them.</summary>
    public static List<Vehicle> Vehicles()
    {
        List<Vehicle> into = [];
        if (KSA.Universe.CurrentSystem is not { } system) return into;
        ReadOnlySpan<Astronomical> all = system.All.AsSpan();
        for (int i = 0; i < all.Length; i++) if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
        return into;
    }

    public static bool IsAlive(Vehicle? v) => v is { IsDisposed: false };

    // What a craft's parts look like from outside, cheaply: a change means its definition is sent again.
    /// <summary>
    /// What a craft is, for deciding whether the others have to be told how it is built again: its
    /// parts and the body it is going round.
    ///
    /// <para>The body because a definition carries one -- <c>VehicleData.ParentBody</c> -- and it
    /// was only ever sent again when the part count changed. So every game's stored definition of a
    /// craft stayed pinned to wherever it was when they last heard, and a player who joined after
    /// somebody flew to the Moon built that craft back at the Earth, where nothing then moved it.
    /// Only a hash within this process, so what it is made of does not matter.</para>
    /// </summary>
    public static int Shape(Vehicle v) =>
        (v.Parts.Count * 397) ^ (v.Parts.Root?.Template?.Id?.GetHashCode() ?? 0) ^ (BodyOf(v)?.GetHashCode() ?? 0);

    /// <summary>What this game has a craft going round, by its own physics origin, or null.</summary>
    public static string? BodyOf(Vehicle v) => (v.GetPhysicsStatesMutable().Origin.Parent as Astronomical)?.Id;

    // ------------------------------------------------------------------ saves

    /// <summary>
    /// The craft in this world that are inside the body they belong to, by name.
    ///
    /// <para>KSA loses a craft into a body by itself, and once it is there no orbit can be made from
    /// where it says it is: a game handed a world holding one aborts on its first physics step. A
    /// world with any of these in it is not fit to give to anybody.</para>
    /// </summary>
    public static List<string> LostCraft()
    {
        List<string> lost = [];
        try
        {
            foreach (Vehicle v in Vehicles())
            {
                if (!IsAlive(v) || v.Parent is not Astronomical body || v.Parent is not IPosition at || body.MeanRadius <= 0.0) continue;
                if (Vec(v.GetPositionEcl() - at.GetPositionEcl()) < body.MeanRadius * HalfWayIn) lost.Add(v.Id);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not look over this world: {e.GetBaseException().Message}");
        }
        return lost;
    }

    /// <summary>
    /// Why this world can be neither saved nor loaded at the moment, or empty when it can be.
    ///
    /// <para>KSA refuses both while the vehicle editor is open: every save and load it has begins by
    /// asking, and the answer is a line in its own log and a return. Both calls are void, so a mod
    /// that did not ask the same question hands on whatever save was last written as the world now,
    /// or counts its frames over a world it never loaded and says it is in the shared one.</para>
    /// </summary>
    public static string Refuses() => Program.IsEditorOpen ? "the vehicle editor is open, and KSA saves and loads nothing while it is" : "";

    /// <summary>
    /// This world saved as the handover save and zipped, with the universe time it was saved at, or
    /// null and why not.
    ///
    /// <para>The folder is emptied first and the save is whatever is found in it afterwards, because
    /// KSA's save writes nothing while the editor is open and nothing again if the folder cannot be
    /// replaced: the handover save left there from last time would be zipped and handed on as the
    /// world now, and every other game corrected to a world minutes out of date.</para>
    /// </summary>
    public static (byte[] Zip, double Universe)? SaveWorld(out string why)
    {
        why = Refuses();
        if (why.Length > 0) return null;
        try
        {
            string folder = Path.Combine(GameSaves.SaveFolderPath, HandoverSave);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            double universe = Universe;
            GameSaves.MakeUncompressedSave(HandoverSave);
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
            {
                why = "KSA wrote no save of this world";
                return null;
            }
            return (Blob.ZipFolder(folder), universe);
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return null;
        }
    }

    /// <summary>A world from the server written as the handover save and loaded in place of this one, or false and why not.</summary>
    public static bool LoadWorld(byte[] zip, out string why)
    {
        why = Refuses();
        if (why.Length > 0) return false;
        try
        {
            Blob.UnzipInto(zip, Path.Combine(GameSaves.SaveFolderPath, HandoverSave));
            GameSaves.Refresh();
            GameSaves.LoadSaveGame(HandoverSave);
            return true;
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ craft definitions

    // Built once: an XmlSerializer for a type this size takes seconds to make.
    private static XmlSerializer? _serializer;
    private static XmlSerializer Serializer => _serializer ??= new XmlSerializer(typeof(VehicleData), XmlHelper.AttributeOverrides);

    /// <summary>
    /// A craft as KSA saves it, gzipped: what another game builds it from, under <paramref name="key"/>.
    ///
    /// <para>The name in the definition is the one the whole world knows the craft by, which is not
    /// this game's own when the server has had to rename it: a craft is built from this elsewhere, and
    /// two craft of one name in one world would be one craft to everything that looks them up.</para>
    /// </summary>
    public static byte[] Define(Vehicle v, string key)
    {
        VehicleData data = v.SerializeSave();
        if (v is KittenEva kitten) data.Character = kitten.Character.Id;
        if (key != v.Id)
        {
            data.Id = key;
            data.IdHash = KeyHash.Make(key.AsSpan());
        }
        using MemoryStream stream = new();
        XmlHelper.SerializeWithoutNaN(Serializer, data, stream);
        return Blob.Gzip(stream.ToArray());
    }

    /// <summary>
    /// Whether this world already has something of this name: a craft, a body, anything of KSA's.
    ///
    /// <para>One lookup holds all of them, under a hash of the name, and nothing else in the game
    /// goes near two of one name -- so this asks that lookup rather than only the craft.</para>
    /// </summary>
    public static bool Taken(string id)
    {
        try
        {
            return KSA.Universe.CurrentSystem is not { } system || system.All.TryGet(id, out _);
        }
        catch (Exception e)
        {
            Log.Warn($"could not look {id} up in this world: {e.GetBaseException().Message}");
            return true;
        }
    }

    /// <summary>A name nothing in this world answers to, like the one asked for.</summary>
    public static string FreeLocalId(string root)
    {
        for (int n = 2; n < 1000; n++)
        {
            string tried = $"{root} ({n})";
            if (!Taken(tried)) return tried;
        }
        return $"{root} ({Guid.NewGuid():N})";
    }

    /// <summary>
    /// A craft built from another game's definition under <paramref name="localId"/>, or null with why.
    ///
    /// <para>The name it is built under is this world's, which is not what everyone calls the craft when
    /// this game already has one of that name: KSA looks a craft up by its Id, and two of one name here
    /// would be one craft to everything that does.</para>
    /// </summary>
    public static Vehicle? Build(byte[] definition, string localId, out string why)
    {
        why = "";
        try
        {
            if (KSA.Universe.CurrentSystem is not { } system)
            {
                why = "no world is loaded";
                return null;
            }
            using MemoryStream stream = new(Blob.Gunzip(definition));
            if (Serializer.Deserialize(stream) is not VehicleData data)
            {
                why = "it could not be read";
                return null;
            }
            if (localId.Length > 0 && localId != data.Id)
            {
                data.Id = localId;
                data.IdHash = KeyHash.Make(localId.AsSpan());
            }
            data.OnDataLoad(Mod.Empty);
            if (data.RootPartInstance?.FindMissingParts() is { } missing)
            {
                why = $"it has parts this game does not: {missing}";
                return null;
            }
            if (data.ParentBody.Get() is not IParentBody parent)
            {
                why = $"its body {data.ParentBody.Id} is not in this world";
                return null;
            }
            // KSA's lookup refuses a second thing under one hash with a line in its own log and
            // nothing else: no throw, and a craft back from here that is in no lookup. Nothing would
            // ever list it, nothing would step it, nothing would sweep it -- another player's craft
            // standing in this world for ever, which the mod would go on treating as their puppet.
            if (Taken(data.Id))
            {
                why = $"this world already has something called {data.Id}";
                return null;
            }
            Vehicle built = string.IsNullOrEmpty(data.Character)
                ? Vehicle.CreateVehicleFromSaveGameData(system, data)
                : KittenEva.CreateKittenFromSaveData(system, data);
            if (system.All.Get(data.Id) is not { } registered || !ReferenceEquals(registered, built))
            {
                why = $"KSA did not take {data.Id} into this world";
                return null;
            }
            parent.Children.Add(built);
            return built;
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return null;
        }
    }

    public static void Destroy(Vehicle v)
    {
        try
        {
            if (IsAlive(v) && !ReferenceEquals(v, Controlled)) KSA.Universe.DestroyVehicle(v);
        }
        catch (Exception e)
        {
            Log.Warn($"could not take {v.Id} away: {e.GetBaseException().Message}");
        }
    }

    // ------------------------------------------------------------------ states

    // Nearer the middle of a body than this and a craft is not somewhere, it is broken: a landed
    // craft stands at the body's own radius and one in orbit is further out again.
    private const double HalfWayIn = 0.5;

    // Said once a craft, not once a frame.
    private static readonly HashSet<string> _lost = [];

    /// <summary>
    /// Which way a craft lies from the centre of its body, as a unit vector in the body's fixed
    /// frame. What a landmark is measured against: KSA decides a craft is on a pad by how far this
    /// is from the pad's own direction.
    /// </summary>
    public static double3? DirectionOf(Vehicle v, Celestial body)
    {
        try
        {
            // From where the craft is, not from its physics state: a craft outside a physics bubble
            // -- which is every craft standing on a pad nobody is near -- has nothing in that state,
            // so reading it found no craft anywhere and every pad looked free.
            double3 cce = v.GetPositionEcl() - body.GetPositionEcl();
            double3 ccf = cce.Transform(body.GetCce2Ccf());
            return ccf.LengthSquared() > 0.0 ? ccf.Normalized() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Where a craft is now, in its body's fixed frame off the physics state and in its inertial frame off the ecliptic.</summary>
    /// <param name="key">What the world knows this craft by, which is not this game's own name for it when the server has renamed it.</param>
    public static Wire.State? StateOf(Vehicle v, string key, bool controlled)
    {
        PhysicsStates states = v.GetPhysicsStatesMutable();
        // Both frames and the name of the body from the one place, which is the craft's own physics
        // origin. Read apart, they disagree for a frame: the ecliptic position of a craft is a cache
        // refreshed once a frame in ApplyVehicleSolvers, and a teleport lands after that -- so a
        // craft moved to the Moon went out saying "Luna" with its Ccf frame already there and its
        // Cci frame still on the pad it left, 384,000 km and a kilometre a second wrong. KSA's own
        // movers refresh the cache themselves; the debug teleport does not.
        if (states.Origin.Parent is not Astronomical parent) return null;
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        PhysicsStates.GetStatesCci(in states.Origin, in states.Kinematic, out double3 positionCci, out double3 velocityCci, out _);
        double3 spin = states.Kinematic.AngularVelocityPhys.Transform(states.Kinematic.Body2Phys.Inverse());

        (bool lit, float throttle) = EngineOf(v);

        Wire.StateFlags flags = Wire.StateFlags.None;
        if (lit) flags |= Wire.StateFlags.EngineOn;
        if (v.Situation.IsOnRails()) flags |= Wire.StateFlags.Rails;
        if (v.Situation.HasAnyContact()) flags |= Wire.StateFlags.Landed;
        if (v.LightsOn) flags |= Wire.StateFlags.Lights;
        if (controlled) flags |= Wire.StateFlags.Controlled;

        // A craft inside its own body is one KSA has lost: near the centre its own gravity maths
        // runs away, no orbit can be made from the state at all, and the game that is handed it dies
        // on the first physics step with "cannot construct a UniverseTime from NaN". It stays here.
        // Finite is not enough: the centre of the Earth is a perfectly finite place to be.
        if (parent.MeanRadius > 0.0 && Vec(positionCcf) < parent.MeanRadius * HalfWayIn)
        {
            if (_lost.Add(key)) Log.Warn($"{key} is inside {parent.Id}: the others are not told where it is");
            return null;
        }
        _lost.Remove(key);

        Wire.State s = new(0, key, 0, Universe, parent.Id, V(positionCcf), V(velocityCcf), V(positionCci), V(velocityCci),
                           new Quat(body2Ccf.X, body2Ccf.Y, body2Ccf.Z, body2Ccf.W), V(spin),
                           float.IsFinite(throttle) ? Math.Clamp(throttle, 0f, 1f) : 0f, flags);
        return s.PositionCcf.IsFinite && s.PositionCci.IsFinite && s.Body2Ccf.IsFinite ? s : null;
    }

    /// <summary>
    /// A craft with no physics here put where its owner says, by its orbit or, on the ground, by
    /// setting it down there. Seldom: each is a craft rebuilt. True if it was moved.
    /// </summary>
    public static bool PlaceAnalytic(Vehicle v, Wire.State s, double nearEnoughM)
    {
        if (v.HasPhysicsBubble) return false;
        if (KSA.Universe.CurrentSystem?.Get(s.Body) is not Celestial body) return false;
        // Sent by a game old enough not to hold this back, or lost on the way: a craft put inside a
        // body here is a world this game cannot save and nobody else can load.
        if (body.MeanRadius > 0.0 && Vec(D(s.PositionCcf)) < body.MeanRadius * HalfWayIn) return false;
        if ((s.Flags & Wire.StateFlags.Landed) != 0)
        {
            PhysicsStates states = v.GetPhysicsStatesMutable();
            PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 hereCcf, out _, out _);
            if (ReferenceEquals(v.Parent, body) && Vec(hereCcf - D(s.PositionCcf)) < nearEnoughM) return false;
            double3 cce = D(s.PositionCcf).Transform(body.GetCcf2Cce());
            v.TeleportToLocation(body, body.GetLatitudeFromCce(cce), body.GetLongitudeFromCce(cce));
            return true;
        }
        double3 at = v.Orbit.StateVectors.PositionCci;
        if (ReferenceEquals(v.Parent, body) && Vec(at - D(s.PositionCci)) < nearEnoughM) return false;
        // Never an epoch behind the craft's own origin. A state is interpolated a fixed delay behind
        // this game's clock, so its time is always in the past, and KSA refuses an origin that moves
        // backwards (PhysicsStates.TryUpdateKinematicOrigin logs "with origin at time ... with new
        // origin at time ..." and leaves the craft exactly where it was). A craft whose origin has
        // been carried forward -- by a physics bubble it has just left, say -- then stands still for
        // every frame until the delay is made up and jumps when it is, which is the jitter players
        // see. Carried forward along its own velocity instead, which over a tenth of a second is the
        // same place.
        double epoch = s.Universe;
        // Forward as far as the craft's own origin, so KSA does not refuse the placement -- but never
        // past this world's own clock. Taking the origin alone was a ratchet: it only ever moved
        // forward, so a craft that got ahead of the simulation stayed ahead for good, and every
        // physics bubble it joined afterwards refused to rebase it. Watched on the real server, a
        // craft 2.7 s ahead of the bubble it was joining, which is kilometres out of place in orbit
        // and the pull dragging it back every frame -- a player jittering about. Clamped, a craft
        // that gets ahead is overtaken by the clock and comes right on its own.
        double origin = v.GetPhysicsStatesMutable().Origin.Time.Seconds();
        double ahead = Math.Max(0.0, Math.Min(origin, Universe) - epoch);
        double3 positionCci = D(s.PositionCci) + (D(s.VelocityCci) * ahead);
        Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(epoch + ahead), positionCci, D(s.VelocityCci), v.OrbitColor);
        v.SetFlightPlan(new FlightPlan(orbit, KeyHash.Make(v.Id.AsSpan())));
        return true;
    }

    // Further out than this a craft is put where it is wanted outright; nearer, it is drawn there over a few steps, so a late datagram is not a jolt.
    private const double SnapM = 5.0, PullSeconds = 0.15;

    /// <summary>
    /// The most of the drawing-there that may show up as speed, m/s, and the distance a kitten is
    /// left alone within, m.
    ///
    /// <para>Because the speed a craft is drawn there at is the speed KSA measures it moving at, and
    /// for a kitten that is the speed it is animated walking at.
    /// <c>PhysicsBubble.BuildKittenLocomotionFacts</c> reads this very field back as
    /// <c>TangentialSpeedPhys</c>, and <c>KittenLocomotion</c> picks the walk clip above 0.2 m/s,
    /// saturates its playback rate at 1.6, and trips the kitten over at 7. Divided by
    /// <see cref="PullSeconds"/>, that is three centimetres of disagreement to start its legs
    /// moving and a metre to make it fall over -- and a puppet kitten is asked for no movement at
    /// all, so KSA's traction servo brakes whatever this writes back towards nothing. For a few
    /// centimetres the servo wins, the gap never closes, and the same correction goes in again every
    /// frame: a kitten walking on the spot for as long as anybody watches. Players called it
    /// jittering.</para>
    ///
    /// <para>So a kitten's correction is held below the walking threshold and it is left alone over
    /// a wider gap. The gap then closes more slowly, or not at all while the servo pushes back,
    /// which is the trade: a kitten standing four centimetres from where its owner says is nobody's
    /// problem, and <see cref="SnapM"/> still answers for a real distance. A rocket keeps the fast
    /// pull, having no legs to move.</para>
    /// </summary>
    private const double KittenPullMetresPerSecond = 0.15, KittenRestM = 0.25;

    /// <summary>
    /// Another player's craft written where its game says it is, from the physics window: turned as
    /// told, moving as told, and drawn onto its place. Nothing in it may throw.
    /// </summary>
    public static void Pull(Vehicle v, Wire.State s, double dt)
    {
        if (!v.HasPhysicsBubble || dt <= 0.0 || dt > 0.25) return;
        // Across a change of body the two frames differ, so there is nothing to write. Nothing
        // takes this over either: the analytic placing is only for a craft with no bubble, and this
        // craft has one -- it runs on its own physics with its owner's engines until the two games
        // agree on what it is going round, which is a moment at an SOI boundary.
        if ((v.Parent as Astronomical)?.Id is { } body && body != s.Body) return;
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out _, out doubleQuat body2Ccf);
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        doubleQuat ccf2Body = body2Ccf.Inverse();
        double3 off = D(s.PositionCcf) - positionCcf;

        // A kitten is left alone over a wider gap than a rocket, and the gap it is left alone within
        // is the gap that would otherwise wake it and set it walking on the spot.
        bool walks = v is KittenEva;
        double rest = walks ? KittenRestM : 0.05;
        // Standing where it is told to stand, it is left on KSA's rails: woken every step, its physics is built again every step.
        if (v.Situation.IsOnRails() && Vec(D(s.VelocityCcf)) < 0.05 && Vec(off) < rest) return;
        bool snap = Vec(off) > SnapM;
        if (snap) states.Kinematic.PositionPhys += off.Transform(ccf2Body).Transform(body2Phys);
        doubleQuat wantedBody2Ccf = new(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W);
        states.Kinematic.Body2Phys = body2Phys * (ccf2Body * wantedBody2Ccf);

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out double3 nowVelocity, out doubleQuat nowBody2Ccf);
        double3 draw = snap ? double3.Zero : off / Math.Max(PullSeconds, dt);
        // Held to a walk, not to a distance: what matters to a kitten's legs is how fast this says
        // it is going, and the distance was already answered by the snap above.
        if (walks && Vec(draw) > KittenPullMetresPerSecond)
        {
            draw = draw.Normalized() * KittenPullMetresPerSecond;
        }
        double3 wanted = D(s.VelocityCcf) + draw;
        states.Kinematic.VelocityPhys += (wanted - nowVelocity).Transform(nowBody2Ccf.Inverse()).Transform(states.Kinematic.Body2Phys);
        states.Kinematic.AngularVelocityPhys = D(s.Spin).Transform(states.Kinematic.Body2Phys);
        if (v.Situation.IsOnRails()) v.TakeOffRails();
    }

    /// <summary>
    /// Every exhaust plume, smoke trail and explosion cloud in this world taken away.
    ///
    /// <para>What is left behind after a lot of craft are swept costs more than the craft did: the
    /// emitters go on being stepped and drawn with nothing left to have made them, and on a server
    /// where a breakup had just filled the sky it was the slowest thing in the frame. These are the
    /// three calls KSA itself makes when it loads a world, which is the only other moment a world's
    /// contents vanish at once.</para>
    ///
    /// <para>Asked for from the frame's quiet window, with nothing of KSA's running.</para>
    /// </summary>
    public static void ClearSmoke()
    {
        try
        {
            Program.Instance.ParticleSystem.EmitterPool.ResetAll();
            Program.Instance.ClearPlumeTrails();
            Program.Instance.ClearExplosionVolumes();
        }
        catch (Exception e)
        {
            Log.Warn($"could not clear the smoke: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Another player's craft moved to the body they say it is going round, by handing it to KSA's
    /// own mover. True if it is there now.
    ///
    /// <para>Nothing else can follow a change of body. <see cref="Pull"/> has no coherent frame to
    /// write in and stands aside; <see cref="PlaceAnalytic"/> writes a flight plan without rebasing
    /// the craft's physics origin, so a bubble either reverts it on the next step or reads the old
    /// body's coordinates against the new one and puts the craft underground. <c>Vehicle.Teleport</c>
    /// is what KSA uses for the same job: it takes the craft out of its bubble first, rebases the
    /// origin onto the new body, and leaves it an orphan for the solver to pick up round the right
    /// one in the same frame.</para>
    ///
    /// <para>Asked for from the frame's quiet window, because it moves the craft between two
    /// celestials' lists of children.</para>
    /// </summary>
    public static bool Rebase(Vehicle v, Wire.State s)
    {
        try
        {
            if (KSA.Universe.CurrentSystem?.Get(s.Body) is not Celestial body) return false;
            Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(s.Universe), D(s.PositionCci), D(s.VelocityCci), v.OrbitColor);
            // Refused rather than thrown when the trajectory cannot be worked out, so this is asked
            // again rather than taken as done.
            v.Teleport(orbit, null, null);
            // As KSA does after its own teleports. The distance it would credit is zero across a
            // change of body, so nothing is awarded for the 384,000 km the craft did not travel.
            v.UpdatePerFrameData();
            return ReferenceEquals(v.GetPhysicsStatesMutable().Origin.Parent, body);
        }
        catch (Exception e)
        {
            Log.Warn($"could not put {s.Key} round {s.Body}: {e.GetBaseException().Message}");
            return false;
        }
    }

    public static void SetLights(Vehicle v, bool on)
    {
        if (v.LightsOn != on) v.LightsOn = on;
    }

    // What a craft's engines are doing is held in a private field of a public type: the throttle can
    // be read through GetManualThrottle, but whether the engine is lit cannot, and neither can be
    // written. Read once, kept, and null when a build moves it -- engines simply stay dark then.
    private static AccessTools.FieldRef<Vehicle, ManualControlInputs>? _engineInputs;
    private static bool _lookedForEngineInputs;

    private static AccessTools.FieldRef<Vehicle, ManualControlInputs>? EngineInputs
    {
        get
        {
            if (_lookedForEngineInputs) return _engineInputs;
            _lookedForEngineInputs = true;
            try
            {
                _engineInputs = AccessTools.FieldRefAccess<Vehicle, ManualControlInputs>("_manualControlInputs");
            }
            catch (Exception e)
            {
                Log.Warn($"engines of other players' craft cannot be shown firing: {e.GetBaseException().Message}");
            }
            return _engineInputs;
        }
    }

    /// <summary>
    /// Whether a craft's engine is burning, and how far its throttle is open.
    ///
    /// <para>From the nozzles and not from the player's hand on the key. EngineOn is wrong both ways:
    /// KSA sets it false every frame while the flight computer runs the burn, so a craft burning a
    /// manoeuvre node said its engines were cold; and it stays true on a craft whose ignition is on
    /// with nothing behind it -- no propellant, nothing staged -- which said it was burning when it
    /// was not, and was seen to, once the others could draw it. What the nozzles say is what this
    /// game's own renderer draws a plume from, so it is what the others are told.</para>
    /// </summary>
    public static (bool On, float Throttle) EngineOf(Vehicle v)
    {
        try
        {
            float throttle = v.GetManualThrottle(), firing = Firing(v);
            return firing > 0f ? (true, throttle > 0f ? throttle : Math.Min(1f, firing)) : (false, throttle);
        }
        catch (Exception)
        {
            return (false, 0f);
        }
    }

    // How hard this craft's engines are actually running: the fraction of full chamber pressure each
    // nozzle reached, and how much of the last step it ran for. Both are worked out in the physics
    // step, so they are nothing at all for a craft that is not being stepped -- which is why another
    // player's craft out of this game's own physics cannot be shown burning whatever it says.
    private static float Firing(Vehicle v)
    {
        if (v.Parts?.RocketNozzles is not { } nozzles) return 0f;
        ReadOnlySpan<RocketNozzleState> states = nozzles.States;
        float most = 0f;
        for (int i = 0; i < states.Length; i++) most = Math.Max(most, Math.Max(states[i].DutyCycle, states[i].ThrustFraction));
        return most;
    }

    /// <summary>
    /// A puppet's engines lit or dark as its owner has them, so another player's craft is seen to
    /// burn. What it does to this world is nothing that lasts: the craft is pulled back to where its
    /// owner says it is every frame regardless.
    /// </summary>
    public static void SetEngine(Vehicle v, bool on, float throttle)
    {
        try
        {
            if (EngineInputs is not { } inputs) return;
            ref ManualControlInputs now = ref inputs(v);
            if (now.EngineOn != on) now.EngineOn = on;
            if (Math.Abs(now.EngineThrottle - throttle) > 1e-4f) now.EngineThrottle = throttle;
        }
        catch (Exception)
        {
            // An engine that will not light is a plume nobody sees, and nothing worse.
        }
    }

    // Craft whose engines were last drawn from here rather than out of KSA's own step. What is
    // written below is gone again by the next step, so a craft that has stopped burning needs
    // writing dark once; and a craft KSA is burning for real is left to its own numbers.
    private static readonly HashSet<Vehicle> _shown = [];
    private static bool _saidNoPlume;

    /// <summary>Forgets which craft were drawn burning, when this game is its own again.</summary>
    public static void ForgetEngines() => _shown.Clear();

    /// <summary>Whether this craft's engines were last drawn from the mod rather than out of KSA's own step.</summary>
    public static bool IsShown(Vehicle v) => _shown.Contains(v);

    /// <summary>The duty cycle this craft's engines would be drawn from now: what every engine visual reads.</summary>
    public static double DutyOf(Vehicle v)
    {
        try
        {
            if (v.Parts?.RocketNozzles is not { } nozzles) return 0.0;
            ReadOnlySpan<RocketNozzleState> states = nozzles.States;
            double most = 0.0;
            for (int i = 0; i < states.Length; i++) most = Math.Max(most, states[i].DutyCycle);
            return most;
        }
        catch (Exception)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// Another player's craft drawn as burning, for the frame about to be drawn.
    ///
    /// <para>Setting the engine on is only an input, and out there nothing reads it. What the plume,
    /// the volumetric exhaust and the exhaust light are every one of them drawn from is
    /// <c>RocketNozzleState.DutyCycle</c>, and that is accumulated only inside a full physics step:
    /// a craft of this game's own in freefall or at rest goes down a cheaper path that clears the
    /// duty cycle every frame and never adds to it, so another player's craft out there is dark
    /// however hard its owner is burning. This writes what the step it never ran would have left
    /// behind -- the chamber conditions that engine's own design gives at that throttle, the exhaust
    /// its nozzles make of them, and a duty cycle -- in a prefix on the calls that read it.</para>
    ///
    /// <para>It changes nothing the craft does. KSA works a core's conditions out from its throttle
    /// and a nozzle's thrust fraction out from those conditions on every step it runs; neither the
    /// throttle nor the thrust fraction is written here, and the active nozzles that thrust and draw
    /// propellant are counted from the thrust fraction. What is written is read by this frame's
    /// renderers and overwritten by the next step from numbers this never touched.</para>
    /// </summary>
    public static void ShowEngine(Vehicle v, bool on, float throttle)
    {
        if (!on && !_shown.Contains(v)) return;
        try
        {
            if (v.Parts is not { RocketNozzles: { } nozzles, RocketCores: { } cores }) return;
            if (on && !_shown.Contains(v) && Drawn(nozzles)) return;
            float ambient = v.PhysicsEnvironment.AtmosphericPressure;
            // Enough of a throttle to be something: a core at nothing has no conditions and makes no
            // plume, and a craft said to be burning at nothing is a craft whose throttle was not read.
            float open = float.IsFinite(throttle) ? Math.Clamp(throttle, 0.05f, 1f) : 1f;
            Span<RocketNozzle> modules = nozzles.Modules;
            for (int i = 0; i < modules.Length; i++)
            {
                RocketNozzle nozzle = modules[i];
                if (nozzle.Rocket?.Core is not { } core) continue;
                ModuleStateful<RocketNozzle, RocketNozzleState, EmptyStruct, RocketNozzleFxState>.StateList.ModuleAndAllMutableStatesRef noz =
                    nozzles.GetModuleAndAllMutableStatesForInitialization(nozzle);
                if (!on)
                {
                    (noz.State.DutyCycle, noz.State.AverageThrustFraction) = (0f, 0f);
                    continue;
                }
                RocketCoreConditions conditions = core.ComputeConditions(open);
                ModuleStateful<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>.StateList.ModuleAndAllMutableStatesRef hot =
                    cores.GetModuleAndAllMutableStatesForInitialization(core);
                hot.State.Conditions = conditions;
                NozzlePerformance made = nozzle.ComputePerformance(in conditions, ambient);
                noz.State.Exhaust = made.Exhaust;
                noz.State.ExhaustVelocity = made.ActualExhaustVelocity;
                // The whole of the step, and how hard: the other statistic the step would have left,
                // which is what the engine's own sound is pitched by within earshot.
                (noz.State.DutyCycle, noz.State.AverageThrustFraction) = (1f, open);
            }
            if (on) _shown.Add(v);
            else _shown.Remove(v);
        }
        catch (Exception e)
        {
            if (_saidNoPlume) return;
            _saidNoPlume = true;
            Log.Warn($"other players' engines cannot be shown firing: {e.GetBaseException().Message}");
        }
    }

    // Whether KSA is already drawing this craft's engines, which it does for one it is stepping in
    // physics: close to this player, or anywhere in an atmosphere. Its own numbers are the better ones.
    private static bool Drawn(ModuleStateful<RocketNozzle, RocketNozzleState, EmptyStruct, RocketNozzleFxState>.StateList nozzles)
    {
        ReadOnlySpan<RocketNozzleState> states = nozzles.States;
        for (int i = 0; i < states.Length; i++) if (states[i].DutyCycle > 0f) return true;
        return false;
    }

    /// <summary>
    /// A craft's next sequence fired, which is what the space bar does, and the new active sequence
    /// or null if there was none left. For a test that has to stage a rocket with no hand on the
    /// keyboard; nothing in the mod itself stages anything.
    /// </summary>
    public static int? Stage(Vehicle v)
    {
        try
        {
            SequenceList sequences = v.Parts.SequenceList;
            int was = sequences.ActiveSequence;
            sequences.ActivateNextSequence(v);
            return sequences.ActiveSequence == was ? null : sequences.ActiveSequence;
        }
        catch (Exception e)
        {
            Log.Warn($"could not stage {v.Id}: {e.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Where a craft is on the screen, or null when it is behind the camera or off to the side.</summary>
    /// <summary>How big the window is, for telling a tag drawn off the edge from one never drawn.</summary>
    public static float2? ScreenSize
    {
        get
        {
            try
            {
                return ImGui.GetMainViewport().WorkSize;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Where to write a craft's name on the screen: a point above it rather than through it, in
    /// absolute screen coordinates. <c>EclToScreen</c> answers in the viewport's own, which are the
    /// window's only when the viewport starts at the corner of it, and a draw list wants the screen's.
    /// Null when the point is behind the camera or off the edge.
    /// </summary>
    /// <summary>
    /// Where to write a craft's name on the screen: a point above the hull rather than through it.
    /// Null when there is no camera or the point will not project.
    /// </summary>
    public static float2? ScreenOf(Vehicle v)
    {
        try
        {
            // The camera the rest of this file uses: the viewport's own has come back null here, and a
            // tag that is never placed is worse than one placed a few pixels out.
            if (Program.GetMainCamera() is not { } camera) return null;

            double3 at = v.GetPositionEcl();
            float2 local = camera.EclToScreen(at);
            if (!float.IsFinite(local.X) || !float.IsFinite(local.Y)) return null;

            // Over the hull rather than through it, by however big the craft looks from here: a
            // metre is nothing across a sky and everything from ten metres off, so the lift is
            // measured on the screen and capped so a near hull keeps its name on the screen.
            float lift = 14f;
            if (v.Parent is IPosition parent)
            {
                double3 up = at - parent.GetPositionEcl();
                double len = Vec(up);
                if (len > 0.0)
                {
                    float2 edge = camera.EclToScreen(at + (up * (v.MeanRadius / len)));
                    if (float.IsFinite(edge.X) && float.IsFinite(edge.Y))
                    {
                        float dx = edge.X - local.X, dy = edge.Y - local.Y;
                        lift = Math.Clamp(MathF.Sqrt((dx * dx) + (dy * dy)) + 10f, 14f, 140f);
                    }
                }
            }
            local = new float2(local.X, local.Y - lift);

            // Off the edge of the view, or behind the camera, which projects to somewhere finite and
            // wrong: nothing is drawn rather than drawn outside the window it belongs to. Measured
            // against the view's own size, and offset by where the view sits, which is the space a
            // draw list counts from -- both only when the game will say, and left alone when not.
            try
            {
                if (Program.MainViewport is { Width: > 0, Height: > 0 } viewport)
                {
                    if (local.X < 0f || local.Y < 0f || local.X > viewport.Width || local.Y > viewport.Height) return null;
                    return new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
                }
            }
            catch (Exception)
            {
                // Taken as the window's own, which is what it was before anybody asked.
            }
            return local;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>How far a craft is from the camera, m, or infinity when there is no camera.</summary>
    public static double RangeFromCamera(Vehicle v)
    {
        try
        {
            return Program.GetMainCamera() is { } camera ? Vec(v.GetPositionEcl() - camera.PositionEcl) : double.PositiveInfinity;
        }
        catch (Exception)
        {
            return double.PositiveInfinity;
        }
    }

    /// <summary>The distance between two craft, m, or infinity if they are not near the same body.</summary>
    public static double Between(Vehicle a, Vehicle b)
    {
        try { return Vec(a.GetPositionEcl() - b.GetPositionEcl()); }
        catch (Exception) { return double.PositiveInfinity; }
    }

    /// <summary>
    /// The craft the camera is on, which is not always the one being flown: another player's craft
    /// can be watched and never taken.
    /// </summary>
    public static Vehicle? Watched => Program.GetMainCamera()?.Following as Vehicle;

    /// <summary>
    /// The camera put on a craft without taking it over. KSA's camera takes control of whatever it
    /// follows unless told not to, and control of another player's craft is refused, so following
    /// and flying have to be asked for separately.
    /// </summary>
    public static bool Watch(Vehicle v)
    {
        try
        {
            if (Program.GetMainCamera() is not { } camera) return false;
            camera.SetFollow(v, tidalLocking: true, changeControl: false);
            return ReferenceEquals(camera.Following, v);
        }
        catch (Exception e)
        {
            Log.Warn($"could not watch {v.Id}: {e.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// Control of a craft given up while the camera stays on it, which is what being an observer is.
    /// Without this a game goes on steering a craft the server has given to somebody else: the pull
    /// puts the hull back where its owner says, but a kitten still jumps where its owner never did.
    /// </summary>
    public static bool LetGo(Vehicle v)
    {
        try
        {
            if (!ReferenceEquals(Program.ControlledVehicle, v)) return false;
            Program.ControlledVehicle = null;
            Watch(v);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not let go of {v.Id}: {e.GetBaseException().Message}");
            return false;
        }
    }

    public static bool GoTo(Vehicle v)
    {
        try
        {
            if (Program.GetMainCamera() is not { } camera) return false;
            camera.SetFollow(v, tidalLocking: true);
            Program.ControlledVehicle = v;
            // The hook on the setter refuses another player's craft without saying so, so whether it
            // took is read back rather than assumed.
            return ReferenceEquals(Program.ControlledVehicle, v);
        }
        catch (Exception e)
        {
            Log.Warn($"could not go to {v.Id}: {e.GetBaseException().Message}");
            return false;
        }
    }

    private static Vec3 V(double3 v) => new(v.X, v.Y, v.Z);
    private static double3 D(Vec3 v) => new(v.X, v.Y, v.Z);
    private static double Vec(double3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
}
