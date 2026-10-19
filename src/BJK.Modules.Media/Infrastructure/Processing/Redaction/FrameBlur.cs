namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Irreversible blur: the region is reduced to a few large blocks of average colour (at most about 8 across and 3
/// down a plate, 3 to 4 across a face), then smoothed. So few blocks carry too little information to rebuild a face
/// or read a plate, unlike a light Gaussian blur, which can be partly undone.
/// </summary>
public static class FrameBlur
{
    /// <summary>Smallest block, in pixels.</summary>
    public const int MinBlockSize = 8;

    /// <summary>Blurs the rectangle (right and bottom exclusive) in place.</summary>
    /// <param name="frame">The picture to change.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge, exclusive.</param>
    /// <param name="bottom">Bottom edge, exclusive.</param>
    public static void Apply(RgbFrame frame, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, frame.Width);
        right = Math.Clamp(right, 0, frame.Width);
        top = Math.Clamp(top, 0, frame.Height);
        bottom = Math.Clamp(bottom, 0, frame.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var block = BlockSize(width, height);
        Pixelate(frame, left, top, right, bottom, block);
        BoxBlur(frame, left, top, right, bottom, Math.Max(1, block / 2));
    }

    /// <summary>Block size for a region: big enough that the longer side has at most 8 blocks and the shorter at most 3.</summary>
    /// <param name="width">Region width.</param>
    /// <param name="height">Region height.</param>
    public static int BlockSize(int width, int height)
    {
        var longer = Math.Max(width, height);
        var shorter = Math.Min(width, height);
        return Math.Max(MinBlockSize, (int)Math.Ceiling(Math.Max(longer / 8.0, shorter / 3.0)));
    }

    private static void Pixelate(RgbFrame frame, int left, int top, int right, int bottom, int block)
    {
        var pixels = frame.Pixels;
        var stride = frame.Stride;
        for (var by = top; by < bottom; by += block)
        {
            var bh = Math.Min(block, bottom - by);
            for (var bx = left; bx < right; bx += block)
            {
                var bw = Math.Min(block, right - bx);
                long r = 0, g = 0, b = 0;
                for (var y = by; y < by + bh; y++)
                {
                    var i = (y * stride) + (bx * 3);
                    for (var x = 0; x < bw; x++, i += 3)
                    {
                        r += pixels[i];
                        g += pixels[i + 1];
                        b += pixels[i + 2];
                    }
                }

                var count = bw * bh;
                var (ar, ag, ab) = ((byte)(r / count), (byte)(g / count), (byte)(b / count));
                for (var y = by; y < by + bh; y++)
                {
                    var i = (y * stride) + (bx * 3);
                    for (var x = 0; x < bw; x++, i += 3)
                    {
                        pixels[i] = ar;
                        pixels[i + 1] = ag;
                        pixels[i + 2] = ab;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Two passes (horizontal then vertical) of a box blur, confined to the region. Uses running sums, so the cost
    /// doesn't grow with the radius.
    /// </summary>
    private static void BoxBlur(RgbFrame frame, int left, int top, int right, int bottom, int radius)
    {
        var pixels = frame.Pixels;
        var stride = frame.Stride;
        var width = right - left;
        var height = bottom - top;
        var line = new byte[Math.Max(width, height) * 3];

        for (var y = top; y < bottom; y++)
        {
            var row = y * stride;
            Buffer.BlockCopy(pixels, row + (left * 3), line, 0, width * 3);
            BlurLine(line, width, radius, pixels, row + (left * 3), 3);
        }

        for (var x = left; x < right; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var i = ((top + y) * stride) + (x * 3);
                line[y * 3] = pixels[i];
                line[(y * 3) + 1] = pixels[i + 1];
                line[(y * 3) + 2] = pixels[i + 2];
            }

            BlurLine(line, height, radius, pixels, (top * stride) + (x * 3), stride);
        }
    }

    /// <summary>
    /// Averages each of <paramref name="count"/> RGB values in <paramref name="line"/> with its neighbours within
    /// <paramref name="radius"/> (clipped at the ends) and writes them to <paramref name="target"/>, starting at
    /// <paramref name="offset"/>, <paramref name="step"/> bytes apart.
    /// </summary>
    private static void BlurLine(byte[] line, int count, int radius, byte[] target, int offset, int step)
    {
        int r = 0, g = 0, b = 0;
        var to = Math.Min(count - 1, radius);
        for (var k = 0; k <= to; k++)
        {
            r += line[k * 3];
            g += line[(k * 3) + 1];
            b += line[(k * 3) + 2];
        }

        for (var i = 0; i < count; i++)
        {
            var from = Math.Max(0, i - radius);
            var n = to - from + 1;
            var o = offset + (i * step);
            target[o] = (byte)(r / n);
            target[o + 1] = (byte)(g / n);
            target[o + 2] = (byte)(b / n);

            // Slide the window one along: add the next value on the right, drop the one leaving on the left.
            if (i + radius + 1 < count)
            {
                to = i + radius + 1;
                r += line[to * 3];
                g += line[(to * 3) + 1];
                b += line[(to * 3) + 2];
            }

            if (i - radius >= 0)
            {
                var drop = i - radius;
                r -= line[drop * 3];
                g -= line[(drop * 3) + 1];
                b -= line[(drop * 3) + 2];
            }
        }
    }
}
