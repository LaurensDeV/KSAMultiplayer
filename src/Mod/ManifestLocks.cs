using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAMultiplayer;

/// <summary>
/// A padlock on each craft's row of KSA's universe manifest, in the room the row keeps for a tree
/// arrow: a craft has nothing under it, so the room is empty. Shut for a craft the server protects,
/// which everybody sees, since it is why Delete and Clear Debris leave it alone; open and faint for
/// the rest, which only an admin sees, because only an admin can shut it.
///
/// <para>Drawn before the row's name is, which is what lets the padlock take the click: ImGui
/// gives the mouse to the first thing under it that does not share, and the name is put down
/// after this sharing its place with whatever came first.</para>
/// </summary>
internal static class ManifestLocks
{
    private static readonly ImColor8 Shut = new(235, 190, 90, 255);
    private static readonly ImColor8 Open = new(160, 160, 160, 110);
    private static readonly ImColor8 OpenHovered = new(235, 190, 90, 200);

    private static bool _complained;

    /// <summary>Called by the manifest for every row, just before the row's name.</summary>
    public static void BeforeName(Astronomical? body)
    {
        try
        {
            if (body is not Vehicle v) return;
            bool locked = Sync.IsProtected(v);
            // Protecting is a trusted player's as much as an admin's.
            bool admin = Sync.Trusted;
            if (!locked && !admin) return;

            float font = ImGui.GetFontSize();
            float2 at = ImGui.GetCursorScreenPos();
            float padding = ImGui.GetStyle().FramePadding.X;
            float tall = ImGui.GetFrameHeight();
            float2 min = new(at.X + padding, at.Y), max = new(at.X + padding + font, at.Y + tall);

            bool hovered;
            if (admin)
            {
                ImGui.PushID(v.GetHashCode());
                ImGui.SetCursorScreenPos(min);
                bool clicked = ImGui.InvisibleButton("ksamp-lock"u8, max - min);
                hovered = ImGui.IsItemHovered();
                if (hovered) Tip(locked
                    ? "This craft is protected. Click to unprotect it."
                    : "Click to protect this craft, so nobody can delete, recover or clear it.");
                ImGui.PopID();
                // Back where the row began, which is above everything the button took up: the name
                // goes there as if nothing had been, and the cell is no taller for it.
                ImGui.SetCursorScreenPos(at);
                if (clicked) Sync.Ask($"{(locked ? "unprotect" : "protect")} {Sync.KeyOf(v)}");
            }
            else
            {
                hovered = ImGui.IsMouseHoveringRect(min, max);
                if (hovered) Tip("This craft is protected, so it can't be deleted, recovered or cleared.");
            }

            Draw(min, max, font, locked, locked ? Shut : hovered ? OpenHovered : Open);
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("drawing a padlock in the universe manifest failed", e);
        }
    }

    // A shackle over a body, open by lifting the shackle's right foot off the body.
    private static void Draw(float2 min, float2 max, float font, bool locked, ImColor8 colour)
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        float wide = font * 0.62f, cx = (min.X + max.X) * 0.5f, cy = (min.Y + max.Y) * 0.5f;
        float bodyTop = cy - (font * 0.05f), bodyBottom = cy + (font * 0.38f);
        float r = wide * 0.32f, thick = MathF.Max(1.5f, font * 0.1f);
        float lift = locked ? 0f : font * 0.18f;
        draw.AddRectFilled(new float2(cx - (wide / 2f), bodyTop), new float2(cx + (wide / 2f), bodyBottom), colour, font * 0.08f);
        float archY = bodyTop - (font * 0.12f) - lift;
        draw.PathArcTo(new float2(cx, archY), r, MathF.PI, MathF.PI * 2f);
        draw.PathStroke(colour, ImDrawFlags.None, thick);
        draw.AddLine(new float2(cx - r, archY), new float2(cx - r, bodyTop), colour, thick);
        draw.AddLine(new float2(cx + r, archY), new float2(cx + r, bodyTop - lift), colour, thick);
    }

    private static void Tip(string text)
    {
        if (!ImGui.BeginTooltip()) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
        ImGui.Text(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
