using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SynthiaCode.App.Services;

public sealed record GeneratedImageEditSelection(byte[]? RegionGuidePng)
{
    public static GeneratedImageEditSelection EntireImage { get; } = new((byte[]?)null);

    public bool HasRegionGuide => RegionGuidePng is { Length: > 0 };
}

public enum GeneratedImageEditRegionKind
{
    Rectangle,
    Freehand
}

public readonly record struct NormalizedImagePoint(double X, double Y)
{
    public NormalizedImagePoint Clamp() => new(
        Math.Clamp(X, 0, 1),
        Math.Clamp(Y, 0, 1));
}

public sealed record GeneratedImageEditRegion(
    GeneratedImageEditRegionKind Kind,
    IReadOnlyList<NormalizedImagePoint> Points)
{
    public static GeneratedImageEditRegion Rectangle(
        NormalizedImagePoint start,
        NormalizedImagePoint end) =>
        new(GeneratedImageEditRegionKind.Rectangle, [start.Clamp(), end.Clamp()]);

    public static GeneratedImageEditRegion Freehand(
        IEnumerable<NormalizedImagePoint> points) =>
        new(
            GeneratedImageEditRegionKind.Freehand,
            points.Select(point => point.Clamp()).ToArray());
}

public static class GeneratedImageRegionGuideRenderer
{
    private static readonly Brush RegionFill = CreateFrozenBrush(Color.FromArgb(92, 255, 32, 32));
    private static readonly Brush RegionStroke = CreateFrozenBrush(Color.FromArgb(220, 255, 32, 32));

    public static byte[] RenderPng(BitmapSource source, GeneratedImageEditRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(region);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new ArgumentException("The source image has invalid dimensions.", nameof(source));
        }

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var imageBounds = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            drawing.DrawImage(source, imageBounds);
            DrawRegion(drawing, imageBounds, region);
        }

        var rendered = new RenderTargetBitmap(
            source.PixelWidth,
            source.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void DrawRegion(
        DrawingContext drawing,
        Rect imageBounds,
        GeneratedImageEditRegion region)
    {
        switch (region.Kind)
        {
            case GeneratedImageEditRegionKind.Rectangle:
                DrawRectangle(drawing, imageBounds, region.Points);
                break;
            case GeneratedImageEditRegionKind.Freehand:
                DrawFreehand(drawing, imageBounds, region.Points);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(region), region.Kind, "Unsupported image edit region.");
        }
    }

    private static void DrawRectangle(
        DrawingContext drawing,
        Rect imageBounds,
        IReadOnlyList<NormalizedImagePoint> points)
    {
        if (points.Count != 2)
        {
            throw new ArgumentException("A rectangle selection requires two points.", nameof(points));
        }

        var start = ToPixelPoint(points[0], imageBounds);
        var end = ToPixelPoint(points[1], imageBounds);
        var rectangle = new Rect(start, end);
        if (rectangle.Width < 1 || rectangle.Height < 1)
        {
            throw new ArgumentException("The selected rectangle is too small.", nameof(points));
        }

        drawing.DrawRectangle(
            RegionFill,
            new Pen(RegionStroke, StrokeWidth(imageBounds)),
            rectangle);
    }

    private static void DrawFreehand(
        DrawingContext drawing,
        Rect imageBounds,
        IReadOnlyList<NormalizedImagePoint> points)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("A freehand selection requires at least two points.", nameof(points));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPixelPoint(points[0], imageBounds), false, false);
            foreach (var point in points.Skip(1))
            {
                context.LineTo(ToPixelPoint(point, imageBounds), true, false);
            }
        }
        geometry.Freeze();

        drawing.DrawGeometry(
            null,
            new Pen(RegionStroke, Math.Max(12, Math.Min(imageBounds.Width, imageBounds.Height) * 0.035))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            },
            geometry);
    }

    private static Point ToPixelPoint(NormalizedImagePoint point, Rect bounds)
    {
        var clamped = point.Clamp();
        return new Point(
            bounds.Left + clamped.X * bounds.Width,
            bounds.Top + clamped.Y * bounds.Height);
    }

    private static double StrokeWidth(Rect bounds) =>
        Math.Max(3, Math.Min(bounds.Width, bounds.Height) * 0.008);

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
