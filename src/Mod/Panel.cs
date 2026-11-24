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

    // Written down, as the question is in Sync: "not everyone answered" said nothing of who had seen it.
    private static void Vote(Client c, Wire.Warp w, bool yes)
    {
        Log.Info($"answered {(yes ? "yes" : "no")} to {c.NameOf(w.Who)}'s warp to {w.Speed:0.##}x");
        c.VoteWarp(yes);
    }

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
    private sealed record Settings(string Name, string Address, string Password, bool Visible, string[]? Recent = null, string? Warps = null);

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

    /// <summary>
    /// KSA's welcome is not wanted at all this session, because the bridge started this game and
    /// there is nobody at the keyboard to dismiss it. Unlike <see cref="WelcomeHeld"/>, which keeps
    /// it back until a player asks for it, this turns it away every time KSA makes one.
    /// </summary>
    public static bool WelcomeUnwanted;

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
        DisconnectedFlash();
        ClosingCountdown();
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
        WorldWaitsWindow();
        WarpWindow();
        ServerListWindow();
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

        // See-through while in a server, where it sits over the flight it is about; solid while it
        // is a form to fill in.
        if (Sync.Now != Sync.Stage.Off) ImGui.SetNextWindowBgAlpha(0.72f);
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
        if (!KSA.Program.IsEditorOpen || Sync.WorldWaitsOnTheEditor) return;
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
            Shown.Coloured(in warn, $"Every pad at {site} has a craft on it.");
            Shown.Dim("You can't launch until one is free. Move or recover a craft, or pick another site.");
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
    }

    /// <summary>
    /// Said over the vehicle editor while the world a server has sent waits for it to close, since
    /// KSA loads no save with it open. Said once in the log, it was missed: a player put back in a
    /// restarted server from the editor sat in it waiting, and gave up after two minutes and a
    /// second try.
    /// </summary>
    private static void WorldWaitsWindow()
    {
        if (!Sync.WorldWaitsOnTheEditor) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        float2 at = new(screen.WorkPos.X + ((screen.WorkSize.X - WarpWidth) * 0.5f), screen.WorkPos.Y + 90f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, null);
        ImGui.SetNextWindowSize(new float2(WarpWidth, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###KSAMultiplayerWorldWaits", flags))
        {
            ImGui.PushTextWrapPos(0f);
            float4 warn = new(1.0f, 0.82f, 0.4f, 1.0f);
            Shown.Coloured(in warn, "The shared world has arrived.");
            Shown.Dim("Leave the vehicle editor to load it: KSA can't load a world while the editor is open.");
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
        Shown.Dim(slowest.Id == c.Id
            ? $"your PC can only keep up with {slowest.Can:0.##}x"
            : $"{slowest.Name}'s PC can only keep up with {slowest.Can:0.##}x");
    }

    private static void Asking(Client c, Wire.Warp w)
    {
        float4 warn = new(1.0f, 0.82f, 0.4f, 1.0f);
        Shown.Coloured(in warn, w.Who == c.Id
            ? $"You asked to {Fast(w.Speed)}. Waiting for everyone to agree."
            : $"{c.NameOf(w.Who)} wants to {Fast(w.Speed)}.");
        Brake(c, w.Speed);

        if (w.Waiting.Contains(c.Id))
        {
            // Everyone has to agree, so saying nothing is not a quiet yes: it simply runs out.
            if (ImGui.Button("Agree", null)) Vote(c, w, true);
            ImGui.SameLine(0f, -1f);
            if (ImGui.Button("No", null)) Vote(c, w, false);
        }
        else
        {
            Shown.Dim(w.Waiting.Length == 0
                ? "waiting..."
                : $"waiting on {string.Join(", ", w.Waiting.Select(c.NameOf))}");
        }
    }

    private static void Warping(Client c, Wire.Warp w)
    {
        ImGui.Text($"The world is running at {w.Speed:0.##}x, started by {c.NameOf(w.Who)}.");
        if (w.Who != c.Id)
        {
            Shown.Dim("only the player who started it can stop it");
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
    // When this game last left a server it was in, on the mod's own clock.
    private static double _disconnectedAt = double.NegativeInfinity;

    // Held, then faded, s; and how many times the window's text size it is drawn at.
    private const double DisconnectedHoldSeconds = 1.5, DisconnectedFadeSeconds = 1.0;
    private const float DisconnectedScale = 4f;

    /// <summary>
    /// This game has left a server it was in, by any means: the window is put back where the player
    /// can rejoin from, and the screen says so. A dropped connection said only in a window the
    /// player had shut was a world that stopped answering for no reason anybody could see.
    /// </summary>
    public static void Disconnected()
    {
        Log.Info($"left the server: said so on screen{(Visible ? "" : ", and opened the Multiplayer window again")}");
        _disconnectedAt = Sync.Wall();
        Visible = true;
    }

    // Over everything, the game's own windows included, so it is seen wherever the player is looking.
    private static void DisconnectedFlash()
    {
        double since = Sync.Wall() - _disconnectedAt;
        if (since is < 0.0 or > DisconnectedHoldSeconds + DisconnectedFadeSeconds) return;
        float alpha = since < DisconnectedHoldSeconds ? 1f : (float)(1.0 - ((since - DisconnectedHoldSeconds) / DisconnectedFadeSeconds));
        const string text = "Disconnected";
        float px = ImGui.GetFontSize() * DisconnectedScale;
        float2 size = ImGui.CalcTextSize(text) * DisconnectedScale;
        ImGuiViewportPtr view = ImGui.GetMainViewport();
        float2 at = view.Pos + ((view.Size - size) * 0.5f);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        draw.AddText(font, px, at + new float2(3f, 3f), new ImColor8(0, 0, 0, (byte)(200 * alpha)), text);
        draw.AddText(font, px, at, new ImColor8(235, 40, 40, (byte)(255 * alpha)), text);
    }

    // The server stopping, counted down in the red of "Disconnected", which follows it: in the
    // upper part of the screen, since it stays up for seconds over whatever the player is flying.
    private static void ClosingCountdown()
    {
        if (Sync.Client is not { Joined: true } c || !double.IsFinite(c.ClosingAt))
        {
            _closingSaid = double.NaN;
            return;
        }
        if (double.IsNaN(_closingSaid))
        {
            Log.Info($"the server says it stops in {c.ClosingAt - Sync.Wall():F0} s: {c.ClosingWhy}");
            _closingSaid = c.ClosingAt;
        }
        int left = (int)Math.Ceiling(Math.Max(0.0, c.ClosingAt - Sync.Wall()));
        string big = $"Server stops in {left}";
        string small = c.ClosingWhy.Length > 0 ? $"{c.ClosingWhy}; the world is saved first" : "the world is saved first";
        ImGuiViewportPtr view = ImGui.GetMainViewport();
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        float bigPx = ImGui.GetFontSize() * ClosingScale, smallPx = ImGui.GetFontSize() * ClosingWhyScale;
        float2 bigSize = ImGui.CalcTextSize(big) * ClosingScale, smallSize = ImGui.CalcTextSize(small) * ClosingWhyScale;
        float top = view.Pos.Y + (view.Size.Y * 0.2f);
        float2 bigAt = new(view.Pos.X + ((view.Size.X - bigSize.X) * 0.5f), top);
        float2 smallAt = new(view.Pos.X + ((view.Size.X - smallSize.X) * 0.5f), top + bigSize.Y + 4f);
        draw.AddText(font, bigPx, bigAt + new float2(3f, 3f), new ImColor8(0, 0, 0, 200), big);
        draw.AddText(font, bigPx, bigAt, new ImColor8(235, 40, 40, 255), big);
        draw.AddText(font, smallPx, smallAt + new float2(2f, 2f), new ImColor8(0, 0, 0, 200), small);
        draw.AddText(font, smallPx, smallAt, new ImColor8(255, 255, 255, 235), small);
    }

    // Set once a stop is logged, so a countdown said again each second is logged once.
    private static double _closingSaid = double.NaN;

    private const float ClosingScale = 3f, ClosingWhyScale = 1.5f;

    public static void DrawTags()
    {
        DrawYou();
        List<(float2 At, string Who, string Craft, double Range, bool Flying, bool Editing)> tags = Sync.Nametags();
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
        foreach ((float2 at, string who, string craft, double range, bool flying, bool editing) in tags.OrderBy(t => t.Range))
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
            // Why a craft is hanging in the air: its pilot is building something, and it is held for them.
            float2 editBox = editing ? ImGui.CalcTextSize(InTheEditor) : float2.Zero;
            float wide = Math.Max(Math.Max(nameBox.X, underBox.X), editBox.X), high = nameBox.Y + underBox.Y + editBox.Y;
            float2 corner = new(at.X - (wide / 2f), at.Y - high - 7f);

            draw.AddRectFilled(new float2(corner.X - 5f, corner.Y - 3f),
                               new float2(corner.X + wide + 5f, corner.Y + high + 3f), TagShade, 4f);
            // A nose on the box pointing at what it names, so two near each other are told apart.
            draw.AddTriangleFilled(new float2(at.X - 5f, at.Y - 7f), new float2(at.X + 5f, at.Y - 7f), at, TagShade);
            draw.AddText(font, px, new float2(at.X - (nameBox.X / 2f), corner.Y), Tag, who);
            draw.AddText(font, px, new float2(at.X - (underBox.X / 2f), corner.Y + nameBox.Y), TagFaint, under);
            if (editing) draw.AddText(font, px, new float2(at.X - (editBox.X / 2f), corner.Y + nameBox.Y + underBox.Y), TagEditing, InTheEditor);
            drawn++;
        }
        TagReport = $"{drawn} of {tags.Count} drawn, {tags.Count(t => t.Flying)} flown, at {px:F0}px";
    }

    /// <summary>
    /// "You" over the craft this player flies: no box, no name of the craft and no distance, and
    /// fainter than a parked craft's mark, so it says which one is yours without reading as another
    /// player's tag.
    /// </summary>
    private static void DrawYou()
    {
        if (Sync.YouAt() is not { } at) return;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        ImFontPtr font = ImGui.GetFont();
        float px = ImGui.GetFontSize() * 0.9f;
        float2 box = ImGui.CalcTextSize(You) * 0.9f;
        float2 put = new(at.X - (box.X / 2f), at.Y - box.Y - 4f);
        draw.AddText(font, px, new float2(put.X + 1f, put.Y + 1f), YouShadow, You);
        draw.AddText(font, px, put, YouMark, You);
    }

    private const string You = "You";
    private static readonly ImColor8 YouMark = new(190, 205, 225, 95);
    private static readonly ImColor8 YouShadow = new(0, 0, 0, 110);

    private const int MostMarks = 6;

    private const string InTheEditor = "IN THE VEHICLE EDITOR";

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
        // Clicked, it opens the Multiplayer window, or shuts it: the badge is where a player looks
        // for the server they are in, so it is where they reach for it too.
        bool hovered = ImGui.IsMouseHoveringRect(new float2(at.X - gap, at.Y), at + ImGui.CalcTextSize(where));
        float4 shade = hovered ? ConsoleStyle.TextPrimary : ConsoleStyle.TextMuted;
        Shown.Coloured(in shade, where);
        if (hovered)
        {
            ImGui.SetTooltip(Visible ? "Hide the Multiplayer window" : "Show the Multiplayer window");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Visible = !Visible;
                Save();
            }
        }
        float2 top = ImGui.GetItemRectMin(), bottom = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddCircleFilled(new float2(at.X - gap + radius, (top.Y + bottom.Y) * 0.5f), radius,
                                                  off ? OutOfIt : here ? InTheWorld : OnTheWay);
    }

    private static readonly ImColor8 Claim = new(190, 205, 225, 120);
    private static readonly ImColor8 TagShade = new(0, 0, 0, 160);
    private static readonly ImColor8 Tag = new(255, 255, 255, 235);
    private static readonly ImColor8 TagFaint = new(190, 205, 225, 200);
    private static readonly ImColor8 TagShadow = new(0, 0, 0, 190);
    // The purple of the tint on the craft it names, so the line and the craft read as one thing.
    private static readonly ImColor8 TagEditing = new(205, 170, 255, 235);

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
        // Not while a line is being typed, but through a click on the board's own buttons: a button
        // held down is an active item too, and the board went away under the mouse before it let go.
        if (World.InMapView || ImGui.GetIO().WantTextInput) return;
        if (!World.KeyDown(GlfwKey.Tab) && Sync.Wall() >= BoardUntil) return;

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        // Across the middle of the screen, where a list held up on a key is looked for, and above the
        // middle, where the flown craft is: centred by its own middle, a third of the way down, since
        // its height is however many are in the room.
        float2 at = new(screen.WorkPos.X + (screen.WorkSize.X * 0.5f), screen.WorkPos.Y + (screen.WorkSize.Y * 0.34f));
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.85f);

        const ImGuiWindowFlags board = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar
                                       | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("###KSAMultiplayerPlayers", board))
        {
            // A table, so a column is as wide as the widest thing in it and two of them can never
            // land on each other. The server's name heads the first, and the id behind the ### keeps
            // the column the same one while that name is being learned.
            //
            // At least so wide, in letters of the window's own font, with the room shared out between
            // the columns: three columns each as narrow as what is in them read as one run-on line.
            // The buttons' column is wider still by its buttons, so the three keep that room.
            float buttons = MathF.Max(ImGui.CalcTextSize(GiveLabel).X, ImGui.CalcTextSize(SpectateLabel).X) + (ImGui.GetStyle().FramePadding.X * 2f);
            // Never wider than the game itself, less the window's own edges: on a small screen that
            // much was more than the whole window. The names then share what there is.
            float room = screen.WorkSize.X - (ImGui.GetStyle().WindowPadding.X * 2f) - 16f;
            float wide = MathF.Max(buttons * 2f, MathF.Min((ImGui.GetFontSize() * 36f) + buttons + 20f, room));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(10f, 5f));
            if (ImGui.BeginTable("###KSAMultiplayerBoardRows", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.RowBg,
                                 new float2(wide, 0f)))
            {
                ImGui.TableSetupColumn($"{(c.ServerName.Length > 0 ? c.ServerName : "Multiplayer")}###who", ImGuiTableColumnFlags.None, 0.45f, 0);
                ImGui.TableSetupColumn("Ping###ping"u8, ImGuiTableColumnFlags.None, 0.15f, 1);
                ImGui.TableSetupColumn("Flying###flying"u8, ImGuiTableColumnFlags.None, 0.4f, 2);
                // As wide as its widest button and no wider: a share of the board cut "Give control" short.
                ImGui.TableSetupColumn("###act"u8, ImGuiTableColumnFlags.WidthFixed, buttons, 3);
                ImGui.TableHeadersRow();
                bool anyDiscord = c.Players.Any(q => q.Discord);
                foreach (Wire.Player p in c.Players.OrderBy(p => p.Id))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    DiscordMark(anyDiscord, p.Discord);
                    ImGui.Text(p.Id == c.Id ? $"{p.Name} (you)" : p.Name);
                    SpectatingYou(p);
                    ImGui.TableNextColumn();
                    // Every game measures its own and tells the server, so this is each player's own line.
                    Shown.Dim(p.Rtt >= 0f ? $"{p.Rtt * 1000f:F0} ms" : "--");
                    ImGui.TableNextColumn();
                    Shown.Dim(!p.Loaded ? "joining"
                                       : Sync.InTheEditor(p) ? (p.Controlling.Length > 0 ? $"{p.Controlling}, in the vehicle editor" : "in the vehicle editor")
                                       : p.Controlling.Length > 0 ? p.Controlling : "--");
                    ImGui.TableNextColumn();
                    PlayerButton(p, "board");
                }
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
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
                if (line.NotAPlayer) Shown.Coloured(in ServerSaid, line.Text);
                else Shown.Wrapped(line.Text);
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
                if (_choices.Length > 0) Shown.Dim(_choices);
            }
            else
            {
                Shown.Dim("Enter to chat, / for a command, Tab for a name");
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
            _choices = typed.Length > 0 ? $"no player's name starts with '{typed}'" : "";
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
        float below = PanelTall + (spacing * 2f);
        Servers(typed, MathF.Max(150f, ImGui.GetContentRegionAvail().Y - below));

        bool edited = false;
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        // Who is playing: the name and the Discord login, facts about this player beside a list of
        // facts about strangers. The protocol is not among them, since every row of the list says
        // where it does not match.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, in PanelBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, in PanelPadding);
        ImGui.BeginChild("###KSAMultiplayerYou", new float2(half, PanelTall), ImGuiChildFlags.Borders,
                         ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.Columns(2, "###KSAMultiplayerYouRows".AsSpan(), false);
        ImGui.SetColumnWidth(0, half * 0.42f);
        edited |= Field("Nickname"u8, "###KSAMultiplayerName", NameText);
        edited |= DiscordField();
        ImGui.Columns(1);
        // On this block's floor, as CONNECT is on the other's. What KSA shows before it starts is
        // something about this game and not about any server, so it belongs on this side.
        if (ahead)
        {
            ImGui.SetCursorPosY(PanelTall - ImGui.GetFrameHeight() - PanelPadding.Y);
            // The gap between a checkbox and its words is the same one ImGui puts between any two
            // things on a line, which beside a box reads as the label leaning on it.
            ImGui.PushStyleVarX(ImGuiStyleVar.ItemInnerSpacing, 10f);
            ImGui.Checkbox("Display settings before starting", ref _settingsFirst);
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // Where that server is, what it wants, and the button that goes to it.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, in PanelBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, in PanelPadding);
        // No bar of its own: the button is pinned to the floor of this block, so a block that
        // scrolls is one whose button slides out from under the pointer.
        ImGui.BeginChild("###KSAMultiplayerPick", new float2(half, PanelTall), ImGuiChildFlags.Borders,
                         ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.Columns(2, "###KSAMultiplayerPickRows".AsSpan(), false);
        ImGui.SetColumnWidth(0, half * 0.30f);
        // Only this field is a reason to ask again: a new name or password is said to the same
        // server, and nothing it answers about itself has changed.
        bool readdressed = Field("Server"u8, "###KSAMultiplayerServer", AddressText);
        edited |= readdressed;
        // Said in words where the server has told us it wants none, which every welcome has said
        // since protocol 23. It was a greyed-out box, and an empty box a player cannot type in looks
        // very like an empty box they have not typed in yet. Still a box while nothing is known
        // about the address: not knowing is not knowing there is none.
        if (Known(typed) is { Password: false }) Told("Password"u8, "none needed");
        else edited |= Field("Password"u8, "###KSAMultiplayerPassword", PasswordText, ImGuiInputTextFlags.Password);

        // Asked only where the list has not asked already: a row carries this game's own answer to
        // the same question, and probing a server it has just probed spends a datagram on an answer
        // it has.
        if (readdressed) _asked = "";
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
        string? link = null;
        if (Known(typed) is { } about && Refused(about) is { Length: > 0 } no)
        {
            ImGui.PushTextWrapPos(0f);
            Shown.Coloured(in ConsoleStyle.Danger, no);
            link = ModsLink(about, typed);
            if (link is not null && _linkSaid.Length > 0) Shown.Coloured(in ConsoleStyle.Pending, _linkSaid);
            ImGui.PopTextWrapPos();
        }

        // Against the floor of the block, so it stays where the player left it while the rows above
        // it grow a mod list or a refusal.
        //
        // Measured off PanelPadding and not off the style, which is where this went wrong: the
        // padding is pushed for the child and popped the moment it has begun, so asking the style
        // for it in here answers with the modal's own, which KSA sets to nothing. The button went a
        // whole padding too low, the content ran past the floor, and the block grew a scrollbar
        // with three rows in it.
        ImGui.SetCursorPosY(PanelTall - ConsoleWidgets.ButtonHeight - PanelPadding.Y);
        bool ready = typed.Length > 0 && NameText.ToString().Trim().Length > 0 && !Sync.AskingAhead && !WouldBeRefused;
        float2 across = new(ImGui.GetContentRegionAvail().X, ConsoleWidgets.ButtonHeight);
        // In CONNECT's place and not beside it. A server that would turn this game away for its
        // mods has that button greyed, and a greyed button over a live one is two answers to
        // "what do I press"; the one thing there is to do about it is the one button.
        if (link is not null)
        {
            if (ConsoleWidgets.PrimaryButton("GET THESE MODS WITH BOREA".AsSpan(), across)) OpenModsLink(link);
        }
        else
        {
            ImGui.BeginDisabled(!ready);
            if (ConsoleWidgets.PrimaryButton("CONNECT".AsSpan(), across))
            {
                Save();
                Remember(AddressText.ToString());
                if (ahead) Sync.AskAhead(AddressText.ToString());
                else Sync.Connect(AddressText.ToString(), NameText.ToString(), PasswordText.ToString());
            }
            ImGui.EndDisabled();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        // Kept as soon as a field is done with, not only on connecting: a name typed before a server
        // refuses, or before the player thinks better of it, is still the name they want next time.
        if (edited) Save();

        ConsoleStyle.PopWidgetStyle();
        ConsoleStyle.EndBody();
        ConsoleStyle.EndModal();
    }

    // The address the window has asked about, so it asks once a server and again when the field changes.
    private static string _asked = "";

    // The link last made and what it was made from, since making one reads every mod's version
    // and this is asked every frame; and what came of pressing its button.
    private static string _linkOf = "", _linkSaid = "";
    private static string? _link;

    /// <summary>
    /// A link that has a mod manager make a game with the mods this server wants, where its mods
    /// are why this game would be turned away and a link can say them. See <see cref="ModLink"/>.
    ///
    /// <para>Not for another build of KSA, which no list of mods mends. And said in the log the
    /// once, so a player without the manager has it to paste and one with a question has it to
    /// show.</para>
    /// </summary>
    private static string? ModsLink(ServerFacts f, string typed)
    {
        if (f.KsaBuild.Length > 0 && World.KsaBuild.Length > 0 && f.KsaBuild != World.KsaBuild) return null;
        string of = $"{typed}\n{f.ServerName}\n{f.LinkId}\n{f.Mods}";
        if (of == _linkOf) return _link;
        (_linkOf, _linkSaid) = (of, "");
        (string missing, string extra, string wrong) = Sync.ModsAgainst(f.Mods, f.Optional);
        _link = missing.Length > 0 || wrong.Length > 0 || (f.Mods.Length > 0 && extra.Length > 0)
            ? ModLink.For(f.Mods, World.Mods, f.ServerName, typed, f.LinkId)
            : null;
        if (_link is not null) Log.Info($"the mods {typed} wants, as a link a mod manager opens: {_link}");
        return _link;
    }

    /// <summary>The button pressed by the bridge: the link the window offers now, opened, and what the window says came of it.</summary>
    public static (string? Link, string Said) PressModsLink()
    {
        if (_link is null) return (null, "");
        OpenModsLink(_link);
        return (_link, _linkSaid);
    }

    /// <summary>
    /// Hands the link to whatever this machine opens such links with. Only a link of this mod's
    /// own making: what goes to the shell is a line a server had a hand in.
    /// </summary>
    private static void OpenModsLink(string link)
    {
        if (!ModLink.Is(link)) return;
        try
        {
            // Asked first on Windows, which answers a link nothing is registered for with a
            // window of its own offering to look in its shop.
            if (OperatingSystem.IsWindows())
            {
                using Microsoft.Win32.RegistryKey? known = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("borea");
                if (known is null)
                {
                    _linkSaid = "Borea is not installed here. It is a mod manager for KSA: github.com/KSAModding/Borea";
                    return;
                }
            }
            using global::System.Diagnostics.Process? opened =
                global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
            _linkSaid = "Opened in Borea. Create the instance there, and play from it.";
            Log.Info("handed the link to the mod manager");
        }
        catch (Exception e)
        {
            _linkSaid = "Borea did not open. It is a mod manager for KSA: github.com/KSAModding/Borea";
            Log.Warn($"the mods link did not open: {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// How tall the list of servers is. Fixed, and scrolled inside: the fields and the buttons below
    /// it must not move as servers answer, because a window whose controls walk out from under the
    /// pointer is worse than one that shows less.
    /// </summary>
    /// <summary>
    /// How tall the two blocks under the list are. Everything above them is the list.
    ///
    /// <para>Room for a refusal that runs to two lines, which the longer ones do once the mods are
    /// named. Shorter than that and the block grew a scrollbar beside a button pinned to its
    /// floor.</para>
    /// </summary>
    private const float PanelTall = 220f;

    /// <summary>
    /// What the two blocks under the list are filled with: white at an alpha that lifts them a
    /// little off the body behind them, rather than a colour of this window's own choosing.
    ///
    /// <para>Measured off the screen rather than judged by eye, which got it wrong twice. The alpha
    /// arrives exactly as written -- the body sits at 18 and the blocks land on 18 + a*237, so 0.08
    /// is the small step the wireframe has and 0.20, which looked right in a screenshot, was three
    /// times it.</para>
    /// </summary>
    private static readonly float4 PanelBackground = new(1f, 1f, 1f, 0.08f);

    /// <summary>
    /// What a cell of the list keeps between its words and the rule beside them, and what the two
    /// blocks keep between theirs and their own edges.
    ///
    /// <para>Both are set because neither comes out right on its own here: KSA empties the modal's
    /// window padding, which its children then inherit, and a table's own default is close enough
    /// that a player count sits against the line drawn beside it.</para>
    /// </summary>
    private static readonly float2 CellPadding = new(12f, 5f);
    private static readonly float2 PanelPadding = new(14f, 12f);

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
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, in CellPadding);
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
            // As wide as the REFRESH button under it, so the rule down its left lands on that
            // button's left edge instead of somewhere across its face, where it read as a line
            // running into the button.
            ImGui.TableSetupColumn("Latency###ping"u8, ImGuiTableColumnFlags.WidthFixed,
                                   ConsoleWidgets.ButtonWidth("REFRESH".AsSpan()));
            ImGui.TableHeadersRow();

            foreach (Browser.Row r in Browser.Rows) ServerRow(r, typed);
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        // Along the floor of the box: what the list is doing on the left, and the button that makes
        // it do it again on the right.
        ImGui.SetCursorPosY(tall - ConsoleWidgets.ButtonHeight - pad.Y);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((ConsoleWidgets.ButtonHeight - ImGui.GetTextLineHeight()) * 0.5f));
        string note =
            Browser.Fetching && rows == 0 ? "Refreshing list..."
            : asked == 0 ? "No directories to ask. Type an address below."
            : answered == 0 ? $"Couldn't reach {(asked == 1 ? "the directory" : "any directory")}. Type an address below."
            : rows == 0 ? "No servers listed. Type an address below."
            : $"{Browser.Joinable} of {rows} joinable, from {answered} of {asked} director{(asked == 1 ? "y" : "ies")}.";
        // Where the chosen one came from. A directory chooses which addresses a player is shown and
        // nothing at all about what they are called, so that is the one thing worth attributing --
        // and somebody who dislikes what one carries needs to know which to take out of their file.
        if (Browser.Rows.FirstOrDefault(r => string.Equals(r.Where, typed, StringComparison.OrdinalIgnoreCase)) is { Found.Count: > 0 } found
            && found.Found.Where(n => n != Browser.Yours).ToArray() is { Length: > 0 } directories)
        {
            note += $"  {found.Where} is listed by {string.Join(", ", directories)}.";
        }
        // Short of REFRESH, which it ran under: the attribution goes first, and what is left is
        // shortened rather than drawn beneath the button.
        float noteRoom = ImGui.GetContentRegionAvail().X - ConsoleWidgets.ButtonWidth("REFRESH".AsSpan()) - (ImGui.GetStyle().ItemSpacing.X * 2f);
        int listedBy = note.IndexOf("  ", StringComparison.Ordinal);
        if (ImGui.CalcTextSize(note).X > noteRoom && listedBy > 0) note = note[..listedBy];
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.Text(Fit(note, noteRoom));
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
        // The marker at the right of this column, worked out first so the name can stop short of
        // it: a long name ran under it, and both were unreadable.
        string pill = Browser.Short(r) is { Length: > 0 } why ? why : r.Found.Contains(Browser.Yours) ? "recent" : "";
        float pillWide = pill.Length == 0 ? 0f : ImGui.CalcTextSize(pill).X + ImGui.GetTextLineHeight() + 10f;
        string title = Fit($"{r.Title}{locked}", ImGui.GetContentRegionAvail().X - pillWide);
        // The id is the row's own number and the label is the name, so a server that renames
        // itself mid-list is the same row, and two rows shown under one address for a moment are
        // two: ImGui stops everything to say so where two things in a window share an id.
        // As tall as the row. ImGui fills a selectable half the item spacing past its text each
        // way, and a row is the cell padding past it: with the two unlike, the fill of a chosen
        // row stopped three pixels short of the band its neighbour is shaded with, top and
        // bottom, and one row of two read as the shorter.
        ImGui.PushStyleVarY(ImGuiStyleVar.ItemSpacing, CellPadding.Y * 2f);
        bool picked = ImGui.Selectable($"{title}###KSAMultiplayerRow{r.Serial}", chosen, ImGuiSelectableFlags.SpanAllColumns);
        ImGui.PopStyleVar();
        // One marker, against the right of the server's own column: why this game cannot go there,
        // or that the player has been before. Short enough to read without stopping, with the whole
        // of it in the block below where there is room for a sentence.
        // In the colour KSA gives to what is waiting on the player where it is why they cannot
        // join, and in its quietest where it is only that they have been before: two kinds of
        // word in one place, and a list is glanced down for the first kind.
        if (Browser.Short(r) is { Length: > 0 }) PillAtRight(pill, in ConsoleStyle.Pending);
        else if (pill.Length > 0) PillAtRight(pill, in ConsoleStyle.TextMuted);
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
             : r.Waiting ? "..." : r.Speaks != 0 ? $"Protocol {r.Speaks}, yours is {Wire.Version}" : "No answer");
        ImGui.TableNextColumn();
        // The round trip to the server, which is not the time to join it and is the best guide
        // there is to that before anybody has.
        Cell(double.IsFinite(r.Ping) ? $"{r.Ping * 1000:F0} ms" : "");
    }

    // How much of a pill's colour its ground is. Enough to be a shape on a row that is chosen
    // or under the pointer, and little enough that the word on it is still the brightest thing.
    private const float PillGround = 0.20f;

    /// <summary>
    /// A word or two on a rounded ground of their own colour, against the right of the line being
    /// drawn. Brackets round a word made it part of the sentence beside it; this is a label on the
    /// row and reads as one.
    /// </summary>
    /// <summary>The text, or as much of it as fits in this many pixels with "..." after it.</summary>
    private static string Fit(string text, float room)
    {
        if (room <= 0f || ImGui.CalcTextSize(text).X <= room) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string shorter = text[..n].TrimEnd() + "...";
            if (ImGui.CalcTextSize(shorter).X <= room) return shorter;
        }
        return "...";
    }

    private static void PillAtRight(string says, in float4 colour)
    {
        float2 text = ImGui.CalcTextSize(says);
        float inset = MathF.Round(text.Y * 0.5f), wide = text.X + (inset * 2f);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - wide);
        float2 at = ImGui.GetCursorScreenPos();
        // A pixel over the text above and below and no more: a row is one line of text high, and a
        // pill taller than its row is drawn across the rule between two servers.
        ImGui.GetWindowDrawList().AddRectFilled(new float2(at.X, at.Y - 1f), new float2(at.X + wide, at.Y + text.Y + 1f),
                                                (ImColor8)new float4(colour.X, colour.Y, colour.Z, PillGround), (text.Y * 0.5f) + 1f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + inset);
        Shown.Coloured(in colour, says);
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
            return $"This server runs KSA {f.KsaBuild} and you have {World.KsaBuild}. You need the same build to join.";
        }
        (string missing, string extra, string wrong) = Sync.ModsAgainst(f.Mods, f.Optional);
        // One or several, because "turn it off" about two mods reads as badly as "turn them off"
        // about one, and this is the line a player acts on. And everything in the way at once: a
        // player told of one thing, who does it and starts KSA again to be told of the next, has
        // been made to start it twice.
        List<string> todo = [];
        if (missing.Length > 0) todo.Add($"turn {missing} on in KSA's Mods");
        if (f.Mods.Length > 0 && extra.Length > 0) todo.Add($"turn {extra} off");
        if (wrong.Length > 0) todo.Add($"install {wrong}");
        return todo.Count == 0 ? "" : $"To join this server, {string.Join(", and ", todo)}. Then start KSA again.";
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
            if (!listed)
            {
                Told("Server name"u8, there.ServerName.Length > 0 ? there.ServerName : "unnamed");
                Told("World"u8, !there.HasWorld ? "none yet (it will use yours)"
                              : there.System.Length > 0 ? Named(there.System) : "yes");
            }
            Mods(there.Mods, there.Optional);
            return;
        }
        // What the server said when it was asked, or what the list already knows of it: a server
        // that speaks another protocol version never answers an ask in this one, and the row's
        // two words up there are not room to say which of the two has to be updated.
        string trouble = mine && Sync.ServerTrouble.Length > 0
            ? Sync.ServerTrouble
            : Browser.Rows.FirstOrDefault(r => r.Speaks != 0 && r.Where == typed) is { } row ? $"This server {row.Trouble}." : "";
        if (trouble.Length > 0)
        {
            ImGui.Text("Server name"u8);
            ImGui.NextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.Pending);
            Shown.Wrapped(trouble);
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
    private static void Mods(string world, string optional)
    {
        (string[] missing, string[] extra, string[] wrong) = ModList.Against(world, World.Mods, optional);
        // A mod at another version is said as the comparison says it, with both versions: its name
        // is the first word of that.
        Dictionary<string, string> other = new(StringComparer.OrdinalIgnoreCase);
        foreach (string says in wrong) other[says.Split(' ')[0]] = says;
        HashSet<string> absent = new(missing, StringComparer.OrdinalIgnoreCase);
        // A version only beside a mod that has to be gone and got. Beside one this game has and
        // the server is content with, it is a number nobody asked for.
        (string Says, bool Bad)[] shown =
        [
            .. ModList.Parse(world)
                .Where(m => absent.Contains(m.Id) || other.ContainsKey(m.Id) || !Given.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                .Select(m => other.TryGetValue(m.Id, out string? says) ? (says, true)
                           : absent.Contains(m.Id) ? ($"{m.Id} {ModList.Release(m.Version)}".TrimEnd(), true)
                           : (m.Id, false)),
        ];
        string[] spare = [.. extra.Where(m => !Given.Contains(m, StringComparer.OrdinalIgnoreCase))];

        // No count on the label. A bare number beside the word Mods reads as a number the server
        // wants, so "Mods (1)" against one mod to turn off said the opposite of the truth -- and
        // said it twice, the row beside it already being "1 to turn off". The number goes where its
        // meaning is and nowhere else.
        ImGui.Text("Mods"u8);
        ImGui.NextColumn();
        // Which the number is, in the same breath. This read "Required mods" over a list of mods to
        // turn off, filing the one thing in the way under a word meaning the opposite.
        string what = absent.Count > 0 && other.Count > 0 ? $"{absent.Count} to turn on, {other.Count} at another version"
                    : absent.Count > 0 ? $"{absent.Count} to turn on"
                    : other.Count > 0 ? $"{other.Count} at another version"
                    : spare.Length > 0 ? $"{spare.Length} to turn off"
                    : $"{shown.Length} needed";
        if (shown.Length == 0 && spare.Length == 0)
        {
            Shown.Dim("none needed");
        }
        else if (ImGui.TreeNode($"{what}###KSAMultiplayerMods"))
        {
            foreach ((string says, bool bad) in shown)
            {
                float4 how = bad ? ConsoleStyle.Danger : ConsoleStyle.Positive;
                Shown.Coloured(in how, says);
            }
            // The caption only where there are two lists to tell apart. With nothing above them
            // the node already says "1 to turn off", and saying it again over the one name under it
            // is the same instruction twice.
            if (spare.Length > 0 && shown.Length > 0) Shown.Dim("turn these off:");
            foreach (string mod in spare) Shown.Coloured(in ConsoleStyle.Danger, mod);
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

    /// <summary>
    /// The row under the name that logs this machine in with Discord, or says who it is logged in
    /// as. Here because the name is where a player says who they are; once logged in, every server
    /// joined after is told, with nothing more to press.
    /// </summary>
    private static bool DiscordField()
    {
        DiscordLogin.Update();
        ImGui.Text("Discord"u8);
        ImGui.NextColumn();
        DiscordControls();
        ImGui.NextColumn();
        return false;
    }

    private static bool _linkCopied;

    // Discord's own blue, and a lighter one that reads as text on the window's dark panels.
    private static readonly float4 Blurple = new(0.345f, 0.396f, 0.949f, 1f), BlurpleHover = new(0.42f, 0.47f, 1f, 1f),
                                    BlurpleDown = new(0.28f, 0.32f, 0.82f, 1f), BlurpleText = new(0.66f, 0.70f, 1f, 1f);

    /// <summary>Log in, or who this machine is and Log out, or the login under way: the same wherever it is drawn.</summary>
    private static void DiscordControls()
    {
        if (DiscordLogin.NewName.Trim() is { Length: > 0 } discordName)
        {
            NameText.SetValue(Wire.Clip(discordName, Wire.NameChars));
            DiscordLogin.NewName = "";
            Save();
        }
        // On the line of the field beside it rather than a few pixels above, which is where plain
        // text sits next to a framed control.
        ImGui.AlignTextToFramePadding();
        if (DiscordLogin.Waiting)
        {
            Shown.Coloured(in ConsoleStyle.Pending, "Waiting...");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Log in with Discord in your browser, then come back here. If it opened in the wrong browser, copy the link into the right one.");
            }
            // The browser the system opens is not always the one the player is logged in to Discord in.
            switch (LinksAtRight(_linkCopied ? "Copied" : "Copy link", "Cancel"))
            {
                case 0:
                    ImGui.SetClipboardText(DiscordLogin.Url);
                    _linkCopied = true;
                    break;
                case 1:
                    DiscordLogin.Stop();
                    break;
            }
            return;
        }
        if (DiscordLogin.Who is { } me)
        {
            // A dot drawn rather than typed: a font without the glyph draws a box.
            float2 at = ImGui.GetCursorScreenPos();
            float line = ImGui.GetTextLineHeight(), r = MathF.Round(line * 0.2f);
            // Down by the frame padding the text is set down by, to sit on its middle.
            float middle = at.Y + ImGui.GetStyle().FramePadding.Y + (line * 0.5f);
            ImGui.GetWindowDrawList().AddCircleFilled(new float2(at.X + r, middle), r, (ImColor8)Blurple);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (r * 2f) + 8f);
            Shown.Coloured(in BlurpleText, me.Name);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Logged in with Discord on this computer until {DateTimeOffset.FromUnixTimeSeconds(me.Exp).LocalDateTime:d MMMM}. Every server you join is told.");
            }
            if (LinksAtRight("Log out") == 0) DiscordLogin.LogOut();
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.Button, in Blurple);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, in BlurpleHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, in BlurpleDown);
        ImGui.PushStyleColor(ImGuiCol.Text, new float4(1f, 1f, 1f, 1f));
        bool pressed = ImGui.Button("Log in with Discord###KSAMultiplayerDiscordLogIn", new float2(ImGui.GetContentRegionAvail().X, 0f));
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Once per computer, for every KSA instance on it. A server where your Discord account is an admin makes you one when you join.");
        }
        if (pressed)
        {
            _linkCopied = false;
            DiscordLogin.Start();
        }
        if (DiscordLogin.Said.Length > 0) Shown.Coloured(in ConsoleStyle.Danger, DiscordLogin.Said);
    }

    private static bool _serversOpen;

    /// <summary>
    /// The start screen's list of servers, opened from a server: the same rows, and JOIN to move to
    /// the one picked, which leaves this one for it as connecting from the start does.
    /// </summary>
    private static void ServerListWindow()
    {
        if (!_serversOpen) return;
        if (Sync.Now == Sync.Stage.Off)
        {
            _serversOpen = false;
            return;
        }
        Browser.Update();
        if (!Browser.Started) Browser.Refresh();

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        float2 middle = new(screen.WorkPos.X + (screen.WorkSize.X * 0.5f), screen.WorkPos.Y + (screen.WorkSize.Y * 0.5f));
        ImGui.SetNextWindowPos(in middle, ImGuiCond.FirstUseEver, new float2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new float2(760f, 460f), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Servers###KSAMultiplayerServerList", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            string typed = AddressText.ToString().Trim();
            float floor = ConsoleWidgets.ButtonHeight + ImGui.GetStyle().ItemSpacing.Y;
            bool password = Known(typed) is { Password: true };
            if (password) floor += ImGui.GetFrameHeightWithSpacing();
            Servers(typed, MathF.Max(150f, ImGui.GetContentRegionAvail().Y - floor));

            if (password)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Password"u8);
                ImGui.SameLine();
                ImGui.PushItemWidth(-1f);
                if (ImGui.InputText("###KSAMultiplayerListPassword", PasswordText, ImGuiInputTextFlags.Password) && ImGui.IsItemDeactivatedAfterEdit()) Save();
                ImGui.PopItemWidth();
            }

            // Not the one this game is in: joining it again would load the world it already has.
            bool here = string.Equals(typed, Sync.ServerAddress, StringComparison.OrdinalIgnoreCase);
            bool ready = typed.Length > 0 && !here && NameText.ToString().Trim().Length > 0 && !WouldBeRefused;
            ImGui.BeginDisabled(!ready);
            if (BottomRight("JOIN".AsSpan()))
            {
                Save();
                Remember(typed);
                _serversOpen = false;
                Sync.Connect(typed, NameText.ToString(), PasswordText.ToString());
            }
            ImGui.EndDisabled();
            if (here || WouldBeRefused)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetStyle().WindowPadding.X);
                ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ConsoleWidgets.ButtonHeight - ImGui.GetStyle().WindowPadding.Y
                                    + ((ConsoleWidgets.ButtonHeight - ImGui.GetTextLineHeight()) * 0.5f));
                Shown.Coloured(in ConsoleStyle.TextMuted, here ? "You're in this server." : "This server would refuse you: see its row.");
            }
        }
        ImGui.End();
        if (!open) _serversOpen = false;
    }

    /// <summary>
    /// The dot before a name on a player list for a player whose Discord login the server has
    /// checked, and the room for one before every other name while anybody has one, so the names
    /// stay in a column. Nothing at all where nobody has.
    /// </summary>
    /// <summary>Said on a player's row, after the name, while their camera is on the craft this player flies.</summary>
    private static void SpectatingYou(Wire.Player p)
    {
        if (!Sync.SpectatingMe(p)) return;
        ImGui.SameLine();
        Shown.Dim("spectating you");
    }

    /// <summary>
    /// What a player's row offers: their craft to give them when they are spectating this player's,
    /// or theirs to spectate when they fly one. Nothing on this player's own row, nor for somebody
    /// joining, flying nothing, or in the editor, where there is nothing to see.
    /// </summary>
    private static string? PlayerButtonLabel(Wire.Player p) =>
        Sync.Client is not { } c || p.Id == c.Id || !p.Loaded ? null
        : Sync.SpectatingMe(p) ? GiveLabel
        : p.Controlling.Length > 0 && !Sync.InTheEditor(p) ? SpectateLabel
        : null;

    private const string GiveLabel = "Give control", SpectateLabel = "Spectate";

    private static void PlayerButton(Wire.Player p, string where)
    {
        if (PlayerButtonLabel(p) is not { } label) return;
        bool give = label == GiveLabel;
        // Already looking at them: a button that does nothing would read as broken.
        bool already = !give && Sync.Spectating(p);
        if (already) ImGui.BeginDisabled();
        bool clicked = ImGui.Button($"{label}##{where}{p.Id}", null);
        if (already) ImGui.EndDisabled();
        if (ImGui.BeginItemTooltip())
        {
            ImGui.Text(give ? $"Hand the craft you're flying to {p.Name}, who is watching it. You'll go on watching it."
                       : already ? $"You're watching {p.Controlling}" : $"Watch {p.Controlling}, which {p.Name} is flying, without taking it over");
            ImGui.EndTooltip();
        }
        if (!clicked) return;
        if (give) Sync.GiveControl(p);
        else Sync.Spectate(p.Name);
    }

    private static void DiscordMark(bool anyone, bool verified)
    {
        if (!anyone) return;
        float line = ImGui.GetTextLineHeight(), r = MathF.Round(line * 0.2f), slot = (r * 2f) + 8f;
        float2 at = ImGui.GetCursorScreenPos();
        if (verified)
        {
            float2 middle = new(at.X + r, at.Y + (line * 0.5f));
            ImGui.GetWindowDrawList().AddCircleFilled(middle, r, (ImColor8)Blurple);
            float2 from = new(at.X, at.Y), to = new(at.X + slot, at.Y + line);
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(in from, in to)) ImGui.SetTooltip("Logged in with Discord");
        }
        // An empty Dummy rather than a cursor moved by hand, so the line it starts is a line.
        ImGui.Dummy(new float2(slot, line));
        ImGui.SameLine(0f, 0f);
    }

    /// <summary>
    /// Buttons that read as links, on the right of the line just drawn: no box until the pointer is
    /// on one. Which was pressed, or -1.
    /// </summary>
    private static int LinksAtRight(params string[] labels)
    {
        float pad = ImGui.GetStyle().FramePadding.X, gap = ImGui.GetStyle().ItemSpacing.X;
        float wide = labels.Sum(l => ImGui.CalcTextSize(l).X + (pad * 2f)) + (gap * (labels.Length - 1));
        // On the line just drawn where they fit beside it, and on a line of their own where they
        // would not: a column is only so wide, and a link cut in half is no link.
        float x = ImGui.GetCursorPosX();
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < wide) ImGui.NewLine();
        else x = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(MathF.Max(x, x + ImGui.GetContentRegionAvail().X - wide));
        ImGui.PushStyleColor(ImGuiCol.Button, in ConsoleStyle.Transparent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, in ConsoleStyle.Inset);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, in ConsoleStyle.Frame);
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.PushStyleColor(ImGuiCol.Border, in ConsoleStyle.Transparent);
        int pressed = -1;
        for (int k = 0; k < labels.Length; k++)
        {
            if (k > 0) ImGui.SameLine();
            if (ImGui.Button($"{labels[k]}###KSAMultiplayerDiscordLink{k}", null)) pressed = k;
        }
        ImGui.PopStyleColor(5);
        return pressed;
    }

    /// <summary>A row of KSA's two columns that says something rather than asking it.</summary>
    private static void Told(ImString label, string what)
    {
        ImGui.Text(label);
        ImGui.NextColumn();
        Shown.Dim(what);
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

    /// <summary>
    /// What PLAY ALONE presses. Reachable by the bridge as well as by a hand, because a tool asked
    /// to load a save at start is making the same choice and the window is in front of the same
    /// popup either way.
    /// </summary>
    /// <param name="welcome">
    /// False turns KSA's welcome away for the rest of the session, rather than only declining to
    /// show it now. KSA makes that popup again while the world loads, and the patch that holds it
    /// back only holds it while this window is up -- which this method has just stopped being true.
    /// So declining it here and nothing more moved the popup rather than removing it: it appeared a
    /// few seconds later, over the loaded game, with nobody there to press OKAY.
    /// </param>
    internal static void PlayAlone(bool welcome = true)
    {
        _lockWhatTheServerSettled = false;
        Starting = false;
        Sync.EndAhead();
        WelcomeUnwanted = !welcome;
        if (!WelcomeHeld) return;
        WelcomeHeld = false;
        if (welcome && KSA.GameSettings.ShowWelcomePopup()) KSA.WelcomePopup.Create();
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
            bool readdressed = Field("Server"u8, "###KSAMultiplayerJoinServer", AddressText);
            edited |= readdressed;
            edited |= Field("Your name"u8, "###KSAMultiplayerJoinName", NameText);
            edited |= DiscordField();
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
            // Only a change of address is a reason to ask again, as in the window at start.
            if (readdressed) _asked = "";
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
        if (!ahead && Elsewhere(typed) is { } system)
        {
            string world = Named(system), here = Named(World.System);
            // KSA started with its configuration is this mod's window at start, which asks the
            // server and starts where it says. Started without, it is the system it was last in
            // and no window: a restart alone comes back here, so the system is chosen from here.
            if (World.AsksAtStart)
            {
                Shown.Coloured(in ConsoleStyle.Danger, $"This world is in {world} and you're in {here}. Restart KSA to join it.");
            }
            else if (World.StartsNextIn == system)
            {
                Shown.Coloured(in ConsoleStyle.Positive, $"KSA will start in {world} next time. Restart it to join this world.");
            }
            else if (!World.Has(system))
            {
                Shown.Coloured(in ConsoleStyle.Danger, $"This world is in {world}, which your game doesn't have.");
            }
            else
            {
                Shown.Coloured(in ConsoleStyle.Danger, $"This world is in {world}, but you're in {here} and KSA is set to start in {here} again.");
                if (ConsoleWidgets.Button($"START IN {world.ToUpperInvariant()} NEXT TIME".AsSpan())) PressStartThere();
            }
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
    /// The system of the world at the address typed, where this game is in another: the one
    /// refusal the window can see coming, a universe being built once.
    /// </summary>
    private static string? Elsewhere(string typed) =>
        Sync.ServerAsked == typed && Sync.Server is { } there
        && there.HasWorld && there.System.Length > 0 && World.System.Length > 0 && there.System != World.System
            ? there.System
            : null;

    /// <summary>
    /// The button that has KSA start in the world's system next time, pressed, by the player or
    /// by the bridge: the system chosen, or null and why nothing was.
    /// </summary>
    public static (string? System, string Why) PressStartThere()
    {
        if (Elsewhere(AddressText.ToString().Trim()) is not { } system) return (null, "the window names no world in another system");
        if (!World.StartNextIn(system, out string why))
        {
            Log.Warn($"could not have KSA start in {system} next time: {why}");
            return (null, why);
        }
        Log.Info($"KSA starts in {system} next time, for the world at {AddressText.ToString().Trim()}");
        return (system, "");
    }

    /// <summary>
    /// The window's one action, in KSA's outlined button and in the corner KSA's own modals put a
    /// footer button in. Placed against the window rather than after the last thing drawn, so it
    /// stays where the player left it while the rows above it grow a player list or a warning.
    /// </summary>
    private static bool BottomLeft(ReadOnlySpan<char> label)
    {
        float2 pad = ImGui.GetStyle().WindowPadding;
        ImGui.SetCursorPosX(pad.X);
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ConsoleWidgets.ButtonHeight - pad.Y);
        return ConsoleWidgets.Button(label);
    }

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
        // The server's name with the ping at the right of the same line: a header, and the one
        // number about the connection a player glances at. Never wrapped, so the line is one line
        // whatever the name, and nothing under it moves.
        ImGui.PopTextWrapPos();
        ImGui.Text(c is { ServerName.Length: > 0 } ? c.ServerName : "Server");
        if (c is { Joined: true })
        {
            // The round trip as it is now; Rtt is the best since joining, which the clock is set by
            // and which reads as 0 ms on one machine.
            string ping = double.IsFinite(c.Ping) ? $"{c.Ping * 1000:F0} ms" : "...";
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(ping).X));
            Shown.Dim(ping);
        }
        // Only while it says more than that all is well: connecting, the world on its way, the
        // clock being caught up.
        if (Sync.Status != Sync.AllWell) Shown.Coloured(in ConsoleStyle.Pending, Sync.Status);
        ImGui.PushTextWrapPos(0f);
        if (c is { Joined: true })
        {
            ImGui.Separator();
            // Who is here and nothing else: what each is flying is the Tab list's.
            bool anyDiscord = c.Players.Any(q => q.Discord);
            foreach (Wire.Player p in c.Players)
            {
                DiscordMark(anyDiscord, p.Discord);
                if (p.Id == c.Id) ImGui.Text($"{p.Name} (you)");
                else if (!p.Loaded) Shown.Dim($"{p.Name} (joining)");
                else ImGui.Text(p.Name);
                SpectatingYou(p);
                // At the right of the row, as the ping is on the line above.
                if (PlayerButtonLabel(p) is { } label)
                {
                    ImGui.SameLine();
                    float wide = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - wide));
                    PlayerButton(p, "panel");
                }
            }
            WarpRow();
            DiscordRow(c);
            if (Sync.RenamedCraft is { Count: > 0 } renamed)
            {
                Shown.Dim($"other players see your craft as: {string.Join(", ", renamed)}");
            }
        }
        // The list of servers, as at start, to move to another without leaving first.
        if (BottomLeft("SERVERS".AsSpan())) _serversOpen = !_serversOpen;
        // Only where it does anything: to anybody else it would be a button that says no.
        if (Sync.Trusted)
        {
            ImGui.SameLine();
            if (ConsoleWidgets.Button("SERVER SAVES".AsSpan())) ServerSaves.Toggle();
        }
        if (Sync.Operator)
        {
            ImGui.SameLine();
            if (ConsoleWidgets.Button("SERVER LOG".AsSpan())) ServerLogs.Ask();
            if (ImGui.BeginItemTooltip())
            {
                ImGui.Text("Download today's server log and the audit log, and open them. /logs 3 in chat gets the last three days.");
                ImGui.EndTooltip();
            }
        }
        if (BottomRight("DISCONNECT".AsSpan())) Sync.Disconnect();
    }

    /// <summary>
    /// Which warps the game agrees to without asking. Asking stays the default: a warp is the whole
    /// room's time, and agreeing to one is this player's to hand over, not the mod's to assume.
    /// </summary>
    private static void WarpRow()
    {
        ImGui.Separator();
        Shown.Dim("Agree to warps:");
        WarpChoice("Ask me", Sync.WarpAnswer.Ask, "Agree or say no to each warp yourself.");
        ImGui.SameLine();
        WarpChoice("When not flying", Sync.WarpAnswer.NotFlying,
            "Agree for me while I'm in the vehicle editor or not flying a craft, and to any pause or slow-down. Ask me while I'm flying.");
        ImGui.SameLine();
        WarpChoice("Always", Sync.WarpAnswer.Always, "Agree for me to every warp anybody asks for.");
    }

    private static void WarpChoice(string label, Sync.WarpAnswer answer, string tip)
    {
        if (ImGui.RadioButton(label, Sync.AnswerWarps == answer) && Sync.AnswerWarps != answer)
        {
            Sync.AnswerWarps = answer;
            Save();
        }
        if (ImGui.BeginItemTooltip())
        {
            ImGui.Text(tip);
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Who the server says this player is on Discord, on a server that takes Discord logins, and
    /// this machine's login where it has none: logging in here is proved to this server at once.
    /// Nothing on a server that takes none, where a button would only ever say no.
    /// </summary>
    private static void DiscordRow(Client c)
    {
        if (c.DiscordNonce.Length == 0) return;
        ImGui.Separator();
        if (c.Discord is { Id.Length: > 0 } seen)
        {
            Shown.Dim($"Discord: {seen.Name}");
            return;
        }
        if (c.DiscordAsking)
        {
            Shown.Dim("Discord: checking with the server...");
            return;
        }
        DiscordControls();
        if (c.Discord is { Refused.Length: > 0 } refused) Shown.Dim(refused.Refused);
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
        Sync.AnswerWarps = Enum.TryParse(s.Warps, out Sync.WarpAnswer warps) ? warps : Sync.WarpAnswer.Ask;
    }

    /// <summary>Keeps what was typed, for the next session. Nothing to keep until the panel has read what there is.</summary>
    public static void Save()
    {
        if (!_loaded) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(NameText.ToString().Trim(), AddressText.ToString().Trim(), PasswordText.ToString(), Visible, _recent, Sync.AnswerWarps.ToString())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write {SettingsPath}: {e.Message}");
        }
    }
}
