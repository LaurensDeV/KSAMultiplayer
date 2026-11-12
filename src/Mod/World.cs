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

    /// <summary>
    /// Every mod this game has enabled, each with the version its own assembly carries where it
    /// has one: <c>Core,KSAMultiplayer@0.18.0+f0fd6cb...</c>. See <see cref="ModList"/>.
    /// </summary>
    public static string Mods =>
        ModLibrary.Manifest is { } manifest
            ? string.Join(',', manifest.Mods.Where(m => m.Enabled).Select(m => ModVersions().TryGetValue(m.Id, out string? at) ? $"{m.Id}@{at}" : m.Id))
            : "";

    private static Dictionary<string, string>? _modVersions;

    /// <summary>
    /// The version of each mod that brought code, by the folder it was loaded from: KSA knows a
    /// mod's name and where it is and nothing of what version it is at, so it is read off the
    /// assembly the mod's own <c>mod.toml</c> names. Looked for until something is found, since a
    /// game may ask before every mod has loaded.
    /// </summary>
    private static Dictionary<string, string> ModVersions()
    {
        if (_modVersions is { Count: > 0 } known) return known;
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (global::System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (a.IsDynamic || a.Location is not { Length: > 0 } at || Path.GetDirectoryName(at) is not { } folder) continue;
                string toml = Path.Combine(folder, ModLibrary.MOD_TOML);
                if (!File.Exists(toml)) continue;
                global::System.Text.RegularExpressions.Match entry =
                    global::System.Text.RegularExpressions.Regex.Match(File.ReadAllText(toml), "EntryAssembly\\s*=\\s*\"([^\"]+)\"");
                string named = entry.Success ? Path.GetFileNameWithoutExtension(entry.Groups[1].Value) : "";
                if (!string.Equals(named, a.GetName().Name, StringComparison.OrdinalIgnoreCase)) continue;
                string? version = a.GetCustomAttributes(typeof(global::System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<global::System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
                version ??= a.GetName().Version?.ToString(3);
                // Nothing in it that would be read as the end of an entry or the start of a version.
                if (version is { Length: > 0 } && !version.Contains(',') && !version.Contains('@')) found[Path.GetFileName(folder)] = version;
            }
            catch (Exception)
            {
                // An assembly that will not say where it is from is not a mod's.
            }
        }
        return _modVersions = found;
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
    /// <summary>
    /// Whether a craft is being stepped through physics and is not one KSA has put on rails: a
    /// craft something is pushing, or that is still moving from having been.
    /// </summary>
    /// <summary>Whether a craft is within this game's physics: near enough to what is flown or watched to be stepped and struck.</summary>
    public static bool InBubble(Vehicle v)
    {
        try { return v.HasPhysicsBubble; }
        catch (Exception) { return false; }
    }

    public static bool UnderWay(Vehicle v)
    {
        try { return v.HasPhysicsBubble && !v.Situation.IsOnRails(); }
        catch (Exception) { return false; }
    }

    /// <summary>Whether the vehicle editor is open, which is where KSA stops the world.</summary>
    public static bool InEditor => Program.IsEditorOpen;

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
                why = $"the server's world is in {system}, which this game does not have";
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

    /// <summary>A world from the server written as the handover save and loaded in place of this one, or false and why not.</summary>
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
            GameSaves.LoadSaveGame(HandoverSave);
            return true;
        }
        catch (Exception e)
        {
            why = e.GetBaseException().Message;
            return false;
        }
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
                           float.IsFinite(throttle) ? Math.Clamp(throttle, 0f, 1f) : 0f, flags, (ushort)Math.Min(v.Parts.Count, ushort.MaxValue));
        return s.PositionCcf.IsFinite && s.PositionCci.IsFinite && s.Body2Ccf.IsFinite ? s : null;
    }

    /// <summary>
    /// A craft with no physics here put where its owner says, by its orbit or, on the ground, by
    /// setting it down there. Seldom: each is a craft rebuilt. True if it was moved.
    /// </summary>
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
        if (!v.HasPhysicsBubble || dt <= 0.0 || dt > 0.25) return null;
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

        // Nothing heard for long enough that the state has stopped moving: Track carries a craft on
        // past its newest word for a second and then holds it where it got to, and chasing that held
        // point drags the craft back its whole frame's travel, every frame -- 130 m a frame in low
        // orbit, for as long as four seconds, or fifty behind a warp. The craft is better left
        // coasting on the velocity it was last given. True only when the carrying-on was cut short,
        // because Track stamps the time it actually drew at.
        // Or told of a time that is not now at all: this world is further behind its states than
        // any of them is kept, and the oldest is where the craft will be, not where it is.
        bool held = Math.Abs(Universe - s.Universe) > 1e-6;
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
        bool born = first && !held && !misshapen && Vec(off) > 0.05;
        bool snap = !held && !misshapen && (Vec(off) > SnapM || born);
        if (snap) states.Kinematic.PositionPhys += off.Transform(ccf2Body).Transform(body2Phys);
        doubleQuat wantedBody2Ccf = new(s.Body2Ccf.X, s.Body2Ccf.Y, s.Body2Ccf.Z, s.Body2Ccf.W);
        states.Kinematic.Body2Phys = body2Phys * (ccf2Body * wantedBody2Ccf);

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic, out _, out double3 nowVelocity, out doubleQuat nowBody2Ccf);
        double3 draw = snap || held ? double3.Zero : off / Math.Max(PullSeconds, dt);
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
        if (!held && Vec(off) >= rest && v.Situation.IsOnRails()) v.TakeOffRails();
        return new Pulled(V(positionCcf), V(velocityCcf), Vec(off), snap && !born, held, Rest: false);
    }

    /// <summary>What one pull found and did.</summary>
    /// <param name="At">Where the craft stood before anything was written, in its body's fixed frame, and <paramref name="Moving"/> how.</param>
    /// <param name="Off">How far that was from where it was told to stand, m.</param>
    /// <param name="Snap">Whether it was put there outright, which is the teleport.</param>
    /// <param name="Held">Whether the place it was told to stand had stopped moving, and it was left to coast.</param>
    /// <param name="Rest">Whether it stood near enough, on rails, to be left alone.</param>
    public readonly record struct Pulled(Vec3 At, Vec3 Moving, double Off, bool Snap, bool Held, bool Rest);

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
            // The orbit its owner's word puts it on, and then that orbit as of now. KSA stands a
            // craft at the place its new orbit was made from until the next step carries it on,
            // and a word is a twentieth of a second old at the least: handed the orbit as said,
            // the craft was drawn for one frame where it had been then, 350 to 650 m back in low
            // orbit, every time it was put right.
            Orbit said = Orbit.CreateFromStateCci(body, new UniverseTime(s.Universe), D(s.PositionCci), D(s.VelocityCci), v.OrbitColor);
            StateVectors now = said.GetStateVectorsAt(new UniverseTime(Universe));
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

    /// <summary>Called as a craft's physics step is prepared, for every craft.</summary>
    public static void PointAsAsked(Vehicle v)
    {
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
    public static double Vec(double3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
}
