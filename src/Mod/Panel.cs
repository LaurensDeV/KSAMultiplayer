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
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                                           | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

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
        float2 at = new(screen.WorkPos.X + screen.WorkSize.X - 360f, screen.WorkPos.Y + 80f);
        ImGui.SetNextWindowPos(in at, ImGuiCond.FirstUseEver, null);

        if (!Visible)
        {
            if (ImGui.Begin("Multiplayer##reopen", Flags | ImGuiWindowFlags.NoTitleBar))
            {
                string label = Sync.Now == Sync.Stage.Off ? "MP" : $"MP: {Sync.Client?.Players.Length ?? 0}";
                if (ImGui.SmallButton(label)) Visible = true;
            }
            ImGui.End();
            return;
        }

        bool open = true;
        if (ImGui.Begin("Multiplayer###KSAMultiplayer", ref open, Flags))
        {
            if (Sync.Now == Sync.Stage.Off) Join();
            else Joined();
            foreach (string said in Sync.Said.TakeLast(3)) ImGui.TextDisabled(said);
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
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("Server", AddressText);
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("Your name", NameText);
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("Password", PasswordText, ImGuiInputTextFlags.Password);
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
            ImGui.SetNextItemWidth(260f);
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

    private static void Save()
    {
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
