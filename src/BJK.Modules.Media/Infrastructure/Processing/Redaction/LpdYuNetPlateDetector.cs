using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Number plates, with OpenCV Zoo's LPD-YuNet (Apache 2.0 licence;
/// <c>license_plate_detection_lpd_yunet_2023mar.onnx</c>). The model takes a fixed 320x240 input, so each frame is
/// searched whole (scaled to fit) and, with <see cref="MediaRedactionOptions.PlateTiles"/>, in overlapping tiles at twice
/// that size for small, distant plates. It was trained on Chinese plates; UK plates are found less reliably, which is
/// why uncertain scores send media to review. The decoding (prior boxes, four corner points) matches OpenCV Zoo's
/// lpd_yunet.py.
/// </summary>
internal sealed class LpdYuNetPlateDetector : OnnxDetector
{
    private const float NmsThreshold = 0.3f;
    private static readonly int[][] MinSizes = [[10, 16, 24], [32, 48], [64, 96], [128, 192, 256]];
    private static readonly int[] Steps = [8, 16, 32, 64];
    private static readonly float[] Variance = [0.1f, 0.2f];

    private readonly bool _tiles;
    private readonly Lazy<(int Width, int Height, float[] Priors)> _input;

    /// <param name="options">Model path and tiling.</param>
    /// <param name="logger">For load failures.</param>
    public LpdYuNetPlateDetector(IOptions<MediaRedactionOptions> options, ILogger<LpdYuNetPlateDetector> logger)
        : base(options.Value.PlateModelPath, "Media:Redaction:PlateModelPath", options.Value, logger)
    {
        _tiles = options.Value.PlateTiles;
        _input = new Lazy<(int, int, float[])>(() =>
        {
            var (w, h) = FixedInputSize() ?? (320, 240);
            return (w, h, Priors(w, h));
        });
    }

    public override RedactionTarget Target => RedactionTarget.NumberPlate;

    protected override IReadOnlyList<DetectionBox> DetectCore(RgbFrame frame, float minConfidence)
    {
        var (width, height, priors) = _input.Value;
        var found = new List<DetectionBox>();
        found.AddRange(DetectRegion(frame, 0, 0, frame.Width, frame.Height, width, height, priors, minConfidence));

        if (_tiles && (frame.Width >= width * 2 || frame.Height >= height * 2))
        {
            // A 2x2 grid for 720p video (more for big photos) of tiles at most 2.2 times the model's input size,
            // overlapping by about a tenth so a plate on a seam is whole in one of them.
            var tileW = TileSize(frame.Width, (int)(width * 2.2));
            var tileH = TileSize(frame.Height, (int)(height * 2.2));
            foreach (var top in Offsets(frame.Height, tileH))
            {
                foreach (var left in Offsets(frame.Width, tileW))
                {
                    found.AddRange(DetectRegion(frame, left, top, tileW, tileH, width, height, priors, minConfidence));
                }
            }
        }

        return BoxMath.NonMaxSuppression(found, NmsThreshold);
    }

    /// <summary>
    /// Tile length along one side: half the frame plus a tenth overlap, but no more than <paramref name="maxTile"/>
    /// (about twice the model's input) so small plates stay big enough to find.
    /// </summary>
    /// <param name="total">Frame length.</param>
    /// <param name="maxTile">Longest tile.</param>
    internal static int TileSize(int total, int maxTile) =>
        Math.Min(total, Math.Max(Math.Min(maxTile, ((total * 11) + 19) / 20), Math.Min(total, maxTile / 2)));

    /// <summary>Tile positions with about a tenth overlap, the last one flush with the far edge.</summary>
    /// <param name="total">Frame length.</param>
    /// <param name="tile">Tile length.</param>
    internal static List<int> Offsets(int total, int tile)
    {
        var offsets = new List<int>();
        if (tile >= total)
        {
            offsets.Add(0);
            return offsets;
        }

        var count = (int)Math.Ceiling((total - (tile * 0.1)) / (tile * 0.9));
        var step = (total - tile) / (double)(count - 1);
        for (var i = 0; i < count; i++)
        {
            offsets.Add((int)Math.Round(i * step));
        }

        return offsets;
    }

    private List<DetectionBox> DetectRegion(
        RgbFrame frame,
        int left,
        int top,
        int regionWidth,
        int regionHeight,
        int width,
        int height,
        float[] priors,
        float minConfidence)
    {
        var input = FrameOps.Resize(frame, width, height, left, top, regionWidth, regionHeight);
        var outputs = Run(FrameOps.ToBgrPlanes(input, width, height), width, height);
        var loc = outputs["loc"].Data;
        var conf = outputs["conf"].Data;
        var iou = outputs["iou"].Data;
        var scaleX = regionWidth / (float)width;
        var scaleY = regionHeight / (float)height;
        var count = Math.Min(priors.Length / 4, Math.Min(conf.Length / 2, Math.Min(iou.Length, loc.Length / 14)));

        var boxes = new List<DetectionBox>();
        for (var n = 0; n < count; n++)
        {
            var score = MathF.Sqrt(Math.Max(0, conf[(n * 2) + 1]) * Math.Clamp(iou[n], 0, 1));
            if (score < minConfidence)
            {
                continue;
            }

            var (pcx, pcy, pw, ph) = (priors[n * 4], priors[(n * 4) + 1], priors[(n * 4) + 2], priors[(n * 4) + 3]);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var offset in (ReadOnlySpan<int>)[4, 6, 10, 12])
            {
                var x = (pcx + (loc[(n * 14) + offset] * Variance[0] * pw)) * width;
                var y = (pcy + (loc[(n * 14) + offset + 1] * Variance[0] * ph)) * height;
                (minX, maxX) = (Math.Min(minX, x), Math.Max(maxX, x));
                (minY, maxY) = (Math.Min(minY, y), Math.Max(maxY, y));
            }

            boxes.Add(new DetectionBox(
                RedactionTarget.NumberPlate,
                left + (minX * scaleX),
                top + (minY * scaleY),
                (maxX - minX) * scaleX,
                (maxY - minY) * scaleY,
                score));
        }

        return boxes;
    }

    /// <summary>Prior (anchor) boxes as centre x, centre y, width, height, all as fractions of the input size.</summary>
    internal static float[] Priors(int width, int height)
    {
        var f2 = (H: ((height + 1) / 2) / 2, W: ((width + 1) / 2) / 2);
        var f3 = (H: f2.H / 2, W: f2.W / 2);
        var f4 = (H: f3.H / 2, W: f3.W / 2);
        var f5 = (H: f4.H / 2, W: f4.W / 2);
        var f6 = (H: f5.H / 2, W: f5.W / 2);
        var maps = new[] { f3, f4, f5, f6 };

        var priors = new List<float>();
        for (var k = 0; k < maps.Length; k++)
        {
            for (var i = 0; i < maps[k].H; i++)
            {
                for (var j = 0; j < maps[k].W; j++)
                {
                    foreach (var minSize in MinSizes[k])
                    {
                        priors.Add((j + 0.5f) * Steps[k] / width);
                        priors.Add((i + 0.5f) * Steps[k] / height);
                        priors.Add(minSize / (float)width);
                        priors.Add(minSize / (float)height);
                    }
                }
            }
        }

        return priors.ToArray();
    }
}
