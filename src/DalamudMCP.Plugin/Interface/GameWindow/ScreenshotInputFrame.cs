using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudMCP.Plugin.Tools;

internal sealed record ScreenshotViewport(uint ID, Vector2 Origin, Vector2 Size, GameWindowState.WindowIdentity? Window)
{
    internal static ScreenshotViewport? Read(ScreenshotViewport? previous = null, bool includeWindow = true)
    {
        if (ImGui.GetCurrentContext().IsNull) return null;
        var viewport = ImGui.GetMainViewport();
        if (viewport.IsNull || !float.IsFinite(viewport.Pos.X) || !float.IsFinite(viewport.Pos.Y) ||
            !float.IsFinite(viewport.Size.X) || !float.IsFinite(viewport.Size.Y) || viewport.Size.X <= 0 || viewport.Size.Y <= 0) return null;
        if (!includeWindow && previous is not null && previous.ID == viewport.ID &&
            previous.Origin == viewport.Pos && previous.Size == viewport.Size) return previous;
        return new(viewport.ID, viewport.Pos, viewport.Size, includeWindow ? GameWindowState.ReadInputWindow() : null);
    }
}

internal sealed record ScreenshotInputFrame(ScreenshotViewport Viewport, RenderCrop Region, int SourceWidth, int SourceHeight,
    int Width, int Height, long CapturedTick)
{
    internal bool IsCurrent(ScreenshotViewport? current) =>
        current is not null && Viewport == current && Environment.TickCount64 - CapturedTick is >= 0 and < 15000;

    internal Vector2 Map(int x, int y, bool game)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentException("坐标超出截图范围。");
        var normalized = new Vector2(
            (float)((Region.X + (x + .5) * Region.Width / Width) / SourceWidth),
            (float)((Region.Y + (y + .5) * Region.Height / Height) / SourceHeight));
        if (game && Viewport.Window is { } window)
            return new(Math.Clamp(MathF.Floor(normalized.X * window.Width), 0, window.Width - 1),
                Math.Clamp(MathF.Floor(normalized.Y * window.Height), 0, window.Height - 1));
        return Viewport.Origin + normalized * Viewport.Size;
    }
}
