using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// The Multiplayer window: a server's address, a name and a password to join with, who is playing and
/// what is said. Drawn from the GUI pass. Only the chat line takes the keyboard, and only while it is
/// typed in: KSA drops the flown craft's held keys while a window has it.
/// </summary>
internal static class Panel
{
    // Not AlwaysAutoResize: a window sized to its content is as wide as the longest line the server
    // ever sends, which is most of the screen. It is given a size instead, and the text is wrapped to it.
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoCollapse
                                           | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    private static readonly float2 FirstSize = new(400f, 300f);
    private static readonly float2 SmallestSize = new(320f, 120f);
    private static readonly float2 LargestSize = new(560f, 720f);


    // Where the player list's columns start, from the left of its window.
    // The scoreboard's columns size themselves to what is in them, so there is nothing here to be
    // outgrown. Fixed offsets were: a name longer than 150 px ran under the ping beside it, and
    // "Kitten 6985 (you)" is longer than 150 px.


    private static readonly float2 ChatFirstSize = new(420f, 190f);

    // KSA draws the navball at the bottom left at a fixed size and offset, whatever the window's size
    // (NavBallRenderer: the translation and the scale are both in pixels), so one clearance holds at
    // every resolution. What has to be cleared is the whole cluster and not the ball: the mode row
    // above it -- STAR, SURF, ORBT, OVEL, TGT -- is the topmost thing there. Measured off a 1600x1024
    // screenshot, the disc's top edge is 271 px up and that row's is 310; this leaves the chat's
    // bottom edge above the row with a little air.
    //
    // It was 190 before, read off a screenshot of a test profile at 800x500, which put the chat's
    // lower half inside the navball at the sizes anybody actually plays at.
    private const float NavballClear = 324f;

    // The same, for KSA's altimeter: it sits under the menu bar at the top centre, which is where the
    // scoreboard goes too. Measured at 1600x1024 with hudScale 100, the menu bar ends 26 px down and
    // the altimeter's box 58. The board sat at 60 -- flush under it, which read as clipping it -- so
    // this is that bottom edge with air under it.
    private const float AltimeterClear = 88f;

    // What the server says, told from what a player says at a glance: an answer to a command is not
    // conversation, and in a room of several players the one voice that is not a person should not
    // read like one.
    private static readonly float4 ServerSaid = new(0.56f, 0.80f, 1f, 1f);
    private const float WarpWidth = 340f;
    private const int ChatShown = 14;

    // Whether a chat line is being typed: only then does any window of this mod hold the keyboard.
    private static bool _typing, _focusChat;

    // Enter is read as a press, not as a hold: it both opens a line and sends it, and the key is
    // still down the frame a line is sent.
    private static bool _enterWas, _escapeWas, _tabWas;

    // What Tab could not settle on its own, shown under the line until the next one is typed.
    private static string _choices = "";

    /// <param name="Recent">Where this player has connected before, most recent first. Shown in the server list and asked about like anything else, so a server they have played on says whether anybody is on it now.</param>
    private sealed record Settings(string Name, string Address, string Password, bool Visible, string[]? Recent = null);

    /// <summary>How many addresses are remembered. Enough for the servers somebody actually plays on, few enough that the list is not mostly history.</summary>
    private const int MostRecent = 8;

    private static string[] _recent = [];

    private static readonly ImInputString NameText = new(Wire.NameChars * 4, ""u8);
    private static readonly ImInputString AddressText = new(128, ""u8);
    private static readonly ImInputString PasswordText = new(64, ""u8);
    private static readonly ImInputString ChatText = new(Wire.ChatChars * 4, ""u8);
    private static bool _loaded;

    // Said when it changes and not otherwise. A window that is drawing off the edge of the game
    // looks exactly like a window that is not drawing at all, and only one line told them apart.
    private static bool _drewWas;

    public static bool Visible = true;

    /// <summary>Shown, and kept shown: what KSA's Mods menu asks for, and the only way back to a shut window.</summary>
    public static void Show()
    {
        // Written down because the one thing that cannot be told apart from outside is a menu entry
        // that was never reached: a panel that does not appear looks the same whether this ran or not.
        Log.Info($"Mods menu: Multiplayer chosen (visible was {Visible}, starting {Starting}, stage {Sync.Now})");
        Visible = true;
        Save();
    }

    /// <summary>
    /// The game has just started and the player has neither joined a server nor chosen to play alone.
    /// KSA's welcome is held until they have (<see cref="Hooks"/>): it is a modal over the middle of
    /// the screen, and a player who must click through it first meets the mod as something behind it.
    /// </summary>
    public static bool Starting = true;

    /// <summary>Whether KSA asked for its welcome while this was starting, so it is shown to a player who plays alone.</summary>
    public static bool WelcomeHeld;

    // Asked for from KSA's configuration, which has no universe to join with: the server is joined
    // once the game has made one, in the system the server said.
    private static bool _joinOnLoad;

    /// <summary>
    /// Until when the player list is shown without Tab held, on this game's own clock. The bridge sets
    /// it, which only a developer's install starts: holding a key is the one thing it cannot do, and a
    /// list nobody can photograph is a list nobody can check.
    /// </summary>
    public static double BoardUntil;

    private static string SettingsPath => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "settings.json");

    /// <param name="atACraft">
    /// Whether the player is at a craft. The Multiplayer window is drawn either way, since a server
    /// is joined before there is anything to fly and a window nobody can find is a mod nobody can
    /// use; so is a warp being asked for, which is answered wherever the player is. The scoreboard
    /// and the chat are about the craft around you, and the chat takes the keyboard, so those wait
    /// until there is one.
    /// </param>
    public static void Draw(bool atACraft)
    {
        if (!_loaded) Load();
        if (_joinOnLoad)
        {
            _joinOnLoad = false;
            Sync.Connect(AddressText.ToString(), NameText.ToString(), PasswordText.ToString());
        }
        // Wherever this window is drawn, the asking it started has to be finished with. Only the
        // configuration at start acts on the answer, so only there is it left alone to take it: a
        // look drained anywhere else is stored for the rows and says nothing, and a look drained
        // nowhere stands for ever -- the rows stuck on "asking...", and Sync.Look refusing to begin
        // another because one is still going, so the field could be changed and nothing happened.
        if (!TheConfigurationIsAsking()) Sync.AnsweredAhead();
        PadsWindow();
        WarpWindow();
        if (atACraft)
        {
            Scoreboard();
            ChatWindow();
        }

        // Joined by whatever means, the bridge's included, is a choice made.
        if (Starting && Sync.Now != Sync.Stage.Off) Starting = false;
        if (Starting)
        {
            StartWindow();
            return;
        }

        // Shut is shut: nothing of this mod is drawn over the game. KSA's Mods menu is the way back,
        // which is a place a player already looks rather than a button of this mod's own parked in a
        // corner of the screen for ever.
        if (!Visible) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        // Placed off the game's own window and not off the viewport's work area. With a second
        // monitor the work area spans both, so "the right edge, less the width of this window" was
        // 500 px beyond the edge of the game: the window opened every time, drew every time, and
        // sat on a monitor the game is not on. A nineteen-pixel sliver of it was all that was ever
        // on screen, which reads exactly like a menu entry that does nothing.
        //
        // Clear of KSA's own instruments down that edge, which are drawn over this window.
        float2 room = float2.Unpack(Program.GetWindow().Size);
        float wide = MathF.Min(room.X, screen.WorkSize.X);
        float2 at = new(screen.WorkPos.X + MathF.Max(20f, wide - (FirstSize.X + 140f)), screen.WorkPos.Y + 80f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);
        ImGui.SetNextWindowSize(in FirstSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(in SmallestSize, in LargestSize);

        bool open = true;
        bool drew = ImGui.Begin("Multiplayer###KSAMultiplayer", ref open, Flags);
        if (drew != _drewWas)
        {
            _drewWas = drew;
            float2 where = ImGui.GetWindowPos(), size = ImGui.GetWindowSize();
            Log.Info($"panel: drew={drew} at {where.X:F0},{where.Y:F0} size {size.X:F0}x{size.Y:F0} "
                     + $"collapsed={ImGui.IsWindowCollapsed()} starting={Starting} visible={Visible}");
        }
        if (drew)
        {
            // 0: wrap at the edge of the window, so nothing a server says runs off the side.
            ImGui.PushTextWrapPos(0f);
            if (Sync.Now == Sync.Stage.Off) Join();
            else Joined();
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
        if (!open)
        {
            Visible = false;
            Save();
        }
    }

    /// <summary>
    /// Said over the vehicle editor while every pad at the chosen site has something standing on it,
    /// which is also while the Launch button does nothing. Drawn for as long as it is true rather
    /// than after a refused click: a player should see that there is nowhere to go before they
    /// reach for the button, not after.
    /// </summary>
    private static void PadsWindow()
    {
        if (!KSA.Program.IsEditorOpen) return;
        string site = Pads.Full;
        if (site.Length == 0) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        float2 at = new(screen.WorkPos.X + ((screen.WorkSize.X - WarpWidth) * 0.5f), screen.WorkPos.Y + 90f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, null);
        ImGui.SetNextWindowSize(new float2(WarpWidth, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###KSAMultiplayerPads", flags))
        {
            ImGui.PushTextWrapPos(0f);
            float4 warn = new(1.0f, 0.82f, 0.4f, 1.0f);
            ImGui.TextColored(in warn, $"Every pad at {site} has a craft on it.");
            ImGui.TextDisabled("Launch is held until one is clear. Move or recover something, or pick another site.");
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
    }

    /// <summary>
    /// What is being asked of this player about the world's speed, and what it is doing. Its own
    /// window near the top of the screen, so an ask is seen whether or not the panel is open: a
    /// warp nobody noticed being asked for is a warp that never happens.
    /// </summary>
    private static void WarpWindow()
    {
        if (Sync.Client is not { Joined: true } c || c.Warp is not { } w) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        float2 at = new(screen.WorkPos.X + ((screen.WorkSize.X - WarpWidth) * 0.5f), screen.WorkPos.Y + 150f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, null);
        ImGui.SetNextWindowSize(new float2(WarpWidth, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.88f);

        const ImGuiWindowFlags ask = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar
                                     | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                     | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav
                                     | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###KSAMultiplayerWarp", ask))
        {
            ImGui.PushTextWrapPos(0f);
            if (w.Phase == Wire.WarpPhase.Asking) Asking(c, w);
            else Warping(c, w);
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
    }

    // Whose machine the room's speed is held to, when it is held to anybody's. A player reads this
    // and knows the world is slow for a reason and whose, rather than wondering.
    private static void Brake(Client c, double asked)
    {
        Wire.Player slowest = default;
        foreach (Wire.Player p in c.Players)
        {
            if (!p.Loaded || p.Can <= 0f) continue;
            if (slowest.Name is null || p.Can < slowest.Can) slowest = p;
        }
        if (slowest.Name is null || slowest.Can >= asked - 0.01) return;
        ImGui.TextDisabled(slowest.Id == c.Id
            ? $"your machine can fly {slowest.Can:0.##}x"
            : $"{slowest.Name}'s machine can fly {slowest.Can:0.##}x");
    }

    private static void Asking(Client c, Wire.Warp w)
    {
        float4 warn = new(1.0f, 0.82f, 0.4f, 1.0f);
        ImGui.TextColored(in warn, w.Who == c.Id
            ? $"You want to {Fast(w.Speed)}. Waiting for everyone."
            : $"{c.NameOf(w.Who)} wants to {Fast(w.Speed)}.");
        Brake(c, w.Speed);

        if (w.Waiting.Contains(c.Id))
        {
            // Everyone has to agree, so saying nothing is not a quiet yes: it simply runs out.
            if (ImGui.Button("Agree", null)) c.VoteWarp(true);
            ImGui.SameLine(0f, -1f);
            if (ImGui.Button("No", null)) c.VoteWarp(false);
        }
        else
        {
            ImGui.TextDisabled(w.Waiting.Length == 0
                ? "waiting..."
                : $"waiting on {string.Join(", ", w.Waiting.Select(c.NameOf))}");
        }
    }

    private static void Warping(Client c, Wire.Warp w)
    {
        ImGui.Text($"The world is at {w.Speed:0.##}x, {c.NameOf(w.Who)}'s.");
        if (w.Who != c.Id)
        {
            ImGui.TextDisabled("whoever started it ends it");
            return;
        }
        if (ImGui.Button("Stop", null)) c.AskWarp(1.0);
    }

    private static string Fast(float speed) => speed < 0.01f ? "pause the world" : $"warp the world to {speed:0.##}x";

    /// <summary>
    /// Whose craft is whose, written over the world: every other player's craft in sight carries its
    /// owner's name. Drawn into a window the size of the screen that takes no input and has no
    /// background of its own, so it is only the text.
    /// </summary>
    /// <summary>What the last draw of the tags did, for a tag that is computed and never seen.</summary>
    public static string TagReport { get; private set; } = "not drawn yet";

    /// <summary>
    /// Each other player's name over their craft, drawn as KSACars draws a racer's: on the main
    /// viewport's background list, which is written after the world and before every window, with
    /// the font given outright, since a list taken outside a window carries none.
    /// </summary>
    public static void DrawTags()
    {
        List<(float2 At, string Who, string Craft, double Range, bool Flying)> tags = Sync.Nametags();
        if (tags.Count == 0)
        {
            TagReport = "nothing of another player's to name";
            return;
        }

        // The main viewport's background list with the font handed over, as KSACars draws a racer's
        // name: written after the world and under every window, and never a window of its own, which
        // ImGui is free to carry outside the game's.
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        ImFontPtr font = ImGui.GetFont();
        float px = ImGui.GetFontSize();
        int drawn = 0, marks = 0;
        foreach ((float2 at, string who, string craft, double range, bool flying) in tags.OrderBy(t => t.Range))
        {
            // Hers, not her: a faint line and no box, so a craft somebody parked is never read as one
            // they are sitting in. Only the nearest few, or a field of wreckage is a wall of a name.
            if (!flying)
            {
                if (++marks > MostMarks) continue;
                string owned = $"{who}'s {craft}";
                float2 mark = ImGui.CalcTextSize(owned);
                float2 put = new(at.X - (mark.X / 2f), at.Y - mark.Y - 4f);
                draw.AddText(font, px, new float2(put.X + 1f, put.Y + 1f), TagShadow, owned);
                draw.AddText(font, px, put, Claim, owned);
                drawn++;
                continue;
            }

            string under = $"{craft} · {Far(range)}";
            float2 nameBox = ImGui.CalcTextSize(who), underBox = ImGui.CalcTextSize(under);
            float wide = Math.Max(nameBox.X, underBox.X), high = nameBox.Y + underBox.Y;
            float2 corner = new(at.X - (wide / 2f), at.Y - high - 7f);

            draw.AddRectFilled(new float2(corner.X - 5f, corner.Y - 3f),
                               new float2(corner.X + wide + 5f, corner.Y + high + 3f), TagShade, 4f);
            // A nose on the box pointing at what it names, so two near each other are told apart.
            draw.AddTriangleFilled(new float2(at.X - 5f, at.Y - 7f), new float2(at.X + 5f, at.Y - 7f), at, TagShade);
            draw.AddText(font, px, new float2(at.X - (nameBox.X / 2f), corner.Y), Tag, who);
            draw.AddText(font, px, new float2(at.X - (underBox.X / 2f), corner.Y + nameBox.Y), TagFaint, under);
            drawn++;
        }
        TagReport = $"{drawn} of {tags.Count} drawn, {tags.Count(t => t.Flying)} flown, at {px:F0}px";
    }

    private const int MostMarks = 6;

    private static readonly float4 TagName = new(1f, 1f, 1f, 0.97f);

    // In KSA's own menu bar, beside its version: green once this game is in the shared world, amber
    // while it is still getting there. Nothing at all when no server is being talked to, because a
    // menu bar is not somewhere to put a mod that is doing nothing.
    private static readonly ImColor8 InTheWorld = new(120, 215, 130, 255);
    private static readonly ImColor8 OnTheWay = new(235, 190, 90, 255);
    private static readonly ImColor8 OutOfIt = new(225, 95, 95, 255);

    /// <summary>
    /// Drawn from a postfix on <c>Program.DrawProgramMenusHook</c>, which KSA leaves empty inside its
    /// menu bar for exactly this, just before it writes its own version down the right-hand side.
    /// </summary>
    public static void DrawMenuBarStatus()
    {
        // Red and saying so rather than gone: a bar that empties as a game drops out of a world
        // looks the same as a bar belonging to a game that was never in one, and a player who has
        // just been dropped is exactly the one who needs telling. Nothing at all until this game
        // has been pointed at a server, so a game played alone carries no badge about it.
        bool off = Sync.Now == Sync.Stage.Off;
        if (off && Sync.LastServer.Length == 0) return;
        bool here = Sync.Now == Sync.Stage.Playing;
        // How many are in the world, this game counted among them, so the number agrees with the
        // list Tab shows rather than being one short of it. Only once joined: before that the roster
        // is empty and a "(0)" would read as an empty server rather than as a join still going on.
        int count = Sync.Client is { Joined: true } joined ? joined.Players.Length : 0;
        // "(2 players)" rather than "(2)", which beside an address reads as a port or a version as
        // readily as it does a count of anybody.
        // Joining, seeding, waiting for the world and loading it are all one thing to somebody
        // watching the corner of the screen: it has not finished yet. The badge said only the
        // address through all of them, which reads the same as being in the world.
        string where = off ? "disconnected"
                           : !here ? $"{Sync.ServerAddress} (connecting...)"
                           : count > 0 ? $"{Sync.ServerAddress} ({count} player{(count == 1 ? "" : "s")})"
                           : Sync.ServerAddress;
        if (where.Length == 0) return;

        float line = ImGui.GetTextLineHeight();
        float radius = MathF.Max(3f, line * 0.22f);
        float gap = radius * 3f;
        float room = gap + ImGui.CalcTextSize(where).X;
        // Left of KSA's version, which it puts ten pixels in from the right edge.
        float version = ImGui.CalcTextSize(VersionInfo.Current.VersionString).X + 10f;
        float from = ImGui.GetWindowWidth() - version - room - 18f;
        if (from < ImGui.GetCursorPosX()) return;
        ImGui.SetCursorPosX(from);

        // Drawn after the text and off the text's own rectangle rather than off the cursor: the menu
        // bar moves the cursor down for its own line, and the dot sat high of centre by that much.
        ImGui.SetCursorPosX(from + gap);
        float2 at = ImGui.GetCursorScreenPos();
        ImGui.TextColored(in ConsoleStyle.TextMuted, where);
        float2 top = ImGui.GetItemRectMin(), bottom = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddCircleFilled(new float2(at.X - gap + radius, (top.Y + bottom.Y) * 0.5f), radius,
                                                  off ? OutOfIt : here ? InTheWorld : OnTheWay);
    }

    private static readonly ImColor8 Claim = new(190, 205, 225, 120);
    private static readonly ImColor8 TagShade = new(0, 0, 0, 160);
    private static readonly ImColor8 Tag = new(255, 255, 255, 235);
    private static readonly ImColor8 TagFaint = new(190, 205, 225, 200);
    private static readonly ImColor8 TagShadow = new(0, 0, 0, 190);

    // Centred on the craft and written over its own shadow, so it can be read against the sky as well
    // as against the ground.

    private static string Far(double m) =>
        m < 1_000.0 ? $"{m:F0} m" : m < 100_000.0 ? $"{m / 1000.0:F1} km" : $"{m / 1000.0:F0} km";

    /// <summary>
    /// Who is here, while Tab is held, as a shooter shows its scoreboard: each player's ping, what
    /// they fly and whether they are still loading. Shown whether or not the panel itself is open,
    /// and never in the map, where Tab is KSA's own, nor while something is being typed into.
    /// </summary>
    private static void Scoreboard()
    {
        if (Sync.Client is not { Joined: true } c) return;
        if (World.InMapView || ImGui.IsAnyItemActive()) return;
        if (!World.KeyDown(GlfwKey.Tab) && Sync.Wall() >= BoardUntil) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        // Centred by its middle rather than by a width written down here, since the width is now
        // whatever the longest name in the room makes it.
        float2 at = new(screen.WorkPos.X + (screen.WorkSize.X * 0.5f), screen.WorkPos.Y + AltimeterClear);
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, new float2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.85f);

        const ImGuiWindowFlags board = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar
                                       | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("###KSAMultiplayerPlayers", board))
        {
            // A table, so a column is as wide as the widest thing in it and two of them can never
            // land on each other. The server's name heads the first, and the id behind the ### keeps
            // the column the same one while that name is being learned.
            if (ImGui.BeginTable("###KSAMultiplayerBoardRows", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn($"{(c.ServerName.Length > 0 ? c.ServerName : "Multiplayer")}###who");
                ImGui.TableSetupColumn("Ping###ping"u8);
                ImGui.TableSetupColumn("Flying###flying"u8);
                ImGui.TableHeadersRow();
                foreach (Wire.Player p in c.Players.OrderBy(p => p.Id))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(p.Id == c.Id ? $"{p.Name} (you)" : p.Name);
                    ImGui.TableNextColumn();
                    // Every game measures its own and tells the server, so this is each player's own line.
                    ImGui.TextDisabled(p.Rtt >= 0f ? $"{p.Rtt * 1000f:F0} ms" : "--");
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(!p.Loaded ? "joining" : p.Controlling.Length > 0 ? p.Controlling : "--");
                }
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    /// <summary>
    /// The chat, in a window of its own that can be dragged and resized and is seen through until it
    /// is used. Enter starts a line, Enter sends it, Escape drops it.
    ///
    /// <para>The window holds the keyboard only while a line is being typed: KSA gives the flown
    /// craft's held keys up to whatever has the keyboard, so a chat box that always had it would
    /// take the controls away for the whole session.</para>
    /// </summary>
    private static void ChatWindow()
    {
        if (Sync.Client is not { Joined: true }) return;

        // Both edges are read from the window. KSA's ImGui is given typed characters but not key
        // presses, so a text field cannot tell that Enter was struck: it would take the line and
        // never send it.
        bool enter = World.KeyDown(GlfwKey.Enter) || World.KeyDown(GlfwKey.KpEnter);
        bool struck = enter && !_enterWas;
        _enterWas = enter;
        bool escape = World.KeyDown(GlfwKey.Escape);
        bool quit = escape && !_escapeWas;
        _escapeWas = escape;
        // The scoreboard takes Tab too, but not while anything is being typed into, so the two never
        // want it at once.
        bool tab = World.KeyDown(GlfwKey.Tab);
        bool completing = tab && !_tabWas;
        _tabWas = tab;

        if (_typing && completing) Complete();
        else if (_typing && quit) Stop();
        else if (struck && !World.InMapView)
        {
            if (_typing) Send();
            else if (!ImGui.IsAnyItemActive()) (_typing, _focusChat) = (true, true);
        }

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        // Above the navball, and inside the viewport whatever its size: a window placed outside one
        // is given an OS window of its own by ImGui, and lands on the desktop beside the game.
        float top = screen.WorkPos.Y + screen.WorkSize.Y - (ChatFirstSize.Y + NavballClear);
        float lowest = screen.WorkPos.Y + Math.Max(24f, screen.WorkSize.Y - (ChatFirstSize.Y + 24f));
        float2 at = new(screen.WorkPos.X + 24f, Math.Clamp(top, screen.WorkPos.Y + 24f, lowest));
        ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);
        ImGui.SetNextWindowSize(in ChatFirstSize, ImGuiCond.FirstUseEver);
        // Seen through while it is only being read, solid enough to read against the sky while typed in.
        ImGui.SetNextWindowBgAlpha(_typing ? 0.72f : 0.22f);

        const ImGuiWindowFlags chat = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav
                                      | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###KSAMultiplayerChat", chat))
        {
            // 0: wrap at the edge of the window, which TextColored honours as TextWrapped does.
            ImGui.PushTextWrapPos(0f);
            foreach (Sync.Line line in Sync.Chat.TakeLast(ChatShown))
            {
                if (line.NotAPlayer) ImGui.TextColored(in ServerSaid, line.Text);
                else ImGui.TextWrapped(line.Text);
            }
            ImGui.PopTextWrapPos();

            if (_typing)
            {
                if (_focusChat)
                {
                    ImGui.SetKeyboardFocusHere(0);
                    _focusChat = false;
                }
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("##chatline", ChatText);
                if (_choices.Length > 0) ImGui.TextDisabled(_choices);
            }
            else
            {
                ImGui.TextDisabled("Enter to chat, / for a command, Tab for a name");
            }
        }
        ImGui.End();
    }

    // The line said and the box closed; an empty one just closes it.
    private static void Send()
    {
        string said = ChatText.ToString().Trim();
        bool asking = Sync.Typed(said);
        Log.Info($"{(asking ? "command" : "chat")} line of {said.Length} characters{(said.Length > 0 ? " sent" : ", nothing to send")}");
        Stop();
    }

    /// <summary>
    /// Tab fills in a player's name from what has been typed of it, which is what every command
    /// taking a name is for -- /spectate, /op, /kick. The last word of the line is the one being
    /// finished, so it works wherever in the line the name is. Several matches take it as far as
    /// they agree and then say what is left to choose from, as a shell does.
    /// </summary>
    private static void Complete()
    {
        if (Sync.Client is not { Joined: true } c) return;
        string line = ChatText.ToString();
        int from = line.LastIndexOf(' ') + 1;
        string typed = line[from..];
        string[] fit = [.. c.Players.Select(p => p.Name)
                                    .Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                                    .Order()];
        if (fit.Length == 0)
        {
            _choices = typed.Length > 0 ? $"nobody here starts with '{typed}'" : "";
            return;
        }
        string common = fit[0];
        foreach (string name in fit)
        {
            while (!name.StartsWith(common, StringComparison.OrdinalIgnoreCase)) common = common[..^1];
        }
        ChatText.SetValue(line[..from] + common + (fit.Length == 1 ? " " : ""));
        _choices = fit.Length == 1 ? "" : string.Join("   ", fit);
    }

    private static void Stop()
    {
        _choices = "";
        ChatText.SetValue("");
        // The box stops being drawn, which is what gives the keyboard back: ImGui drops an active
        // item that is no longer there, and KSA hands the craft its keys again the same frame.
        (_typing, _focusChat) = (false, false);
    }

    /// <summary>
    /// Drawn in place of KSA's configuration at start, from the prefix on <c>Popup.DrawAll</c>: true
    /// when it was, and KSA's popups are left undrawn. Playing alone gives the configuration back;
    /// connecting asks the server which system to start in and starts there, with the rest of the
    /// configuration as the player last left it -- or, for a player who asked to see it first,
    /// gives it back with the server's system chosen, and joins once its Start has been pressed.
    /// </summary>
    /// <summary>
    /// Whether <see cref="InsteadOfConfiguration"/> is the one drawing this frame, and so the one
    /// to take what a server answers: the system it names is what KSA is about to start in, and a
    /// drain anywhere else would swallow it. The same guard that method opens with.
    /// </summary>
    private static bool TheConfigurationIsAsking() =>
        Starting && !_configuring && World.Configuration is not null;

    public static bool InsteadOfConfiguration()
    {
        if (!Starting || _configuring || World.Configuration is not { } configuration) return false;
        if (!_loaded) Load();
        if (Sync.AnsweredAhead() is { } system)
        {
            if (World.StartIn(configuration, system, out string why, start: !_settingsFirst))
            {
                _joinOnLoad = true;
                _configuring = _settingsFirst;
                // Only what the server has already settled. A server with no world yet is given the
                // game this configuration starts, so its system, its game type and where it starts
                // are the player's to choose after all.
                _lockWhatTheServerSettled = _configuring && system.Length > 0;
            }
            else Sync.Say(why);
        }
        StartWindow(ahead: true);
        return true;
    }

    /// <summary>
    /// The first three regions of KSA's configuration -- Select System, Game Type and Starting
    /// Situation -- drawn but not changeable, because the server has settled all three and a player
    /// who changes them here is only choosing which way to be refused.
    ///
    /// <para>By the order they are drawn in rather than by their names: a region's name reaches a
    /// patch as an <c>ImString</c>, which is a ref struct, and keeping one out of the patch's
    /// signature is worth more than reading it. The count is of calls and not of regions entered, so
    /// a region the player has collapsed still takes its turn and the three that are locked stay the
    /// three that are locked.</para>
    /// </summary>
    private const int ServerSettles = 3;

    private static bool _lockWhatTheServerSettled;
    private static int _regionsDrawn;
    private static readonly Stack<bool> _disabledRegions = new();

    /// <summary>KSA's configuration is about to draw itself again, from the top.</summary>
    public static void StartOfConfiguration()
    {
        _regionsDrawn = 0;
        while (_disabledRegions.Count > 0)
        {
            if (_disabledRegions.Pop()) ImGui.EndDisabled();
        }
    }

    /// <summary>A region of it has begun; <paramref name="entered"/> is false for a collapsed one.</summary>
    public static void RegionBegun(bool entered)
    {
        if (!_lockWhatTheServerSettled) return;
        bool lockThis = ++_regionsDrawn <= ServerSettles;
        if (!entered) return;
        if (lockThis) ImGui.BeginDisabled();
        _disabledRegions.Push(lockThis);
    }

    /// <summary>Whether KSA's configuration is the popup being drawn at this moment.</summary>
    private static bool _inConfiguration;

    public static void ConfigurationDrawing(bool now) => _inConfiguration = now;

    /// <summary>
    /// A way back out of KSA's configuration, in the footer beside its Start, because that is where
    /// a button that leaves belongs.
    ///
    /// <para>Drawn from the footer's own beginning rather than added to the configuration's buttons:
    /// KSA builds that row from an array of one, held in a static this mod has no business resizing.
    /// The footer is begun, then right-aligned for what KSA puts in it, so anything drawn in between
    /// takes the left of the same row.</para>
    ///
    /// <para>On both ways in, not only the one this mod asked for: a player who chose to play alone
    /// is looking at the same screen, and a mind changed there has nowhere to go without this.</para>
    /// </summary>
    public static void ConfigurationFooter()
    {
        if (!_inConfiguration) return;
        bool back = ConsoleWidgets.Button("BACK TO MULTIPLAYER".AsSpan());
        // So KSA's right-aligned Start lands on this line rather than under it.
        ImGui.SameLine();
        if (!back) return;
        // The popup stays as it is: KSA waits on it until there is a universe, and the start window
        // is drawn over it again by the prefix the moment this mod is the one answering for it.
        // Starting again as well, for the player who got here by choosing to play alone: that is
        // what turned this mod's own window off, and it is what turns it back on.
        _configuring = false;
        _lockWhatTheServerSettled = false;
        _joinOnLoad = false;
        Starting = true;
        Sync.EndAhead();
    }

    /// <summary>And has ended. Only ever called for a region that was entered.</summary>
    public static void RegionEnding()
    {
        if (!_lockWhatTheServerSettled || _disabledRegions.Count == 0) return;
        if (_disabledRegions.Pop()) ImGui.EndDisabled();
    }

    // Whether KSA's own configuration is shown once the server has said which system to start in.
    // It is a modal, so nothing behind it can be clicked while it is up -- which is why it comes
    // after the asking and not beside it, with nothing to go back to but its Start button.
    private static bool _settingsFirst, _configuring;

    /// <summary>
    /// The first thing a player sees: where to join, or to play alone. A window of its own rather
    /// than the panel moved, so the panel keeps the place the player has given it.
    ///
    /// <para>The world KSA starts in is a new game, so a server with no world yet is given this one
    /// as it would be from anywhere else, and a server that has one replaces it.</para>
    /// </summary>
    /// <summary>
    /// Drawn as KSA draws its own configuration: the same frame and titlebar, the same grey regions,
    /// the same label-and-control rows and the same yellow button, from KSA's `ConsoleStyle` and
    /// `ImGuiHelper` rather than a window of this mod's own making. It stands in that window's place,
    /// so looking like anything else would say it came from somewhere less trustworthy than the game.
    /// </summary>
    private static void StartWindow(bool ahead = false)
    {
        float2 screen = float2.Unpack(Program.GetWindow().Size);
        float scale = GameSettings.GetInterfaceScale();
        float wide = float.Clamp(screen.X * (0.62f / scale), 820f, 1180f);
        float tall = MathF.Min(screen.Y - 60f, 900f);
        // No footer. The two things a player came here to press are where the things they press them
        // about are: PLAY ALONE beside the title, and CONNECT under the server it would connect to.
        if (!ConsoleStyle.BeginModal("###KSAMultiplayerStart".AsSpan(), "PLAY MULTIPLAYER".AsSpan(), new float2(wide, tall),
                                     hasFooter: false, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            return;
        }
        // In the titlebar itself, against its right end. KSA draws that strip with its own draw
        // calls and then leaves the cursor at the top of the body, so a widget put back up there is
        // an ordinary widget over the strip rather than anything the modal has to know about.
        // Playing alone is the other half of what this window is for, and the title is the one row
        // of it that is about the window rather than about a server.
        ConsoleStyle.PushWidgetStyle();
        float2 at = ImGui.GetWindowPos(), box = ImGui.GetWindowSize();
        float bar = ConsoleStyle.TitlebarHeight, padding = ConsoleStyle.ContentPaddingPx;
        ImGui.SetCursorScreenPos(new float2(at.X + box.X - ConsoleWidgets.ButtonWidth("PLAY ALONE".AsSpan()) - padding,
                                            at.Y + ((bar - ConsoleWidgets.ButtonHeight) * 0.5f)));
        if (ConsoleWidgets.PrimaryButton("PLAY ALONE".AsSpan())) PlayAlone();
        ConsoleStyle.PopWidgetStyle();

        ConsoleStyle.BeginBody();
        ConsoleStyle.PushWidgetStyle();

        // Asked for once, when a player first sees this window, and never on the way to anywhere:
        // the fetching and the asking happen on their own and are only ever looked at for having
        // finished.
        Browser.Update();
        if (!Browser.Started) Browser.Refresh();
        string typed = AddressText.ToString().Trim();

        // The list gets whatever the two blocks under it do not need. It is what the window is for.
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float below = PanelTall + (spacing * 2f) + (ahead ? ImGui.GetFrameHeight() + spacing : 0f);
        Servers(typed, MathF.Max(150f, ImGui.GetContentRegionAvail().Y - below));

        bool edited = false;
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        // Who is playing, and what this game is. Beik's reading: a player's own protocol version is
        // a fact about them and had been nowhere, while the list beside it is full of facts about
        // strangers. It belongs on this side of the window.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, in PanelBackground);
        ImGui.BeginChild("###KSAMultiplayerYou", new float2(half, PanelTall), ImGuiChildFlags.Borders);
        ImGui.Columns(2, "###KSAMultiplayerYouRows".AsSpan(), false);
        ImGui.SetColumnWidth(0, half * 0.42f);
        edited |= Field("Nickname"u8, "###KSAMultiplayerName", NameText);
        Told("MP protocol"u8, $"Version {Wire.Version}");
        ImGui.Columns(1);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // Where that server is, what it wants, and the button that goes to it.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, in PanelBackground);
        ImGui.BeginChild("###KSAMultiplayerPick", new float2(half, PanelTall), ImGuiChildFlags.Borders);
        ImGui.Columns(2, "###KSAMultiplayerPickRows".AsSpan(), false);
        ImGui.SetColumnWidth(0, half * 0.30f);
        if (_focusAddress)
        {
            _focusAddress = false;
            ImGui.SetKeyboardFocusHere();
        }
        edited |= Field("Server"u8, "###KSAMultiplayerServer", AddressText);
        // Said in words where the server has told us it wants none, which every welcome has said
        // since protocol 23. It was a greyed-out box, and an empty box a player cannot type in looks
        // very like an empty box they have not typed in yet. Still a box while nothing is known
        // about the address: not knowing is not knowing there is none.
        if (Known(typed) is { Password: false }) Told("Password"u8, "none needed");
        else edited |= Field("Password"u8, "###KSAMultiplayerPassword", PasswordText, ImGuiInputTextFlags.Password);

        // Asked only where the list has not asked already: a row carries this game's own answer to
        // the same question, and probing a server it has just probed spends a datagram on an answer
        // it has.
        if (edited) _asked = "";
        if (!Browser.Rows.Any(r => string.Equals(r.Where, typed, StringComparison.OrdinalIgnoreCase))
            && typed.Length > 0 && _asked.Length == 0 && !Sync.Asking)
        {
            _asked = typed;
            Sync.Forget();
            Sync.Look(typed);
        }
        ThisServer(typed);
        ImGui.Columns(1);

        // The refusal this window can see coming, between what the server wants and the button that
        // would go there anyway.
        if (Known(typed) is { } about && Refused(about) is { Length: > 0 } no)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(in ConsoleStyle.Danger, no);
            ImGui.PopTextWrapPos();
        }

        // Against the floor of the block, so it stays where the player left it while the rows above
        // it grow a mod list or a refusal.
        float2 pad = ImGui.GetStyle().WindowPadding;
        ImGui.SetCursorPosY(PanelTall - ConsoleWidgets.ButtonHeight - pad.Y);
        bool ready = typed.Length > 0 && NameText.ToString().Trim().Length > 0 && !Sync.AskingAhead && !WouldBeRefused;
        ImGui.BeginDisabled(!ready);
        if (ConsoleWidgets.PrimaryButton("CONNECT".AsSpan(), new float2(ImGui.GetContentRegionAvail().X, ConsoleWidgets.ButtonHeight)))
        {
            Save();
            Remember(AddressText.ToString());
            if (ahead) Sync.AskAhead(AddressText.ToString());
            else Sync.Connect(AddressText.ToString(), NameText.ToString(), PasswordText.ToString());
        }
        ImGui.EndDisabled();
        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (ahead)
        {
            // The gap between a checkbox and its words is the same one ImGui puts between any two
            // things on a line, which beside a box reads as the label leaning on it.
            ImGui.PushStyleVarX(ImGuiStyleVar.ItemInnerSpacing, 10f);
            ImGui.Checkbox("Display settings before starting", ref _settingsFirst);
            ImGui.PopStyleVar();
        }
        // Kept as soon as a field is done with, not only on connecting: a name typed before a server
        // refuses, or before the player thinks better of it, is still the name they want next time.
        if (edited) Save();

        ConsoleStyle.PopWidgetStyle();
        ConsoleStyle.EndBody();
        ConsoleStyle.EndModal();
    }

    // The address the window has asked about, so it asks once a server and again when the field changes.
    private static string _asked = "";

    /// <summary>
    /// How tall the list of servers is. Fixed, and scrolled inside: the fields and the buttons below
    /// it must not move as servers answer, because a window whose controls walk out from under the
    /// pointer is worse than one that shows less.
    /// </summary>
    /// <summary>How tall the two blocks under the list are. Everything above them is the list.</summary>
    private const float PanelTall = 190f;

    /// <summary>
    /// What the two blocks under the list are filled with: white at an alpha that lifts them off
    /// whatever KSA's own background is, rather than a colour of this window's own choosing.
    ///
    /// <para>Higher than it looks like it should be. Something scales this down on the way to the
    /// screen -- at 0.04 and again at 0.09 the blocks were not distinguishable from the window
    /// behind them, and only a value that could not possibly be missed proved the colour was being
    /// applied at all rather than ignored.</para>
    /// </summary>
    private static readonly float4 PanelBackground = new(1f, 1f, 1f, 0.20f);

    /// <summary>Whether the address field should take the keyboard, which "Add server" asks for.</summary>
    private static bool _focusAddress;

    /// <summary>
    /// The table heading's own background. KSA's style gives it the same grey a selected row gets,
    /// so the heading and the row the player had picked read as one block of grey with no telling
    /// which was which. Darker, so the heading sits behind the list rather than in it.
    /// </summary>
    private static readonly float4 HeadingBackground = new(0f, 0f, 0f, 0.40f);

    /// <summary>
    /// The servers the directories carry, and the ones this player has been to.
    ///
    /// <para>Picking one fills the address field rather than connecting, so there is one way into a
    /// server however it was found: the player can see what they are about to connect to, change it,
    /// and press the same button either way. Nothing here is on the connect path -- a directory that
    /// is down costs a player who has an address nothing at all.</para>
    /// </summary>
    private static void Servers(string typed, float tall)
    {
        (int asked, int answered) = Browser.Directories;
        int rows = Browser.Rows.Count;

        ImGui.BeginChild("###KSAMultiplayerServers", new float2(0f, tall), ImGuiChildFlags.Borders);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, in HeadingBackground);
        // Given the height it is to fill rather than left to end with its last row. A table that
        // stops where its rows stop takes the lines between its columns with it, so three servers
        // drew three rows' worth of rule and then nothing, and the box below looked unfinished.
        // Scrolling comes with the fixed height, and takes the headings with it as it goes.
        float2 pad = ImGui.GetStyle().WindowPadding;
        float rowsTall = MathF.Max(80f, tall - ConsoleWidgets.ButtonHeight - (pad.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);
        if (ImGui.BeginTable("###KSAMultiplayerServerRows", 4,
                             ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV
                             | ImGuiTableFlags.ScrollY,
                             new float2(0f, rowsTall)))
        {
            ImGui.TableSetupColumn("Server###server"u8, ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Players###players"u8, ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("World###world"u8, ImGuiTableColumnFlags.WidthStretch, 0.95f);
            // Last, where a server browser puts it: it is the least of what decides whether to join.
            ImGui.TableSetupColumn("Latency###ping"u8, ImGuiTableColumnFlags.WidthFixed, 78f);
            ImGui.TableHeadersRow();

            // The way in for an address no directory carries, at the top of the list rather than in
            // a field of its own somewhere else: typing one in and picking one out are the same
            // thing to a player, so they live in the same place.
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable("Add server###KSAMultiplayerAdd"u8, false, ImGuiSelectableFlags.SpanAllColumns))
            {
                AddressText.SetValue("");
                _asked = "";
                Sync.Forget();
                _focusAddress = true;
            }
            foreach (Browser.Row r in Browser.Rows) ServerRow(r, typed);
            ImGui.EndTable();
        }
        ImGui.PopStyleColor();

        // Along the floor of the box: what the list is doing on the left, and the button that makes
        // it do it again on the right.
        ImGui.SetCursorPosY(tall - ConsoleWidgets.ButtonHeight - pad.Y);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((ConsoleWidgets.ButtonHeight - ImGui.GetTextLineHeight()) * 0.5f));
        string note =
            Browser.Fetching && rows == 0 ? "Refreshing list..."
            : asked == 0 ? "No directories to ask. Add a server above."
            : answered == 0 ? $"Couldn't reach {(asked == 1 ? "the directory" : "any directory")}. Add a server above."
            : rows == 0 ? "No servers listed. Add a server above."
            : $"{Browser.Joinable} of {rows} joinable, from {answered} of {asked} director{(asked == 1 ? "y" : "ies")}.";
        // Where the chosen one came from. A directory chooses which addresses a player is shown and
        // nothing at all about what they are called, so that is the one thing worth attributing --
        // and somebody who dislikes what one carries needs to know which to take out of their file.
        if (Browser.Rows.FirstOrDefault(r => string.Equals(r.Where, typed, StringComparison.OrdinalIgnoreCase)) is { Found.Count: > 0 } found
            && found.Found.Where(n => n != Browser.Yours).ToArray() is { Length: > 0 } directories)
        {
            note += $"  {found.Where} is listed by {string.Join(", ", directories)}.";
        }
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.Text(note);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ConsoleWidgets.ButtonWidth("REFRESH".AsSpan()));
        ImGui.SetCursorPosY(tall - ConsoleWidgets.ButtonHeight - pad.Y);
        if (ConsoleWidgets.Button("REFRESH".AsSpan())) Browser.Refresh(anyway: true);
        ImGui.EndChild();
    }

    /// <summary>
    /// One server. Every column is something about the server; whether this game can join it is the
    /// state of the row.
    ///
    /// <para>The reason used to be written into the World column, which put this game's own verdict
    /// in among the server's facts -- and lost the world exactly when something was wrong with it,
    /// which is when a player most wants to compare one server against another. A row that cannot
    /// be joined is dimmed, and the reason is said beside the button that would do the joining.</para>
    /// </summary>
    private static void ServerRow(Browser.Row r, string typed)
    {
        bool chosen = typed.Length > 0 && string.Equals(typed, r.Where, StringComparison.OrdinalIgnoreCase);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        string locked = r.Facts is { Password: true } ? "  (password)" : "";
        // The id is the address and the label is the name, so a server that renames itself mid-list
        // does not become a different row and lose what the player had picked.
        bool picked = ImGui.Selectable($"{r.Title}{locked}###KSAMultiplayerRow{r.Where}", chosen, ImGuiSelectableFlags.SpanAllColumns);
        // One marker, against the right of the server's own column: why this game cannot go there,
        // or that the player has been before. Short enough to read without stopping, with the whole
        // of it in the block below where there is room for a sentence.
        string mark = Browser.Short(r) is { Length: > 0 } why ? $"[{why}]"
                    : r.Found.Contains(Browser.Yours) ? "[recent]" : "";
        if (mark.Length > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(mark).X);
            ImGui.Text(mark);
        }
        if (picked)
        {
            AddressText.SetValue(r.Where);
            _asked = "";
            Sync.Forget();
            Save();
        }
        // Every row at full strength, and the marker beside the name is what says a server is not
        // for this game. Dimming a whole row did that before there was a marker, and it fought the
        // colour a row is filled with when it is chosen or merely under the pointer: muted grey on
        // that grey is nothing at all, so a server could vanish from its own list by being hovered.
        void Cell(string text) => ImGui.Text(text);

        ImGui.TableNextColumn();
        Cell(r.Facts is { } f ? $"{f.Players}/{f.MaxPlayers}" : r.Waiting ? "..." : "-");
        ImGui.TableNextColumn();
        // Capitalised, because the rest of this column is "Earth and Moon" and a cell that starts
        // small beside one that does not reads as a different kind of thing than it is.
        Cell(r.Facts is { } g ? !g.HasWorld ? "No world yet" : g.System.Length > 0 ? Named(g.System) : "A world"
             : r.Waiting ? "..." : "No answer");
        ImGui.TableNextColumn();
        // The round trip to the server, which is not the time to join it and is the best guide
        // there is to that before anybody has.
        Cell(double.IsFinite(r.Ping) ? $"{r.Ping * 1000:F0} ms" : "");
    }

    /// <summary>
    /// What the server over there said of itself, before anybody has joined it: its name, whether it
    /// holds a world, and the mods it wants against the mods this game has. The tick is a checkbox
    /// nobody can press rather than a character, because a font that has no tick draws a box instead
    /// and the one thing this must not be is ambiguous.
    /// </summary>
    /// <summary>
    /// What this game knows about the address in the field, from the list where it is a row there
    /// and from the window's own asking otherwise. Null while it knows nothing.
    /// </summary>
    /// <remarks>
    /// The list only while the start window is up, because only there is it being kept: nothing
    /// refreshes it once the game is running, so in a game its rows are an answer from whenever the
    /// window was last open. A server whose world has changed since would be described by what it
    /// used to be.
    /// </remarks>
    private static ServerFacts? Known(string typed) =>
        (Starting ? Browser.Rows.FirstOrDefault(r => string.Equals(r.Where, typed, StringComparison.OrdinalIgnoreCase))?.Facts : null)
        ?? (Sync.ServerAsked == typed ? Sync.Server : null);

    /// <summary>
    /// Why this game would be turned away by that server, in a line to put in front of the player,
    /// or empty where it would not be.
    ///
    /// <para>Said beside the button and not left to the list of mods, which is a tree a player has
    /// to open. A refusal that is only in the log is a CONNECT that does nothing.</para>
    /// </summary>
    private static string Refused(ServerFacts f)
    {
        if (f.KsaBuild.Length > 0 && World.KsaBuild.Length > 0 && f.KsaBuild != World.KsaBuild)
        {
            return $"This world is KSA {f.KsaBuild} and you run {World.KsaBuild}: joining needs the same build.";
        }
        (string missing, string extra) = Sync.ModsAgainst(f.Mods);
        // One or several, because "turn it off" about two mods reads as badly as "turn them off"
        // about one, and this is the line a player acts on.
        if (missing.Length > 0) return $"This server needs {missing}: turn {One(missing)} on in KSA's Mods and start again.";
        if (f.Mods.Length > 0 && extra.Length > 0) return $"This server doesn't run {extra}: turn {One(extra)} off and start again.";
        return "";
    }

    /// <summary>
    /// Whether the server in the address field would turn this game away, as far as this game knows.
    ///
    /// <para>Only the three a player cannot do anything about without starting KSA again: the build,
    /// mods they have not got and mods they have that the world has not. A server that is full and
    /// one that did not answer are left alone on purpose -- the first changes between looking and
    /// clicking, and the second is as likely to be a lost datagram as a dead server. Greying the
    /// button over either would lock a player out of a server that is fine on the strength of a
    /// guess this game made two seconds ago.</para>
    /// </summary>
    private static bool WouldBeRefused => Known(AddressText.ToString().Trim()) is { } f && Refused(f).Length > 0;

    /// <summary>"it" for one mod in a list and "them" for more.</summary>
    private static string One(string mods) => mods.Contains(',') ? "them" : "it";

    private static void ThisServer(string typed)
    {
        // Always these three rows, answered or not: a window that grows a section when an answer
        // arrives moves its own buttons out from under the pointer.
        bool mine = Sync.ServerAsked == typed;
        // Not said twice. A server picked out of the list has its name and its world on the row up
        // there, so repeating them here is the same answer in two places and pushes the one thing
        // the row has no room for -- the mods -- further down the window. An address typed by hand
        // is in no list and has nothing else saying what it is, so it gets all three.
        bool listed = Starting && Browser.Rows.Any(r => string.Equals(r.Where, typed, StringComparison.OrdinalIgnoreCase));
        if (Known(typed) is { } there)
        {
            (string missing, string extra) = Sync.ModsAgainst(there.Mods);
            string[] wants = [.. there.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            if (!listed)
            {
                Told("Server name"u8, there.ServerName.Length > 0 ? there.ServerName : "unnamed");
                Told("World"u8, !there.HasWorld ? "none yet: yours becomes it"
                              : there.System.Length > 0 ? Named(there.System) : "yes");
            }
            Mods(wants, missing, extra);
            return;
        }
        if (mine && Sync.ServerTrouble.Length > 0)
        {
            ImGui.Text("Server name"u8);
            ImGui.NextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.Pending);
            ImGui.TextWrapped(Sync.ServerTrouble);
            ImGui.PopStyleColor();
            ImGui.NextColumn();
        }
        else Told("Server name"u8, Sync.Asking ? "asking..." : "-");
        Told("World"u8, "-");
        Told("Mods"u8, "-");
    }

    /// <summary>
    /// Mods that are nobody's choice, so no use in a list of what to go and turn on: KSA's own
    /// content, which a game without is not a game, and this mod, which anybody reading this window
    /// is running. Hidden only while this game has them -- one that is somehow missing is the most
    /// worth seeing of all.
    /// </summary>
    private static readonly string[] Given = ["Core", "KSAMultiplayer"];

    /// <summary>
    /// The mods a server wants: how many in the label, and the list behind a dropdown, each green
    /// where this game has it and red where it has not. Anything this game runs that the world has
    /// not is under it, said as what to do about it rather than as what it is: that stops a join
    /// just as surely as something missing, and a player reading this wants the next step.
    /// </summary>
    private static void Mods(string[] wants, string missing, string extra)
    {
        HashSet<string> absent = [.. missing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        string[] shown = [.. wants.Where(m => absent.Contains(m) || !Given.Contains(m, StringComparer.OrdinalIgnoreCase))];
        string[] spare = [.. extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Where(m => !Given.Contains(m, StringComparer.OrdinalIgnoreCase))];

        // No count on the label. A bare number beside the word Mods reads as a number the server
        // wants, so "Mods (1)" against one mod to turn off said the opposite of the truth -- and
        // said it twice, the row beside it already being "1 to turn off". The number goes where its
        // meaning is and nowhere else.
        ImGui.Text("Mods"u8);
        ImGui.NextColumn();
        // Which the number is, in the same breath. This read "Required mods" over a list of mods to
        // turn off, filing the one thing in the way under a word meaning the opposite.
        string what = absent.Count > 0 ? $"{absent.Count} to turn on"
                    : spare.Length > 0 ? $"{spare.Length} to turn off"
                    : $"{shown.Length} needed";
        if (shown.Length == 0 && spare.Length == 0)
        {
            ImGui.TextDisabled("none needed");
        }
        else if (ImGui.TreeNode($"{what}###KSAMultiplayerMods"))
        {
            foreach (string mod in shown)
            {
                float4 how = absent.Contains(mod) ? ConsoleStyle.Danger : ConsoleStyle.Positive;
                ImGui.TextColored(in how, mod);
            }
            // The caption only where there are two lists to tell apart. With nothing above them
            // the node already says "1 to turn off", and saying it again over the one name under it
            // is the same instruction twice.
            if (spare.Length > 0 && shown.Length > 0) ImGui.TextDisabled("turn these off:");
            foreach (string mod in spare) ImGui.TextColored(in ConsoleStyle.Danger, mod);
            ImGui.TreePop();
        }
        ImGui.NextColumn();
    }

    /// <summary>
    /// A system as KSA's own configuration names it in its list -- "Earth and Moon" rather than
    /// "SolLite" -- or the id itself for one this game does not have, which is a thing worth seeing
    /// raw because it is why the join will be refused.
    /// </summary>
    private static string Named(string system) =>
        SystemLibrary.PickerSystems().FirstOrDefault(s => s.Id == system)?.DisplayName.Value is { Length: > 0 } shown
            ? shown
            : system;

    /// <summary>A row of KSA's two columns that says something rather than asking it.</summary>
    private static void Told(ImString label, string what)
    {
        ImGui.Text(label);
        ImGui.NextColumn();
        ImGui.TextDisabled(what);
        ImGui.NextColumn();
    }

    /// <summary>
    /// One of KSA's label-and-control rows, written out rather than taken from `ImGuiHelper.DrawInput`.
    ///
    /// <para>That helper keeps the text in a string the caller owns and only hands it back when Enter
    /// is struck, so a buffer of this mod's own had to be read into it every frame -- which put the
    /// stored text back over whatever had just been typed, a letter at a time. It also has no masked
    /// input for the password. The two columns are the same either way: a label, then the control.</para>
    /// </summary>
    private static bool Field(ImString label, string id, ImInputString into, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ImGui.Text(label);
        ImGui.NextColumn();
        ImGui.PushItemWidth(-1f);
        ImGui.InputText(id, into, flags);
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.PopItemWidth();
        ImGui.NextColumn();
        return done;
    }

    private const float StartWidth = 380f;

    private static void PlayAlone()
    {
        _lockWhatTheServerSettled = false;
        Starting = false;
        Sync.EndAhead();
        if (!WelcomeHeld) return;
        WelcomeHeld = false;
        if (KSA.GameSettings.ShowWelcomePopup()) KSA.WelcomePopup.Create();
    }

    /// <param name="ahead">From KSA's configuration, where there is no world yet: Connect asks the server which one to start in.</param>
    /// <summary>
    /// The box in the window over a running game, which is the only way back to a server list once
    /// KSA has a universe: disconnect, type another, connect. What it cannot do is change the system
    /// this game was started in, so that is the one thing it says out loud before the attempt.
    /// </summary>
    private static void Join(bool ahead = false)
    {
        // The same two columns in the same region as the start window's, rather than a block of
        // fields with the server's answer under a heading of its own: one thing to read, laid out
        // once, wherever the player meets it.
        ConsoleStyle.PushWidgetStyle();
        bool edited = false;
        if (ImGuiHelper.BeginRegion("Server"u8))
        {
            edited |= Field("Server"u8, "###KSAMultiplayerJoinServer", AddressText);
            edited |= Field("Your name"u8, "###KSAMultiplayerJoinName", NameText);
            // Said in words where the server has told us it wants none, which every welcome has
            // said since protocol 23. It was a greyed-out box, and an empty box a player cannot
            // type in looks very like an empty box they have not typed in yet. A field that is not
            // there at all cannot be mistaken for one that is.
            //
            // Still a field while nothing is known about the address: not knowing is not the same
            // as knowing there is none, and a player with a password and nowhere to put it is worse
            // off than one who typed into a box they did not need.
            if (Known(AddressText.ToString().Trim()) is { Password: false }) Told("Password"u8, "none needed");
            else edited |= Field("Password"u8, "###KSAMultiplayerJoinPassword", PasswordText, ImGuiInputTextFlags.Password);

            string asking = AddressText.ToString().Trim();
            if (edited) _asked = "";
            if (!ahead && asking.Length > 0 && _asked.Length == 0 && !Sync.Asking)
            {
                _asked = asking;
                Sync.Forget();
                Sync.Look(asking);
            }
            if (!ahead) ThisServer(asking);
            ImGuiHelper.EndRegion();
        }
        ConsoleStyle.PopWidgetStyle();
        // Kept as soon as a field is done with, not only on connecting: a name typed before a server
        // refuses, or before the player thinks better of it, is still the name they want next time.
        if (edited) Save();

        // The one refusal this window can see coming and never fix: a universe is built once, and the
        // system it was built in is the system this game has until it is started again. Above the
        // button rather than under it, since the button is the floor of the window.
        string typed = AddressText.ToString().Trim();
        if (!ahead && Sync.ServerAsked == typed && Sync.Server is { } there
            && there.HasWorld && there.System.Length > 0 && World.System.Length > 0 && there.System != World.System)
        {
            ImGui.TextColored(in ConsoleStyle.Danger,
                              $"This world is in {Named(there.System)}; you are in {Named(World.System)}. Restart KSA to join it.");
        }

        bool ready = typed.Length > 0 && NameText.ToString().Trim().Length > 0 && !Sync.AskingAhead && !WouldBeRefused;
        ImGui.BeginDisabled(!ready);
        if (BottomRight("CONNECT".AsSpan()))
        {
            Save();
            Remember(AddressText.ToString());
            if (ahead) Sync.AskAhead(AddressText.ToString());
            else Sync.Connect(AddressText.ToString(), NameText.ToString(), PasswordText.ToString());
        }
        ImGui.EndDisabled();
    }

    /// <summary>
    /// The window's one action, in KSA's outlined button and in the corner KSA's own modals put a
    /// footer button in. Placed against the window rather than after the last thing drawn, so it
    /// stays where the player left it while the rows above it grow a player list or a warning.
    /// </summary>
    private static bool BottomRight(ReadOnlySpan<char> label)
    {
        float2 pad = ImGui.GetStyle().WindowPadding;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - ConsoleWidgets.ButtonWidth(label) - pad.X);
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ConsoleWidgets.ButtonHeight - pad.Y);
        return ConsoleWidgets.Button(label);
    }

    private static void Joined()
    {
        Client? c = Sync.Client;
        ImGui.Text(c is { ServerName.Length: > 0 } ? c.ServerName : "Server");
        // On a line of its own and never wrapped. It changes between "playing" and "catching up:
        // 12 s behind" as the clock is made up, and sharing a wrapped line with the server's name
        // meant the longer one took two lines and the shorter one took one: everything below moved
        // up and down as it changed, which reads as the window shaking.
        ImGui.PopTextWrapPos();
        ImGui.TextDisabled(Sync.Status);
        ImGui.PushTextWrapPos(0f);
        if (c is { Joined: true })
        {
            // The round trip as it is now, the same one the player list shows; Rtt is the best since
            // joining, which the clock is set by and which reads as 0 ms on one machine.
            ImGui.TextDisabled(double.IsFinite(c.Ping) ? $"ping {c.Ping * 1000:F0} ms" : "ping ...");
            ImGui.Separator();
            foreach (Wire.Player p in c.Players)
            {
                string flying = p.Controlling.Length > 0 ? $": {p.Controlling}" : "";
                string state = p.Loaded ? "" : " (joining)";
                if (p.Id == c.Id) ImGui.Text($"{p.Name} (you){flying}");
                else ImGui.Text($"{p.Name}{state}{flying}");
            }
            if (Sync.RenamedCraft is { Count: > 0 } renamed)
            {
                ImGui.TextDisabled($"known to the others as: {string.Join(", ", renamed)}");
            }
        }
        if (BottomRight("DISCONNECT".AsSpan())) Sync.Disconnect();
    }

    /// <summary>
    /// The name offered to a player who has not chosen one, which names nobody.
    ///
    /// <para>Not the machine's account name, which is what this used to be. It is in front of the
    /// player for one click before they join, so a real name put there by Windows years ago goes to
    /// the server and onto everybody's player list without anybody deciding it should. "Kitten" is
    /// also what the server calls a player who sends no name at all, so the two agree, and the
    /// digits are there because a name already in the room gets a " 2" after it.</para>
    /// </summary>
    private static string Callsign() => $"Kitten {Random.Shared.Next(1000, 10000)}";

    /// <summary>
    /// Keeps an address the player has just gone to, at the front. Kept on the attempt rather than on
    /// arriving: a server that refused this player for the wrong mods is one they will want to find
    /// again once they have them, and a name typed and then thought better of is still the last place
    /// they meant to go.
    /// </summary>
    private static void Remember(string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        _recent = [address, .. _recent.Where(a => !string.Equals(a, address, StringComparison.OrdinalIgnoreCase)).Take(MostRecent - 1)];
        Browser.Remembered = _recent;
        Save();
    }

    private static void Load()
    {
        _loaded = true;
        Settings s = new("", "", "", true);
        try
        {
            if (File.Exists(SettingsPath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"could not read {SettingsPath}: {e.Message}");
        }
        // Whatever was chosen before stands, including a name that came from the account name back
        // when that was the default: it is what the others know this player by, and what an admin's
        // token is kept against.
        NameText.SetValue(s.Name.Trim().Length > 0 ? s.Name : Callsign());
        AddressText.SetValue(s.Address);
        PasswordText.SetValue(s.Password);
        Visible = s.Visible;
        // A settings file written before there was a list of these has none, which is not a problem
        // to report: it means this player has been nowhere yet as far as anybody here knows.
        _recent = [.. (s.Recent ?? []).Where(a => a.Trim().Length > 0).Take(MostRecent)];
        Browser.Remembered = _recent;
    }

    /// <summary>Keeps what was typed, for the next session. Nothing to keep until the panel has read what there is.</summary>
    public static void Save()
    {
        if (!_loaded) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(NameText.ToString().Trim(), AddressText.ToString().Trim(), PasswordText.ToString(), Visible, _recent)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write {SettingsPath}: {e.Message}");
        }
    }
}
