using System.Reflection;
using EmbyHistory.Core;
using SkiaSharp;

namespace EmbyHistory;

/// <summary>Full-color, contained artwork with a font-independent, amber history marker.</summary>
internal static class HistoryPosterRenderer
{
    private const long MaxSourcePixels = 40_000_000;
    private const string LabelResource = "EmbyHistory.Assets.removed-label.png";
    private static readonly SKRect Artwork = new(14, 14, 586, 886);

    public static byte[] Render(HistoryRecord record)
    {
        using var bitmap = new SKBitmap(600, 900);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x50, 0x38, 0x20));
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xDD, 0xA3, 0x43) };
        canvas.DrawRect(2, 2, 596, 896, fill);
        fill.Color = new SKColor(0xDE, 0xD6, 0xC8);
        canvas.DrawRect(Artwork, fill);
        using var source = LoadSource(record.Snapshot.PosterPath);
        if (source != null)
        {
            // One uniform scale preserves every edge: never stretch, crop, desaturate or dim.
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(source, Contain(source.Width, source.Height, Artwork), paint);
        }
        else DrawFallback(canvas, record.Code);
        using var edge = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            Color = new SKColor(0xFF, 0xE4, 0xA8)
        };
        canvas.DrawRect(13.5f, 13.5f, 573, 873, edge);
        DrawBadge(canvas);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        if (data == null) throw new InvalidOperationException("History poster encoding failed");
        return data.ToArray();
    }

    private static SKBitmap? LoadSource(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var info = codec.Info;
            // Inspect the encoded header BEFORE allocating/decompressing the source bitmap.
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels) return null;
            return SKBitmap.Decode(codec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, unreadable or corrupt artwork is a valid fallback, not a failed archive.
            return null;
        }
    }

    private static SKRect Contain(int width, int height, SKRect bounds)
    {
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var w = width * scale;
        var h = height * scale;
        return SKRect.Create(bounds.MidX - w / 2, bounds.MidY - h / 2, w, h);
    }

    private static void DrawBadge(SKCanvas canvas)
    {
        using var badge = new SKPath();
        badge.MoveTo(352, 24);
        badge.LineTo(586, 24);
        badge.LineTo(586, 116);
        badge.LineTo(328, 116);
        badge.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0x50, 0x38, 0x20, 75) };
        canvas.Save();
        canvas.Translate(0, 3);
        canvas.DrawPath(badge, paint);
        canvas.Restore();
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 24), new SKPoint(0, 116),
            new[] { new SKColor(0xF4, 0xCA, 0x74), new SKColor(0xE7, 0xAE, 0x49) }, SKShaderTileMode.Clamp);
        paint.Color = SKColors.White;
        paint.Shader = gradient;
        canvas.DrawPath(badge, paint);
        paint.Shader = null;
        paint.Color = new SKColor(0xFF, 0xE4, 0xA8, 210);
        paint.StrokeWidth = 1;
        canvas.DrawLine(354, 26, 584, 26, paint);

        // The embedded PNG contains only dark Chinese glyphs on transparency; no system fonts.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LabelResource)
            ?? throw new InvalidOperationException("Missing history badge resource: " + LabelResource);
        using var label = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Invalid history badge resource: " + LabelResource);
        paint.Color = SKColors.White;
        paint.FilterQuality = SKFilterQuality.High;
        canvas.DrawBitmap(label, Contain(label.Width, label.Height, new SKRect(354, 37, 574, 103)), paint);
    }

    private static void DrawFallback(SKCanvas canvas, string code)
    {
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(14, 14), new SKPoint(586, 886),
            new[] { new SKColor(0x2D, 0x3A, 0x46), new SKColor(0x45, 0x50, 0x58) }, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, Shader = gradient };
        canvas.DrawRect(Artwork, paint);
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 4;
        paint.Color = new SKColor(0xDD, 0xA3, 0x43, 210);
        canvas.DrawRoundRect(251, 350, 98, 77, 4, 4, paint);
        canvas.DrawRect(246, 337, 108, 22, paint);
        canvas.DrawLine(286, 379, 314, 379, paint);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(0xF1, 0xE7, 0xD5);
        BitmapText.Draw(canvas, "HISTORY", 176, 467, 6, paint);
        paint.Color = new SKColor(0xCD, 0xC7, 0xBD);
        BitmapText.Draw(canvas, "OFFLINE", 218, 535, 4, paint);
        if (!string.IsNullOrWhiteSpace(code))
        {
            var scale = Math.Min(4f, 480f / (code.Length * 6f));
            BitmapText.Draw(canvas, code, 300 - (code.Length * 6 - 1) * scale / 2, 598, scale, paint);
        }
    }
}
