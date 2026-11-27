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

    /// <summary>The map opened or shut, as the Map Camera and Orbit Camera items of KSA's menu do. Null if it was done.</summary>
    public static string? ShowMap(bool open)
    {
        try
        {
            if (Program.MainViewport is not { } viewport) return "there is no view to turn to the map";
            viewport.SetCameraMode(open ? CameraMode.Map : CameraMode.Orbit);
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    public static string KsaBuild => typeof(Vehicle).Assembly.GetName().Version?.ToString() ?? "";

    /// <summary>The celestial system being played, such as Sol entire or Earth and its Moon alone.</summary>
    public static string System => KSA.Universe.CurrentSystem?.Id ?? "";

    /// <summary>
    /// Every mod this game has enabled, each with the version its own assembly carries where it
    /// has one: <c>Core,KSAMultiplayer@0.18.0+f0fd6cb...</c>. See <see cref="ModList"/>.
    /// </summary>
    public static string Mods =>
        ModLibrary.Manifest is { } manifest
            ? string.Join(',', manifest.Mods.Where(m => m.Enabled).Select(m => VersionOf(m.Id) is { Length: > 0 } at ? $"{m.Id}@{at}" : m.Id))
            : "";

    // Empty for a mod that brought no code, which is an answer too and is not looked for again.
    private static readonly Dictionary<string, string> ModVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The version of a mod that brought code, or empty: KSA knows a mod's name and where it is
    /// and nothing of what version it is at, so it is read off the assembly the mod's own
    /// <c>mod.toml</c> names.
    ///
    /// <para>Found from the mod's name, where StarMap finds it, and read off the file. Not found
    /// among the assemblies already loaded by the folder each says it came from: a mod manager
    /// puts a mod in as a link to a folder it names after the version, the runtime may say the
    /// folder the link leads to, and a mod asked about before StarMap has reached it is not
    /// loaded at all. Either way the mod was there with no version, and a server that wants one
    /// refused a player who had exactly the mod it asked for.</para>
    /// </summary>
    private static string VersionOf(string id)
    {
        if (ModVersions.TryGetValue(id, out string? known)) return known;
        string version = "";
        try
        {
            // Where StarMap looks, in its order: beside the game, then the player's own mods.
            string? folder = new[] { Path.Combine("Content", id), Path.Combine(ModLibrary.LocalModsFolderPath, id) }
                .FirstOrDefault(f => File.Exists(Path.Combine(f, ModLibrary.MOD_TOML)));
            if (folder is not null)
            {
                global::System.Text.RegularExpressions.Match entry = global::System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(Path.Combine(folder, ModLibrary.MOD_TOML)), "EntryAssembly\\s*=\\s*\"([^\"]+)\"");
                // A mod that names none is looked for under its own name, as StarMap does it.
                string named = entry.Success ? entry.Groups[1].Value.Trim() : id;
                if (named.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) named = named[..^4];
                string dll = Path.Combine(folder, named + ".dll");
                if (File.Exists(dll)) version = Carried(dll);
            }
        }
        catch (Exception e)
        {
            // Said once, since it is kept: a mod whose version cannot be read is one a server
            // that wants a version turns away, and the player is owed the reason.
            Log.Warn($"could not read what version {id} is at: {e.GetBaseException().Message}");
        }
        return ModVersions[id] = version;
    }

    /// <summary>The version an assembly's file says it is, with the commit it was built from where it carries one.</summary>
    private static string Carried(string dll)
    {
        string? version = global::System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Trim();
        if (version is not { Length: > 0 }) version = global::System.Reflection.AssemblyName.GetAssemblyName(dll).Version?.ToString(3);
        // Nothing in it that would be read as the end of an entry or the start of a version.
        return version is { Length: > 0 } && !version.Contains(',') && !version.Contains('@') ? version : "";
    }

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

    /// <summary>
    /// What a debris sweep takes: what KSA marks as debris, broken off a craft, and any craft with
    /// no control part on it, which is what a spent stage is. Not a kitten, which has none. Read
    /// from the parts and not from <see cref="Vehicle.IsControllable"/>, which a probe out of power
    /// is not, though it is somebody's probe.
    /// </summary>
    private static readonly global::System.Reflection.FieldInfo? ControlOverrideField =
        typeof(Vehicle).GetField("_overrideIsControllable", global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Static);

    /// <summary>Whether KSA's debug "Override Is Controllable" is on, which makes every craft count as having a control module.</summary>
    public static bool ControlOverride => ControlOverrideField?.GetValue(null) is true;

    /// <summary>KSA's debug "Override Is Controllable" ticked or not, for a run to tick it as a player does. False where it cannot be reached.</summary>
    public static bool SetControlOverride(bool on)
    {
        if (ControlOverrideField is null) return false;
        ControlOverrideField.SetValue(null, on);
        return true;
    }

    /// <summary>
    /// The debris KSA's Clear Debris takes, into a list: KSA's own count, or while its debug
    /// "Override Is Controllable" is on, the same rule read from the craft's real control modules
    /// (<c>PartFailure.IsExpendable</c>), which the override hides.
    /// </summary>
    public static void CollectDebris(List<Vehicle> into)
    {
        if (KSA.Universe.CurrentSystem is not { } system) return;
        if (!ControlOverride)
        {
            PartFailure.CollectSystemDebris(system, into);
            return;
        }
        Vehicle? flown = Controlled;
        ReadOnlySpan<Astronomical> all = system.All.AsSpan();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] is not Vehicle { IsDisposed: false, IsDebris: true } v || v is KittenEva || ReferenceEquals(v, flown)) continue;
            if (v.Parts.Controls.NumModules > 0 || Crewed(v)) continue;
            into.Add(v);
        }
    }

    private static bool Crewed(Vehicle v)
    {
        ReadOnlySpan<IVASeat> crew = v.Crew;
        for (int i = 0; i < crew.Length; i++)
        {
            if (crew[i].AssignedKittenHash != KeyHash.Zero) return true;
        }
        return false;
    }

    public static bool IsDebris(Vehicle v) => v is not KittenEva && (v.IsDebris || v.Parts.Modules.Get<Control>().Length == 0);

    /// <summary>Every kitten aboard back to the roster, as <see cref="Vehicle.Recover"/> sends them, and nothing else that it does.</summary>
    public static void SendCrewHome(Vehicle v)
    {
        foreach (IVASeat seat in v.Crew)
        {
            if (seat.AssignedKittenHash != KeyHash.Zero) KSA.Universe.KittenRoster.Find(seat.AssignedKittenHash)?.LeaveVehicle(v.HasLaunched);
        }
    }

    /// <summary>
    /// A craft given another name in this world, as KSA would give it: false where the name is
    /// something else's here, a body's or another craft's.
    ///
    /// <para>KSA's own <c>CelestialSystem.Rename</c>, which nothing in the game calls, and the crew
    /// with it as the launch menu moves them when its name field changes: a kitten is assigned to a
    /// craft by name, and left on the old one is aboard nothing the roster can find.</para>
    /// </summary>
    public static bool Rename(Vehicle v, string name)
    {
        try
        {
            if (KSA.Universe.CurrentSystem is not { } system || system.All.TryGet(name, out Astronomical? _)) return false;
            string was = v.Id;
            if (!system.Rename(v, name)) return false;
            foreach (IVASeat seat in v.Crew)
            {
                if (seat.AssignedKittenHash == KeyHash.Zero) continue;
                if (KSA.Universe.KittenRoster.Find(seat.AssignedKittenHash) is { } kitten && kitten.AssignedVehicleId == was) kitten.AssignedVehicleId = name;
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not rename {v.Id} to {name}: {e.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>Whether a name is a planet's, a moon's or a star's in this world, which a craft can never take.</summary>
    public static bool IsBody(string name) =>
        KSA.Universe.CurrentSystem is { } system && system.All.TryGet(name, out Astronomical? found) && found is not Vehicle;

    /// <summary>
    /// What a craft is, for deciding whether the others have to be told how it is built again: its
    /// parts. Not the body it goes round, though a definition carries one: every copy is put round
    /// its owner's body from their newest state as it is built, and moved if found round another,
    /// so a change of body re-sent the whole craft to every game for nothing. A late joiner is sent
    /// definitions only of craft somebody owns, who is saying where it is.
    /// Only a hash within this process, so what it is made of does not matter.
    /// </summary>
    public static int Shape(Vehicle v) =>
        (v.Parts.Count * 397) ^ (v.Parts.Root?.Template?.Id?.GetHashCode() ?? 0);

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

    /// <summary>Whether a craft is within this game's physics: near enough to what is flown or watched to be stepped and struck.</summary>
    public static bool InBubble(Vehicle v)
    {
        try { return v.HasPhysicsBubble; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether a craft is being stepped through physics and is not one KSA has put on rails: a
    /// craft something is pushing, or that is still moving from having been.
    /// </summary>
    public static bool UnderWay(Vehicle v)
    {
        try { return v.HasPhysicsBubble && !v.Situation.IsOnRails(); }
        catch (Exception) { return false; }
    }

    /// <summary>Whether the vehicle editor is open, which is where KSA stops the world.</summary>
    public static bool InEditor => Program.IsEditorOpen;

    /// <summary>Whether the vehicle editor has been built, which is a frame after it is said to be open.</summary>
    public static bool EditorBuilt => Program.Editor is not null;

    /// <summary>Whether the editor has been asked for on the craft being controlled and not yet built, which KSA does at the start of the next frame (Program.OnFrameEditor).</summary>
    public static bool EditorAsked => Program.EditorFlag && Program.Editor is null;

    /// <summary>Takes back an editor asked for and not yet built.</summary>
    public static void CancelEditor()
    {
        if (Program.Editor is null) Program.EditorFlag = false;
    }

    /// <summary>Whether the vehicle editor is open on a new vehicle rather than on the craft that was being flown.</summary>
    public static bool EditingNew => Program.Editor is { ExistingVehicle: null };

    /// <summary>
    /// Why this world can be neither saved nor loaded at the moment, or empty when it can be.
    ///
    /// <para>KSA refuses both while the vehicle editor is open: every save and load it has begins by
    /// asking, and the answer is a line in its own log and a return. Both calls are void, so a mod
    /// that did not ask the same question hands on whatever save was last written as the world now,
    /// or counts its frames over a world it never loaded and says it is in the shared one.</para>
    /// </summary>
    public static string Refuses() => Program.IsEditorOpen ? "the vehicle editor is open, and KSA can't save or load while it is" : "";

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
                why = "KSA didn't write a save of this world";
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

    /// <summary>
    /// Parts failed on a craft the way a crash fails them, or why not. Empty on success.
    ///
    /// <para>The one thing a test cannot arrange by flying. <c>PhysicsBubble</c> stands KSA's own
    /// G-load path down wherever a craft touches terrain, ocean or another craft, so no amount of
    /// driving reaches a part failure on the ground -- which is exactly where the failure this mod
    /// guards against was reported. This is the same <see cref="PartFailureEvent.Apply"/> that path
    /// calls, so the guard is reached as it would be and nothing is simulated around it.</para>
    ///
    /// <para><paramref name="whole"/> is the crash rather than the stripping: it sheds twelve pieces
    /// of debris and destroys the craft in one call, which is the frame a piece's ancestry has to be
    /// read in.</para>
    /// </summary>
    public static string Fail(Vehicle v, int parts, bool whole)
    {
        try
        {
            List<Part> failed = [];
            ReadOnlySpan<Part> all = v.Parts.Parts;
            Part? root = v.Parts.Root;
            // Never the root: isolating that is destroying the craft, which `whole` is for.
            for (int i = all.Length - 1; i >= 0 && failed.Count < Math.Max(parts, 1); i--)
            {
                if (!ReferenceEquals(all[i], root)) failed.Add(all[i]);
            }
            if (failed.Count == 0) return $"{v.Id} has no part to fail but its root";
            new PartFailureEvent { FailedParts = failed, DestroyWholeVehicle = whole }.Apply(v);
            return "";
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    // ------------------------------------------------------------------------- the vehicle editor

    /// <summary>
    /// The vehicle saves on this machine, by name.
    ///
    /// <para>Read off disk rather than out of the list KSA built at start, so a design written
    /// since -- by a test, a moment ago -- is in it.</para>
    /// </summary>
    public static List<string> Designs()
    {
        List<string> names = [];
        try
        {
            VehicleSaves.Refresh();
            foreach (VehicleSave save in VehicleSaves.AsSpan()) names.Add(save.Id);
        }
        catch (Exception e)
        {
            Log.Warn($"the vehicle saves could not be listed: {e.GetBaseException().Message}");
        }
        return names;
    }

    /// <summary>
    /// A craft standing in this world written out as a vehicle save, or why not.
    ///
    /// <para>KSA names the design and looks the craft up by the one string, so a design can only be
    /// called what its craft is called. The craft is found by its local <see cref="Vehicle.Id"/>,
    /// not by the name the room shares, which KSA has never heard of.</para>
    /// </summary>
    public static string SaveDesign(string craft)
    {
        if (!Vehicles().Any(v => v.Id == craft)) return $"no craft called '{craft}' in this world";
        // A design is a folder named after it, so a name KSA would not accept as a file name is
        // refused here rather than half-written.
        if (!SaveName.IsSanitized(craft)) return $"'{craft}' cannot be a design's name";
        try
        {
            return UncompressedVehicleSave.Make(craft) is null ? $"KSA wrote no design for '{craft}'" : "";
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// A blank editor whatever is being flown, which is what KSA's own "Build New Vehicle" menu item
    /// does. False when one is already open.
    ///
    /// <para><c>EditorFlag</c> on its own opens the editor on the craft being controlled, and an
    /// editor opened that way builds nothing: its exit makes a craft only where
    /// <c>ExistingVehicle</c> is null. The menu item constructs the editor with <c>newVehicle</c>
    /// set and lets go of the craft, which is both halves of what a game already flying something
    /// needs to be driven into a launch -- and two games doing it at once is the only way to put two
    /// launches in one window on purpose, which is where two craft want one name and one pad.</para>
    /// </summary>
    public static bool EditBlank()
    {
        if (Program.Editor is not null) return false;
        Program.Editor = new VehicleEditor(Program.ControlledVehicle, newVehicle: true);
        Program.ControlledVehicle = null;
        return true;
    }

    /// <summary>
    /// A saved design put into the open editor, or why not.
    ///
    /// <para>This is the whole of what a test needs the editor for. A part tree arrives, and the
    /// editor's own Launch -- which the bridge can already press -- turns it into a craft on a pad.
    /// No part can be <em>placed</em> from here, so part placement, symmetry and staging edits stay
    /// out of reach; the launch path that shipped unwatched three times does not.</para>
    ///
    /// <para>KSA's own <c>loadvehicle</c> command cannot be used for this: it ends in the
    /// parameterless <c>VehicleSave.Load()</c>, which every vehicle save throws
    /// <see cref="NotImplementedException"/> from. The overload that works takes a viewport and
    /// never reads it.</para>
    /// </summary>
    public static string LoadDesign(string name)
    {
        if (Program.Editor is not { } editor) return "the vehicle editor is not open";
        try
        {
            VehicleSave? found = null;
            foreach (VehicleSave save in VehicleSaves.AsSpan())
            {
                if (save.Id != name) continue;
                found = save;
                break;
            }
            found ??= DefaultVehicleSaves.FindSave(name);
            if (found is null) return $"no design called '{name}'";
            if (found.Load(Program.MainViewport) is not { } parts) return $"'{name}' could not be read, and KSA's own log says why";
            editor.LoadVehicle(parts);
            return "";
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// What the editor would launch: the name the craft will have in this world and how many parts
    /// are in it, or null when the editor is not open.
    ///
    /// <para>The name is the editor's own, picked by <c>GenerateNewVehicleId</c> as the first one
    /// free in this system. Two games holding the same world therefore pick the same one, which is
    /// how two launches can be made to collide on purpose rather than waited for.</para>
    /// </summary>
    public static (string Id, int Parts)? Editing =>
        Program.Editor is { } editor ? (editor.EditingSpace.Id, editor.EditingSpace.Parts?.Count ?? 0) : null;

    // ------------------------------------------------------------------ before there is a universe

    private static readonly System.Reflection.FieldInfo? PopupList = AccessTools.Field(typeof(Popup), "Popups");

    /// <summary>
    /// Whether KSA has a world up. False at the configuration popup at start, where there is no
    /// viewport to measure, no camera to ask and no universe to time -- so anything that wants one
    /// of those has to ask this first.
    /// </summary>
    public static bool Loaded => Program.IsLoaded;

    /// <summary>How many frames KSA has drawn of a world. None at the configuration popup at start, where only the interface is drawn.</summary>
    public static ulong Frames => Program.FrameNumber;

    /// <summary>
    /// What KSA has standing open, by the name of each popup's own type. A tool driving the game is
    /// otherwise blind to them: a picture taken through <c>capture</c> is taken with the interface
    /// hidden, so a popup sitting over everything does not appear in it, and a run can look clean in
    /// a screenshot while nobody can see past the box on the screen.
    /// </summary>
    public static List<string> Popups =>
        PopupList?.GetValue(null) is List<Popup> popups
            ? [.. popups.Where(p => p.Active).Select(p => p.GetType().Name)]
            : [];

    /// <summary>
    /// A system to start the configuration at start in: the one asked for, else the one this game
    /// would have offered -- what it last played, or the first it has. Null where it has none at
    /// all. <see cref="StartIn"/> wants a name and does nothing with an empty one, and a game
    /// started with no system chosen is a game with no world to read a save into.
    /// </summary>
    public static string? ASystemToStartIn(string wanted)
    {
        if (wanted.Length > 0) return wanted;
        if (SystemLibrary.Default?.Id is { Length: > 0 } standing) return standing;
        if (GameSettings.Current.System.LastSystemId is { Length: > 0 } last) return last;
        return SystemLibrary.PickerSystems().FirstOrDefault()?.Id;
    }

    /// <summary>
    /// KSA's configuration at start, while it is up: the one popup <c>SelectSystem</c> loops on before
    /// the universe is made. Found in KSA's list of popups rather than through
    /// <c>SelectSystem.Current</c>, which is set only after its first frame has been drawn.
    /// </summary>
    public static ConfigOnStartPopup? Configuration =>
        !Program.IsLoaded && PopupList?.GetValue(null) is List<Popup> popups
            ? popups.OfType<ConfigOnStartPopup>().FirstOrDefault(p => p.Active)
            : null;

    /// <summary>
    /// KSA started in a system, as its configuration's Start button would, with the rest of that
    /// configuration left as the player last set it. Empty to keep the system already chosen. False
    /// and why for a system this game does not have.
    /// </summary>
    /// <param name="start">False to choose the system and leave the configuration up, for its own Start.</param>
    public static bool StartIn(ConfigOnStartPopup configuration, string system, out string why, bool start = true)
    {
        why = "";
        if (system.Length > 0 && system != GameSettings.Current.System.LastSystemId)
        {
            if (SystemLibrary.PickerSystems().FirstOrDefault(s => s.Id == system) is not { } chosen)
            {
                why = $"the server's world is in {system}, which your game doesn't have";
                return false;
            }
            // What choosing it from the configuration's own list does: the craft and the place a
            // game starts at belong to a system, and the ones chosen for another are no good here.
            Traverse popup = Traverse.Create(configuration);
            popup.Field("_systemTemplate").SetValue(chosen);
            SystemLibrary.Default = chosen;
            GameSettings.Current.System.LastSystemId = chosen.Id;
            popup.Method("SetVehicles").GetValue();
            popup.Method("SetStartingVehicleLocation").GetValue();
            GameSettings.Current.Save();
        }
        if (start) configuration.Active = false;
        return true;
    }

    /// <summary>Whether a system is one this game can be started in.</summary>
    public static bool Has(string system) => SystemLibrary.PickerSystems().Any(s => s.Id == system);

    /// <summary>
    /// The system KSA starts in next time, chosen from a game that is already in another: for a
    /// world whose system the graphics card cannot hold beside this one, which it can on its own.
    /// The one setting KSA reads at start, with or without its configuration, is the one written here.
    /// </summary>
    public static bool StartNextIn(string system, out string why)
    {
        why = "";
        try
        {
            if (!Has(system))
            {
                why = $"your game doesn't have {system}";
                return false;
            }
            GameSettings.Current.System.LastSystemId = system;
            GameSettings.Current.Save();
            return true;
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// KSA's two rules a server decides for its world: whether kittens need food, water and air,
    /// and whether they take a dose from radiation.
    /// </summary>
    public static (bool LifeSupport, bool Radiation) Rules
    {
        get => (KSA.Universe.Rules.LifeSupport, KSA.Universe.Rules.Radiation);
        set => (KSA.Universe.Rules.LifeSupport, KSA.Universe.Rules.Radiation) = value;
    }

    /// <summary>
    /// Whether the save KSA is loading is one this mod asked it for -- the server's world, or a
    /// tool's -- rather than one the player picked: the first is how a game gets into the shared
    /// world, and the second is refused while it is in one.
    /// </summary>
    public static bool LoadAsked { get; private set; }

    /// <summary>Loads a save of this machine's by name, as this mod's own doing.</summary>
    public static void LoadNamed(string save)
    {
        LoadAsked = true;
        try
        {
            GameSaves.LoadSaveGame(save);
        }
        finally
        {
            LoadAsked = false;
        }
    }

    /// <summary>
    /// A world from the server written as the handover save and loaded in place of this one, or
    /// false and why not. In another system than this game's, the system is swapped first, which
    /// KSA does on a later frame: <see cref="TakeLoadTrouble"/> says if that went wrong.
    ///
    /// <para>Swapped by <c>SaveSystemSwap.RequestJoin</c> rather than left to the load, because the
    /// load alone reads a save into the running system wherever that system has all its bodies: a
    /// world of Earth and its Moon opened in a game of Sol entire stays in Sol, and the next save
    /// this game hands the server is a world of Sol for everybody. The join asks for the system
    /// by name, and checks the graphics card has room for it before tearing this one down.</para>
    /// </summary>
    public static bool LoadWorld(byte[] zip, out string why)
    {
        why = Refuses();
        if (why.Length > 0) return false;
        try
        {
            string folder = Path.Combine(GameSaves.SaveFolderPath, HandoverSave);
            Blob.UnzipInto(zip, folder);
            CallItTheHandover(folder);
            GameSaves.Refresh();
            _loadTrouble = null;
            string system = (GameSaves.Find(HandoverSave) as UncompressedSave)?.MetaData.Systems.FirstOrDefault() ?? "";
            if (system.Length == 0)
            {
                LoadNamed(HandoverSave);
                return true;
            }
            string here = System;
            switch (SaveSystemSwap.RequestJoin(system, LoadSwappedInto))
            {
                case SystemSwapRequest.NotInstalled:
                    why = $"the server's world is in {system}, which your game doesn't have";
                    return false;
                case SystemSwapRequest.WontFit:
                    // Started in it, the graphics card holds nothing else: what would not fit
                    // beside this system fits on its own.
                    why = StartNextIn(system, out _)
                        ? $"your graphics card can't hold {system} as well as {here}: KSA will start in {system} next time, so restart it to join"
                        : $"your graphics card can't hold {system} as well as {here}: start KSA in {system} to join";
                    return false;
                case SystemSwapRequest.Queued:
                    Log.Info($"swapping this game from {here} to {system} for the server's world");
                    return true;
                default:
                    why = TakeLoadTrouble() ?? "";
                    return why.Length == 0;
            }
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return false;
        }
    }

    // Called by KSA once the system is the world's, which after a swap is from inside its own
    // frame: thrown on into it, this would take the game down.
    private static void LoadSwappedInto()
    {
        try
        {
            LoadNamed(HandoverSave);
        }
        catch (Exception e)
        {
            _loadTrouble = e.GetBaseException().Message;
            Log.Error("loading the server's world into the system it is in", e);
        }
    }

    private static string? _loadTrouble;

    /// <summary>Why the world asked for by <see cref="LoadWorld"/> did not go in after all, once; null while nothing has gone wrong.</summary>
    public static string? TakeLoadTrouble()
    {
        string? why = _loadTrouble;
        _loadTrouble = null;
        return why;
    }

    /// <summary>
    /// The name in a world's <c>meta.toml</c> made to match the folder it was just written to.
    ///
    /// <para>KSA lists a save under the name its meta gives, not the folder it is in --
    /// <c>UncompressedSave.FromDirectory</c> registers <c>SaveMetaData.Name</c> -- so a world whose
    /// meta says anything else is written to the handover folder and then cannot be found in it:
    /// <c>LoadSaveGame</c> logs "save 'Multiplayer' not found" and the join sits on "loading the
    /// world" for ever. A world handed over by another player happens to agree, because the game
    /// that saved it saved it under this same name. One imported by an operator, or an empty one
    /// the server carries, agrees only by luck.</para>
    ///
    /// <para>Left alone if there is no meta or no name in it: that is a save KSA will refuse for
    /// its own reasons, and saying so is its job rather than this one's.</para>
    /// </summary>
    private static void CallItTheHandover(string folder)
    {
        string meta = Path.Combine(folder, "meta.toml");
        if (!File.Exists(meta)) return;
        string[] lines = File.ReadAllLines(meta);
        bool found = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("name", StringComparison.Ordinal)) continue;
            int eq = lines[i].IndexOf('=');
            if (eq < 0 || lines[i][..eq].Trim() != "name") continue;
            lines[i] = $"name = \"{HandoverSave}\"";
            found = true;
            break;
        }
        if (found) File.WriteAllLines(meta, lines);
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
            // Read as the serializer reads a stream itself, but for a document type, which a craft
            // has none of and which can be written to unpack without end: this is another player's.
            using global::System.Xml.XmlTextReader reader = new(stream)
            {
                WhitespaceHandling = global::System.Xml.WhitespaceHandling.Significant,
                Normalization = true,
                XmlResolver = null,
                DtdProcessing = global::System.Xml.DtdProcessing.Prohibit,
            };
            if (Serializer.Deserialize(reader) is not VehicleData data)
            {
                why = "it couldn't be read";
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
                why = $"it uses parts your game doesn't have: {missing}";
                return null;
            }
            if (data.ParentBody.Get() is not IParentBody parent)
            {
                why = $"its parent body {data.ParentBody.Id} isn't in this world";
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
                why = $"KSA refused to add {data.Id} to this world";
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
            if (!IsAlive(v) || ReferenceEquals(v, Controlled)) return;
            // KSA takes a craft out of its lookup by the hash of its name, then fills the slot the
            // craft says it has with the last one. A craft the lookup refused as a duplicate says
            // slot 0, which is the Sun: taken away, it takes the other craft's entry and the Sun.
            if (KSA.Universe.CurrentSystem is { } system && !ReferenceEquals(system.All.Get(v.Id), v))
            {
                if (_notOurs.Add(v.Id)) Sync.Noted($"left {v.Id} where it is: this world's lookup holds something else under that name, and taking it away would take the Sun out of the world");
                return;
            }
            KSA.Universe.DestroyVehicle(v);
        }
        catch (Exception e)
        {
            Log.Warn($"could not take {v.Id} away: {e.GetBaseException().Message}");
        }
    }

    // Said once a name, since a craft that cannot be taken away is asked to be again.
    private static readonly HashSet<string> _notOurs = [];

    /// <summary>
    /// Each kitten seated in parts not yet launched, held to the craft by its old name, moved to its new one.
    /// </summary>
    /// <returns>How many were moved.</returns>
    public static int MoveCrew(PartTree parts, string was, string now)
    {
        int moved = 0;
        foreach (IVASeat seat in parts.Modules.Get<IVASeat>())
        {
            if (seat.AssignedKittenHash == KeyHash.Zero) continue;
            if (KSA.Universe.KittenRoster.Find(seat.AssignedKittenHash) is not { } kitten || kitten.AssignedVehicleId != was) continue;
            kitten.TransferToVehicle(now);
            moved++;
        }
        return moved;
    }

    // The system last seen with a Sun, and whether its losing it has been said.
    private static CelestialSystem? _sunSeenIn;
    private static bool _sunLostSaid;

    /// <summary>
    /// Says once, loudly, when this world's lookup has lost its Sun. Nothing in KSA survives that
    /// for long: the Universe Manifest draws nothing, and the vehicle editor dereferences the Sun
    /// as it opens and takes the game down. Said here, the next time it happens explains itself.
    /// </summary>
    public static void WatchTheSun()
    {
        if (KSA.Universe.CurrentSystem is not { } system) return;
        if (system.GetWorldSun() is not null)
        {
            if (!ReferenceEquals(_sunSeenIn, system)) (_sunSeenIn, _sunLostSaid) = (system, false);
            return;
        }
        if (_sunLostSaid || !ReferenceEquals(_sunSeenIn, system)) return;
        _sunLostSaid = true;
        Log.Warn("this world has lost its Sun from KSA's lookup: the Universe Manifest will be empty and opening the vehicle editor will crash KSA");
        Sync.Client?.Note("this world lost its Sun from KSA's lookup");
        TimedAlert.CreateWarning("This world has lost track of its Sun: restart KSA before opening the vehicle editor, or it will crash.");
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
                           float.IsFinite(throttle) ? Math.Clamp(throttle, 0f, 1f) : 0f, flags, (ushort)Math.Min(v.Parts.Count, ushort.MaxValue),
                           ThrustersOf(v), EnginesOf(v), (float)v.NavBallData.DeltaV, v.PropellantMass, (float)v.NavBallData.ThrustWeightRatio);
        return s.PositionCcf.IsFinite && s.PositionCci.IsFinite && s.Body2Ccf.IsFinite ? s : null;
    }

    /// <summary>
    /// A craft just built here set down where its owner has it lying, before it is first drawn.
    /// What it is built from says where it was when that was written, which for a piece of
    /// wreckage built late is in the air where it broke off. True if it was moved.
    /// </summary>
    public static bool SetDown(Vehicle v, Wire.State s, double nearEnoughM)
    {
        try
        {
            if ((s.Flags & Wire.StateFlags.Landed) == 0 || KSA.Universe.CurrentSystem?.Get(s.Body) is not Celestial body) return false;
            if (body.MeanRadius > 0.0 && Vec(D(s.PositionCcf)) < body.MeanRadius * HalfWayIn) return false;
            PhysicsStates states = v.GetPhysicsStatesMutable();
            PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 hereCcf, out _, out _);
            if (ReferenceEquals(v.Parent, body) && Vec(hereCcf - D(s.PositionCcf)) < nearEnoughM) return false;
            if (!ReferenceEquals(v.Parent, body)) return false;
            // Written into the craft as the pull writes it, and not asked of KSA's own setting
            // down, which is done a frame later and puts a craft on the ground at that spot: a
            // piece was drawn for that frame 1.75 m from where it lay.
            PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out _, out doubleQuat body2Ccf);
            doubleQuat body2Phys = states.Kinematic.Body2Phys, ccf2Body = body2Ccf.Inverse();
            states.Kinematic.PositionPhys += (D(s.PositionCcf) - hereCcf).Transform(ccf2Body).Transform(body2Phys);
            states.Kinematic.Body2Phys = body2Phys * (ccf2Body * new doubleQuat(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W));
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not set {v.Id} down where its owner has it: {e.GetBaseException().Message}");
            return false;
        }
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
    private const double SnapM = 5.0, PullSeconds = 0.15, LongestPullSeconds = 0.25;

    // KSA writes these only from a step's results; a turn written between steps has nowhere else to go.
    private static readonly Action<Vehicle, doubleQuat>? SetBody2Cce = Setter<doubleQuat>(nameof(Vehicle.Body2Cce));
    private static readonly Action<Vehicle, double3>? SetBodyRates = Setter<double3>(nameof(Vehicle.BodyRates));

    private static Action<Vehicle, T>? Setter<T>(string name)
    {
        try
        {
            return AccessTools.PropertySetter(typeof(Vehicle), name)?.CreateDelegate<Action<Vehicle, T>>();
        }
        catch (Exception e)
        {
            Log.Warn($"a craft's {name} cannot be written, so another player's craft may turn by itself in a warp: {e.GetBaseException().Message}");
            return null;
        }
    }

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
    ///
    /// <para>That is for a kitten its owner has standing. One being walked has its legs moving
    /// already, and the servo brakes it all the same: held to the standing correction, the copy of
    /// a kitten walking at 1.8 m/s went at 1.6 and fell behind by 0.18 m every second its owner
    /// kept walking -- 1.6 m after nine seconds. So the correction may be as much as
    /// <see cref="KittenKeepsUp"/> of the speed its owner says, which is a kitten walking a little
    /// faster than it was and never one sent past <see cref="KittenTripsMetresPerSecond"/>.</para>
    /// </summary>
    private const double KittenPullMetresPerSecond = 0.15, KittenRestM = 0.25, KittenKeepsUp = 0.5, KittenTripsMetresPerSecond = 6.0;

    /// <summary>
    /// Another player's craft written where its game says it is, from the physics window: turned as
    /// told, moving as told, and drawn onto its place. Nothing in it may throw.
    /// </summary>
    /// <returns>What it found and did, for a run that records it; null where there was nothing to measure.</returns>
    /// <param name="first">Whether this is the first time the craft is pulled since it was built here: it is put where it is told however near, having been built where it stood when it was described and not drawn yet.</param>
    private const double MisshapenPullMetresPerSecond = 3.0;

    /// <param name="patient">Whether a copy in another shape than its owner's is still being waited for to be built again, and so is not put where the other shape's centre stands.</param>
    public static Pulled? Pull(Vehicle v, Wire.State s, double dt, bool first = false, bool patient = true)
    {
        if (!v.HasPhysicsBubble || dt <= 0.0) return null;
        // Across a change of body the two frames differ, so there is nothing to write. Nothing
        // takes this over either: the analytic placing is only for a craft with no bubble, and this
        // craft has one -- it runs on its own physics with its owner's engines until the two games
        // agree on what it is going round, which is a moment at an SOI boundary.
        if ((v.Parent as Astronomical)?.Id is { } body && body != s.Body) return null;
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        doubleQuat ccf2Body = body2Ccf.Inverse();
        double3 off = D(s.PositionCcf) - positionCcf;

        // A kitten on the ground, which is the only place the correction can be read as walking: off
        // the ground KSA gives a kitten's traction servo no budget at all, so there is nothing to be
        // held back for and the clamp only stopped an EVA kitten keeping up with its owner.
        bool walks = v is KittenEva && v.Situation.HasAnyContact();
        double rest = walks ? KittenRestM : 0.05;
        // Standing where it is told to stand, it is left on KSA's rails: woken every step, its physics is built again every step.
        if (v.Situation.IsOnRails() && Vec(D(s.VelocityCcf)) < 0.05 && Vec(off) < rest)
        {
            return new Pulled(V(positionCcf), V(velocityCcf), Vec(off), Snap: false, Held: false, Rest: true);
        }

        // A warp's step is too long to draw a craft on over, and its place is its orbit's to keep,
        // but it is still turned as its owner has it. On rails KSA turns a craft by itself, carried
        // on from the spin it last had, and left to that for a warp a rocket its owner held steady
        // was drawn here sideways, and swung back the moment the warp ended.
        if (dt > LongestPullSeconds)
        {
            states.Kinematic.Body2Phys = body2Phys * (ccf2Body * new doubleQuat(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W));
            states.Kinematic.AngularVelocityPhys = D(s.Spin).Transform(states.Kinematic.Body2Phys);
            // And into the craft itself, which is where KSA's step on rails starts from when it has no
            // spin to carry on: the physics state alone, it put back the attitude the craft had.
            SetBody2Cce?.Invoke(v, states.GetBody2Cce());
            SetBodyRates?.Invoke(v, states.GetBodyRates());
            return new Pulled(V(positionCcf), V(velocityCcf), Vec(off), Snap: false, Held: false, Rest: false, Warped: true);
        }

        // Nothing heard for long enough that the state has stopped moving: Track carries a craft on
        // past its newest word for a second and then holds it where it got to, and chasing that held
        // point drags the craft back its whole frame's travel, every frame -- 130 m a frame in low
        // orbit, for as long as four seconds, or fifty behind a warp. The craft is better left
        // coasting on the velocity it was last given. True only when the carrying-on was cut short,
        // because Track stamps the time it actually drew at.
        // Or told of a time that is not now at all: this world is further behind its states than
        // any of them is kept, and the oldest is where the craft will be, not where it is.
        // Except a craft its owner has asleep on the ground, which stands at the same place in its
        // body's fixed frame at every time: a game seconds behind the clock says nothing else of
        // now, and held for it, a rocket put under the pad stayed there until its pilot launched.
        bool asleep = (s.Flags & (Wire.StateFlags.Rails | Wire.StateFlags.Landed)) == (Wire.StateFlags.Rails | Wire.StateFlags.Landed);
        bool held = !asleep && Math.Abs(Universe - s.Universe) > 1e-6;
        // Told where a craft of another shape is. A craft's place is its centre, and one that has
        // docked, staged or lost a part has its centre elsewhere: the copy here is the old shape
        // until it is built again, and put where the new one stands it was put nine metres along
        // a rocket that a capsule had just joined, twenty-four steps running. Never put there, and
        // drawn there only slowly. Only a craft its owner has coasting, which is where a docking
        // or a stage leaves one: a hull coming apart on the ground is another shape most of the
        // time, and held any less closely than it was, its copy stood in the way of its own
        // wreckage, which fell three metres behind in a second.
        bool misshapen = patient && s.Parts != 0 && s.Parts != v.Parts.Count
                         && (s.Flags & Wire.StateFlags.Rails) != 0 && (s.Flags & Wire.StateFlags.Landed) == 0;
        // However far: a piece that waited for its hull to be built without it is built seconds
        // after it was described, where it was then.
        // And put there from a word of the past too, when its owner's game is behind: built from
        // that game's description, KSA carries it along an orbit to this world's time, and an
        // orbit knows nothing of the ground. Pieces of a rocket coming apart low over the sea,
        // described by a game seconds behind, were built under the water at up to 11,000 g. Where
        // its owner last had it is above the ground in its owner's world. Not from a word of the
        // future, which is a world behind its states: that copy is still owed its first place.
        bool born = first && (!held || s.Universe < Universe) && !misshapen && Vec(off) > 0.05;
        bool snap = !misshapen && ((!held && Vec(off) > SnapM) || born);
        if (snap) states.Kinematic.PositionPhys += off.Transform(ccf2Body).Transform(body2Phys);
        doubleQuat wantedBody2Ccf = new(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W);
        states.Kinematic.Body2Phys = body2Phys * (ccf2Body * wantedBody2Ccf);

        // Left coasting means its velocity is left alone as well. A held state's velocity is the
        // craft's at another time, and in orbit the direction it points turns by a degree every
        // fifteen seconds: written in at every step, it stops gravity bending the path. A world
        // still catching up after a load was minutes behind every word, so the copy of a craft in
        // orbit flew straight on along a line pointing down into the ocean, 1,229 km off its orbit
        // when it was put back on it, and right after another load it was put back 5 km off every
        // half second for thirty seconds.
        if (held && !born)
        {
            states.Kinematic.AngularVelocityPhys = D(s.Spin).Transform(states.Kinematic.Body2Phys);
            return new Pulled(V(positionCcf), V(velocityCcf), Vec(off), Snap: false, Held: true, Rest: false);
        }

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out double3 nowVelocity, out doubleQuat nowBody2Ccf);
        double3 draw = snap ? double3.Zero : off / Math.Max(PullSeconds, dt);
        if (misshapen && Vec(draw) > MisshapenPullMetresPerSecond) draw = draw.Normalized() * MisshapenPullMetresPerSecond;
        // Held to a walk, not to a distance: what matters to a kitten's legs is how fast this says
        // it is going, and the distance was already answered by the snap above.
        if (walks)
        {
            double said = Vec(D(s.VelocityCcf));
            double most = Math.Max(KittenPullMetresPerSecond, Math.Min(said * KittenKeepsUp, KittenTripsMetresPerSecond - said));
            if (Vec(draw) > most) draw = draw.Normalized() * most;
        }
        double3 wanted = D(s.VelocityCcf) + draw;
        states.Kinematic.VelocityPhys += (wanted - nowVelocity).Transform(nowBody2Ccf.Inverse()).Transform(states.Kinematic.Body2Phys);
        states.Kinematic.AngularVelocityPhys = D(s.Spin).Transform(states.Kinematic.Body2Phys);
        // Only when something was actually corrected. Taking a craft off rails keeps it awake, and a
        // craft in contact that is awake can never get back on them -- KSA only puts one back when
        // its body sleeps, and it keeps it awake for as long as it is off rails. So doing this
        // unconditionally latched every landed craft off rails on its first frame, and the standing
        // still path above could never be reached again.
        if (Vec(off) >= rest && v.Situation.IsOnRails()) v.TakeOffRails();
        return new Pulled(V(positionCcf), V(velocityCcf), Vec(off), snap && !born, Held: false, Rest: false);
    }

    /// <summary>What one pull found and did.</summary>
    /// <param name="At">Where the craft stood before anything was written, in its body's fixed frame, and <paramref name="Moving"/> how.</param>
    /// <param name="Off">How far that was from where it was told to stand, m.</param>
    /// <param name="Snap">Whether it was put there outright, which is the teleport.</param>
    /// <param name="Held">Whether the place it was told to stand had stopped moving, and it was left to coast.</param>
    /// <param name="Rest">Whether it stood near enough, on rails, to be left alone.</param>
    /// <param name="Warped">Whether the step was a warp's, and the craft only turned.</param>
    public readonly record struct Pulled(Vec3 At, Vec3 Moving, double Off, bool Snap, bool Held, bool Rest, bool Warped = false);

    /// <summary>Where a craft was held for the vehicle editor and how it was moving, against the ground under it.</summary>
    /// <param name="Body">What it was going round, whose fixed frame the rest is in.</param>
    /// <param name="Spin">Its angular velocity in its own frame, as <see cref="StateOf"/> says it.</param>
    public readonly record struct Hold(string Body, double3 AtCcf, doubleQuat Body2Ccf, double3 VelocityCcf, double3 Spin, bool EngineOn, float Throttle);

    /// <summary>
    /// What to hold a craft by while its pilot is in the vehicle editor, or null where it needs no
    /// holding: on the ground or the sea, on rails, or out where KSA steps nothing through physics,
    /// which is an orbit, and an orbit is no danger to leave.
    /// </summary>
    public static Hold? HoldOf(Vehicle v)
    {
        try
        {
            if (!v.HasPhysicsBubble || v.Situation.IsOnRails() || v.Situation.HasAnyContact() || !v.PhysicsEnvironment.InPhysicsRadius) return null;
            if (v.Parent is not Astronomical parent) return null;
            PhysicsStates states = v.GetPhysicsStatesMutable();
            PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 atCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
            double3 spin = states.Kinematic.AngularVelocityPhys.Transform(states.Kinematic.Body2Phys.Inverse());
            (bool on, float throttle) = EngineInputs is { } inputs ? (inputs(v).EngineOn, inputs(v).EngineThrottle) : (false, v.GetManualThrottle());
            return new Hold(parent.Id, atCcf, body2Ccf, velocityCcf, spin, on, throttle);
        }
        catch (Exception e)
        {
            Log.Warn($"could not hold {v.Id} for the vehicle editor: {e.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>How fast a held craft was going, m/s, against the ground.</summary>
    public static double SpeedOf(in Hold h) => Vec(h.VelocityCcf);

    /// <summary>
    /// A held craft put back where it was held and stopped there, from the physics window. Put back
    /// in its body's fixed frame and stopped against the surface (KSA's own
    /// <c>StopRelativeToSurface</c>), so it turns with the ground under it rather than the ground
    /// turning away beneath it: held in the frame of the stars, a craft over a pad drifts off it at
    /// the planet's rotation.
    /// </summary>
    public static void Keep(Vehicle v, in Hold h)
    {
        if (!v.HasPhysicsBubble || v.Situation.IsOnRails()) return;
        if ((v.Parent as Astronomical)?.Id != h.Body) return;
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out double3 positionCcf, out _, out doubleQuat body2Ccf);
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        doubleQuat ccf2Body = body2Ccf.Inverse();
        states.Kinematic.PositionPhys += (h.AtCcf - positionCcf).Transform(ccf2Body).Transform(body2Phys);
        states.Kinematic.Body2Phys = body2Phys * (ccf2Body * h.Body2Ccf);
        states.StopRelativeToSurface();
    }

    /// <summary>A held craft let go of, from the physics window: moving, turning and burning as it was when it was held.</summary>
    public static void Release(Vehicle v, in Hold h)
    {
        SetEngine(v, h.EngineOn, h.Throttle);
        if (!v.HasPhysicsBubble || (v.Parent as Astronomical)?.Id != h.Body) return;
        // KSA puts a craft that has stood still in the air for a second on its rails, as landed.
        if (v.Situation.IsOnRails()) v.TakeOffRails();
        PhysicsStates states = v.GetPhysicsStatesMutable();
        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out double3 nowVelocity, out doubleQuat nowBody2Ccf);
        states.Kinematic.VelocityPhys += (h.VelocityCcf - nowVelocity).Transform(nowBody2Ccf.Inverse()).Transform(states.Kinematic.Body2Phys);
        states.Kinematic.AngularVelocityPhys = h.Spin.Transform(states.Kinematic.Body2Phys);
    }

    /// <summary>
    /// A craft tinted, or not: KSA's own picking tint, the one drawn over every other. Its picking
    /// clears only the parts it tinted itself, so a tint put on here stays until it is taken off.
    /// </summary>
    public static void Tint(Vehicle v, bool on)
    {
        try
        {
            HighlightTint tint = on ? HighlightTint.Purple : HighlightTint.None;
            ReadOnlySpan<Part> all = v.Parts.Parts;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].HighlightedForPicking != tint) all[i].HighlightedForPicking = tint;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not tint {v.Id}: {e.GetBaseException().Message}");
        }
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
    /// The orbit its owner's word puts a craft on, as of another universe time. KSA stands a craft
    /// at the place its new orbit was made from until the next step carries it on, and a word is a
    /// twentieth of a second old at the least: handed the orbit as said, the craft was drawn for
    /// one frame where it had been then, 350 to 650 m back in low orbit, every time it was put right.
    /// </summary>
    public static (Celestial Body, StateVectors Now)? OrbitAt(Wire.State s, double universe)
    {
        if (KSA.Universe.CurrentSystem?.Get(s.Body) is not Celestial body) return null;
        Orbit said = Orbit.CreateFromStateCci(body, new UniverseTime(s.Universe), D(s.PositionCci), D(s.VelocityCci), default);
        return (body, said.GetStateVectorsAt(new UniverseTime(universe)));
    }

    /// <summary>
    /// How far a craft stands from the orbit its owner's word puts it on, at this world's time: the
    /// same orbit <see cref="Rebase"/> puts it on, so a craft put there measures nothing. Measured
    /// against the owner's state carried on instead, a craft put on its orbit was a metre from where
    /// it was measured, and was put on it again every half second.
    /// Null for another body, or a word too far from now to carry an orbit across.
    /// </summary>
    public static double? OffItsOrbit(Wire.State stands, Wire.State said)
    {
        if (stands.Body != said.Body || Math.Abs(Universe - said.Universe) > CarryOrbitWithinSeconds) return null;
        try
        {
            return OrbitAt(said, Universe) is ({ }, var now) ? Vec(D(stands.PositionCci) - now.PositionCci) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool UnderTheGround(Celestial body, double3 positionCci)
    {
        double far = Vec(positionCci);
        if (far <= 0.0) return true;
        return far < body.MeanRadius + body.GetTerrainHeightFromDirCci(positionCci / far);
    }

    // A game behind the clock is told no state of now, and was corrected for nothing while it
    // caught up: one put on nothing for 33 s after a warp, and then found a craft 4,542 km off.
    private const double CarryOrbitWithinSeconds = 600.0;

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
            if (OrbitAt(s, Universe) is not ({ } body, var now)) return false;
            // An orbit knows nothing of the ground. A word from a game seconds behind, of a rocket
            // dropping the last metre onto its pad, carried eleven seconds on put the copy 642 m
            // under it; what such a craft does next is the next word's to say.
            if (UnderTheGround(body, now.PositionCci)) return false;
            Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(Universe), now.PositionCci, now.VelocityCci, v.OrbitColor);
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

    /// <summary>
    /// A craft's translation keys held down or let go, as the keyboard holds them: what walks a
    /// kitten across the ground and fires a craft's thrusters sideways. KSA sets these on a key going
    /// down and clears them on its coming up, so written once they stay until written again.
    /// </summary>
    public static bool Move(Vehicle v, int forward, int right, int up, bool sprint)
    {
        if (EngineInputs is not { } inputs) return false;
        const ThrusterMapFlags Every = ThrusterMapFlags.TranslateForward | ThrusterMapFlags.TranslateBackward | ThrusterMapFlags.TranslateRight
                                     | ThrusterMapFlags.TranslateLeft | ThrusterMapFlags.TranslateUp | ThrusterMapFlags.TranslateDown;
        ThrusterMapFlags held = ThrusterMapFlags.None;
        if (forward != 0) held |= forward > 0 ? ThrusterMapFlags.TranslateForward : ThrusterMapFlags.TranslateBackward;
        if (right != 0) held |= right > 0 ? ThrusterMapFlags.TranslateRight : ThrusterMapFlags.TranslateLeft;
        if (up != 0) held |= up > 0 ? ThrusterMapFlags.TranslateUp : ThrusterMapFlags.TranslateDown;
        ref ManualControlInputs now = ref inputs(v);
        now.ThrusterCommandFlags = (now.ThrusterCommandFlags & ~Every) | held;
        now.Sprint = sprint;
        return true;
    }

    /// <summary>
    /// How high a craft is above the ground under it and how fast it is rising, m and m/s. The
    /// height is its centre's, so a craft standing on the ground reads its own height and not zero.
    /// </summary>
    public static (double Radar, double Up) Descent(Vehicle v)
    {
        try
        {
            double3 from = v.Orbit.StateVectors.PositionCci;
            double far = Vec(from);
            return far > 0.0 ? (v.GetRadarAltitude(), double3.Dot(v.GetSurfaceVelocityCci(), from) / far) : (0.0, 0.0);
        }
        catch (Exception)
        {
            return (0.0, 0.0);
        }
    }

    /// <summary>
    /// What a craft's flight computer points it at, set as the buttons on KSA's own panel set it:
    /// one of <see cref="FlightComputerAttitudeTrackTarget"/> by name, or nothing to hand the craft
    /// back to its pilot. Null if it was done.
    /// </summary>
    public static string? Track(Vehicle v, string target)
    {
        if (target.Length == 0 || target.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            ToPoint[v] = null;
            return null;
        }
        if (!Enum.TryParse(target, ignoreCase: true, out FlightComputerAttitudeTrackTarget track))
        {
            return $"no such thing to point at as '{target}': one of {string.Join(", ", Enum.GetNames<FlightComputerAttitudeTrackTarget>())}";
        }
        ToPoint[v] = track;
        return null;
    }

    // What each craft's flight computer has been asked to point at and has not been told yet.
    // Told where a physics step is prepared and nowhere else: KSA hands the step a copy of the
    // flight computer and takes the copy back when it is done, so what is written to it from a
    // frame hook is written over a moment later, every time -- twenty seconds of asking a craft
    // to line up with a port left it in Manual with nothing to track.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vehicle, FlightComputerAttitudeTrackTarget?> ToPoint = new();

    // A turn asked of a craft, made where its attitude may be written: as its physics step is prepared.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vehicle, doubleQuat> ToRoll = new();

    /// <summary>Called as a craft's physics step is prepared, for every craft.</summary>
    public static void PointAsAsked(Vehicle v)
    {
        if (!ToRoll.IsEmpty && ToRoll.TryRemove(v, out doubleQuat turn))
        {
            // In the craft's own physics frame, which stands to the frame the turn was worked out
            // in as the craft's two attitudes stand to each other.
            PhysicsStates states = v.GetPhysicsStatesMutable();
            doubleQuat body2Cce = v.Body2Cce;
            states.Kinematic.Body2Phys = body2Cce.Concatenate(turn).Concatenate(body2Cce.Inverse()).Concatenate(states.Kinematic.Body2Phys);
            states.Kinematic.AngularVelocityPhys = double3.Zero;
        }
        if (ToPoint.IsEmpty || !ToPoint.TryRemove(v, out FlightComputerAttitudeTrackTarget? asked)) return;
        if (asked is { } track) v.SetEnum(track);
        else v.SetEnum(FlightComputerAttitudeMode.Manual);
    }

    /// <summary>How many docking ports a craft has with nothing on them.</summary>
    public static int FreePorts(Vehicle v)
    {
        try
        {
            int free = 0;
            foreach (DockingPort port in v.Parts.Modules.Get<DockingPort>()) if (!port.Docked) free++;
            return free;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // KSA's own flag for a craft whose hull met another's in the step just solved. It is on the
    // solver's side of the craft and has no public way in.
    private static readonly System.Reflection.FieldInfo? SolverState = AccessTools.Field(typeof(Vehicle), "_threadWorkerUpdateState");

    private static bool TouchedACraft(Vehicle v) => SolverState?.GetValue(v) is VehicleUpdateState { HadVehicleContactThisFrame: true };

    // What DockingPort asks of two ports that touch, beside its own latching energy.
    private const double LatchWithinM = 0.5, LatchWithinDeg = 10.0, PortsNearM = 1.5, PortCraftWithinM = 300.0;

    // KSA asks nothing of the roll, but the androgynous port's guides repeat every 120 degrees and
    // slot together only where the angle between the two ports' second axes is 60 or 180 (300
    // reads as 60, the angle having no sign). Turned a sixth of a turn from that, guide tip meets
    // guide tip and the ports stop 0.67 m apart. 11 degrees off has docked and 30 has bounced.
    private static readonly double[] DockingRollsDeg = [60.0, 180.0];
    private const double GuidesMeetDeg = 30.0;

    /// <summary>
    /// What stood between two ports and a docking, where a craft has touched another with a free
    /// port of each close together and both craft are still there. Null where it touched nothing
    /// or no two ports were near.
    ///
    /// <para>KSA docks two craft on a contact between their ports only where the ports are under
    /// half a metre apart, within ten degrees of facing each other, and closing with at least the
    /// port's latching energy, and says nothing of a contact that fell short. A player then sees
    /// two craft bounce and has nothing to tell anybody but that it did not dock; and where every
    /// one of those held and the craft bounced all the same, that is the report worth having.</para>
    /// </summary>
    public static string? PortContact(Vehicle craft)
    {
        try
        {
            if (!IsAlive(craft) || !TouchedACraft(craft)) return null;
            (Vehicle Other, DockingPort Mine, DockingPort Theirs, int I, int J, double Apart)? nearest = null;
            foreach (Vehicle other in Vehicles())
            {
                if (ReferenceEquals(other, craft) || !IsAlive(other) || !TouchedACraft(other) || Between(craft, other) > PortCraftWithinM) continue;
                int i = 0;
                foreach (DockingPort mine in craft.Parts.Modules.Get<DockingPort>())
                {
                    int j = 0;
                    foreach (DockingPort theirs in other.Parts.Modules.Get<DockingPort>())
                    {
                        double apart = Vec(PortAt(craft, mine) - PortAt(other, theirs));
                        if (!mine.Docked && !theirs.Docked && apart < (nearest?.Apart ?? PortsNearM)) nearest = (other, mine, theirs, i, j, apart);
                        j++;
                    }
                    i++;
                }
            }
            if (nearest is not { } n) return null;

            double3 axis = Facing(n.Other, n.Theirs), facing = Facing(craft, n.Mine);
            double3 between = PortAt(craft, n.Mine) - PortAt(n.Other, n.Theirs);
            double along = double3.Dot(between, axis), across = Vec(between - (axis * along));
            double tilt = Math.Acos(Math.Clamp(-double3.Dot(facing, axis), -1.0, 1.0)) * 180.0 / Math.PI;
            // About the line between them, by where each port's own second axis lies across it.
            double3 Across(Vehicle v, DockingPort port)
            {
                double3 y = new double3(0.0, 1.0, 0.0).Transform(port.Connector.Asmb2VehicleAsmb.Concatenate(v.Asmb2Cce));
                return y - (axis * double3.Dot(y, axis));
            }
            double3 mineAcross = Across(craft, n.Mine), theirsAcross = Across(n.Other, n.Theirs);
            double roll = Vec(mineAcross) < 1e-6 || Vec(theirsAcross) < 1e-6 ? double.NaN
                : Math.Acos(Math.Clamp(double3.Dot(mineAcross, theirsAcross) / (Vec(mineAcross) * Vec(theirsAcross)), -1.0, 1.0)) * 180.0 / Math.PI;
            // The raw angle reads 180 for two ports the flight computer's Align lines up with roll Up,
            // and a player read that as half a turn wrong; what they can act on is how far to turn.
            double offRoll = Math.Min(Math.Abs(roll - DockingRollsDeg[0]), Math.Abs(roll - DockingRollsDeg[1]));
            // KSA measures the closing along the lighter craft's port, and against that port's latch.
            bool lighter = craft.TotalMass <= n.Other.TotalMass;
            double closing = double3.Dot(craft.GetVelocityCce() - n.Other.GetVelocityCce(), lighter ? facing : -axis);
            double reduced = (double)craft.TotalMass * n.Other.TotalMass / ((double)craft.TotalMass + n.Other.TotalMass);
            double energy = closing > 0.0 ? 0.5 * reduced * closing * closing : 0.0, latch = (lighter ? n.Mine : n.Theirs).LatchingKineticEnergy;

            List<string> short_ = [];
            if (offRoll >= GuidesMeetDeg)
                short_.Add($"the port guides met, turned {offRoll:F0} degrees from a docking roll: hold Align with roll Up, or roll {offRoll:F0} degrees");
            if (n.Apart >= LatchWithinM) short_.Add($"the ports were {n.Apart:F2} m apart and KSA latches under {LatchWithinM} m");
            if (tilt > LatchWithinDeg) short_.Add($"they were {tilt:F0} degrees off facing each other and KSA allows {LatchWithinDeg:F0}");
            if (closing <= 0.0) short_.Add("they were no longer closing");
            else if (energy < latch) short_.Add($"they met with {energy:F0} J and the port latches at {latch:F0} J");
            string why = short_.Count > 0 ? string.Join(", ", short_) : "everything KSA asks of two ports held when this was read";
            return $"{craft.Id} ({craft.TotalMass:F0} kg) port {n.I + 1} of {craft.Parts.Modules.Get<DockingPort>().Length} on {n.Mine.Parent.FullPart.DisplayName}, "
                 + $"{n.Other.Id} ({n.Other.TotalMass:F0} kg) port {n.J + 1} of {n.Other.Parts.Modules.Get<DockingPort>().Length} on {n.Theirs.Parent.FullPart.DisplayName}: "
                 + $"{along:F2} m along and {across:F2} m across, {tilt:F1} degrees of tilt, {offRoll:F0} degrees of roll from docking, closing at {closing:F2} m/s -- {why}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DockingPort? FreePort(Vehicle v)
    {
        foreach (DockingPort port in v.Parts.Modules.Get<DockingPort>()) if (!port.Docked) return port;
        return null;
    }

    /// <summary>
    /// Where a craft's free docking port is from another's, in the frame KSA's own port menu
    /// measures in: what an approach is steered by.
    /// </summary>
    /// <param name="Apart">From their port to this craft's, m.</param>
    /// <param name="Axis">The way their port faces, and <paramref name="Mine"/> the way this craft's does.</param>
    /// <param name="Closing">This craft's velocity less theirs, m/s.</param>
    /// <param name="Body2Cce">This craft's own frame, which is what its thrusters push in.</param>
    public readonly record struct PortsApart(double3 Apart, double3 Axis, double3 Mine, double3 Closing, doubleQuat Body2Cce);

    public static PortsApart? Ports(Vehicle craft, Vehicle target)
    {
        try
        {
            if (FreePort(craft) is not { } mine || FreePort(target) is not { } theirs) return null;
            return new(PortAt(craft, mine) - PortAt(target, theirs), Facing(target, theirs), Facing(craft, mine),
                craft.GetVelocityCce() - target.GetVelocityCce(), craft.Body2Cce);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // As DockingPort.DrawUi works them out for its Dock item: a port's place, and the way it faces.
    private static double3 PortAt(Vehicle v, DockingPort port) =>
        v.GetPositionCce() + (port.Connector.PositionVehicleAsmb - v.CenterOfMassAsmb).Transform(v.Body2Cce);

    private static double3 Facing(Vehicle v, DockingPort port) =>
        new double3(1.0, 0.0, 0.0).Transform(port.Connector.Asmb2VehicleAsmb.Concatenate(v.Asmb2Cce));

    /// <summary>
    /// How far a craft's free port is turned about the line between it and another's, degrees
    /// either way from the two ports' own second axes lying together. KSA asks nothing of it
    /// before it docks two craft, and the ports' own shapes may.
    /// </summary>
    public static double? PortRoll(Vehicle craft, Vehicle target)
    {
        try
        {
            if (FreePort(craft) is not { } mine || FreePort(target) is not { } theirs) return null;
            double3 axis = Facing(target, theirs);
            double3 Lying(Vehicle v, DockingPort port)
            {
                double3 y = new double3(0.0, 1.0, 0.0).Transform(port.Connector.Asmb2VehicleAsmb.Concatenate(v.Asmb2Cce));
                return y - (axis * double3.Dot(y, axis));
            }
            double3 a = Lying(craft, mine), b = Lying(target, theirs);
            if (Vec(a) < 1e-6 || Vec(b) < 1e-6) return null;
            return Math.Atan2(double3.Dot(double3.Cross(b, a), axis), double3.Dot(a, b)) * 180.0 / Math.PI;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A craft turned about the line out of another's free port, so many degrees, and left still: what sets the roll two ports meet at.</summary>
    public static void RollAboutPort(Vehicle craft, Vehicle target, double degrees)
    {
        if (FreePort(target) is not { } theirs) return;
        ToRoll[craft] = doubleQuat.CreateFromAxisAngle(Facing(target, theirs), degrees * Math.PI / 180.0);
    }

    /// <summary>
    /// A craft set up to dock with another as a player sets it up: their port targeted, this
    /// craft controlled from its own, and the flight computer told to line the two up. Null if done.
    /// </summary>
    public static string? AimAtPort(Vehicle craft, Vehicle target)
    {
        try
        {
            if (CannotDock(craft, target) is { } why) return why;
            DockingPort mine = FreePort(craft)!, theirs = FreePort(target)!;
            InputEvents.ChangeTargetBuffer.Add(new InputEvents.ChangeTargetData { Vehicle = craft, Target = target, TargetPart = theirs.Parent.FullPart });
            craft.SetControlPart(mine.Parent.FullPart, mine.Connector);
            ToPoint[craft] = FlightComputerAttitudeTrackTarget.Align;
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>What a craft's flight computer is doing about where it points, for a tool that cannot see the panel.</summary>
    public static Dictionary<string, object?> Pointing(Vehicle v)
    {
        try
        {
            FlightComputer fc = v.FlightComputer;
            return new()
            {
                ["mode"] = fc.AttitudeMode.ToString(), ["track"] = fc.AttitudeTrackTarget.ToString(), ["frame"] = fc.AttitudeFrame.ToString(),
                ["rcs"] = fc.RCSMode.ToString(), ["has_rcs"] = fc.VehicleConfig?.HasRcs, ["has_gimbals"] = fc.VehicleConfig?.HasGimbals,
                ["by"] = $"{fc.ActiveControlSystem.X}/{fc.ActiveControlSystem.Y}/{fc.ActiveControlSystem.Z}",
                ["rcs_torque"] = Math.Round(Math.Sqrt((fc.RcsTorqueAuthority.X * fc.RcsTorqueAuthority.X) + (fc.RcsTorqueAuthority.Y * fc.RcsTorqueAuthority.Y) + (fc.RcsTorqueAuthority.Z * fc.RcsTorqueAuthority.Z)), 3),
                ["error_deg"] = Math.Round(Math.Sqrt((fc.ErrorAngles.X * fc.ErrorAngles.X) + (fc.ErrorAngles.Y * fc.ErrorAngles.Y) + (fc.ErrorAngles.Z * fc.ErrorAngles.Z)) * 180.0 / Math.PI, 1),
                ["target"] = (v.Target as Vehicle)?.Id, ["target_part"] = v.TargetPart is not null,
                ["control_part"] = v.ControlPart is not null, ["controllable"] = v.IsControllable,
            };
        }
        catch (Exception e)
        {
            return new() { ["error"] = e.GetBaseException().Message };
        }
    }

    /// <summary>Whether a craft's flight computer is holding it lined up with the port it has targeted.</summary>
    public static bool Aligning(Vehicle v) =>
        v.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto && v.FlightComputer.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.Align
        && v.ControlPart is not null && v.TargetPart is not null;

    /// <summary>
    /// A craft put in front of another's free docking port, so many metres out along the way the
    /// port faces and moving as that craft moves. Null if it was done.
    /// </summary>
    public static string? PutBeforePort(Vehicle v, Vehicle other, double metres)
    {
        try
        {
            if (FreePort(other) is not { } port) return $"{other.Id} has no docking port free";
            PhysicsStates states = other.GetPhysicsStatesMutable();
            if (states.Origin.Parent is not Astronomical parent || KSA.Universe.CurrentSystem?.Get(parent.Id) is not Celestial body)
            {
                return $"{other.Id} is going round nothing";
            }
            PhysicsStates.GetStatesCci(in states.Origin, in states.Kinematic, out double3 positionCci, out double3 velocityCci, out _);
            double3 out_ = PortAt(other, port) - other.GetPositionCce() + (Facing(other, port) * metres);
            Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(Universe), positionCci + out_.Transform(body.GetCce2Cci()), velocityCci, v.OrbitColor);
            v.Teleport(orbit, null, null);
            v.UpdatePerFrameData();
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// A craft undocked at its first docked port, through the input KSA's own port menu puts in:
    /// applied where KSA applies a player's. Null if it was asked.
    /// </summary>
    public static string? Undock(Vehicle v)
    {
        try
        {
            foreach (DockingPort port in v.Parts.Modules.Get<DockingPort>())
            {
                if (!port.Docked) continue;
                InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData { Vehicle = v, DockingPort = port, Undock = true });
                return null;
            }
            return $"{v.Id} has nothing docked";
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>Why two craft cannot be docked, or null if they can.</summary>
    public static string? CannotDock(Vehicle a, Vehicle b)
    {
        try
        {
            if (FreePort(a) is null) return $"{a.Id} has no docking port free";
            return FreePort(b) is null ? $"{b.Id} has no docking port free" : null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Two craft docked, port to port, wherever they are: the lighter is taken into the heavier,
    /// which is the one that is left. Empty if it was done.
    ///
    /// <para>This is the same <see cref="DockingEvent.Apply"/> KSA's contact solver raises when two
    /// ports touch closely and slowly enough, and it raises it on the lighter craft as this does, so
    /// what follows a docking is reached as it would be. What is not reached is the flying: nothing
    /// here brings the two ports together, and KSA moves the lighter craft onto the port.</para>
    ///
    /// <para>Asked for from the frame's quiet window, because it takes a craft out of the world.</para>
    /// </summary>
    public static string Dock(Vehicle a, Vehicle b)
    {
        try
        {
            (Vehicle from, Vehicle into) = a.TotalMass <= b.TotalMass ? (a, b) : (b, a);
            if (FreePort(from) is not { } port || FreePort(into) is not { } other) return CannotDock(a, b) ?? "no port";
            new DockingEvent { OtherVehicle = into, OtherDockingPort = other, DockingPort = port }.Apply(from);
            return "";
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// A craft put beside another, so many metres further from the body they go round and moving as
    /// it moves: two craft in one place without anybody flying a rendezvous. Beside itself, it is a
    /// craft lifted that far off the ground and let go. Null if it was done.
    /// </summary>
    public static string? PutBeside(Vehicle v, Vehicle other, double metres)
    {
        try
        {
            PhysicsStates states = other.GetPhysicsStatesMutable();
            if (states.Origin.Parent is not Astronomical parent || KSA.Universe.CurrentSystem?.Get(parent.Id) is not Celestial body)
            {
                return $"{other.Id} is going round nothing";
            }
            PhysicsStates.GetStatesCci(in states.Origin, in states.Kinematic, out double3 positionCci, out double3 velocityCci, out _);
            double3 further = positionCci * (metres / Vec(positionCci));
            Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(Universe), positionCci + further, velocityCci, v.OrbitColor);
            v.Teleport(orbit, null, null);
            v.UpdatePerFrameData();
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message;
        }
    }

    /// <summary>
    /// A craft of this player's own put into a circular orbit of a body, as the debug menu's Set
    /// Orbit window does: the one way to make a craft change body without flying there, which is
    /// what a test of a shared world needs. Null if it could not be done.
    /// </summary>
    public static string? SetOrbit(Vehicle v, string bodyId, double altitudeM)
    {
        if (KSA.Universe.CurrentSystem?.Get(bodyId) is not Celestial body) return $"no body '{bodyId}' in this world";
        double radius = body.MeanRadius + altitudeM;
        if (!(radius > body.MeanRadius)) return $"{altitudeM} m is not above {bodyId}";
        // Straight from the state, the way the Set Orbit window builds one: a circular orbit needs
        // only a radius and the speed that holds it there.
        double speed = Math.Sqrt(body.Mass * KSA.Constants.GRAVITATIONAL_CONSTANT / radius);
        Orbit orbit = Orbit.CreateFromStateCci(body, new UniverseTime(Universe), new double3(radius, 0.0, 0.0),
                                               new double3(0.0, speed, 0.0), v.OrbitColor);
        v.Teleport(orbit, null, null);
        v.UpdatePerFrameData();
        return ReferenceEquals(v.GetPhysicsStatesMutable().Origin.Parent, body) ? null : $"KSA would not move {v.Id} to {bodyId}";
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
        Span<RocketNozzle> modules = nozzles.Modules;
        float most = 0f;
        // The engines alone: an RCS thruster puffing is not the engine burning, and said to be, the
        // others lit the main engines of a craft its pilot was only nudging sideways.
        for (int i = 0; i < states.Length && i < modules.Length; i++)
        {
            if (!IsThruster(modules[i])) most = Math.Max(most, Math.Max(states[i].DutyCycle, states[i].ThrustFraction));
        }
        return most;
    }

    // An RCS thruster is a rocket nozzle like an engine's, fired by a ThrusterController rather than
    // by the throttle.
    private static bool IsThruster(RocketNozzle nozzle) => nozzle.Rocket?.Controller is ThrusterController;

    /// <summary>
    /// Which of this craft's RCS thrusters are firing, a bit each in the order its nozzles come in,
    /// or null when none is. Read from the last physics step, which is the only place KSA works it out.
    /// </summary>
    public static byte[]? ThrustersOf(Vehicle v) => FiringOf(v, thrusters: true);

    /// <summary>Which of this craft's engine nozzles are firing, as <see cref="ThrustersOf"/> does its thrusters.</summary>
    public static byte[]? EnginesOf(Vehicle v) => FiringOf(v, thrusters: false);

    private static byte[]? FiringOf(Vehicle v, bool thrusters)
    {
        try
        {
            if (v.Parts?.RocketNozzles is not { } nozzles) return null;
            ReadOnlySpan<RocketNozzleState> states = nozzles.States;
            Span<RocketNozzle> modules = nozzles.Modules;
            Span<byte> bits = stackalloc byte[Wire.MostThrusterBytes];
            int n = 0, last = -1;
            for (int i = 0; i < states.Length && i < modules.Length; i++)
            {
                if (IsThruster(modules[i]) != thrusters) continue;
                if (n >= Wire.MostThrusterBytes * 8) break;
                if (states[i].DutyCycle > 0f || states[i].ThrustFraction > 0f)
                {
                    bits[n / 8] |= (byte)(1 << (n % 8));
                    last = n;
                }
                n++;
            }
            return last < 0 ? null : bits[..((last / 8) + 1)].ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A craft's engine key and throttle, which KSA fires the engines of the craft's active stage from
    /// on its next physics step.
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
    // writing dark once.
    private static readonly HashSet<Vehicle> _shown = [];

    // Craft whose thrusters were last drawn from here, which KSA never fires for a craft nobody here flies.
    private static readonly HashSet<Vehicle> _puffed = [];
    private static bool _saidNoPlume;

    /// <summary>Forgets which craft were drawn burning, when this game is its own again.</summary>
    public static void ForgetEngines()
    {
        _shown.Clear();
        _puffed.Clear();
    }

    /// <summary>Whether this craft's engines were last drawn from the mod rather than out of KSA's own step.</summary>
    public static bool IsShown(Vehicle v) => _shown.Contains(v);

    /// <summary>Whether this craft's RCS thrusters were last drawn firing from the mod.</summary>
    public static bool IsPuffed(Vehicle v) => _puffed.Contains(v);

    /// <summary>What this craft's tanks hold in this game, in kilograms: those that feed its RCS thrusters, and all of them.</summary>
    public static (double Thrusters, double All) PropellantOf(Vehicle v)
    {
        try
        {
            ReadOnlySpan<MoleState> moles = v.Parts.Moles.States;
            double thrusters = 0.0, all = 0.0;
            foreach (Tank tank in v.Parts.Modules.Get<Tank>())
            {
                double held = tank.ComputeSubstanceMass(moles);
                all += held;
                if (tank.RoleAffinity.IsSet(ConsumerRole.Thruster)) thrusters += held;
            }
            return (thrusters, all);
        }
        catch (Exception)
        {
            return (double.NaN, double.NaN);
        }
    }

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
    ///
    /// <para>Which nozzles are lit is the owner's word, a bit each, and not this copy's staging,
    /// which is whatever the craft's definition said when it was sent: lit from that, a rocket on
    /// ksamp.gg was seen from another game with its launch escape tower burning, and more engines
    /// than its owner had lit. The bits are in the order the nozzles come in, so a copy of another
    /// shape than its owner's -- a stage its owner has dropped and it has not yet -- takes them from
    /// nobody and lights its own active stage.</para>
    /// </summary>
    public static void ShowEngine(Vehicle v, bool on, float throttle, byte[]? thrusters = null, byte[]? engines = null, bool sameShape = true)
    {
        bool puffing = sameShape && thrusters is { Length: > 0 }, shown = _shown.Contains(v), puffed = _puffed.Contains(v);
        if (!on && !puffing && !shown && !puffed) return;
        try
        {
            if (v.Parts is not { RocketNozzles: { } nozzles, RocketCores: { } cores }) return;
            float ambient = v.PhysicsEnvironment.AtmosphericPressure;
            // Enough of a throttle to be something: a core at nothing has no conditions and makes no
            // plume, and a craft said to be burning at nothing is a craft whose throttle was not read.
            float open = float.IsFinite(throttle) ? Math.Clamp(throttle, 0.05f, 1f) : 1f;
            Span<RocketNozzle> modules = nozzles.Modules;
            // An RCS thruster is a rocket nozzle too, one a ThrusterController fires: lighting every
            // nozzle had a burning rocket puffing every thruster it has in every direction at once.
            int thruster = 0, engine = 0;
            for (int i = 0; i < modules.Length; i++)
            {
                RocketNozzle nozzle = modules[i];
                bool isThruster = IsThruster(nozzle);
                int bit = isThruster ? thruster++ : engine++;
                if (nozzle.Core is not { } core) continue;
                bool lit = isThruster ? puffing && IsSet(thrusters, bit)
                         : on && (sameShape && engines is not null ? IsSet(engines, bit)
                                                                   : nozzle.Rocket.Controller is EngineController { IsActive: true });
                if (!lit && !(isThruster ? puffed : shown)) continue;
                ModuleStateful<RocketNozzle, RocketNozzleState, EmptyStruct, RocketNozzleFxState>.StateList.ModuleAndAllMutableStatesRef noz =
                    nozzles.GetModuleAndAllMutableStatesForInitialization(nozzle);
                if (!lit)
                {
                    (noz.State.DutyCycle, noz.State.AverageThrustFraction) = (0f, 0f);
                    continue;
                }
                // A thruster is all or nothing: the throttle is the engines'.
                RocketCoreConditions conditions = core.ComputeConditions(isThruster ? 1f : open);
                ModuleStateful<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>.StateList.ModuleAndAllMutableStatesRef hot =
                    cores.GetModuleAndAllMutableStatesForInitialization(core);
                hot.State.Conditions = conditions;
                NozzlePerformance made = nozzle.ComputePerformance(in conditions, ambient);
                noz.State.Exhaust = made.Exhaust;
                noz.State.ExhaustVelocity = made.ActualExhaustVelocity;
                // The whole of the step, and how hard: the other statistic the step would have left,
                // which is what the engine's own sound is pitched by within earshot.
                (noz.State.DutyCycle, noz.State.AverageThrustFraction) = (1f, isThruster ? 1f : open);
            }
            if (on) _shown.Add(v);
            else _shown.Remove(v);
            if (puffing) _puffed.Add(v);
            else _puffed.Remove(v);
        }
        catch (Exception e)
        {
            if (_saidNoPlume) return;
            _saidNoPlume = true;
            Log.Warn($"other players' engines cannot be shown firing: {e.GetBaseException().Message}");
        }
    }

    private static bool IsSet(byte[]? bits, int bit) => bits is { } b && bit / 8 < b.Length && (b[bit / 8] & (1 << (bit % 8))) != 0;

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
    /// What KSA's instruments would say of a craft, as far as this game's copy of it can say it: m,
    /// m/s, and seconds from now. Its tanks are left out, since a copy of another player's craft
    /// never burns them. Apoapsis is NaN for a craft not coming back, the times NaN where none comes.
    /// </summary>
    public readonly record struct Gauges(string Body, bool Landed, double Altitude, double AboveGround, double OrbitalSpeed,
                                         double SurfaceSpeed, double VerticalSpeed, double Apoapsis, double ToApoapsis,
                                         double Periapsis, double ToPeriapsis, double Inclination);

    /// <summary>
    /// KSA's HUD is drawn for the craft being flown and for nothing else, and a spectator flies
    /// nothing; these are the same readings off the craft being watched instead.
    /// </summary>
    public static Gauges? GaugesOf(Vehicle v)
    {
        try
        {
            Orbit o = v.Orbit;
            double3 at = o.StateVectors.PositionCci, moving = o.StateVectors.VelocityCci;
            double r = at.Length();
            double now = KSA.Universe.GetElapsedSeconds();
            double Until(UniverseTime t) => t == UniverseTime.EndOfTime ? double.NaN : t.Seconds() - now;
            bool escaping = o.Eccentricity >= 1.0;
            return new Gauges(o.Parent.Id, v.Situation.HasAnyContact(), v.GetBarometricAltitude(), v.GetRadarAltitude(),
                              v.GetInertialSpeed(), v.GetSurfaceSpeed(), r > 0.0 ? double3.Dot(at, moving) / r : 0.0,
                              escaping ? double.NaN : o.ApoapsisAltitude, escaping ? double.NaN : Until(v.NextApoapsisTime),
                              o.PeriapsisAltitude, Until(v.NextPeriapsisTime), o.Inclination * (180.0 / Math.PI));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The camera put on a planet or a moon, as picking one in the map puts it. KSA's camera takes
    /// control of whatever it follows, and a planet is no craft: the craft being flown is let go of.
    /// </summary>
    public static bool WatchBody(string id)
    {
        try
        {
            if (Program.GetMainCamera() is not { } camera || KSA.Universe.CurrentSystem?.Get(id) is not Celestial body) return false;
            camera.SetFollow(body, tidalLocking: false);
            return ReferenceEquals(camera.Following, body);
        }
        catch (Exception e)
        {
            Log.Warn($"could not watch {id}: {e.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>Whatever the camera follows, a craft or a body, or null for nothing.</summary>
    public static object? Following => Program.GetMainCamera()?.Following;

    /// <summary>What a craft or a body is called, for a log line about what the camera is on.</summary>
    public static string NameOf(object? followed) => followed switch
    {
        null => "nothing",
        Astronomical a => a.Id,
        _ => followed.GetType().Name,
    };

    // Why KSA will not fly a craft is a private field; its public tooltip is a localised string.
    private static readonly System.Reflection.FieldInfo? ControlState = AccessTools.Field(typeof(Vehicle), "_control");

    /// <summary>
    /// Why KSA will not answer this craft's controls, or null while it does: no control module
    /// left, a probe core without power, or no crew able to fly it. A craft that has broken apart
    /// keeps its name on the half with its root part, which need not be the half with the pod.
    /// </summary>
    public static string? NoControl(Vehicle v)
    {
        try
        {
            if (v.IsControllable) return null;
            if (!v.HasControlModule) return "it has no control module";
            return ControlState?.GetValue(v) is VehicleControl c ? c switch
            {
                VehicleControl.CrewIncapacitated => "its crew are incapacitated",
                VehicleControl.NoCrew => "it has no crew to fly it",
                VehicleControl.ProbeCoreUnpowered => "its probe core has no power",
                VehicleControl.ProbeCoreOff => "its probe core is off",
                VehicleControl.ProbeCoreFailed => "its probe core has failed",
                VehicleControl.ProbeCoreStormStandby => "its probe core is standing by for a storm",
                VehicleControl.ProbeCoreRebooting => "its probe core is rebooting",
                _ => c.ToString(),
            } : "KSA will not say why";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>How the camera looks at a craft it follows: KSA keeps it on the craft, not the camera.</summary>
    public readonly record struct View(CameraReferenceFrame Frame, double Azimuth, double Elevation, double DistancePower);

    public static View? ViewOf(Vehicle v)
    {
        try
        {
            OrbitView o = v.OrbitView;
            return new View(o.ReferenceFrame, o.Azimuth, o.Elevation, o.DistancePower);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The camera put on a craft and looking at it as it looked at another. A craft built again here
    /// arrives with the view its owner had of it, from its definition, and not the one this player
    /// had: put back on it with that, the camera swings round and out to wherever its owner left it.
    /// </summary>
    public static bool Watch(Vehicle v, View view)
    {
        try
        {
            OrbitView o = v.OrbitView;
            (o.ReferenceFrame, o.Azimuth, o.Elevation, o.DistancePower) = (view.Frame, view.Azimuth, view.Elevation, view.DistancePower);
        }
        catch (Exception e)
        {
            Log.Warn($"could not keep the view of {v.Id}: {e.GetBaseException().Message}");
        }
        return Watch(v);
    }

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
            FollowInTheOtherView(camera, v);
            return ReferenceEquals(camera.Following, v);
        }
        catch (Exception e)
        {
            Log.Warn($"could not watch {v.Id}: {e.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// The camera of the view not being shown put on the same craft. KSA's flight view and its map
    /// each have a camera that follows something of its own, and the main camera is whichever is
    /// showing: a player who spectated somebody and pressed M found the map still on their own
    /// craft, and from then on was watching that, as far as this mod and the room could tell.
    /// KSA's own craft switching sets both (<c>InputEvents</c>, <c>Universe.HandOffCameras</c>).
    /// </summary>
    private static void FollowInTheOtherView(Camera shown, Vehicle v)
    {
        if (Program.MainViewport is not { } view) return;
        Camera other = ReferenceEquals(shown, view.MapCamera) ? view.BaseCamera : view.MapCamera;
        if (other is not null && !ReferenceEquals(other, shown) && !ReferenceEquals(other.Following, v))
            other.SetFollow(v, tidalLocking: false, changeControl: false, alert: false);
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
            FollowInTheOtherView(camera, v);
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
    public static double Vec(double3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
}
