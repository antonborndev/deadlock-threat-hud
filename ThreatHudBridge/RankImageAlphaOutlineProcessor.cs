using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal static class RankImageAlphaOutlineProcessor
{
    /*
     * Almost invisible alpha noise must not
     * become part of the outer outline.
     *
     * Regular anti-aliasing is preserved.
     */
    private const byte AlphaThreshold =
        16;

    private const int MaximumSourceDimension =
        2_048;

    private const long MaximumSourcePixels =
        4_194_304;

    /*
     * Panorama displays the result in a 38 px box. Processing a 2K/4K source
     * at native size only multiplies mask/outline memory without improving the
     * visible result.
     */
    private const int MaximumWorkingDimension =
        256;

    private const long MaximumOutputPixels =
        250_000;

    /*
     * The transparent canvas padding remains unchanged.
     *
     * Previously:
     *
     * canvasPadding / 4
     * 31 / 4 ≈ 8 px
     *
     * Now:
     *
     * canvasPadding / 2
     * 31 / 2 ≈ 16 px
     *
     * This means the gradient outline itself
     * becomes approximately twice as wide,
     * while the canvas and rank image size
     * for Panorama remain unchanged.
     */
    private const int OutlineThicknessDivisor =
        2;

    private readonly record struct OutlineLayer(
        int Radius,
        byte Alpha
    );

    private readonly record struct OutlineCoverageLayer(
        int[] CoverageDeltas,
        byte Alpha
    );

    public static byte[] AddWhiteOutlineAndSubrankMarker(
        byte[] sourcePng,
        byte subrank,
        int targetDisplayBoxPixels,
        int canvasPaddingPixelsAtDisplay,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(
            sourcePng
        );

        if (sourcePng.Length == 0)
        {
            throw new ArgumentException(
                "Source rank PNG is empty.",
                nameof(sourcePng)
            );
        }

        if (
            subrank < 1 ||
            subrank > 6
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(subrank),
                "subrank must be from 1 to 6."
            );
        }

        if (targetDisplayBoxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDisplayBoxPixels),
                "Display size must be greater than 0."
            );
        }

        if (
            canvasPaddingPixelsAtDisplay <= 0 ||
            targetDisplayBoxPixels <=
                canvasPaddingPixelsAtDisplay * 2
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(canvasPaddingPixelsAtDisplay),
                "Transparent padding size is incompatible " +
                "with the display size."
            );
        }

        cancellationToken
            .ThrowIfCancellationRequested();

        ValidatePngHeaderDimensions(
            sourcePng
        );

        using var sourceBitmap =
            DecodeToArgbBitmap(
                sourcePng
            );

        ValidateSourceDimensions(
            sourceBitmap.Width,
            sourceBitmap.Height
        );

        var sourceMaximumDimension =
            Math.Max(
                sourceBitmap.Width,
                sourceBitmap.Height
            );

        /*
         * Padding determines only the size
         * of the output canvas.
         */
        var canvasPadding =
            CalculateSourcePadding(
                sourceMaximumDimension,
                targetDisplayBoxPixels,
                canvasPaddingPixelsAtDisplay
            );

        /*
         * The gradient outline thickness
         * is calculated independently of the canvas.
         */
        var outlineRadius =
            Math.Max(
                1,

                (int)Math.Round(
                    canvasPadding /
                        (double)OutlineThicknessDivisor,

                    MidpointRounding
                        .AwayFromZero
                )
            );

        var outputWidth =
            checked(
                sourceBitmap.Width +
                canvasPadding * 2
            );

        var outputHeight =
            checked(
                sourceBitmap.Height +
                canvasPadding * 2
            );

        ValidateOutputDimensions(
            outputWidth,
            outputHeight
        );

        var opaqueMask =
            ReadOpaqueMask(
                sourceBitmap
            );

        /*
         * Only the transparent area
         * connected to the outer edge of the canvas.
         *
         * This prevents fully enclosed
         * holes inside the emblem from receiving
         * an outer white outline.
         */
        var exteriorTransparentMask =
            BuildExteriorTransparentMask(
                opaqueMask,
                sourceBitmap.Width,
                sourceBitmap.Height,
                canvasPadding,
                outputWidth,
                outputHeight,
                cancellationToken
            );

        var boundaryPixels =
            FindBoundaryPixels(
                opaqueMask,
                sourceBitmap.Width,
                sourceBitmap.Height,
                cancellationToken
            );

        if (boundaryPixels.Length == 0)
        {
            throw new InvalidOperationException(
                "Rank PNG contains no visible pixels " +
                "for building the outline."
            );
        }

        /*
         * Several concentric layers
         * form a white alpha gradient.
         */
        var coverageLayers =
            BuildGradientCoverageLayers(
                boundaryPixels,
                sourceBitmap.Width,
                canvasPadding,
                outlineRadius,
                outputWidth,
                outputHeight,
                cancellationToken
            );

        using var outputBitmap =
            new Bitmap(
                outputWidth,
                outputHeight,
                PixelFormat.Format32bppArgb
            );

        WriteGradientOutline(
            outputBitmap,
            coverageLayers,
            exteriorTransparentMask,
            cancellationToken
        );

        CompositeSourceImage(
            outputBitmap,
            sourceBitmap,
            canvasPadding
        );

        RankImageSubrankMarkerRenderer.Draw(
            outputBitmap,
            sourceBitmap.Width,
            sourceBitmap.Height,
            canvasPadding,
            subrank,
            cancellationToken
        );

        cancellationToken
            .ThrowIfCancellationRequested();

        return EncodePng(
            outputBitmap
        );
    }

    private static int CalculateSourcePadding(
        int sourceMaximumDimension,
        int targetDisplayBoxPixels,
        int paddingPixelsAtDisplay
    )
    {
        var denominator =
            targetDisplayBoxPixels -
            paddingPixelsAtDisplay * 2;

        var padding =
            (int)Math.Ceiling(
                paddingPixelsAtDisplay *
                (double)sourceMaximumDimension /
                denominator
            );

        return Math.Max(
            1,
            padding
        );
    }

    private static Bitmap DecodeToArgbBitmap(
        byte[] sourcePng
    )
    {
        using var input =
            new MemoryStream(
                sourcePng,
                writable:
                    false
            );

        using var decoded =
            Image.FromStream(
                input,
                useEmbeddedColorManagement:
                    false,
                validateImageData:
                true
            );

        ValidateSourceDimensions(
            decoded.Width,
            decoded.Height
        );

        var sourceMaximumDimension =
            Math.Max(
                decoded.Width,
                decoded.Height
            );

        var scale =
            sourceMaximumDimension >
                MaximumWorkingDimension
                ? MaximumWorkingDimension /
                    (double)sourceMaximumDimension
                : 1.0;

        var normalizedWidth =
            Math.Max(
                1,
                (int)Math.Round(
                    decoded.Width * scale,
                    MidpointRounding.AwayFromZero
                )
            );

        var normalizedHeight =
            Math.Max(
                1,
                (int)Math.Round(
                    decoded.Height * scale,
                    MidpointRounding.AwayFromZero
                )
            );

        var normalized =
            new Bitmap(
                normalizedWidth,
                normalizedHeight,
                PixelFormat.Format32bppArgb
            );

        using var graphics =
            Graphics.FromImage(
                normalized
            );

        graphics.CompositingMode =
            CompositingMode.SourceCopy;

        graphics.CompositingQuality =
            CompositingQuality.HighQuality;

        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;

        graphics.PixelOffsetMode =
            PixelOffsetMode.HighQuality;

        graphics.SmoothingMode =
            SmoothingMode.HighQuality;

        graphics.DrawImage(
            decoded,
            new Rectangle(
                0,
                0,
                normalizedWidth,
                normalizedHeight
            ),
            0,
            0,
            decoded.Width,
            decoded.Height,
            GraphicsUnit.Pixel
        );

        return normalized;
    }

    private static void ValidatePngHeaderDimensions(
        byte[] sourcePng
    )
    {
        if (
            sourcePng.Length < 24 ||
            sourcePng[0] != 137 ||
            sourcePng[1] != 80 ||
            sourcePng[2] != 78 ||
            sourcePng[3] != 71 ||
            sourcePng[4] != 13 ||
            sourcePng[5] != 10 ||
            sourcePng[6] != 26 ||
            sourcePng[7] != 10 ||
            sourcePng[12] != (byte)'I' ||
            sourcePng[13] != (byte)'H' ||
            sourcePng[14] != (byte)'D' ||
            sourcePng[15] != (byte)'R'
        )
        {
            throw new InvalidOperationException(
                "Rank image does not contain a valid PNG header."
            );
        }

        var width =
            BinaryPrimitives.ReadInt32BigEndian(
                sourcePng.AsSpan(
                    16,
                    4
                )
            );

        var height =
            BinaryPrimitives.ReadInt32BigEndian(
                sourcePng.AsSpan(
                    20,
                    4
                )
            );

        ValidateSourceDimensions(
            width,
            height
        );
    }

    private static byte[] ReadOpaqueMask(
        Bitmap bitmap
    )
    {
        var width =
            bitmap.Width;

        var height =
            bitmap.Height;

        var mask =
            new byte[
                checked(
                    width *
                    height
                )
            ];

        var rectangle =
            new Rectangle(
                0,
                0,
                width,
                height
            );

        var bitmapData =
            bitmap.LockBits(
                rectangle,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb
            );

        try
        {
            var rowByteCount =
                Math.Abs(
                    bitmapData.Stride
                );

            var row =
                new byte[
                    rowByteCount
                ];

            for (
                var y = 0;
                y < height;
                y++
            )
            {
                Marshal.Copy(
                    GetLogicalRowPointer(
                        bitmapData,
                        y,
                        height
                    ),
                    row,
                    0,
                    rowByteCount
                );

                var maskOffset =
                    y *
                    width;

                for (
                    var x = 0;
                    x < width;
                    x++
                )
                {
                    mask[
                        maskOffset +
                        x
                    ] =
                        row[
                            x * 4 +
                            3
                        ] >= AlphaThreshold

                            ? (byte)1
                            : (byte)0;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(
                bitmapData
            );
        }

        return mask;
    }

    private static byte[]
        BuildExteriorTransparentMask(
            byte[] sourceOpaqueMask,
            int sourceWidth,
            int sourceHeight,
            int sourceOffset,
            int outputWidth,
            int outputHeight,
            CancellationToken cancellationToken
        )
    {
        var exteriorMask =
            new byte[
                checked(
                    outputWidth *
                    outputHeight
                )
            ];

        var queue =
            new Queue<int>();

        void EnqueueIfTransparent(
            int x,
            int y
        )
        {
            var index =
                y *
                outputWidth +
                x;

            if (
                exteriorMask[index] != 0 ||
                IsSourceOpaqueAtOutputPosition(
                    sourceOpaqueMask,
                    sourceWidth,
                    sourceHeight,
                    sourceOffset,
                    x,
                    y
                )
            )
            {
                return;
            }

            exteriorMask[index] =
                1;

            queue.Enqueue(
                index
            );
        }

        for (
            var x = 0;
            x < outputWidth;
            x++
        )
        {
            EnqueueIfTransparent(
                x,
                0
            );

            EnqueueIfTransparent(
                x,
                outputHeight - 1
            );
        }

        for (
            var y = 1;
            y < outputHeight - 1;
            y++
        )
        {
            EnqueueIfTransparent(
                0,
                y
            );

            EnqueueIfTransparent(
                outputWidth - 1,
                y
            );
        }

        var visitedCount =
            0;

        while (queue.Count > 0)
        {
            if (
                (
                    visitedCount++ &
                    4095
                ) == 0
            )
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            var index =
                queue.Dequeue();

            var x =
                index %
                outputWidth;

            var y =
                index /
                outputWidth;

            if (x > 0)
            {
                EnqueueIfTransparent(
                    x - 1,
                    y
                );
            }

            if (
                x + 1 <
                    outputWidth
            )
            {
                EnqueueIfTransparent(
                    x + 1,
                    y
                );
            }

            if (y > 0)
            {
                EnqueueIfTransparent(
                    x,
                    y - 1
                );
            }

            if (
                y + 1 <
                    outputHeight
            )
            {
                EnqueueIfTransparent(
                    x,
                    y + 1
                );
            }
        }

        return exteriorMask;
    }

    private static int[] FindBoundaryPixels(
        byte[] opaqueMask,
        int width,
        int height,
        CancellationToken cancellationToken
    )
    {
        var result =
            new List<int>();

        for (
            var y = 0;
            y < height;
            y++
        )
        {
            if (
                (
                    y &
                    31
                ) == 0
            )
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            for (
                var x = 0;
                x < width;
                x++
            )
            {
                var index =
                    y *
                    width +
                    x;

                if (
                    opaqueMask[index] != 0 &&
                    IsBoundaryPixel(
                        opaqueMask,
                        width,
                        height,
                        x,
                        y
                    )
                )
                {
                    result.Add(
                        index
                    );
                }
            }
        }

        return result.ToArray();
    }

    private static bool IsBoundaryPixel(
        byte[] opaqueMask,
        int width,
        int height,
        int x,
        int y
    )
    {
        for (
            var deltaY = -1;
            deltaY <= 1;
            deltaY++
        )
        {
            for (
                var deltaX = -1;
                deltaX <= 1;
                deltaX++
            )
            {
                if (
                    deltaX == 0 &&
                    deltaY == 0
                )
                {
                    continue;
                }

                var neighborX =
                    x +
                    deltaX;

                var neighborY =
                    y +
                    deltaY;

                if (
                    neighborX < 0 ||
                    neighborX >= width ||
                    neighborY < 0 ||
                    neighborY >= height ||
                    opaqueMask[
                        neighborY *
                        width +
                        neighborX
                    ] == 0
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static OutlineCoverageLayer[]
        BuildGradientCoverageLayers(
            int[] boundaryPixels,
            int sourceWidth,
            int sourceOffset,
            int outlineRadius,
            int outputWidth,
            int outputHeight,
            CancellationToken cancellationToken
        )
    {
        var layers =
            BuildGradientLayers(
                outlineRadius
            );

        var result =
            new OutlineCoverageLayer[
                layers.Length
            ];

        for (
            var index = 0;
            index < layers.Length;
            index++
        )
        {
            var layer =
                layers[index];

            result[index] =
                new OutlineCoverageLayer(
                    CoverageDeltas:
                        BuildOutlineCoverageDeltas(
                            boundaryPixels,
                            sourceWidth,
                            sourceOffset,
                            layer.Radius,
                            outputWidth,
                            outputHeight,
                            cancellationToken
                        ),

                    Alpha:
                        layer.Alpha
                );
        }

        return result;
    }

    /*
     * The gradient keeps the same shape.
     *
     * With the increased outlineRadius, the
     * approximate source radii are now:
     *
     * 16px → outer faint layer
     * 12px → second layer
     *  8px → third layer
     *  4px → bright inner layer
     */
    private static OutlineLayer[]
        BuildGradientLayers(
            int outlineRadius
        )
    {
        var result =
            new List<OutlineLayer>(
                4
            );

        var seenRadii =
            new HashSet<int>();

        void AddLayer(
            double radiusFactor,
            byte alpha
        )
        {
            var radius =
                Math.Max(
                    1,

                    (int)Math.Round(
                        outlineRadius *
                        radiusFactor,

                        MidpointRounding
                            .AwayFromZero
                    )
                );

            if (
                seenRadii.Add(
                    radius
                )
            )
            {
                result.Add(
                    new OutlineLayer(
                        Radius:
                            radius,

                        Alpha:
                            alpha
                    )
                );
            }
        }

        AddLayer(
            1.00,
            40
        );

        AddLayer(
            0.75,
            84
        );

        AddLayer(
            0.50,
            148
        );

        AddLayer(
            0.25,
            224
        );

        return result
            .OrderByDescending(
                layer =>
                    layer.Radius
            )
            .ToArray();
    }

    private static int[]
        BuildOutlineCoverageDeltas(
            int[] boundaryPixels,
            int sourceWidth,
            int sourceOffset,
            int outlineRadius,
            int outputWidth,
            int outputHeight,
            CancellationToken cancellationToken
        )
    {
        var deltaRowWidth =
            checked(
                outputWidth +
                1
            );

        var deltas =
            new int[
                checked(
                    deltaRowWidth *
                    outputHeight
                )
            ];

        var horizontalRadiusByDeltaY =
            new int[
                checked(
                    outlineRadius *
                    2 +
                    1
                )
            ];

        var radiusSquared =
            (long)outlineRadius *
            outlineRadius;

        for (
            var deltaY = -outlineRadius;
            deltaY <= outlineRadius;
            deltaY++
        )
        {
            horizontalRadiusByDeltaY[
                deltaY +
                outlineRadius
            ] =
                (int)Math.Floor(
                    Math.Sqrt(
                        radiusSquared -
                        (long)deltaY *
                        deltaY
                    )
                );
        }

        for (
            var boundaryIndex = 0;
            boundaryIndex <
                boundaryPixels.Length;
            boundaryIndex++
        )
        {
            if (
                (
                    boundaryIndex &
                    255
                ) == 0
            )
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }

            var sourceIndex =
                boundaryPixels[
                    boundaryIndex
                ];

            var centerX =
                sourceIndex %
                sourceWidth +
                sourceOffset;

            var centerY =
                sourceIndex /
                sourceWidth +
                sourceOffset;

            for (
                var deltaY = -outlineRadius;
                deltaY <= outlineRadius;
                deltaY++
            )
            {
                var horizontalRadius =
                    horizontalRadiusByDeltaY[
                        deltaY +
                        outlineRadius
                    ];

                var rowOffset =
                    checked(
                        (
                            centerY +
                            deltaY
                        ) *
                        deltaRowWidth
                    );

                var left =
                    centerX -
                    horizontalRadius;

                var rightExclusive =
                    centerX +
                    horizontalRadius +
                    1;

                deltas[
                    rowOffset +
                    left
                ] +=
                    1;

                deltas[
                    rowOffset +
                    rightExclusive
                ] -=
                    1;
            }
        }

        return deltas;
    }

    private static void WriteGradientOutline(
        Bitmap outputBitmap,
        IReadOnlyList<
            OutlineCoverageLayer
        > coverageLayers,
        byte[] exteriorTransparentMask,
        CancellationToken cancellationToken
    )
    {
        var outputWidth =
            outputBitmap.Width;

        var outputHeight =
            outputBitmap.Height;

        var deltaRowWidth =
            outputWidth +
            1;

        var rectangle =
            new Rectangle(
                0,
                0,
                outputWidth,
                outputHeight
            );

        var bitmapData =
            outputBitmap.LockBits(
                rectangle,
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb
            );

        try
        {
            var rowByteCount =
                Math.Abs(
                    bitmapData.Stride
                );

            var row =
                new byte[
                    rowByteCount
                ];

            var alphaRow =
                new byte[
                    outputWidth
                ];

            for (
                var y = 0;
                y < outputHeight;
                y++
            )
            {
                if (
                    (
                        y &
                        31
                    ) == 0
                )
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                }

                Array.Clear(
                    row,
                    0,
                    row.Length
                );

                Array.Clear(
                    alphaRow,
                    0,
                    alphaRow.Length
                );

                var exteriorOffset =
                    y *
                    outputWidth;

                for (
                    var layerIndex = 0;
                    layerIndex <
                        coverageLayers.Count;
                    layerIndex++
                )
                {
                    var layer =
                        coverageLayers[
                            layerIndex
                        ];

                    var coverage =
                        0;

                    var deltaOffset =
                        y *
                        deltaRowWidth;

                    for (
                        var x = 0;
                        x < outputWidth;
                        x++
                    )
                    {
                        coverage +=
                            layer
                                .CoverageDeltas[
                                    deltaOffset +
                                    x
                                ];

                        if (
                            coverage <= 0 ||
                            exteriorTransparentMask[
                                exteriorOffset +
                                x
                            ] == 0
                        )
                        {
                            continue;
                        }

                        alphaRow[x] =
                            CompositeAlpha(
                                alphaRow[x],
                                layer.Alpha
                            );
                    }
                }

                for (
                    var x = 0;
                    x < outputWidth;
                    x++
                )
                {
                    var alpha =
                        alphaRow[x];

                    if (alpha == 0)
                    {
                        continue;
                    }

                    var pixelOffset =
                        x *
                        4;

                    row[
                        pixelOffset
                    ] =
                        byte.MaxValue;

                    row[
                        pixelOffset +
                        1
                    ] =
                        byte.MaxValue;

                    row[
                        pixelOffset +
                        2
                    ] =
                        byte.MaxValue;

                    row[
                        pixelOffset +
                        3
                    ] =
                        alpha;
                }

                Marshal.Copy(
                    row,
                    0,
                    GetLogicalRowPointer(
                        bitmapData,
                        y,
                        outputHeight
                    ),
                    rowByteCount
                );
            }
        }
        finally
        {
            outputBitmap.UnlockBits(
                bitmapData
            );
        }
    }

    private static byte CompositeAlpha(
        byte destinationAlpha,
        byte sourceAlpha
    )
    {
        if (destinationAlpha == 0)
        {
            return sourceAlpha;
        }

        if (sourceAlpha == 0)
        {
            return destinationAlpha;
        }

        var result =
            sourceAlpha +
            destinationAlpha *
            (255 - sourceAlpha) /
            255;

        return (byte)Math.Clamp(
            result,
            0,
            255
        );
    }

    private static bool
        IsSourceOpaqueAtOutputPosition(
            byte[] sourceOpaqueMask,
            int sourceWidth,
            int sourceHeight,
            int sourceOffset,
            int outputX,
            int outputY
        )
    {
        var sourceX =
            outputX -
            sourceOffset;

        var sourceY =
            outputY -
            sourceOffset;

        if (
            sourceX < 0 ||
            sourceX >= sourceWidth ||
            sourceY < 0 ||
            sourceY >= sourceHeight
        )
        {
            return false;
        }

        return sourceOpaqueMask[
            sourceY *
            sourceWidth +
            sourceX
        ] != 0;
    }

    private static void CompositeSourceImage(
        Bitmap outputBitmap,
        Bitmap sourceBitmap,
        int sourceOffset
    )
    {
        using var graphics =
            Graphics.FromImage(
                outputBitmap
            );

        graphics.CompositingMode =
            CompositingMode.SourceOver;

        graphics.DrawImageUnscaled(
            sourceBitmap,
            sourceOffset,
            sourceOffset
        );
    }

    private static byte[] EncodePng(
        Bitmap bitmap
    )
    {
        using var output =
            new MemoryStream();

        bitmap.Save(
            output,
            ImageFormat.Png
        );

        return output.ToArray();
    }

    private static IntPtr GetLogicalRowPointer(
        BitmapData bitmapData,
        int logicalY,
        int height
    )
    {
        var storageRow =
            bitmapData.Stride >= 0

                ? logicalY

                : height -
                    1 -
                    logicalY;

        return IntPtr.Add(
            bitmapData.Scan0,
            checked(
                storageRow *
                bitmapData.Stride
            )
        );
    }

    private static void ValidateSourceDimensions(
        int width,
        int height
    )
    {
        var pixels =
            checked(
                (long)width *
                height
            );

        if (
            width <= 0 ||
            height <= 0 ||
            width > MaximumSourceDimension ||
            height > MaximumSourceDimension ||
            pixels > MaximumSourcePixels
        )
        {
            throw new InvalidOperationException(
                "Invalid rank PNG size" +
                $" | width={width}" +
                $" | height={height}" +
                $" | maximumDimension={MaximumSourceDimension}" +
                $" | maximumPixels={MaximumSourcePixels}"
            );
        }
    }

    private static void ValidateOutputDimensions(
        int width,
        int height
    )
    {
        var pixels =
            checked(
                (long)width *
                height
            );

        if (
            width <= 0 ||
            height <= 0 ||
            pixels > MaximumOutputPixels
        )
        {
            throw new InvalidOperationException(
                "Rank PNG after outlining has " +
                "an invalid size" +
                $" | width={width}" +
                $" | height={height}" +
                $" | pixels={pixels}" +
                $" | maximumPixels={MaximumOutputPixels}"
            );
        }
    }
}
