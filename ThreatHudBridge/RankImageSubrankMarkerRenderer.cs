using System.Drawing;
using System.Drawing.Drawing2D;

internal static class RankImageSubrankMarkerRenderer
{
    private readonly record struct Segment(
        PointF Start,
        PointF End
    );

    private readonly record struct GradientLayer(
        float RadiusFactor,
        byte Alpha
    );

    private static readonly GradientLayer[] GradientLayers =
    [
        new(1.00f, 40),
        new(0.75f, 84),
        new(0.50f, 148),
        new(0.25f, 224)
    ];

    public static void Draw(
        Bitmap outputBitmap,
        int sourceWidth,
        int sourceHeight,
        int sourceOffset,
        byte subrank,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(outputBitmap);

        if (subrank is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subrank),
                "subrank must be from 1 to 6."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        var layoutScale = MathF.Round(Math.Max(
            12.0f,
            Math.Min(sourceWidth, sourceHeight) * 0.38f
        ));

        var markerHeight = MathF.Round(layoutScale * 0.85f);

        var centerX = MathF.Round(sourceOffset + sourceWidth / 2.0f);
        var requestedMarkerCenterY = MathF.Round(
            sourceOffset +
            sourceHeight * 0.95f -
            layoutScale / 2.0f
        );
        var markerTop = MathF.Round(
            requestedMarkerCenterY -
            markerHeight / 2.0f
        );
        var markerBottom = markerTop + markerHeight;
        var previousWhiteStrokeWidth = MathF.Round(
            Math.Max(4.0f, layoutScale * 0.16f)
        );
        var whiteStrokeWidth = previousWhiteStrokeWidth * 1.05f;
        var previousDarkOutlineRadius = MathF.Round(
            Math.Max(2.0f, layoutScale * 0.08f)
        );
        var darkOutlineRadius = previousDarkOutlineRadius * 1.30f;

        using var graphics = Graphics.FromImage(outputBitmap);

        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (subrank == 6)
        {
            DrawFivePointStar(
                graphics,
                centerX,
                markerTop,
                markerBottom,
                layoutScale,
                darkOutlineRadius
            );
        }
        else
        {
            var segments = BuildSegments(
                subrank,
                centerX,
                markerTop,
                markerBottom,
                layoutScale,
                whiteStrokeWidth
            );

            DrawGradientOutline(
                graphics,
                segments,
                whiteStrokeWidth,
                darkOutlineRadius
            );

            using var whitePen = CreatePen(
                Color.FromArgb(255, 250, 250, 250),
                whiteStrokeWidth
            );

            DrawSegments(graphics, whitePen, segments);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Segment[] BuildSegments(
        byte subrank,
        float centerX,
        float top,
        float bottom,
        float horizontalScale,
        float strokeWidth
    )
    {
        if (subrank <= 3)
        {
            var spacing = MathF.Round(Math.Max(
                strokeWidth * 2.5f,
                horizontalScale * 0.28f
            ));
            var segments = new Segment[subrank];

            for (var index = 0; index < subrank; index++)
            {
                var x = MathF.Round(
                    centerX +
                    (index - (subrank - 1) / 2.0f) * spacing
                );

                segments[index] = Vertical(x, top, bottom);
            }

            return segments;
        }

        if (subrank == 4)
        {
            var vHalfWidth = horizontalScale * 0.23f;
            var gap = Math.Max(
                strokeWidth * 1.2f,
                horizontalScale * 0.10f
            );
            var totalWidth = strokeWidth + gap + vHalfWidth * 2.0f;
            var iX = centerX - totalWidth / 2.0f + strokeWidth / 2.0f;
            var vCenterX = centerX + totalWidth / 2.0f - vHalfWidth;

            return
            [
                Vertical(iX, top, bottom),
                new Segment(
                    Point(vCenterX - vHalfWidth, top),
                    Point(vCenterX, bottom)
                ),
                new Segment(
                    Point(vCenterX + vHalfWidth, top),
                    Point(vCenterX, bottom)
                )
            ];
        }

        if (subrank == 5)
        {
            var vHalfWidth = horizontalScale * 0.23f;

            return
            [
                new Segment(
                    Point(centerX - vHalfWidth, top),
                    Point(centerX, bottom)
                ),
                new Segment(
                    Point(centerX + vHalfWidth, top),
                    Point(centerX, bottom)
                )
            ];
        }

        throw new ArgumentOutOfRangeException(
            nameof(subrank),
            "Line marker supports subranks from 1 to 5."
        );
    }

    private static void DrawGradientOutline(
        Graphics graphics,
        IReadOnlyList<Segment> segments,
        float whiteStrokeWidth,
        float outlineRadius
    )
    {
        foreach (var layer in GradientLayers)
        {
            using var pen = CreatePen(
                Color.FromArgb(layer.Alpha, 8, 8, 8),
                whiteStrokeWidth +
                    outlineRadius * layer.RadiusFactor * 2.0f
            );

            DrawSegments(graphics, pen, segments);
        }
    }

    private static void DrawFivePointStar(
        Graphics graphics,
        float centerX,
        float markerTop,
        float markerBottom,
        float horizontalScale,
        float outlineRadius
    )
    {
        var points = BuildFivePointStar(
            centerX,
            markerTop,
            markerBottom,
            horizontalScale
        );

        using var path = new GraphicsPath();

        path.AddPolygon(points);

        foreach (var layer in GradientLayers)
        {
            using var pen = CreateStarOutlinePen(
                Color.FromArgb(layer.Alpha, 8, 8, 8),
                outlineRadius * layer.RadiusFactor * 2.0f
            );

            graphics.DrawPath(pen, path);
        }

        using var whiteBrush = new SolidBrush(
            Color.FromArgb(255, 250, 250, 250)
        );

        graphics.FillPath(whiteBrush, path);
    }

    private static PointF[] BuildFivePointStar(
        float centerX,
        float markerTop,
        float markerBottom,
        float horizontalScale
    )
    {
        const float InnerRadiusRatio = 0.38196602f;
        const float Sin54Degrees = 0.80901700f;
        const float Cos18Degrees = 0.95105654f;

        var markerHeight = markerBottom - markerTop;
        var desiredHalfWidth = horizontalScale * 0.45f;
        var outerRadiusX = desiredHalfWidth / Cos18Degrees;
        var outerRadiusY = markerHeight / (1.0f + Sin54Degrees);
        var centerY = markerTop + outerRadiusY;
        var points = new PointF[10];

        for (var index = 0; index < points.Length; index++)
        {
            var angle =
                -MathF.PI / 2.0f +
                index * MathF.PI / 5.0f;

            var radiusRatio =
                index % 2 == 0
                    ? 1.0f
                    : InnerRadiusRatio;

            points[index] = new PointF(
                centerX +
                    MathF.Cos(angle) *
                    outerRadiusX *
                    radiusRatio,

                centerY +
                    MathF.Sin(angle) *
                    outerRadiusY *
                    radiusRatio
            );
        }

        return points;
    }

    private static Segment Vertical(float x, float top, float bottom)
    {
        return new Segment(
            Point(x, top),
            Point(x, bottom)
        );
    }

    private static PointF Point(float x, float y)
    {
        return new PointF(
            MathF.Round(x),
            MathF.Round(y)
        );
    }

    private static Pen CreatePen(Color color, float width)
    {
        return new Pen(color, width)
        {
            StartCap = LineCap.Square,
            EndCap = LineCap.Square,
            LineJoin = LineJoin.Round
        };
    }

    private static Pen CreateStarOutlinePen(Color color, float width)
    {
        return new Pen(color, width)
        {
            LineJoin = LineJoin.Miter,
            MiterLimit = 5.0f
        };
    }

    private static void DrawSegments(
        Graphics graphics,
        Pen pen,
        IReadOnlyList<Segment> segments
    )
    {
        foreach (var segment in segments)
        {
            graphics.DrawLine(pen, segment.Start, segment.End);
        }
    }
}
