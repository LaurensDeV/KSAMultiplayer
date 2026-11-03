using System.Text.Json;
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

    private sealed record Settings(string Name, string Address, string Password, bool Visible);

    private static readonly ImInputString NameText = new(Wire.NameChars * 4, ""u8);
    private static readonly ImInputString AddressText = new(128, ""u8);
    private static readonly ImInputString PasswordText = new(64, ""u8);
    private static readonly ImInputString ChatText = new(Wire.ChatChars * 4, ""u8);
    private static bool _loaded;

    public static bool Visible = true;

    private static string SettingsPath => Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMultiplayer", "settings.json");

    public static void Draw()
    {
        if (!_loaded) Load();

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
            ImGui.TextDisabled(double.IsFinite(c.Rtt) ? $"ping {c.Rtt * 1000:F0} ms" : "ping ...");
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
            ImGui.Separator();
            foreach (string line in Sync.Chat.TakeLast(8)) ImGui.TextWrapped(line);
            ImGui.SetNextItemWidth(-46f);
            if (ImGui.InputText("##chat", ChatText, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                Sync.SayChat(ChatText.ToString());
                ChatText.SetValue("");
            }
            ImGui.SameLine(0f, -1f);
            if (ImGui.Button("Say", null))
            {
                Sync.SayChat(ChatText.ToString());
                ChatText.SetValue("");
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
