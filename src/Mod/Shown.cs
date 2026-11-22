using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSAMultiplayer;

/// <summary>
/// Text in a window as it is. The binding's <c>TextDisabled</c>, <c>TextWrapped</c> and
/// <c>TextColored</c> hand what they are given to ImGui as a printf format with nothing after it,
/// and most of what this mod shows was typed on another machine: a name, a line of chat, what a
/// server calls itself. <c>ImGui.Text</c> is the one that takes text for text, so these are it, in
/// the colour or the wrapping the others gave.
/// </summary>
internal static class Shown
{
    public static void Coloured(in float4 colour, ImString text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, in colour);
        ImGui.Text(text);
        ImGui.PopStyleColor();
    }

    public static void Dim(ImString text)
    {
        float4 colour = ImGui.GetStyleColorVec4(ImGuiCol.TextDisabled);
        Coloured(in colour, text);
    }

    public static void Wrapped(ImString text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.Text(text);
        ImGui.PopTextWrapPos();
    }
}
