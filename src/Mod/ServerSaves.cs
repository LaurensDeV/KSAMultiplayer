using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// The worlds the server keeps under names, for an admin to add to and load from: KSA's own
/// GAME SAVES window, with the server's saves in it instead of this machine's.
///
/// <para>It stands in for that window for as long as this game is in a server. A save loaded
/// there is this machine's, into this game alone, which is leaving the shared world by another
/// door; what a player in a server means by loading a save is the world everybody is in, and
/// that is the server's to change and an admin's to ask for. Anybody else who asks for the window
/// is told that only admins can.</para>
///
/// <para>Nothing here loads or saves anything. Each button is the console line an admin could
/// have typed into the chat, and the list is whatever the server last said it keeps.</para>
/// </summary>
internal static class ServerSaves
{
    private enum Column { Name, Size, Version, By, Date }

    /// <summary>KSA's window base, so the frame, the menu strip and the footer are the game's own and not a likeness of them.</summary>
    private sealed class Window : ImGuiWindow, IStaticWindow
    {
        internal Window() : base(new float2(1200f, 600f), lockAspectRatio: true, show: false)
        {
            SetWindowTitle("SERVER SAVES");
            UseConsoleChrome = true;
            ConsoleSignature = "KSA-MPS";
        }

        public override void DrawMenuBar()
        {
            if (ImGui.MenuItem("New"u8)) StringInputPopup.Create(Keep, "", "SAVE THE SERVER'S WORLD");
            Tip("Save the server's current world under a name you choose.");
            base.DrawMenuBar();
            ImGui.TextDisabled("(?)"u8);
            Tip("These saves are stored on the server. Double-click one to load it for everyone, or right-click for more options. "
                + "Loading first keeps the current world as an automatic 'before load' save, so you can undo it.");
        }

        protected override void DrawConsoleFooter()
        {
            int count = Sync.Saves?.Count ?? 0;
            string said = $"{count} {(count == 1 ? "SAVE" : "SAVES")}";
            bool waiting = _askedAt > Sync.ServerSaidAt && Sync.Wall() - _askedAt < AnswerWithinSeconds;
            if (waiting) said += "  -  WAITING FOR THE SERVER";
            else if (Sync.ServerSaidAt >= _askedAt && Sync.Wall() - Sync.ServerSaidAt < AnswerShownSeconds) said += $"  -  {Sync.ServerSaid.ToUpperInvariant()}";
            ConsoleStyle.FooterStatus(said.AsSpan(), waiting);
        }

        public override void DrawContent(IViewport viewport)
        {
            if (Sync.Saves is not { } saves)
            {
                ImGui.TextDisabled(Sync.Admin
                    ? "No save list from this server yet. A server older than this version of the mod doesn't support saves."
                    : "Only admins can save or load the world on this server.");
                return;
            }
            const ImGuiTableFlags flags = ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg
                                          | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.ScrollY;
            if (!ImGui.BeginTable("ksampserversaves"u8, 5, flags, (float2?)null)) return;
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Name"u8, ImGuiTableColumnFlags.None, 0f, (uint)Column.Name);
            ImGui.TableSetupColumn("Size"u8, ImGuiTableColumnFlags.None, 0.2f, (uint)Column.Size);
            ImGui.TableSetupColumn("Version"u8, ImGuiTableColumnFlags.None, 0.3f, (uint)Column.Version);
            ImGui.TableSetupColumn("Saved by"u8, ImGuiTableColumnFlags.None, 0.3f, (uint)Column.By);
            ImGui.TableSetupColumn("Date"u8, ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 0.3f, (uint)Column.Date);
            ImGui.TableHeadersRow();
            ImGuiTableSortSpecsPtr sort = ImGui.TableGetSortSpecs();
            if (!sort.IsNull() && sort.SpecsDirty)
            {
                ImGuiTableColumnSortSpecs by = sort.Specs.Value;
                (_column, _ascending, _sortedFrom) = ((Column)by.ColumnIndex, by.SortDirection == ImGuiSortDirection.Ascending, null);
                sort.SpecsDirty = false;
            }
            foreach (SavedWorld s in Sorted(saves)) Row(s);
            ImGui.EndTable();
        }
    }

    // How long a line the server says is shown under the list, and how long one is waited for
    // before the footer stops saying it is.
    private const double AnswerShownSeconds = 12.0, AnswerWithinSeconds = 5.0;

    private static Window? _window;
    private static string _selected = "";
    private static Column _column = Column.Date;
    private static bool _ascending;

    // The list last sorted and what it came to, so it is sorted when it changes and not each frame.
    private static IReadOnlyList<SavedWorld>? _sortedFrom;
    private static SavedWorld[] _sorted = [];

    // When this window last asked the server for anything, on Sync's clock: what it has said since is the answer.
    private static double _askedAt = double.NegativeInfinity;

    public static bool IsOpen => _window?.IsShown ?? false;

    /// <summary>Shown or put away, as KSA's own is by the same key. Made on first use: KSA's window wants a frame under way to place itself in.</summary>
    public static void Toggle()
    {
        _window ??= new Window();
        _window.Toggle();
    }

    /// <summary>
    /// The player has asked for KSA's save window. In a server that is this one for an admin and a
    /// sentence for anybody else; false when this game is in no server, and KSA's own is theirs.
    /// </summary>
    public static bool Asked()
    {
        if (Sync.Now == Sync.Stage.Off) return false;
        if (Sync.Admin) Toggle();
        else TimedAlert.CreateWarning("Only admins can save or load the world on this server.");
        return true;
    }

    /// <summary>
    /// Once a frame. KSA's own window is put away if it got open in a server anyway -- the patch
    /// that turns the key towards this one is lost if KSA moves the method -- and this one is, the
    /// moment this game is no admin or in no server.
    /// </summary>
    public static void Frame()
    {
        if (Sync.Now != Sync.Stage.Off && GameSaves.IsOpen)
        {
            GameSaves.Close();
            Asked();
        }
        if (IsOpen && !Sync.Admin) _window!.SetShown(false);
    }

    private static SavedWorld[] Sorted(IReadOnlyList<SavedWorld> saves)
    {
        if (ReferenceEquals(saves, _sortedFrom)) return _sorted;
        IEnumerable<SavedWorld> ordered = _column switch
        {
            Column.Name => saves.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            Column.Size => saves.OrderBy(s => s.Bytes),
            Column.Version => saves.OrderBy(s => s.KsaBuild, StringComparer.Ordinal),
            Column.By => saves.OrderBy(s => By(s), StringComparer.OrdinalIgnoreCase),
            _ => saves.OrderBy(s => s.SavedUtc),
        };
        (_sortedFrom, _sorted) = (saves, _ascending ? [.. ordered] : [.. ordered.Reverse()]);
        return _sorted;
    }

    // KSA's own tooltip helper wants a hover test this mod cannot name, so this is ImGui's.
    private static void Tip(string text)
    {
        if (!ImGui.BeginItemTooltip()) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
        ImGui.Text(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string By(SavedWorld s) => s.Auto ? "automatic" : s.By.Length > 0 ? s.By : "copied in by hand";

    private static void Row(SavedWorld s)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        const ImGuiSelectableFlags flags = ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick;
        string menu = $"####KSAMultiplayerSave{s.Name}";
        if (ImGui.Selectable($"{s.Name}###KSAMultiplayerSaveRow{s.Name}", _selected == s.Name, flags, (float2?)null))
        {
            _selected = s.Name;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) LoadOnConfirm(s.Name);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup(menu);
        if (ImGui.BeginPopup(menu))
        {
            if (ImGui.MenuItem("Load"u8)) LoadOnConfirm(s.Name);
            // The server's own are the way back from a load, and only its console removes them.
            if (!s.Auto)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Delete"u8))
                {
                    ConfirmActionPopup.Create(() => Ask($"delete {s.Name}"), "DELETE SERVER SAVE",
                                              $"Are you sure you want to delete the server save '{s.Name}'?");
                }
                if (ImGui.MenuItem("Overwrite"u8)) OverwriteOnConfirm(s.Name);
            }
            ImGui.EndPopup();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.TableNextColumn();
        ImGui.Text(Blob.Size(s.Bytes));
        ImGui.TableNextColumn();
        ImGui.Text(s.KsaBuild.Length > 0 ? s.KsaBuild : "-");
        ImGui.TableNextColumn();
        ImGui.Text(By(s));
        ImGui.TableNextColumn();
        ImGui.Text(s.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        ImGui.PopStyleColor();
    }

    private static void LoadOnConfirm(string name) =>
        ConfirmActionPopup.Create(() => Ask($"load {name}"), "LOAD SERVER SAVE",
                                  $"Load '{name}' for everyone on the server? The current world is kept first as an automatic 'before load' save.");

    private static void OverwriteOnConfirm(string name) =>
        ConfirmActionPopup.Create(() => Ask($"save {name}"), "OVERWRITE SERVER SAVE",
                                  $"Are you sure you want to overwrite the server save '{name}' with the current world?");

    // What the "New" popup hands back. KSA has tidied the name as it would one of its own saves;
    // the server's rule is its own, and said here rather than after a round trip.
    private static void Keep(string typed)
    {
        if (SaveList.Name(typed, out string why) is not { } name)
        {
            TimedAlert.CreateWarning($"Can't save: {why}.");
            return;
        }
        if (Sync.Saves?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } there) OverwriteOnConfirm(there.Name);
        else Ask($"save {name}");
    }

    private static void Ask(string line)
    {
        _askedAt = Sync.Wall();
        Sync.Ask(line);
    }
}
