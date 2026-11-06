using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// Commands from outside the game, read from <c>Logs/bridge/KSAMultiplayer/in</c> and answered in
/// <c>out</c>: what lets an agent in a terminal join a server, fly a named craft, step the world and
/// photograph it without a hand on the keyboard. <c>tools/ksa-mcp/server.py</c> is the other end, and
/// <c>tools/two-games.sh</c> runs two games with a profile each so one machine can be both players.
/// Only a developer's install starts it (<see cref="Build.Developer"/>).
///
/// <para><b>Files, not a socket</b>: a folder needs no port, works across the WSL boundary as it is,
/// and cannot be reached by the server this mod already talks UDP to.</para>
///
/// <para>One command at a time, oldest first. A command that takes frames -- a step, a capture, a
/// load -- holds the queue until it answers, so a sequence of them is a script that runs in order.</para>
/// </summary>
internal sealed class Bridge
{
    // A few times a second is quick enough for a person and costs one directory listing.
    private const double PollSeconds = 0.1;

    // How many frames KSA's own capture hides the interface for before the picture.
    private const int WarmFrames = 24;

    private double _sincePoll;
    private Func<double, Reply?>? _running;
    private BridgeCommand? _current;

    // A folder of its own, under the profile KSA was started with: every mod built from this tooling
    // has a bridge, and two reading one folder each answer whichever command they reach first.
    private static string Root => Path.Combine(KSA.Constants.DocumentsFolderPath, "Logs", "bridge", "KSAMultiplayer");
    private static string Inbox => Path.Combine(Root, "in");
    private static string Outbox => Path.Combine(Root, "out");

    private readonly record struct Reply(bool Ok, string Error, Dictionary<string, object?> Data);

    private static Reply Done(Dictionary<string, object?>? data = null) => new(true, string.Empty, data ?? []);
    private static Reply Failed(string why) => new(false, why, []);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// One frame: advances the command running, or takes the next. From the frame hook, which KSA
    /// always calls, so it runs in the menu and with the interface hidden. Nothing here may throw.
    /// </summary>
    public void Update(double dtPlayer)
    {
        try
        {
            if (_running is not null && _current is not null)
            {
                if (_running(dtPlayer) is { } reply) Answer(_current, reply);
                return;
            }

            _sincePoll += dtPlayer;
            if (_sincePoll < PollSeconds) return;
            _sincePoll = 0.0;

            if (!Directory.Exists(Inbox)) return;
            if (Directory.GetFiles(Inbox, "*.json").Order(StringComparer.Ordinal).FirstOrDefault() is not { } next) return;

            string text = File.ReadAllText(next);
            File.Delete(next);

            if (!BridgeCommand.TryParse(text, out BridgeCommand? command, out string trouble))
            {
                Log.Warn($"bridge: refused {Path.GetFileName(next)} -- {trouble}");
                return;
            }
            Start(command!);
        }
        catch (Exception e)
        {
            if (_current is { } command) Answer(command, Failed($"threw: {e.Message}"));
            else Log.Warn($"bridge: {e.Message}");
        }
    }

    private void Start(BridgeCommand command)
    {
        _current = command;
        Log.Info($"bridge: {command.Name} ({command.Id})");

        Reply? now = command.Name switch
        {
            "status" => Status(),
            "craft" => CraftList(),
            "mp" => Multiplayer(command),
            "goto" => GoTo(command),
            "watch" => Watch(command),
            "eva" => Eva(command),
            "stage" => Stage(command),
            "setorbit" => SetOrbit(command),
            "engine" => Engine(command),
            "pause" => Paused(true),
            "resume" => Paused(false),
            "speed" => Speed(command),
            "warpkey" => WarpKey(command),
            "save" => SaveGame(command),
            "step" => BeginStep(command),
            "load" => BeginLoad(command),
            "capture" => BeginCapture(command),
            _ => Failed($"no command '{command.Name}'"),
        };

        if (now is { } reply) Answer(command, reply);
    }

    private void Answer(BridgeCommand command, Reply reply)
    {
        _running = null;
        _current = null;
        Directory.CreateDirectory(Outbox);

        Dictionary<string, object?> body = new()
        {
            ["id"] = command.Id,
            ["cmd"] = command.Name,
            ["ok"] = reply.Ok,
            ["error"] = reply.Ok ? null : reply.Error,
            ["data"] = reply.Data,
        };

        // Written aside and moved, so the other end never reads half a reply.
        string final = Path.Combine(Outbox, command.Id + ".json");
        File.WriteAllText(final + ".part", JsonSerializer.Serialize(body, JsonOptions));
        File.Move(final + ".part", final, overwrite: true);

        if (!reply.Ok) Log.Warn($"bridge: {command.Name} ({command.Id}) failed -- {reply.Error}");
    }

    // ---- what this game is doing ------------------------------------------------------------

    private static Reply Status()
    {
        Dictionary<string, object?> data = new()
        {
            ["ksa"] = World.KsaBuild,
            ["protocol"] = Wire.Version,
            ["in_flight"] = World.InFlight,
            ["craft"] = World.Controlled?.Id,
            ["watching"] = World.Watched?.Id,
            ["map_view"] = World.InMapView,
            ["can"] = Math.Round(Sync.Sustains, 3),
            ["here"] = World.Focused,
            ["achieved"] = Math.Round(World.Achieved, 3),
            ["analytic"] = World.Analytic,
            ["tag_draw"] = Panel.TagReport,
            // What the nametag pass would draw this frame, so a tag that never appears can be told
            // from one drawn somewhere nobody looks.
            ["nametags"] = Sync.Nametags().Select(t => (object?)new Dictionary<string, object?>
            {
                ["craft"] = t.Craft,
                ["who"] = t.Who,
                ["range_m"] = Math.Round(t.Range, 1),
                ["flying"] = t.Flying,
                ["x"] = Math.Round(t.At.X, 1),
                ["y"] = Math.Round(t.At.Y, 1),
            }).ToList(),
            ["screen"] = World.ScreenSize is { } size ? new Dictionary<string, object?> { ["w"] = size.X, ["h"] = size.Y } : null,
            ["speed"] = Math.Round(World.Speed, 4),
            ["universe"] = Math.Round(World.Universe, 2),
            ["stage"] = Sync.Now.ToString(),
            ["said"] = Sync.Said.ToList(),
            ["chat"] = Sync.Chat.Select(l => (object?)l.Text).ToList(),
        };

        if (Sync.Client is { } c)
        {
            data["server"] = c.ServerName;
            data["address"] = Sync.ServerAddress;
            data["player_id"] = c.Id;
            data["rtt_ms"] = c.ClockKnown ? Math.Round(c.Rtt * 1000.0, 1) : null;
            data["behind_s"] = Sync.Behind is { } b ? Math.Round(b, 2) : null;
            data["held_speed"] = Sync.HeldSpeed;
            data["admin"] = c.Admin;
            data["players"] = c.Players.Select(p => (object?)new Dictionary<string, object?>
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["loaded"] = p.Loaded,
                ["flying"] = p.Controlling,
                ["can"] = Math.Round(p.Can, 3),
            }).ToList();
            // Who the server says flies what, against what this game is doing about it: the one
            // table that says whether a craft should be a puppet here and whether it is one.
            data["owners"] = c.Owners.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => (object?)new Dictionary<string, object?>
            {
                ["craft"] = o.Key,
                ["owner"] = o.Value,
                ["name"] = c.NameOf(o.Value),
                ["mine"] = o.Value == c.Id,
                ["here"] = World.Vehicles().Any(v => v.Id == o.Key),
                ["puppet"] = Sync.PuppetCraft.Contains(o.Key),
                ["heard_s"] = c.Tracked.Contains(o.Key) ? Math.Round(World.Universe - c.TrackOf(o.Key).HeardAt, 2) : null,
            }).ToList();
            data["owned_here"] = Sync.OwnedCraft.Order(StringComparer.Ordinal).ToList();
            data["puppets_here"] = Sync.PuppetCraft.Order(StringComparer.Ordinal).ToList();
            data["renamed"] = Sync.RenamedCraft.Order(StringComparer.Ordinal).ToList();
            data["arriving"] = c.Arriving;
            data["warp"] = c.Warp is { } w
                ? new Dictionary<string, object?>
                {
                    ["who"] = c.NameOf(w.Who),
                    ["speed"] = Math.Round(w.Speed, 3),
                    ["phase"] = w.Phase.ToString(),
                    ["waiting"] = w.Waiting.Select(c.NameOf).ToList(),
                }
                : null;
        }

        return Done(data);
    }

    // Every craft in this world, whatever the server knows of it: what a test looks at to see
    // whether a puppet was built, and where it ended up.
    private static Reply CraftList()
    {
        Vehicle? flown = World.Controlled;
        List<object?> all = [];
        foreach (Vehicle v in World.Vehicles())
        {
            string key = Sync.KeyOf(v);
            all.Add(new Dictionary<string, object?>
            {
                ["id"] = v.Id,
                // What everyone calls it, which differs from this world's name when the server renamed it.
                ["key"] = key == v.Id ? null : key,
                ["parent"] = (v.Parent as Astronomical)?.Id,
                ["situation"] = v.Situation.ToString(),
                ["parts"] = v.Parts.Count,
                ["physics"] = v.HasPhysicsBubble,
                ["flown"] = ReferenceEquals(v, flown),
                ["watched"] = ReferenceEquals(v, World.Watched),
                ["puppet"] = Sync.IsPuppet(v),
                ["owned"] = Sync.OwnedCraft.Contains(key),
                ["from_flown_m"] = flown is null || ReferenceEquals(v, flown) ? null : Math.Round(World.Between(flown, v), 1),
                // What this game would draw this craft's engines from, and whether that number is
                // KSA's own or the mod's: the whole of whether another player is seen to burn.
                ["engine_duty"] = Math.Round(World.DutyOf(v), 3),
                ["engine_drawn_here"] = World.IsShown(v),
            });
        }
        return Done(new() { ["craft"] = all });
    }

    // ---- the session -----------------------------------------------------------------------

    // Joining a server and leaving it, which the panel's own buttons do: what makes a second game a
    // second player without a hand on it.
    private static Reply Multiplayer(BridgeCommand command)
    {
        if (command.Flag("disconnect", false))
        {
            Sync.Disconnect();
            return Done(new() { ["stage"] = Sync.Now.ToString() });
        }

        if (command.String("connect") is { Length: > 0 } address)
        {
            string name = command.String("name");
            Sync.Connect(address, name.Length > 0 ? name : "Kitten", command.String("password"));
            return Done(new() { ["stage"] = Sync.Now.ToString(), ["address"] = address });
        }

        // A warp is asked for and answered through the panel; these are the same two things, for a
        // test that has no hands.
        if (command.Has("warp"))
        {
            if (Sync.Client is not { Joined: true } c) return Failed("not connected");
            c.AskWarp(command.Number("warp", 1.0));
            return Done(new() { ["asked"] = command.Number("warp", 1.0) });
        }

        if (command.Has("vote"))
        {
            if (Sync.Client is not { Joined: true } c) return Failed("not connected");
            c.VoteWarp(command.Flag("vote", true));
            return Done(new() { ["voted"] = command.Flag("vote", true) });
        }

        // The editor, which only a hand on the keyboard could open before: the mod behaves
        // differently while it is up -- it holds no speed and hands over no world -- and none of
        // that could be driven by a run.
        // Pressing the editor's Launch button, which is the one thing about launching that could not
        // be driven from here -- and so the one thing about it that kept being shipped unwatched.
        if (command.Has("launch"))
        {
            if (KSA.Program.Editor is not { } editor) return Failed("the vehicle editor is not open");
            // Past the unsaved-work question, which a run cannot answer: this is what clicking
            // through it calls. The mod's own refusal sits on this method, so a full site still
            // stops it.
            if (HarmonyLib.AccessTools.Method(typeof(KSA.VehicleEditor), "FinalizeExit") is not { } finish)
            {
                return Failed("KSA no longer has FinalizeExit");
            }
            finish.Invoke(editor, [true]);
            return Done(new() { ["asked"] = true, ["still_open"] = KSA.Program.IsEditorOpen });
        }

        // What the pad picker can see: every step of it, so a wrong answer says which step was wrong
        // rather than leaving it to be inferred from what did or did not end up in the log.
        if (command.Has("pads"))
        {
            Dictionary<string, object?> said = new() { ["editor_open"] = KSA.Program.IsEditorOpen };
            if (KSA.Program.Editor is { } open)
            {
                (KSA.Celestial? body, string site) = Pads.Chosen(open);
                said["chosen_body"] = body?.Id;
                said["chosen_site"] = site;
                said["room"] = Pads.Room(open);
                if (body?.BodyTemplate is { } t && site.Length > 0)
                {
                    said["pads_at_site"] = Pads.At(t, site).Select(p => (object?)p.Id).ToList();
                    said["free_pad"] = Pads.Free(body, site)?.Id;
                }
            }
            return Done(said);
        }

        if (command.Has("editor"))
        {
            bool want = command.Flag("editor", true);
            KSA.Program.EditorFlag = want;
            return Done(new() { ["editor"] = want, ["open"] = KSA.Program.IsEditorOpen });
        }

        if (command.Has("board"))
        {
            Panel.BoardUntil = Sync.Wall() + Math.Clamp(command.Number("board", 3.0), 0.0, 60.0);
            return Done(new() { ["board_for_s"] = Math.Round(Panel.BoardUntil - Sync.Wall(), 1) });
        }

        // Through the same door as the keyboard, / and all, so what a run drives is what a player does.
        if (command.String("chat") is { Length: > 0 } text)
        {
            if (Sync.Client is null) return Failed("not connected");
            return Done(new() { ["asked"] = Sync.Typed(text) });
        }

        return Status();
    }

    // Flying a named craft, so each of two games flies a different one out of the same world.
    private static Reply GoTo(BridgeCommand command)
    {
        string id = command.String("craft");
        if (id.Length == 0) return Failed("goto needs a craft");
        if (World.Vehicles().FirstOrDefault(v => v.Id == id) is not { } v) return Failed($"no craft '{id}' in this world");
        if (World.GoTo(v)) return Done(new() { ["craft"] = v.Id, ["flying"] = true });
        // Another player's: the camera still goes there, which is all anyone can do with it.
        return World.Watch(v)
            ? Done(new() { ["craft"] = v.Id, ["flying"] = false, ["owner"] = Sync.OwnerOf(v) })
            : Failed($"could not fly or watch '{id}'");
    }

    // The camera on a craft without taking it: how another player's is seen close up.
    private static Reply Watch(BridgeCommand command)
    {
        string id = command.String("craft");
        if (id.Length == 0) return Failed("watch needs a craft");
        if (World.Vehicles().FirstOrDefault(v => v.Id == id) is not { } v) return Failed($"no craft '{id}' in this world");
        return World.Watch(v)
            ? Done(new() { ["craft"] = v.Id, ["owner"] = Sync.OwnerOf(v) })
            : Failed($"could not watch '{id}'");
    }

    // A kitten out of a craft's hatch: the shortest way to make a craft that the loaded world did not
    // have, and the way two games come to make one name for two craft.
    private static Reply Eva(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");

        string who = command.String("kitten");
        IVASeat? seat = null;
        foreach (IVASeat s in craft.Parts.Modules.Get<IVASeat>())
        {
            if (s.AssignedKittenHash == KeyHash.Zero) continue;
            if (who.Length > 0 && KSA.Universe.KittenRoster.Find(s.AssignedKittenHash) is { } k && k.Name != who) continue;
            seat = s;
            break;
        }
        if (seat is null) return Failed($"nobody aboard {craft.Id} to get out");
        if (!EVADoor.PerformEvaForSeat(craft, seat)) return Failed($"{craft.Id} would not let anybody out");
        return Done(new() { ["from"] = craft.Id });
    }

    // A warp key, which is not the same as asking for a speed: the key says which way, and while
    // connected the mod works out the speed from the warp the server is running.
    private static Reply WarpKey(BridgeCommand command)
    {
        int by = Math.Sign((int)command.Number("by", 1.0));
        World.WarpKey(by);
        return Done(new() { ["by"] = by, ["asked"] = Sync.AskedSpeed });
    }

    // The space bar: a rocket staged while the other game watches, which is the one thing a test of
    // many parts needs and no other command can do.
    private static Reply Stage(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (World.Stage(craft) is not { } sequence) return Failed($"{craft.Id} has no sequence left to fire");
        return Done(new() { ["craft"] = craft.Id, ["sequence"] = sequence, ["parts"] = craft.Parts.Count });
    }

    // The debug menu's Set Orbit window, which is the only way a craft changes the body it is going
    // round without flying there: what a test of whether the others follow it needs.
    private static Reply SetOrbit(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        string body = command.String("body");
        if (body.Length == 0) return Failed("which body to go round");
        double altitude = command.Number("altitude", 100_000.0);
        if (World.SetOrbit(craft, body, altitude) is { } why) return Failed(why);
        return Done(new() { ["craft"] = craft.Id, ["body"] = body, ["altitude"] = altitude });
    }

    // A craft's engine lit or shut, which is the ignition key and the throttle: for a test that has
    // to make a craft burn with no hand on the keyboard. What this writes is what a player's own key
    // writes, so the craft burns here for real and is said to be burning to everyone else.
    private static Reply Engine(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        bool on = command.Flag("on", true);
        float throttle = (float)command.Number("throttle", 1.0);
        World.SetEngine(craft, on, on ? Math.Clamp(throttle, 0f, 1f) : 0f);
        (bool lit, float open) = World.EngineOf(craft);
        return Done(new() { ["craft"] = craft.Id, ["on"] = lit, ["throttle"] = Math.Round(open, 3), ["duty"] = Math.Round(World.DutyOf(craft), 3) });
    }

    // ---- the clock -------------------------------------------------------------------------

    // Pausing and the speed go through Universe.SetSimulationSpeed, which this mod holds to the
    // server's while connected (Hooks.BeforeSpeed): so these say so rather than appear to work.
    private static Reply Paused(bool paused) => Speed(paused ? 0.0 : 1.0);

    private static Reply Speed(BridgeCommand command) => Speed(command.Number("x", 1.0));

    private static Reply Speed(double speed)
    {
        if (Sync.HeldSpeed is { } held)
        {
            return Failed($"the server's clock holds this world at {held:F3}x: the speed is not this game's while it is connected");
        }
        try
        {
            KSA.Universe.SetSimulationSpeed(new SimSpeed(speed));
            return Done(new() { ["speed"] = World.Speed });
        }
        catch (Exception e)
        {
            return Failed($"the speed was refused: {e.Message}");
        }
    }

    private Reply? BeginStep(BridgeCommand command)
    {
        double seconds = command.Number("seconds", 1.0);
        if (!(seconds > 0.0)) return Failed("seconds must be positive");
        if (Sync.HeldSpeed is not null) return Failed("the server's clock runs this world: it cannot be stepped while connected");

        double advanced = 0.0, waited = 0.0, before = World.Universe;
        if (Speed(1.0) is { Ok: false } refused) return refused;

        _running = dtPlayer =>
        {
            waited += dtPlayer;
            advanced = World.Universe - before;
            if (advanced >= seconds)
            {
                Speed(0.0);
                return Done(new() { ["advanced_s"] = Math.Round(advanced, 3) });
            }
            return waited > (seconds * 20.0) + 30.0 ? Failed($"only {advanced:F2} s passed") : null;
        };
        return null;
    }

    private Reply? BeginLoad(BridgeCommand command)
    {
        string save = command.String("save");
        if (save.Length == 0) return Failed("load needs a save");
        try
        {
            GameSaves.Refresh();
            GameSaves.LoadSaveGame(save);
        }
        catch (Exception e)
        {
            return Failed($"could not load '{save}': {e.Message}");
        }

        double waited = 0.0;
        _running = dtPlayer =>
        {
            waited += dtPlayer;
            // A beat past the craft appearing, for the world to settle round it.
            if (waited > 3.0 && World.InFlight) return Done(new() { ["craft"] = World.Controlled!.Id });
            return waited > 60.0 ? Failed("no craft after 60 s") : null;
        };
        return null;
    }

    private static Reply SaveGame(BridgeCommand command)
    {
        string name = command.String("name");
        if (name.Length == 0) return Failed("save needs a name");
        try
        {
            GameSaves.MakeUncompressedSave(name);
            GameSaves.Refresh();
            return Done(new() { ["save"] = name, ["universe"] = Math.Round(World.Universe, 2) });
        }
        catch (Exception e)
        {
            return Failed($"could not save '{name}': {e.Message}");
        }
    }

    // ---- pictures --------------------------------------------------------------------------

    // Photographs through KSA's own capture, which names files to the second: so each one is moved
    // aside and renamed before the next is asked for, and two can never be one file.
    private Reply? BeginCapture(BridgeCommand command)
    {
        string label = command.String("label");
        if (label.Length == 0) label = "shot";
        label = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

        int frames = Math.Clamp((int)command.Number("frames", 1.0), 1, 60);
        int everyFrames = Math.Max((int)command.Number("every_frames", 1.0), 1);
        // KSA hides the interface for the warm-up frames before a picture, so a shot of the world is
        // clean; "ui" keeps it, which is the only way to see the mod's own window in a picture.
        bool keepUi = command.Flag("ui", false);
        string shotFlags = keepUi ? "ui" : $"warm={WarmFrames}";
        int warmFor = keepUi ? 0 : WarmFrames;

        string folder = Path.Combine(Outbox, command.Id);
        Directory.CreateDirectory(folder);
        string shots = Path.Combine(KSA.Constants.DocumentsFolderPath, "exports", "screenshots");

        List<Dictionary<string, object?>> taken = [];
        int index = 0, sinceFrames = 0, warming = 0;
        bool waiting = false;
        double waited = 0.0;
        DateTime askedAt = default;
        Dictionary<string, object?>? manifest = null;

        _running = dtPlayer =>
        {
            if (!waiting)
            {
                if (index > 0 && ++sinceFrames < everyFrames) return null;
                askedAt = DateTime.UtcNow.AddSeconds(-0.5);
                if (!TryRequestScreenshot(shotFlags)) return Failed("KSA would not take a screenshot");
                (manifest, warming, waiting, waited) = (null, warmFor, true, 0.0);
                return null;
            }

            // Written on the frame the picture is actually taken, so it describes that instant.
            if (manifest is null && warming-- <= 0) manifest = Manifest(label, index);

            waited += dtPlayer;
            if (waited > 10.0) return Failed($"screenshot {index} never arrived");
            if (!Directory.Exists(shots)) return null;

            string? file = new DirectoryInfo(shots).GetFiles("ksa_*.png")
                .Where(f => f.LastWriteTimeUtc >= askedAt && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            if (file is null) return null;

            string name = $"{index:D2}-{label}";
            string target = Path.Combine(folder, name + ".png");
            try
            {
                File.Move(file, target, overwrite: true);
            }
            catch (IOException)
            {
                // Still being written; the next frame will find it finished.
                return null;
            }

            manifest ??= Manifest(label, index);
            manifest["file"] = target;
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(manifest, JsonOptions));
            taken.Add(manifest);

            index++;
            (waiting, sinceFrames) = (false, 0);
            return index >= frames ? Done(new() { ["folder"] = folder, ["frames"] = taken }) : null;
        };
        return null;
    }

    private static Dictionary<string, object?> Manifest(string label, int index) => new()
    {
        ["label"] = label,
        ["index"] = index,
        ["wall"] = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        ["universe"] = Math.Round(World.Universe, 2),
        ["speed"] = Math.Round(World.Speed, 4),
        ["stage"] = Sync.Now.ToString(),
        ["craft"] = World.Controlled?.Id,
        ["puppets"] = Sync.PuppetCraft.Order(StringComparer.Ordinal).ToList(),
    };

    private static ScreenshotCapture? _shots;
    private static bool _lookedForShots, _warnedAboutShots;

    // KSA keeps its capture private on Program; there is no public way to ask for a screenshot.
    private static bool TryRequestScreenshot(string flags)
    {
        try
        {
            if (!_lookedForShots)
            {
                _lookedForShots = true;
                _shots = typeof(Program).GetField("_screenshotCapture", BindingFlags.NonPublic | BindingFlags.Instance)
                                        ?.GetValue(Program.Instance) as ScreenshotCapture;
                if (_shots is null)
                {
                    _warnedAboutShots = true;
                    Log.Warn("bridge: Program._screenshotCapture did not resolve; captures will write nothing");
                }
            }
            if (_shots is null) return false;
            _shots.Request(1, flags);
            return true;
        }
        catch (Exception e)
        {
            if (!_warnedAboutShots)
            {
                _warnedAboutShots = true;
                Log.Warn($"bridge: screenshots are not available: {e.Message}");
            }
            return false;
        }
    }
}
