using System.Text.Json;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

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

    private static readonly float2 FirstSize = new(320f, 250f);
    private static readonly float2 SmallestSize = new(240f, 120f);
    private static readonly float2 LargestSize = new(560f, 720f);

    // What the widest label beside a field ("Your name") needs, so a field takes the rest of the
    // window however it is resized and no label is cut off at the edge.
    private const float LabelRoom = -112f;

    // Where the player list's columns start, from the left of its window.
    private const float PingColumn = 150f, FlyingColumn = 215f;
    private const float BoardWidth = 330f;

    private static readonly float2 ChatFirstSize = new(420f, 190f);
    private const float WarpWidth = 340f;
    private const int ChatShown = 14;

    // Whether a chat line is being typed: only then does any window of this mod hold the keyboard.
    private static bool _typing, _focusChat;

    // Enter is read as a press, not as a hold: it both opens a line and sends it, and the key is
    // still down the frame a line is sent.
    private static bool _enterWas, _escapeWas;

    private sealed record Settings(string Name, string Address, string Password, bool Visible);

    private static readonly ImInputString NameText = new(Wire.NameChars * 4, ""u8);
    private static readonly ImInputString AddressText = new(128, ""u8);
    private static readonly ImInputString PasswordText = new(64, ""u8);
    private static readonly ImInputString ChatText = new(Wire.ChatChars * 4, ""u8);
    private static bool _loaded;

    public static bool Visible = true;

    /// <summary>
    /// Until when the player list is shown without Tab held, on this game's own clock. The bridge sets
    /// it, which only a developer's install starts: holding a key is the one thing it cannot do, and a
    /// list nobody can photograph is a list nobody can check.
    /// </summary>
    public static double BoardUntil;

    private static string SettingsPath => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "settings.json");

    public static void Draw()
    {
        if (!_loaded) Load();
        WarpWindow();
        Scoreboard();
        ChatWindow();

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        // Clear of KSA's own instruments down the right edge, which are drawn over this window.
        float2 at = new(screen.WorkPos.X + screen.WorkSize.X - (FirstSize.X + 140f), screen.WorkPos.Y + 80f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);

        if (!Visible)
        {
            if (ImGui.Begin("Multiplayer##reopen", Flags | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                string label = Sync.Now == Sync.Stage.Off ? "MP" : $"MP: {Sync.Client?.Players.Length ?? 0}";
                if (ImGui.SmallButton(label)) Visible = true;
            }
            ImGui.End();
            return;
        }

        ImGui.SetNextWindowSize(in FirstSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(in SmallestSize, in LargestSize);

        bool open = true;
        if (ImGui.Begin("Multiplayer###KSAMultiplayer", ref open, Flags))
        {
            // 0: wrap at the edge of the window, so nothing a server says runs off the side.
            ImGui.PushTextWrapPos(0f);
            if (Sync.Now == Sync.Stage.Off) Join();
            else Joined();
            foreach (string said in Sync.Said.TakeLast(3)) ImGui.TextDisabled(said);
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

    private static void Asking(Client c, Wire.Warp w)
    {
        float4 warn = new(1.0f, 0.82f, 0.4f, 1.0f);
        ImGui.TextColored(in warn, w.Who == c.Id
            ? $"You want to {Fast(w.Speed)}. Waiting for everyone."
            : $"{c.NameOf(w.Who)} wants to {Fast(w.Speed)}.");

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
    public static void DrawTags()
    {
        // Drawn in the map view as well: a craft is a dot there and whose it is matters more, not
        // less. The camera is in map mode for anyone controlling nothing, which an observer is.
        List<(float2 At, string Who, string Craft, double Range)> tags = Sync.Nametags();
        if (tags.Count == 0) return;

        // A window of the mod's own, not the viewport's background or foreground list: those two are
        // never rendered from the pass this mod draws in, while a window is, which is why the panel
        // and the chat appear and anything put on a bare list does not.
        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(screen.WorkPos, ImGuiCond.Always, null);
        ImGui.SetNextWindowSize(screen.WorkSize, ImGuiCond.Always);
        const ImGuiWindowFlags over = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                      | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                                      | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                                      | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing
                                      | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("###KSAMultiplayerTags", over))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        ImFontPtr font = ImGui.GetFont();
        float line = ImGui.GetFontSize();
        foreach ((float2 at, string who, string craft, double range) in tags)
        {
            string under = $"{craft} · {Far(range)}";
            float2 nameBox = ImGui.CalcTextSize(who), underBox = ImGui.CalcTextSize(under);
            float wide = Math.Max(nameBox.X, underBox.X), high = nameBox.Y + underBox.Y;
            float2 corner = new(at.X - (wide / 2f), at.Y - high - 6f);

            draw.AddRectFilled(new float2(corner.X - 5f, corner.Y - 3f),
                               new float2(corner.X + wide + 5f, corner.Y + high + 3f), TagShade, 4f);
            draw.AddText(font, line, new float2(at.X - (nameBox.X / 2f), corner.Y), Tag, who);
            draw.AddText(font, line, new float2(at.X - (underBox.X / 2f), corner.Y + nameBox.Y), TagFaint, under);
        }
        ImGui.End();
    }

    private static readonly ImColor8 TagShade = new(0, 0, 0, 160);
    private static readonly ImColor8 Tag = new(255, 255, 255, 235);
    private static readonly ImColor8 TagFaint = new(190, 205, 225, 200);
    private static readonly ImColor8 TagShadow = new(0, 0, 0, 190);

    // Centred on the craft and written over its own shadow, so it can be read against the sky as well
    // as against the ground.
    private static void Write(ImDrawListPtr draw, float2 at, float dy, string text, ImColor8 colour, float line)
    {
        float2 where = new(at.X - (text.Length * line * 0.25f), at.Y + dy - (line * 1.6f));
        draw.AddText(new float2(where.X + 1f, where.Y + 1f), TagShadow, text);
        draw.AddText(where, colour, text);
    }

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
        float2 at = new(screen.WorkPos.X + ((screen.WorkSize.X - BoardWidth) * 0.5f), screen.WorkPos.Y + 60f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.Always, null);
        ImGui.SetNextWindowBgAlpha(0.85f);

        const ImGuiWindowFlags board = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar
                                       | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                                       | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("###KSAMultiplayerPlayers", board))
        {
            ImGui.Text(c.ServerName.Length > 0 ? c.ServerName : "Multiplayer");
            ImGui.SameLine(PingColumn, -1f);
            ImGui.TextDisabled("Ping");
            ImGui.SameLine(FlyingColumn, -1f);
            ImGui.TextDisabled("Flying");
            ImGui.Separator();
            foreach (Wire.Player p in c.Players.OrderBy(p => p.Id))
            {
                ImGui.Text(p.Id == c.Id ? $"{p.Name} (you)" : p.Name);
                ImGui.SameLine(PingColumn, -1f);
                // Every game measures its own and tells the server, so this is each player's own line.
                ImGui.TextDisabled(p.Rtt >= 0f ? $"{p.Rtt * 1000f:F0} ms" : "--");
                ImGui.SameLine(FlyingColumn, -1f);
                ImGui.TextDisabled(!p.Loaded ? "joining" : p.Controlling.Length > 0 ? p.Controlling : "--");
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

        if (_typing && quit) Stop();
        else if (struck && !World.InMapView)
        {
            if (_typing) Send();
            else if (!ImGui.IsAnyItemActive()) (_typing, _focusChat) = (true, true);
        }

        ImGuiViewportPtr screen = ImGui.GetMainViewport();
        float2 at = new(screen.WorkPos.X + 24f, screen.WorkPos.Y + screen.WorkSize.Y - (ChatFirstSize.Y + 24f));
        ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);
        ImGui.SetNextWindowSize(in ChatFirstSize, ImGuiCond.FirstUseEver);
        // Seen through while it is only being read, solid enough to read against the sky while typed in.
        ImGui.SetNextWindowBgAlpha(_typing ? 0.72f : 0.22f);

        const ImGuiWindowFlags chat = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav
                                      | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("###KSAMultiplayerChat", chat))
        {
            ImGui.PushTextWrapPos(0f);
            foreach (string line in Sync.Chat.TakeLast(ChatShown)) ImGui.TextWrapped(line);
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
            }
            else
            {
                ImGui.TextDisabled("Enter to chat");
            }
        }
        ImGui.End();
    }

    // The line said and the box closed; an empty one just closes it.
    private static void Send()
    {
        string said = ChatText.ToString().Trim();
        Log.Info($"chat line of {said.Length} characters{(said.Length > 0 ? " sent" : ", nothing to send")}");
        if (said.Length > 0) Sync.SayChat(said);
        Stop();
    }

    private static void Stop()
    {
        ChatText.SetValue("");
        // The box stops being drawn, which is what gives the keyboard back: ImGui drops an active
        // item that is no longer there, and KSA hands the craft its keys again the same frame.
        (_typing, _focusChat) = (false, false);
    }

    private static void Join()
    {
        bool edited = false;
        ImGui.SetNextItemWidth(LabelRoom);
        ImGui.InputText("Server", AddressText);
        edited |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SetNextItemWidth(LabelRoom);
        ImGui.InputText("Your name", NameText);
        edited |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SetNextItemWidth(LabelRoom);
        ImGui.InputText("Password", PasswordText, ImGuiInputTextFlags.Password);
        edited |= ImGui.IsItemDeactivatedAfterEdit();
        // Kept as soon as a field is done with, not only on connecting: a name typed before a server
        // refuses, or before the player thinks better of it, is still the name they want next time.
        if (edited) Save();
        bool ready = AddressText.ToString().Trim().Length > 0 && NameText.ToString().Trim().Length > 0;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Connect", null))
        {
            Save();
            Sync.Connect(AddressText.ToString(), NameText.ToString(), PasswordText.ToString());
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("Connecting replaces your open world with the server's,");
        ImGui.TextDisabled("unless the server has none yet: then yours becomes it.");
    }

    private static void Joined()
    {
        Client? c = Sync.Client;
        ImGui.Text(c is { ServerName.Length: > 0 } ? c.ServerName : "Server");
        ImGui.SameLine(0f, -1f);
        ImGui.TextDisabled(Sync.Status);
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
            if (Sync.ConflictingCraft.Count > 0)
            {
                float4 warn = new(1.0f, 0.5f, 0.3f, 1.0f);
                ImGui.TextColored(in warn, $"Rename for others to see: {string.Join(", ", Sync.ConflictingCraft)}");
            }
        }
        if (ImGui.Button("Disconnect", null)) Sync.Disconnect();
    }

    private static void Load()
    {
        _loaded = true;
        Settings s = new(Environment.UserName, "", "", true);
        try
        {
            if (File.Exists(SettingsPath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"could not read {SettingsPath}: {e.Message}");
        }
        NameText.SetValue(s.Name);
        AddressText.SetValue(s.Address);
        PasswordText.SetValue(s.Password);
        Visible = s.Visible;
    }

    /// <summary>Keeps what was typed, for the next session. Nothing to keep until the panel has read what there is.</summary>
    public static void Save()
    {
        if (!_loaded) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(NameText.ToString().Trim(), AddressText.ToString().Trim(), PasswordText.ToString(), Visible)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not write {SettingsPath}: {e.Message}");
        }
    }
}
