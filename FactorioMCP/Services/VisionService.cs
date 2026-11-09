using System.Globalization;
using FactorioMCP.Rcon;
using FactorioMCP.Screenshots;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FactorioMCP.Services;

/// <summary>
/// Service for capturing annotated screenshots of the game world via RCON.
/// Draws temporary rendering overlays (entity bounding boxes, inserter direction arrows,
/// numbered labels) before taking a screenshot, then collects a structured "Map Legend"
/// describing every visible entity for vision-model analysis.
/// </summary>
internal sealed class VisionService : IDisposable
{
    private readonly RconClient _rcon;
    private readonly ScreenshotRetriever _retriever;
    private readonly int? _playerIndex;

    public VisionService(RconClient rcon) : this(rcon, ScreenshotSettings.FromEnvironment()) { }

    internal VisionService(RconClient rcon, ScreenshotSettings settings)
    {
        _rcon = rcon;
        _retriever = new ScreenshotRetriever(settings);
        if (Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_PLAYER") is not null)
            _playerIndex = ScreenshotSettings.ReadInt("FACTORIO_SCREENSHOT_PLAYER", 1, 1, int.MaxValue);
    }

    internal sealed record Capture(string Legend, byte[]? Image, string? Diagnostic);

    // The caller holds GameCommandQueue through capture AND retrieval, not just the RCON call.
    internal async Task<Capture> CaptureAsync(double? x, double? y, double zoom, int width, int height, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var legend = await TakeScreenshotAsync(x, y, zoom, width, height, ct, id).ConfigureAwait(false);
        try
        {
            using var parsed = JsonDocument.Parse(legend);
            if (parsed.RootElement.TryGetProperty("screenshot_error", out var error))
                return new Capture(legend, null, error.GetString());
        }
        catch (JsonException)
        {
            return new Capture(legend, null, "Factorio did not return a valid map legend; capture was not confirmed.");
        }
        try
        {
            return new Capture(legend, await _retriever.RetrieveAsync(id, ct).ConfigureAwait(false), null);
        }
        catch (ScreenshotUnavailableException ex)
        {
            return new Capture(legend, null, ex.Message);
        }
    }

    public void Dispose() => _retriever.Dispose();

    /// <summary>
    /// Maximum image size in bytes before optimization kicks in (1 MB).
    /// Images larger than this are re-encoded as JPEG and/or downscaled.
    /// </summary>
    internal const int MaxImageSizeBytes = 1_048_576;

    /// <summary>
    /// Maximum pixel dimension (width or height) for images sent to the LLM.
    /// Keeping images at or below this size dramatically reduces the token count
    /// that vision models must process, which is the main source of latency.
    /// </summary>
    internal const int MaxDimension = 512;

    /// <summary>
    /// JPEG encoding quality used when optimizing images for the LLM.
    /// </summary>
    private const int JpegQuality = 80;

    /// <summary>
    /// Take an annotated screenshot centered on a position (defaults to player position).
    /// Draws temporary rendering overlays on entities in the visible area, takes the
    /// screenshot, waits for file write, then returns a structured JSON map legend
    /// describing all entities in view.
    /// </summary>
    /// <param name="centerX">Optional X center coordinate (defaults to player position).</param>
    /// <param name="centerY">Optional Y center coordinate (defaults to player position).</param>
    /// <param name="zoom">Map zoom level (default 1.0). Higher = more zoomed in.</param>
    /// <param name="width">Screenshot width in pixels (default 1920).</param>
    /// <param name="height">Screenshot height in pixels (default 1080).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON map legend describing entities in the screenshot area.</returns>
    public Task<string> TakeScreenshotAsync(
        double? centerX = null,
        double? centerY = null,
        double zoom = 1.0,
        int width = 1920,
        int height = 1080,
        CancellationToken cancellationToken = default,
        string? captureId = null)
    {
        if (!double.IsFinite(zoom) || (centerX.HasValue && !double.IsFinite(centerX.Value)) ||
            (centerY.HasValue && !double.IsFinite(centerY.Value)))
            throw new ArgumentOutOfRangeException(nameof(zoom), "Zoom and coordinates must be finite.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(zoom, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ScreenshotSettings.MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ScreenshotSettings.MaxDimension);
        var lua = BuildScreenshotLua(centerX, centerY, zoom, width, height, captureId, _playerIndex);
        return _rcon.ExecuteLuaAsync(lua, cancellationToken);
    }

    /// <summary>
    /// Build the Lua script that draws overlays, takes a screenshot, and collects the map legend.
    /// Visible for testing.
    /// </summary>
    internal static string BuildScreenshotLua(
        double? centerX,
        double? centerY,
        double zoom,
        int width,
        int height,
        string? captureId = null,
        int? playerIndex = null)
    {
        var screenshotPath = ScreenshotFiles.RelativePath(captureId ?? Guid.NewGuid().ToString("N"));
        var playerExpression = playerIndex.HasValue ? $"game.get_player({playerIndex.Value})" : "game.connected_players[1]";
        // Calculate the world-space radius visible in the screenshot.
        // Factorio's zoom=1 means 32 pixels per tile. The visible area in tiles
        // is (pixels / (32 * zoom)). We use half the larger dimension as search radius
        // and add a small margin.
        var posExpr = centerX.HasValue && centerY.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{{x={centerX.Value},y={centerY.Value}}}")
            : "player.position";

        return string.Create(CultureInfo.InvariantCulture, $$"""
            {{FactorioService.LuaJsonEscape}}
            local player = {{playerExpression}}
            if not player or not player.connected then
                rcon.print('{"entity_count":0,"entities":[],"screenshot_error":"No connected screenshot player. Join with graphical Factorio; headless cannot capture."}')
                return
            end
            local surface = player.surface
            local center = {{posExpr}}
            local cx = center.x
            local cy = center.y

            -- Calculate visible area radius from resolution and zoom
            local zoom = {{zoom}}
            local pix_w = {{width}}
            local pix_h = {{height}}
            local tiles_w = pix_w / (32 * zoom)
            local tiles_h = pix_h / (32 * zoom)
            local scan_radius = math.max(tiles_w, tiles_h) / 2 + 2

            -- Find all entities in the visible area
            local lt = {cx - scan_radius, cy - scan_radius}
            local rb = {cx + scan_radius, cy + scan_radius}
            local entities = surface.find_entities_filtered{
                area={left_top=lt, right_bottom=rb}
            }

            -- Direction name lookup
            local dir_names = {}
            for k, v in pairs(defines.direction) do dir_names[v] = k end

            -- Color palette for entity type groups
            local colors = {
                inserter     = {r=1,   g=0.4, b=0,   a=0.8},
                belt         = {r=0.2, g=0.8, b=1,   a=0.6},
                assembler    = {r=0.4, g=1,   b=0.4, a=0.8},
                furnace      = {r=1,   g=0.6, b=0.2, a=0.8},
                miner        = {r=0.8, g=0.8, b=0.2, a=0.8},
                chest        = {r=0.8, g=0.4, b=0.8, a=0.8},
                pole         = {r=0.6, g=0.6, b=1,   a=0.6},
                pipe         = {r=0.4, g=0.6, b=0.8, a=0.5},
                lab          = {r=1,   g=1,   b=0.4, a=0.8},
                default_type = {r=0.7, g=0.7, b=0.7, a=0.5}
            }

            local function get_color(etype)
                if string.find(etype, "inserter") then return colors.inserter end
                if string.find(etype, "belt") or string.find(etype, "splitter") or string.find(etype, "underground") or string.find(etype, "loader") then return colors.belt end
                if string.find(etype, "assembling") then return colors.assembler end
                if string.find(etype, "furnace") then return colors.furnace end
                if string.find(etype, "mining") or string.find(etype, "drill") then return colors.miner end
                if string.find(etype, "chest") or string.find(etype, "container") then return colors.chest end
                if string.find(etype, "pole") then return colors.pole end
                if string.find(etype, "pipe") then return colors.pipe end
                if string.find(etype, "lab") then return colors.lab end
                return colors.default_type
            end

            -- Clear only this tool's previous overlays before drawing the new capture.
            -- Current overlays must survive the command until the renderer runs.
            for _, previous in pairs(storage._factorio_mcp_vision_overlays or {}) do
                if previous.valid then previous.destroy() end
            end
            local render_ids = {}
            storage._factorio_mcp_vision_overlays = render_ids
            local legend_parts = {}
            local idx = 0

            for _, e in pairs(entities) do
                -- Skip resources, trees, and decoratives (clutter)
                if e.type ~= "resource" and e.type ~= "tree" and e.type ~= "simple-entity"
                   and e.type ~= "cliff" and e.type ~= "fish" and e.type ~= "character" then
                    idx = idx + 1
                    local bb = e.bounding_box
                    local lt = bb.left_top
                    local rb = bb.right_bottom
                    local col = get_color(e.type)

                    -- Draw bounding box
                    local rect = rendering.draw_rectangle{
                        color=col, width=2, filled=false,
                        left_top=lt, right_bottom=rb,
                        surface=surface, time_to_live=600
                    }
                    render_ids[#render_ids+1] = rect

                    -- Draw label with index number
                    local label = rendering.draw_text{
                        text=tostring(idx),
                        surface=surface,
                        target={lt.x, lt.y - 0.3},
                        color={r=1, g=1, b=1, a=1},
                        scale=1.2,
                        time_to_live=600
                    }
                    render_ids[#render_ids+1] = label

                    -- Build legend entry
                    local entry = '{"id":'..idx
                        ..',"name":"'..esc(e.name)..'"'
                        ..',"type":"'..esc(e.type)..'"'
                        ..',"x":'..string.format("%.1f", e.position.x)
                        ..',"y":'..string.format("%.1f", e.position.y)

                    local dn = dir_names[e.direction]
                    if dn then
                        entry = entry..',"direction":"'..dn..'"'
                    end

                    -- Inserter-specific: pickup and drop positions
                    if e.type == "inserter" then
                        local pp = e.pickup_position
                        local dp = e.drop_position
                        entry = entry..',"pickup_x":'..string.format("%.1f", pp.x)
                            ..',"pickup_y":'..string.format("%.1f", pp.y)
                            ..',"drop_x":'..string.format("%.1f", dp.x)
                            ..',"drop_y":'..string.format("%.1f", dp.y)

                        -- Draw pickup->drop arrow (line from pickup to drop)
                        local arrow = rendering.draw_line{
                            color={r=1, g=0.2, b=0.2, a=0.9},
                            width=3,
                            from=pp, to=dp,
                            surface=surface,
                            time_to_live=600
                        }
                        render_ids[#render_ids+1] = arrow
                    end

                    -- Assembler/furnace: show current recipe if set
                    if e.type == "assembling-machine" then
                        local recipe = e.get_recipe()
                        if recipe then
                            entry = entry..',"recipe":"'..esc(recipe.name)..'"'
                        end
                    end

                    -- Status for machines
                    if e.status then
                        local status_names = {}
                        for k, v in pairs(defines.entity_status) do status_names[v] = k end
                        local sn = status_names[e.status]
                        if sn then
                            entry = entry..',"status":"'..sn..'"'
                        end
                    end

                    entry = entry..'}'
                    legend_parts[#legend_parts+1] = entry
                end
            end

            -- Requests render after the update. Do NOT destroy overlays in this command.
            local capture_ok, capture_error = pcall(function()
            game.take_screenshot{
                player=player,
                by_player=player,
                surface=surface,
                force_render=true,
                position={cx, cy},
                resolution={x={{width}}, y={{height}}},
                zoom=zoom,
                path="{{screenshotPath}}",
                show_gui=false,
                show_entity_info=true,
                anti_alias=false,
                quality=80
            }

            -- Flush queued writes; this is NOT proof that this graphical peer rendered a file.
            game.set_wait_for_screenshots_to_finish()
            end)
            local capture_diagnostic = ''
            if not capture_ok then
                capture_diagnostic = ',"screenshot_error":"'..esc(tostring(capture_error))..'"'
            end
            -- TTL cleans up overlays after the asynchronous renderer has used them.

            -- Output map legend
            rcon.print('{"entity_count":'..idx
                ..',"center_x":'..string.format("%.1f", cx)
                ..',"center_y":'..string.format("%.1f", cy)
                ..',"zoom":'..zoom
                ..',"tiles_wide":'..string.format("%.1f", tiles_w)
                ..',"tiles_high":'..string.format("%.1f", tiles_h)
                ..',"entities":['..table.concat(legend_parts, ",")..']'..capture_diagnostic..'}')
            """);
    }

    /// <summary>
    /// Optimize image for LLM consumption by downscaling to <see cref="MaxDimension"/> pixels
    /// on the longest side and encoding as JPEG. This reduces both the base64 payload size
    /// and the token count that vision models must process, which is the main source of latency
    /// after the screenshot is returned.
    /// </summary>
    /// <param name="imageBytes">Raw image bytes (typically PNG from Factorio).</param>
    /// <param name="maxDimension">Maximum pixel dimension on the longest side.</param>
    /// <returns>
    /// A tuple of (optimized image bytes, MIME type). If the image already fits within
    /// <paramref name="maxDimension"/>, it is JPEG-encoded without resizing. Otherwise it is
    /// downscaled proportionally first.
    /// </returns>
    internal static (byte[] Data, string MimeType) OptimizeImage(byte[] imageBytes, int maxDimension = MaxDimension)
    {
        using var image = Image.Load(ScreenshotFiles.DecodeOptions, imageBytes);

        var longestSide = Math.Max(image.Width, image.Height);

        if (longestSide > maxDimension)
        {
            var scale = (double)maxDimension / longestSide;
            var newWidth = Math.Max(1, (int)(image.Width * scale));
            var newHeight = Math.Max(1, (int)(image.Height * scale));
            image.Mutate(ctx => ctx.Resize(newWidth, newHeight));
        }

        return (EncodeJpeg(image, JpegQuality), "image/jpeg");
    }

    private static byte[] EncodeJpeg(Image image, int quality)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }
}
