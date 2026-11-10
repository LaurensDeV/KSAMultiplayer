using Brutal.Numerics;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // How long a game begun from the configuration at start has to bring a world up. Generous: it is
    // a cold load of a system, and two games on one machine take turns over the disk.
    private const double StartWithinSeconds = 180.0;

    // How many frames KSA draws of the world Start makes before a save is read over it. One is
    // what was seen to be enough; the rest is margin, and a quarter of a second.
    private const ulong StartedFrames = 10;

    private double _sincePoll;
    private Func<double, Reply?>? _running;
    private BridgeCommand? _current;

    // ---- the one bridge, and which hook serves it ------------------------------------------

    private static Bridge? _open;
    private static bool _framed;
    private static readonly Stopwatch Ticking = Stopwatch.StartNew();
    private static double _ticked;

    /// <summary>
    /// Opens the bridge, on a developer's install. From the immediate load and not from
    /// <c>AllModsLoaded</c>, because KSA's configuration at start comes before StarMap fires that:
    /// a bridge made there does not exist while the start window is up, which is the one window a
    /// tool has to get past, so the rig sat waiting for a bridge that only a hand on the mouse
    /// could bring about.
    /// </summary>
    public static void Open()
    {
        if (!Build.Developer || _open is not null) return;
        _open = new Bridge();
        Log.Info("developer install: the bridge is listening");
    }

    public static void Close() => _open = null;

    /// <summary>One frame, from StarMap's frame hook, which owns the bridge once there is one.</summary>
    public static void ServeFrame(double dtPlayer)
    {
        _framed = true;
        _open?.Update(dtPlayer);
        if (_open is not null)
        {
            FlyDown();
            FlyToPort();
        }
    }

    /// <summary>
    /// One frame from the start window, which is drawn by a prefix on KSA's popups and so runs while
    /// this mod has no frame hook at all. It gives way the moment the frame hook runs, so no frame is
    /// ever served twice, and it keeps a clock of its own because a draw pass is handed no dt.
    ///
    /// <para>What runs here runs in the pass that draws KSA's own menu buttons, which is where the
    /// game already loads a save when somebody presses Load: the same work in the same place, asked
    /// for by a file instead of a mouse.</para>
    /// </summary>
    public static void ServeStartWindow()
    {
        if (_framed || _open is null) return;
        double now = Ticking.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _ticked, 0.0, 0.25);
        _ticked = now;
        _open.Update(dt);
    }

    // A folder of its own, under the profile KSA was started with: every mod built from this tooling
    // has a bridge, and two reading one folder each answer whichever command they reach first.
    private static string Root => Path.Combine(KSA.Constants.DocumentsFolderPath, "Logs", "bridge", "KSAMultiplayer");
    private static string Inbox => Path.Combine(Root, "in");
    private static string Outbox => Path.Combine(Root, "out");

    private readonly record struct Reply(bool Ok, string Error, Dictionary<string, object?> Data);

    private static Reply Done(Dictionary<string, object?>? data = null) => new(true, string.Empty, data ?? []);
    private static Reply Failed(string why) => new(false, why, []);

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            Converters = { new FiniteOnly(), new FiniteOrNothing(), new FiniteFloat(), new FiniteFloatOrNothing() },
        };

    /// <summary>
    /// A distance or a rate that could not be worked out, written as null rather than as a number
    /// JSON has no spelling for.
    ///
    /// <para>This mod says "cannot tell" with infinity -- <see cref="World.Between"/> and
    /// <see cref="World.RangeFromCamera"/> both do -- and <c>JsonSerializer</c> refuses to write one
    /// at all. It throws partway through, so a single unmeasurable distance anywhere in a reply did
    /// not spoil a field: it destroyed the whole reply, and the command answered nothing until it
    /// timed out. <c>status</c> went dead in a joined game the moment a puppet was out of the
    /// camera's reach, which is the one instrument a run has.</para>
    /// </summary>
    private sealed class FiniteOnly : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TryGetDouble(out double d) ? d : 0.0;

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }

    private sealed class FiniteOrNothing : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.TryGetDouble(out double d) ? d : null;

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value is { } d && double.IsFinite(d)) writer.WriteNumberValue(d);
            else writer.WriteNullValue();
        }
    }

    // The same for the floats, which reach a reply straight out of the engine's own vectors.
    private sealed class FiniteFloat : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TryGetSingle(out float f) ? f : 0f;

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (float.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }

    private sealed class FiniteFloatOrNothing : JsonConverter<float?>
    {
        public override float? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.TryGetSingle(out float f) ? f : null;

        public override void Write(Utf8JsonWriter writer, float? value, JsonSerializerOptions options)
        {
            if (value is { } f && float.IsFinite(f)) writer.WriteNumberValue(f);
            else writer.WriteNullValue();
        }
    }

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
            "design" => Design(command),
            "fail" => Fail(command),
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
            "modspeed" => ModSpeed(command),
            "pickspeed" => PickSpeed(command),
            "warpkey" => WarpKey(command),
            "save" => SaveGame(command),
            "step" => BeginStep(command),
            "start" => BeginStart(command),
            "load" => BeginLoad(command),
            "capture" => BeginCapture(command),
            "motion" => RecordMotion(command),
            "move" => Move(command),
            "put" => Put(command),
            "attitude" => Attitude(command),
            "land" => Land(command),
            "dock" => Dock(command),
            "approach" => Approach(command),
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
            ["loaded"] = World.Loaded,
            ["starting"] = Panel.Starting,
            // The time of day this was answered at, which is the one clock this game, the other
            // game and a server on the same machine all read. A tool gets its answer a file and a
            // poll later, and at 1000x that delay is a hundred universe-seconds of not knowing when
            // the universe time below was true.
            ["wall"] = Math.Round((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds, 4),
            // What is standing over the game. A picture is taken with the interface hidden, so a
            // popup never shows up in one: this is the only way a tool can see it is being blocked.
            ["popups"] = World.Popups,
            ["stage"] = Sync.Now.ToString(),
            ["said"] = Sync.Said.ToList(),
            ["chat"] = Sync.Chat.Select(l => (object?)l.Text).ToList(),
        };

        // Everything past here wants a world: a viewport to measure, a camera to ask, a universe to
        // time. The configuration popup at start has none of them, and asking anyway threw
        // "[ViewportRegistry] Main viewport is not set yet" -- so a tool found the bridge dead at
        // the one window it has to get the game past. What is above is true in any state.
        if (!World.Loaded) return Done(data);

        data["in_flight"] = World.InFlight;
        data["at_a_craft"] = World.AtACraft;
        data["craft"] = World.Controlled?.Id;
        data["watching"] = World.Watched?.Id;
        data["map_view"] = World.InMapView;
        data["can"] = Math.Round(Sync.Sustains, 3);
        data["here"] = World.Focused;
        data["achieved"] = Math.Round(World.Achieved, 3);
        data["analytic"] = World.Analytic;
        data["tag_draw"] = Panel.TagReport;
        // What the nametag pass would draw this frame, so a tag that never appears can be told
        // from one drawn somewhere nobody looks.
        data["nametags"] = Sync.Nametags().Select(t => (object?)new Dictionary<string, object?>
        {
            ["craft"] = t.Craft,
            ["who"] = t.Who,
            ["range_m"] = Math.Round(t.Range, 1),
            ["flying"] = t.Flying,
            ["x"] = Math.Round(t.At.X, 1),
            ["y"] = Math.Round(t.At.Y, 1),
        }).ToList();
        data["screen"] = World.ScreenSize is { } size ? new Dictionary<string, object?> { ["w"] = size.X, ["h"] = size.Y } : null;
        data["speed"] = Math.Round(World.Speed, 4);
        data["universe"] = Math.Round(World.Universe, 2);

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
                // By the name the room shares and not by this world's own: a craft built here
                // under a free name when the one on the wire was taken answers to neither map
                // under its local id, and read that way the server's whole roster looked absent.
                ["here"] = World.Vehicles().Any(v => Sync.KeyOf(v) == o.Key),
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
                ["radar_m"] = Math.Round(World.Descent(v).Radar, 2),
                ["up_m_s"] = Math.Round(World.Descent(v).Up, 2),
                ["ports"] = World.FreePorts(v),
                // What this game would draw this craft's engines from, and whether that number is
                // KSA's own or the mod's: the whole of whether another player is seen to burn.
                ["engine_duty"] = Math.Round(World.DutyOf(v), 3),
                ["engine_drawn_here"] = World.IsShown(v),
            });
        }
        return Done(new() { ["craft"] = all });
    }

    /// <summary>
    /// Parts blown off a craft, or a craft crashed, through the same event KSA's own G-load path
    /// raises. The only way to reach a part failure on the ground, where <c>PhysicsBubble</c> stands
    /// that path down -- and so the only way a run can watch this mod's guard refuse one on another
    /// player's craft, which is the failure two players reported from the live server.
    /// </summary>
    private static Reply Fail(BridgeCommand command)
    {
        if (!World.Loaded) return Failed("there is no world yet");
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        bool whole = command.Flag("whole", false);
        int parts = (int)command.Number("parts", 1.0);
        Sync.FailLater(craft, parts, whole);
        // Asked, not done: it is applied in the one window of the frame where the world's vehicles
        // may change, so what came of it is read from the next `craft` dump and from KSA's own log.
        return Done(new()
        {
            ["craft"] = craft.Id,
            ["asked_parts"] = Math.Max(parts, 1),
            ["whole"] = whole,
            ["was_puppet"] = Sync.IsPuppet(craft),
            ["had_parts"] = craft.Parts.Count,
        });
    }

    /// <summary>
    /// The vehicle editor's designs: what there is, one written out of this world, one put into the
    /// editor. With <c>mp {"editor":true}</c> to open it and <c>mp {"launch":true}</c> to press
    /// Launch, that is a craft built and flown without a hand on the mouse -- the path this project
    /// has shipped broken three times and never once watched.
    /// </summary>
    private static Reply Design(BridgeCommand command)
    {
        if (!World.Loaded) return Failed("there is no world yet");

        if (command.String("save") is { Length: > 0 } craft)
        {
            if (World.SaveDesign(craft) is { Length: > 0 } no) return Failed(no);
            return Done(new() { ["saved"] = craft, ["designs"] = World.Designs() });
        }

        if (command.String("load") is { Length: > 0 } name)
        {
            if (World.LoadDesign(name) is { Length: > 0 } no) return Failed(no);
            return Done(Editing(new() { ["loaded"] = name }));
        }

        return Done(Editing(new() { ["designs"] = World.Designs() }));
    }

    // What the editor holds, said the same way whichever of the three forms asked: a load that
    // answered ok and left nothing in the editor is the failure worth seeing, and Launch is silent
    // about an empty one.
    private static Dictionary<string, object?> Editing(Dictionary<string, object?> said)
    {
        said["editor_open"] = KSA.Program.IsEditorOpen;
        said["editing"] = World.Editing is { } e ? e.Id : null;
        said["editing_parts"] = World.Editing is { } p ? p.Parts : null;
        return said;
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
                    said["pads_at_site"] = Pads.At(t, site).Select(p => (object?)p.Key).ToList();
                    said["free_pad"] = Pads.Free(body, site)?.Key;
                }
            }
            return Done(said);
        }

        if (command.Has("editor"))
        {
            bool want = command.Flag("editor", true);
            // "blank" is KSA's own Build New Vehicle: without it the editor opens on the craft being
            // flown and has nothing to launch, so a game already in flight could not be driven into
            // one. Letting go of the craft is part of what that menu item does.
            bool blank = want && command.Flag("blank", false) && World.EditBlank();
            KSA.Program.EditorFlag = want;
            return Done(new() { ["editor"] = want, ["blank"] = blank, ["open"] = KSA.Program.IsEditorOpen });
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
        // The camera on a planet, as a click on one in the map puts it: KSA lets go of the craft
        // being flown when it does, which is a game that is at no craft at all.
        if (command.String("body") is { Length: > 0 } body)
        {
            return World.WatchBody(body)
                ? Done(new() { ["body"] = body, ["craft"] = World.Controlled?.Id, ["at_a_craft"] = World.AtACraft })
                : Failed($"could not put the camera on '{body}'");
        }
        string id = command.String("craft");
        if (id.Length == 0) return Failed("watch needs a craft or a body");
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
        // Queued is the one answer that sent anybody out; the rest say why not, and are worth saying.
        if (EVADoor.PerformEvaForSeat(craft, seat) is var asked && asked != EvaRequest.Queued)
        {
            return Failed($"{craft.Id} would not let anybody out: {asked}");
        }
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

    // The translation keys, held or let go: a kitten walked across the ground, or a craft pushed
    // sideways on its thrusters, with no hand on the keyboard. Held until told otherwise.
    private static Reply Move(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (Sync.IsPuppet(craft)) return Failed($"{craft.Id} is another player's");
        bool stop = command.Flag("stop", false);
        int forward = stop ? 0 : (int)command.Number("forward", 0.0), right = stop ? 0 : (int)command.Number("right", 0.0);
        int up = stop ? 0 : (int)command.Number("up", 0.0);
        if (!World.Move(craft, forward, right, up, !stop && command.Flag("sprint", false))) return Failed("KSA no longer keeps a craft's keys where this looks");
        return Done(new() { ["craft"] = craft.Id, ["forward"] = forward, ["right"] = right, ["up"] = up, ["situation"] = craft.Situation.ToString() });
    }

    // A craft put beside another, or lifted off the ground and let go: the only way two games get
    // two craft into one place, or a craft into the air, without somebody flying it there.
    private static Reply Put(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (Sync.IsPuppet(craft)) return Failed($"{craft.Id} is another player's");
        string beside = command.String("beside");
        Vehicle? other = beside.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == beside) : craft;
        if (other is null) return Failed($"no craft '{beside}' in this world");
        double metres = command.Number("metres", 50.0);
        // In front of its docking port and not above it: where an approach starts from.
        if ((command.Flag("port", false) ? World.PutBeforePort(craft, other, metres) : World.PutBeside(craft, other, metres)) is { } why) return Failed(why);
        return Done(new() { ["craft"] = craft.Id, ["beside"] = other.Id, ["metres"] = metres });
    }

    // The flight computer told what to point a craft at, as its buttons tell it: up, for a craft
    // coming down on its engine, which no test can steer by hand.
    private static Reply Attitude(BridgeCommand command)
    {
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (Sync.IsPuppet(craft)) return Failed($"{craft.Id} is another player's");
        // With nothing to point at, what the flight computer is doing about it now.
        if (!command.Has("track")) return Done(World.Pointing(craft));
        string track = command.String("track");
        if (World.Track(craft, track) is { } why) return Failed(why);
        return Done(new() { ["craft"] = craft.Id, ["track"] = track.Length > 0 ? track : "none" });
    }

    // A craft brought down on its engine. KSA holds a throttle to the engine's minimum, which is
    // full for the engines a test has, so the engine is lit and shut instead: lit whenever the
    // craft is falling faster than its height allows. With "climb" it is flown up that far first.
    // With nothing it answers how the craft being brought down is doing.
    private static Reply Land(BridgeCommand command)
    {
        if (!command.Has("craft") && !command.Has("climb") && !command.Has("stop"))
        {
            if (_landing is not { } l || !World.IsAlive(l.Craft)) return Done(new() { ["landing"] = null });
            (double radar, double up) = World.Descent(l.Craft);
            return Done(new()
            {
                ["landing"] = l.Craft.Id, ["climbed"] = l.Climbed, ["height_m"] = Math.Round(radar - l.Floor, 1),
                ["up_m_s"] = Math.Round(up, 2), ["situation"] = l.Craft.Situation.ToString(), ["lit"] = World.EngineOf(l.Craft).On,
            });
        }
        string id = command.String("craft");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (Sync.IsPuppet(craft)) return Failed($"{craft.Id} is another player's");
        if (command.Flag("stop", false))
        {
            _landing = null;
            World.SetEngine(craft, false, 0f);
            return Done(new() { ["craft"] = craft.Id, ["stopped"] = true });
        }
        double floor = command.Has("floor") ? command.Number("floor", 0.0) : World.Descent(craft).Radar;
        double climb = Math.Max(0.0, command.Number("climb", 0.0));
        _landing = new Landing(craft, floor, climb, Climbed: climb <= 0.0, Math.Clamp(command.Number("slow", 1.5), 0.1, 50.0),
            Math.Clamp(command.Number("touch", 1.0), 0.1, 20.0));
        return Done(new() { ["craft"] = craft.Id, ["floor_m"] = Math.Round(floor, 2), ["climb_m"] = climb });
    }

    /// <param name="Floor">What the craft's radar altitude reads standing on the ground: its own height, near enough.</param>
    /// <param name="Slow">The slowing the descent is planned on, m/s^2, which has to be less than the engine gives.</param>
    /// <param name="Touch">How fast it is let come down the last of the way, m/s.</param>
    private readonly record struct Landing(Vehicle Craft, double Floor, double Climb, bool Climbed, double Slow, double Touch);

    private static Landing? _landing;

    // Every frame, because a second between two answers is thirty metres of falling.
    private static void FlyDown()
    {
        if (_landing is not { } l) return;
        if (!World.IsAlive(l.Craft) || Sync.IsPuppet(l.Craft))
        {
            _landing = null;
            return;
        }
        (double radar, double up) = World.Descent(l.Craft);
        double height = radar - l.Floor;
        if (!l.Climbed)
        {
            if (height < l.Climb)
            {
                World.SetEngine(l.Craft, true, 1f);
                return;
            }
            _landing = l = l with { Climbed = true };
        }
        else if (l.Craft.Situation.HasAnyContact() && height < 1.0)
        {
            World.SetEngine(l.Craft, false, 0f);
            _landing = null;
            return;
        }
        // The speed a craft slowing at Slow from here would be doing, and the engine against any more.
        double allowed = -(l.Touch + Math.Sqrt(2.0 * l.Slow * Math.Max(height, 0.0)));
        World.SetEngine(l.Craft, up < allowed, up < allowed ? 1f : 0f);
    }

    // A craft flown onto another's docking port on its thrusters, as a player flies one: their port
    // targeted, the craft controlled from its own, the flight computer holding the two lined up, and
    // the translation keys held and let go. Nothing here docks anything: the two ports touch, and
    // KSA docks them or does not. With nothing, how the approach is going.
    private static Reply Approach(BridgeCommand command)
    {
        if (!command.Has("craft") && !command.Has("to") && !command.Has("stop")) return Done(Approaching());
        string id = command.String("craft"), to = command.String("to");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (Sync.IsPuppet(craft)) return Failed($"{craft.Id} is another player's");
        if (command.Flag("stop", false))
        {
            _approach = null;
            World.Move(craft, 0, 0, 0, false);
            return Done(new() { ["craft"] = craft.Id, ["stopped"] = true });
        }
        if (World.Vehicles().FirstOrDefault(v => v.Id == to) is not { } target) return Failed($"no craft '{to}' in this world");
        if (World.AimAtPort(craft, target) is { } why) return Failed(why);
        _approach = new Approach_(craft, target)
        {
            Began = Sync.Wall(),
            Final = Math.Clamp(command.Number("final", 0.3), 0.05, 2.0),
            Hold = Math.Clamp(command.Number("hold", 4.0), 1.0, 100.0),
            ForSeconds = Math.Clamp(command.Number("seconds", 300.0), 10.0, 3600.0),
            Fly = command.Flag("fly", true),
        };
        return Done(Approaching());
    }

    private sealed class Approach_(Vehicle craft, Vehicle target)
    {
        public Vehicle Craft { get; } = craft;
        public Vehicle Target { get; } = target;
        public string TargetId { get; } = target.Id;
        public double Began, Final, Hold, ForSeconds;
        /// <summary>False only measures: the craft is aimed and lined up, and no key is held.</summary>
        public bool Fly;
        public string Phase = "turning";
        /// <summary>Which translation key is being tried, and since when, and what the closing velocity was when it went down.</summary>
        public int Key;
        public double KeySince;
        public double3 Before;
        /// <summary>What each of forward, right and up does to this craft, m/s^2 in its own frame.</summary>
        public double3[] Pushes = new double3[3];
        public string? Ended;
        public double Along, Across, Closing, Angle;
    }

    private static Approach_? _approach;

    // How long each key is held to learn what it does, and the least push that counts as one, m/s.
    private const double TryKeySeconds = 0.4, LeastPush = 0.003;

    // What a key is not held for: less than this short of the speed wanted, m/s.
    private const double NearEnoughSpeed = 0.02;

    private static Dictionary<string, object?> Approaching()
    {
        if (_approach is not { } a) return new() { ["approaching"] = null };
        return new()
        {
            ["approaching"] = a.TargetId, ["craft"] = a.Craft.Id, ["phase"] = a.Phase, ["ended"] = a.Ended,
            ["along_m"] = Math.Round(a.Along, 2), ["across_m"] = Math.Round(a.Across, 2),
            ["closing_m_s"] = Math.Round(a.Closing, 3), ["angle_deg"] = Math.Round(a.Angle, 1),
            ["pushes_m_s2"] = a.Pushes.Select(p => (object?)Math.Round(World.Vec(p), 4)).ToList(),
            ["for_s"] = Math.Round(Sync.Wall() - a.Began, 1),
            ["pointing"] = World.IsAlive(a.Craft) ? World.Pointing(a.Craft) : null,
        };
    }

    // Every frame. What each key does is found by holding it and watching, because which way a
    // craft's thrusters push is the craft's business and KSA's, and a guess is a craft flown away.
    private static void FlyToPort()
    {
        if (_approach is not { Ended: null } a) return;
        void End(string why)
        {
            a.Ended = why;
            if (World.IsAlive(a.Craft)) World.Move(a.Craft, 0, 0, 0, false);
        }
        if (!World.IsAlive(a.Craft) || !World.IsAlive(a.Target))
        {
            End("one of the two craft is gone, which is what docking does to the lighter");
            return;
        }
        if (World.Ports(a.Craft, a.Target) is not { } at)
        {
            End("a port is no longer free");
            return;
        }
        double now = Sync.Wall();
        a.Along = double3.Dot(at.Apart, at.Axis);
        double3 across = at.Apart - (at.Axis * a.Along);
        a.Across = World.Vec(across);
        a.Closing = double3.Dot(at.Closing, at.Axis);
        a.Angle = Math.Acos(Math.Clamp(-double3.Dot(at.Mine, at.Axis), -1.0, 1.0)) * 180.0 / Math.PI;
        if (now - a.Began > a.ForSeconds)
        {
            End("ran out of time");
            return;
        }
        // Asked for again until it holds: what is written to a craft's flight computer while a
        // physics step is out is written over by the copy that step brings back.
        if (!World.Aligning(a.Craft) && World.AimAtPort(a.Craft, a.Target) is { } why)
        {
            End(why);
            return;
        }
        if (!a.Fly) return;
        doubleQuat cce2Body = at.Body2Cce.Inverse();
        if (a.Phase == "turning")
        {
            if (a.Angle > 5.0 && now - a.Began < 90.0) return;
            (a.Phase, a.Key, a.KeySince, a.Before) = ("trying", 0, now, at.Closing);
        }
        if (a.Phase == "trying")
        {
            World.Move(a.Craft, a.Key == 0 ? 1 : 0, a.Key == 1 ? 1 : 0, a.Key == 2 ? 1 : 0, false);
            if (now - a.KeySince < TryKeySeconds) return;
            double3 gained = at.Closing - a.Before;
            if (World.Vec(gained) < LeastPush)
            {
                End($"the {new[] { "forward", "right", "up" }[a.Key]} key moved the craft {World.Vec(gained):0.0000} m/s in {TryKeySeconds} s: it has no thrusters that answer it");
                return;
            }
            a.Pushes[a.Key] = gained.Transform(cce2Body) / (now - a.KeySince);
            (a.Key, a.KeySince, a.Before) = (a.Key + 1, now, at.Closing);
            if (a.Key < 3) return;
            a.Phase = "flying";
        }
        // Onto the line out of their port first, held off it; then down the line, slowing, and
        // never slower than the last of it is to be taken at.
        bool lined = a.Across < Math.Max(0.15, 0.05 * a.Along);
        double closing = lined ? -Math.Clamp(0.1 * a.Along, a.Final, 1.0) : -Math.Clamp(0.1 * (a.Along - a.Hold), -0.5, 1.0);
        double3 wanted = at.Axis * closing;
        if (a.Across > 1e-6 && (lined || a.Along > a.Hold - 1.0)) wanted -= across * (Math.Min(0.2 * a.Across, 0.5) / a.Across);
        double3 lacking = (wanted - at.Closing).Transform(cce2Body);
        double3 p0 = a.Pushes[0], p1 = a.Pushes[1], p2 = a.Pushes[2];
        double det = double3.Dot(p0, double3.Cross(p1, p2));
        if (Math.Abs(det) < 1e-12)
        {
            End("the three keys do not push three ways");
            return;
        }
        // Seconds of each key that would make up what is lacking, and a key held for any worth holding.
        double forward = double3.Dot(lacking, double3.Cross(p1, p2)) / det;
        double right = double3.Dot(p0, double3.Cross(lacking, p2)) / det;
        double up = double3.Dot(p0, double3.Cross(p1, lacking)) / det;
        static int Held(double seconds, double3 push) => Math.Abs(seconds) * World.Vec(push) < NearEnoughSpeed ? 0 : Math.Sign(seconds);
        World.Move(a.Craft, Held(forward, p0), Held(right, p1), Held(up, p2), false);
    }

    // Two craft docked, through the same event KSA's own contact between two docking ports raises:
    // the one way two craft become one without flying one onto the other's port.
    private static Reply Dock(BridgeCommand command)
    {
        string id = command.String("craft"), to = command.String("to");
        Vehicle? craft = id.Length > 0 ? World.Vehicles().FirstOrDefault(v => v.Id == id) : World.Controlled;
        if (craft is null) return Failed(id.Length > 0 ? $"no craft '{id}' in this world" : "no craft is being flown");
        if (command.Flag("undo", false))
        {
            int before = craft.Parts.Count;
            if (World.Undock(craft) is { } no) return Failed(no);
            return Done(new() { ["craft"] = craft.Id, ["parts"] = before, ["undocking"] = true });
        }
        if (World.Vehicles().FirstOrDefault(v => v.Id == to) is not { } other) return Failed($"no craft '{to}' in this world");
        if (ReferenceEquals(craft, other)) return Failed("a craft does not dock with itself");
        if (World.CannotDock(craft, other) is { } why) return Failed(why);
        Sync.DockLater(craft, other);
        // Asked, not done, as a part failure is: it takes one craft out of the world's vehicles.
        return Done(new() { ["craft"] = craft.Id, ["to"] = other.Id, ["parts"] = craft.Parts.Count + other.Parts.Count, ["asked"] = true });
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

    // The speed picked the way the player picks it: an input event carrying a speed, which is what
    // KSA's own simulation-speed menu puts in the buffer and what its console's speed command goes
    // through. The other half of ModSpeed, and the half that should reach the room.
    private static Reply PickSpeed(BridgeCommand command)
    {
        double x = command.Number("x", 1.0);
        try
        {
            InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(x) });
            return Done(new() { ["picked"] = x });
        }
        catch (Exception e)
        {
            return Failed(e.GetBaseException().Message);
        }
    }

    // The speed written the way another mod writes it: straight at KSA, with none of the guard
    // Speed keeps. A weapons mod holding the world down while a round is in the air does exactly
    // this, and the whole question is whether the room is told about it, so a test needs to be able
    // to do it while connected.
    private static Reply ModSpeed(BridgeCommand command)
    {
        double x = command.Number("x", 1.0);
        try
        {
            KSA.Universe.SetSimulationSpeed(new SimSpeed(x));
            return Done(new() { ["wrote"] = x, ["speed"] = World.Speed, ["asked"] = Sync.AskedSpeed });
        }
        catch (Exception e)
        {
            return Failed(e.GetBaseException().Message);
        }
    }

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

        // From the configuration popup at start a save cannot simply be read. KSA builds the system,
        // the particle pools and the rest of a world on its way out of that popup, and reading a
        // save before any of it exists threw -- first for want of a system, then for want of
        // everything else. So the popup is started properly, with a system chosen as choosing one
        // from its own list would choose it, and the save goes in once there is a world to put it
        // in. The order a player takes too. Without this a tool cannot get a game out of the start
        // window at all, which is where every game begins.
        bool cold = !World.Loaded;
        if (cold && Begin(command) is { } refused) return refused;

        bool read = !cold;
        if (read && Read(save) is { } straight) return straight;

        double waited = 0.0;
        ulong? since = null;
        _running = dtPlayer =>
        {
            waited += dtPlayer;
            if (!read)
            {
                if (!World.Loaded)
                    return waited > StartWithinSeconds ? Failed($"no world after {StartWithinSeconds:0} s") : null;
                // And not on the pass that world first says it is up, which is still the popup's own:
                // KSA has drawn no frame of it yet. A kitten built out of a save read then is drawn
                // torn open for as long as it lives -- its helmet whole and its body in long dark
                // blades -- in the one game that never loads another world, while both games count
                // one part and agree on its name. errors.md has the runs. A player cannot get here:
                // a hand reaches Load frames later, and a world from a server is loaded from the
                // frame hook.
                since ??= World.Frames;
                if (World.Frames - since.Value < StartedFrames)
                    return waited > StartWithinSeconds ? Failed($"the world was not drawn in {StartWithinSeconds:0} s") : null;
                if (Read(save) is { } bad) return bad;
                (read, waited) = (true, 0.0);
                return null;
            }
            // A beat past the craft appearing, for the world to settle round it.
            if (waited > 3.0 && World.InFlight) return Done(new() { ["craft"] = World.Controlled!.Id });
            return waited > 60.0 ? Failed("no craft after 60 s") : null;
        };
        return null;
    }

    /// <summary>
    /// Presses Start on KSA's configuration at start, or says why it could not be pressed. Null is
    /// success, which is the shape the callers want: they carry on to wait for the world.
    /// </summary>
    private static Reply? Begin(BridgeCommand command)
    {
        if (World.Configuration is not { } configuration)
            return Failed("no world, and no configuration at start to begin one from");
        if (World.ASystemToStartIn(command.String("system")) is not { } system)
            return Failed("this game has no system to start in");
        // The window this mod draws stands in front of that popup; this is the button on it.
        // Without the welcome that button normally lets through: nobody is here to press OKAY.
        Panel.PlayAlone(welcome: false);
        return World.StartIn(configuration, system, out string why, start: true)
            ? null
            : Failed(why.Length > 0 ? why : $"could not start in {system}");
    }

    /// <summary>
    /// A fresh game and nothing read into it: what a player gets by pressing Start and no more. The
    /// other half of <see cref="BeginLoad"/>, and the only way to a world holding KSA's own starting
    /// craft rather than somebody's save -- which is what a test wanting a rocket with stages in it
    /// has to start from, there being no way to drive the editor into building one.
    /// </summary>
    private Reply? BeginStart(BridgeCommand command)
    {
        if (World.Loaded) return Done(new() { ["started"] = false, ["why"] = "a world is already up" });
        if (Begin(command) is { } refused) return refused;

        double waited = 0.0, settling = 0.0;
        _running = dtPlayer =>
        {
            waited += dtPlayer;
            if (!World.Loaded)
                return waited > StartWithinSeconds ? Failed($"no world after {StartWithinSeconds:0} s") : null;
            // A beat past the world appearing, for the starting craft to be put in it.
            settling += dtPlayer;
            return settling > 3.0
                ? Done(new() { ["started"] = true, ["craft"] = World.Controlled?.Id, ["in_flight"] = World.InFlight })
                : null;
        };
        return null;
    }

    /// <summary>The save read, or why it could not be. Wants a world already up.</summary>
    private static Reply? Read(string save)
    {
        try
        {
            GameSaves.Refresh();
            GameSaves.LoadSaveGame(save);
            return null;
        }
        catch (Exception e)
        {
            return Failed($"could not load '{save}': {e.Message}");
        }
    }

    private static Reply SaveGame(BridgeCommand command)
    {
        string name = command.String("name");
        if (name.Length == 0) return Failed("save needs a name");
        // KSA writes nothing while the editor is open and says so only in its own log, so without
        // this the command answered ok over a save that was never made and a test read the file it
        // found as the world it had just asked for.
        if (World.Refuses() is { Length: > 0 } no) return Failed($"could not save '{name}': {no}");
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
    // Where every craft stood at every step, written down between a start and a stop: two commands
    // rather than one that waits, so a run can do things to the craft while they are being recorded.
    private static Reply RecordMotion(BridgeCommand command)
    {
        if (command.Flag("start", false))
        {
            Motion.Begin();
            return Done(new() { ["recording"] = true });
        }
        if (!command.Flag("stop", false)) return Done(new() { ["recording"] = Motion.On });
        string folder = Path.Combine(Outbox, command.Id);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "motion.jsonl");
        try
        {
            return Done(new() { ["file"] = file, ["lines"] = Motion.End(file) });
        }
        catch (Exception e)
        {
            return Failed($"the recording could not be written: {e.GetBaseException().Message}");
        }
    }

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
