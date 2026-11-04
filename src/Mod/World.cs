using System.Xml.Serialization;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ImGuiApi;
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
    public static int Shape(Vehicle v) => (v.Parts.Count * 397) ^ (v.Parts.Root?.Template?.Id?.GetHashCode() ?? 0);

    // ------------------------------------------------------------------ saves

    /// <summary>This world saved as the handover save and zipped, with the universe time it was saved at.</summary>
    public static (byte[] Zip, double Universe) SaveWorld()
    {
        double universe = Universe;
        GameSaves.MakeUncompressedSave(HandoverSave);
        string folder = Path.Combine(GameSaves.SaveFolderPath, HandoverSave);
        return (Blob.ZipFolder(folder), universe);
    }

    /// <summary>A world from the server written as the handover save and loaded in place of this one.</summary>
    public static void LoadWorld(byte[] zip)
    {
        Blob.UnzipInto(zip, Path.Combine(GameSaves.SaveFolderPath, HandoverSave));
        GameSaves.Refresh();
        GameSaves.LoadSaveGame(HandoverSave);
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

    /// <summary>A name no craft in this world has, like the one asked for.</summary>
    public static string FreeLocalId(string root)
    {
        List<Vehicle> all = Vehicles();
        for (int n = 2; n < 1000; n++)
        {
            string tried = $"{root} ({n})";
            if (!all.Any(v => v.Id == tried)) return tried;
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
            Vehicle built = string.IsNullOrEmpty(data.Character)
                ? Vehicle.CreateVehicleFromSaveGameData(system, data)
                : KittenEva.CreateKittenFromSaveData(system, data);
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

    /// <summary>Where a craft is now, in its body's fixed frame off the physics state and in its inertial frame off the ecliptic.</summary>
    /// <param name="key">What the world knows this craft by, which is not this game's own name for it when the server has renamed it.</param>
    public static Wire.State? StateOf(Vehicle v, string key, bool controlled)
    {
        if (v.Parent is not Astronomical parent || v.Parent is not IPosition parentPos || v.Parent is not IVelocity parentVel) return null;
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat cce2Cci = v.Parent.GetCce2Cci();
        double3 positionCci = cce2Cci * (v.GetPositionEcl() - parentPos.GetPositionEcl());
        double3 velocityCci = cce2Cci * (v.GetVelocityEcl() - parentVel.GetVelocityEcl());
        double3 spin = states.Kinematic.AngularVelocityPhys.Transform(states.Kinematic.Body2Phys.Inverse());

        Wire.StateFlags flags = Wire.StateFlags.None;
        if (v.Situation.IsOnRails()) flags |= Wire.StateFlags.Rails;
        if (v.Situation.HasAnyContact()) flags |= Wire.StateFlags.Landed;
        if (v.LightsOn) flags |= Wire.StateFlags.Lights;
        if (controlled) flags |= Wire.StateFlags.Controlled;

        Wire.State s = new(0, key, 0, Universe, parent.Id, V(positionCcf), V(velocityCcf), V(positionCci), V(velocityCci),
                           new Quat(body2Ccf.X, body2Ccf.Y, body2Ccf.Z, body2Ccf.W), V(spin), 0f, flags);
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
        Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(s.Universe), D(s.PositionCci), D(s.VelocityCci), v.OrbitColor);
        v.SetFlightPlan(new FlightPlan(orbit, KeyHash.Make(v.Id.AsSpan())));
        return true;
    }

    // Further out than this a craft is put where it is wanted outright; nearer, it is drawn there over a few steps, so a late datagram is not a jolt.
    private const double SnapM = 5.0, PullSeconds = 0.15;

    /// <summary>
    /// Another player's craft written where its game says it is, from the physics window: turned as
    /// told, moving as told, and drawn onto its place. Nothing in it may throw.
    /// </summary>
    public static void Pull(Vehicle v, Wire.State s, double dt)
    {
        if (!v.HasPhysicsBubble || dt <= 0.0 || dt > 0.25) return;
        // Across a change of body the two frames differ; the analytic placing takes it over.
        if ((v.Parent as Astronomical)?.Id is { } body && body != s.Body) return;
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out _, out doubleQuat body2Ccf);
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        doubleQuat ccf2Body = body2Ccf.Inverse();
        double3 off = D(s.PositionCcf) - positionCcf;

        // Standing where it is told to stand, it is left on KSA's rails: woken every step, its physics is built again every step.
        if (v.Situation.IsOnRails() && Vec(D(s.VelocityCcf)) < 0.05 && Vec(off) < 0.05) return;
        bool snap = Vec(off) > SnapM;
        if (snap) states.Kinematic.PositionPhys += off.Transform(ccf2Body).Transform(body2Phys);
        doubleQuat wantedBody2Ccf = new(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W);
        states.Kinematic.Body2Phys = body2Phys * (ccf2Body * wantedBody2Ccf);

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out double3 nowVelocity, out doubleQuat nowBody2Ccf);
        double3 wanted = D(s.VelocityCcf) + (snap ? double3.Zero : off / Math.Max(PullSeconds, dt));
        states.Kinematic.VelocityPhys += (wanted - nowVelocity).Transform(nowBody2Ccf.Inverse()).Transform(states.Kinematic.Body2Phys);
        states.Kinematic.AngularVelocityPhys = D(s.Spin).Transform(states.Kinematic.Body2Phys);
        if (v.Situation.IsOnRails()) v.TakeOffRails();
    }

    public static void SetLights(Vehicle v, bool on)
    {
        if (v.LightsOn != on) v.LightsOn = on;
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
            if (Program.GetMainCamera() is not { } camera) return null;

            double3 at = v.GetPositionEcl();
            float2 centre = camera.EclToScreen(at);
            if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y)) return null;

            // Over the hull rather than through it, by however big the craft looks from here: a
            // metre is nothing across a sky and everything from ten metres off, so the lift is
            // measured on the screen and not in the world, and capped so a near hull keeps its name.
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
                        float dx = edge.X - centre.X, dy = edge.Y - centre.Y;
                        lift = Math.Clamp(MathF.Sqrt((dx * dx) + (dy * dy)) + 10f, 14f, 140f);
                    }
                }
            }
            return new float2(centre.X, centre.Y - lift);
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
