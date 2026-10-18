namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>Box geometry shared by the detectors, the tracker and the blur.</summary>
public static class BoxMath
{
    /// <summary>Intersection over union: 0 for boxes that don't touch, 1 for identical boxes.</summary>
    public static float IoU(DetectionBox a, DetectionBox b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var intersection = width * height;
        var union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>Keeps the highest-scoring box of each overlapping group (non-maximum suppression).</summary>
    /// <param name="boxes">Candidates.</param>
    /// <param name="iouThreshold">Boxes overlapping a kept one by more than this are dropped.</param>
    public static List<DetectionBox> NonMaxSuppression(IEnumerable<DetectionBox> boxes, float iouThreshold)
    {
        var kept = new List<DetectionBox>();
        foreach (var box in boxes.OrderByDescending(b => b.Confidence))
        {
            if (kept.TrueForAll(k => k.Target != box.Target || IoU(k, box) <= iouThreshold))
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    /// <summary>
    /// Grows a box by <paramref name="padding"/> of its width and height on every side, then clips it to the frame.
    /// Returns integer pixel bounds (left, top, right, bottom; right and bottom exclusive), empty if nothing is left.
    /// </summary>
    /// <param name="box">The detected box.</param>
    /// <param name="padding">Fraction of the box's size to add on each side, e.g. 0.25.</param>
    /// <param name="frameWidth">Frame width in pixels.</param>
    /// <param name="frameHeight">Frame height in pixels.</param>
    public static (int Left, int Top, int Right, int Bottom) Pad(DetectionBox box, double padding, int frameWidth, int frameHeight)
    {
        var padX = box.Width * padding;
        var padY = box.Height * padding;
        var left = (int)Math.Floor(Math.Clamp(box.X - padX, 0, frameWidth));
        var top = (int)Math.Floor(Math.Clamp(box.Y - padY, 0, frameHeight));
        var right = (int)Math.Ceiling(Math.Clamp(box.Right + padX, 0, frameWidth));
        var bottom = (int)Math.Ceiling(Math.Clamp(box.Bottom + padY, 0, frameHeight));
        return right <= left || bottom <= top ? (0, 0, 0, 0) : (left, top, right, bottom);
    }

    /// <summary>Linear interpolation between two boxes; <paramref name="t"/> 0 gives <paramref name="a"/>, 1 gives <paramref name="b"/>.</summary>
    /// <param name="a">Start box.</param>
    /// <param name="b">End box.</param>
    /// <param name="t">Position between them.</param>
    public static DetectionBox Lerp(DetectionBox a, DetectionBox b, float t) => new(
        a.Target,
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        a.Width + ((b.Width - a.Width) * t),
        a.Height + ((b.Height - a.Height) * t),
        Math.Min(a.Confidence, b.Confidence));
}
