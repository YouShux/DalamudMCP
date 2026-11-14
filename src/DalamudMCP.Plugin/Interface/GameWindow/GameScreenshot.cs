using System.Numerics;
using System.Text.Json;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DalamudMCP.Shared;

namespace DalamudMCP.Plugin.Tools;

internal static class GameScreenshot
{
    private static readonly Guid PngFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);
    private static readonly Queue<CachedFrame> Frames = new();
    private static readonly object InputFramesLock = new();
    private static readonly Dictionary<Guid, ScreenshotInputFrame> InputFrames = new();
    private const int CACHE_BYTES = 64 * 1024 * 1024;
    private const int CACHE_COUNT = 4;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    internal static async Task<JsonElement> CaptureAsync(GameServices services, ScreenshotViewport? viewport, Func<ScreenshotViewport?> readViewport, ToolArguments input,
        bool structuredOnly, CancellationToken cancellationToken)
    {
        var includeUi = input.Boolean("include_dalamud_ui", true);
        var maxDimension = input.Integer("max_dimension", 1920, 256, 2560);
        var timeout = input.Integer("timeout_ms", 3000, 250, 5000);
        var crop = input.Has("crop") ? RenderCrop.Parse(input.Element("crop")) : (RenderCrop?)null;
        var reference = input.Has("screenshot_id") ? RepositoryPackages.ParseID(input.Text("screenshot_id")) : (Guid?)null;
        if (reference.HasValue && input.Has("include_dalamud_ui")) throw new ArgumentException("缓存帧的界面内容不能更改。");
        if (viewport is null && !reference.HasValue) return ToolJson.Success(new { status = "no_render_frame" }, structuredOnly);
        if (!CaptureSlot.Wait(0)) return ToolJson.Success(new { status = "busy" }, structuredOnly);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await CaptureFrameAsync(services, viewport, readViewport, includeUi, maxDimension, crop, reference, structuredOnly, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolJson.Success(new { status = "timeout" }, structuredOnly);
        }
    }

    private static async Task<JsonElement> CaptureFrameAsync(GameServices services, ScreenshotViewport? viewport, Func<ScreenshotViewport?> readViewport, bool includeUi,
        int maxDimension, RenderCrop? crop, Guid? reference, bool structuredOnly, CancellationToken token)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            while (Frames.TryPeek(out var oldest) && now - oldest.CapturedAt >= CacheLifetime) Frames.Dequeue();
            var cached = reference.HasValue ? Frames.FirstOrDefault(item => item.ID == reference.Value) : null;
            if (reference.HasValue && cached is null) return ToolJson.Success(new { status = "screenshot_not_found" }, structuredOnly);
            using var frame = cached is null
                ? await services.Textures.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
                {
                    ViewportId = viewport?.ID ?? 0, AutoUpdate = false, TakeBeforeImGuiRender = !includeUi,
                }, "DalamudMCP render frame", token).ConfigureAwait(false)
                : await services.Textures.CreateFromImageAsync(cached.PNG, "DalamudMCP cached frame", token).ConfigureAwait(false);
            var capturedAt = cached?.CapturedAt ?? DateTimeOffset.UtcNow;
            var capturedTick = cached?.CapturedTick ?? Environment.TickCount64;
            var frameViewport = cached is not null ? cached.Viewport : viewport == readViewport() ? viewport : null;
            includeUi = cached?.IncludesUI ?? includeUi;

            var width = frame.Width;
            var height = frame.Height;
            if (width <= 0 || height <= 0) return ToolJson.Success(new { status = "no_render_frame" }, structuredOnly);
            var region = crop ?? new(0, 0, width, height);
            if (!region.Fits(width, height)) return ToolJson.Success(new { status = "crop_out_of_bounds", sourceWidth = width, sourceHeight = height }, structuredOnly);
            var longest = Math.Max(region.Width, region.Height);
            var capped = Math.Min(longest, maxDimension);
            using var scaled = crop is null && longest <= maxDimension ? null : await services.Textures.CreateFromExistingTextureAsync(frame,
                new TextureModificationArgs
                {
                    NewWidth = (int)Math.Max(1, (long)region.Width * capped / longest),
                    NewHeight = (int)Math.Max(1, (long)region.Height * capped / longest),
                    Uv0 = new Vector2((float)region.X / width, (float)region.Y / height),
                    Uv1 = new Vector2((float)(region.X + region.Width) / width, (float)(region.Y + region.Height) / height),
                }, leaveWrapOpen: true, debugName: "DalamudMCP screenshot", cancellationToken: token).ConfigureAwait(false);

            IDalamudTextureWrap image = scaled ?? frame;
            using var stream = new MemoryStream();
            await services.TextureReadback.SaveToStreamAsync(image, PngFormat, stream, leaveWrapOpen: true,
                leaveStreamOpen: true, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (stream.Length == 0) return ToolJson.Success(new { status = "empty_image" }, structuredOnly);
            var screenshotID = cached?.ID;
            if (!reference.HasValue && (long)width * height * 4 <= CACHE_BYTES)
            {
                byte[] png;
                if (scaled is null) png = stream.Length <= CACHE_BYTES ? stream.ToArray() : [];
                else
                {
                    using var original = new MemoryStream();
                    await services.TextureReadback.SaveToStreamAsync(frame, PngFormat, original, leaveWrapOpen: true,
                        leaveStreamOpen: true, cancellationToken: token).ConfigureAwait(false);
                    png = original.Length is > 0 and <= CACHE_BYTES ? original.ToArray() : [];
                }
                token.ThrowIfCancellationRequested();
                if (png.Length is > 0 and <= CACHE_BYTES)
                {
                    while (Frames.Count >= CACHE_COUNT || Frames.Sum(item => (long)item.PNG.Length) + png.Length > CACHE_BYTES) Frames.Dequeue();
                    screenshotID = Guid.NewGuid();
                    Frames.Enqueue(new(screenshotID.Value, capturedAt, capturedTick, frameViewport, includeUi, png));
                }
            }
            Guid? inputFrameID = null;
            if (frameViewport is { Window.Foreground: true } && frameViewport == readViewport() &&
                Environment.TickCount64 - capturedTick < 15000)
                inputFrameID = SaveInputFrame(cached is null ? screenshotID : null,
                    new(frameViewport, region, width, height, image.Width, image.Height, capturedTick));
            return ToolJson.Element(new
            {
                content = new[] { new { type = "image", data = Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length),
                    mimeType = "image/png" } },
                structuredContent = new { status = "ok", backend = "dalamud_render_target", image.Width, image.Height,
                    sourceWidth = width, sourceHeight = height, includesDalamudUi = includeUi, crop, screenshotID, inputFrameID, capturedAt, fromCache = cached is not null },
                isError = false,
            });
        }
        finally { CaptureSlot.Release(); }
    }
    internal static ScreenshotInputFrame? GetInputFrame(Guid id)
    {
        lock (InputFramesLock) return InputFrames.GetValueOrDefault(id);
    }

    private static Guid SaveInputFrame(Guid? original, ScreenshotInputFrame frame)
    {
        lock (InputFramesLock)
        {
            var now = Environment.TickCount64;
            foreach (var id in InputFrames.Where(item => now - item.Value.CapturedTick >= 15000).Select(item => item.Key).ToArray())
                InputFrames.Remove(id);
            if (InputFrames.Count >= 16) InputFrames.Remove(InputFrames.Keys.First());
            var idForInput = original ?? Guid.NewGuid();
            InputFrames.Add(idForInput, frame);
            return idForInput;
        }
    }

    private sealed record CachedFrame(Guid ID, DateTimeOffset CapturedAt, long CapturedTick,
        ScreenshotViewport? Viewport, bool IncludesUI, byte[] PNG);
}

internal readonly record struct RenderCrop(int X, int Y, int Width, int Height)
{
    internal bool Fits(int width, int height) => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        width >= Width && height >= Height && X <= width - Width && Y <= height - Height;

    internal static RenderCrop Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("crop 必须为像素矩形对象。");
        var fields = new Dictionary<string, int>(4, StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (field.Name is not ("x" or "y" or "width" or "height") || field.Value.ValueKind != JsonValueKind.Number ||
                !field.Value.TryGetInt32(out var number) || !fields.TryAdd(field.Name, number))
                throw new ArgumentException("crop 必须包含不重复的整数 x、y、width、height。");
        if (fields.Count != 4 || fields["x"] < 0 || fields["y"] < 0 || fields["width"] <= 0 || fields["height"] <= 0)
            throw new ArgumentException("crop 坐标必须非负，宽高必须大于零。");
        return new(fields["x"], fields["y"], fields["width"], fields["height"]);
    }
}
