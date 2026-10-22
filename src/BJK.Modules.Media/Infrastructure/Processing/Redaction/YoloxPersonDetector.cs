using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// People, with OpenCV Zoo's YOLOX-S (Apache 2.0 licence; <c>object_detection_yolox_2022nov.onnx</c>, trained on COCO),
/// and a box over each person's head to blur. Person detectors find pedestrians far more reliably than face detectors
/// at night, at a distance, side-on or from behind, so this catches faces YuNet misses. The head is taken as the top
/// <see cref="HeadFraction"/> of the person, full width; the usual padding is added when blurring. The decoding (grid
/// offsets, exp sizes, objectness x class score) matches OpenCV Zoo's yolox.py; class 0 is "person".
/// </summary>
internal sealed class YoloxPersonDetector(IOptions<MediaRedactionOptions> options, ILogger<YoloxPersonDetector> logger)
    : OnnxDetector(options.Value.PersonModelPath, "Media:Redaction:PersonModelPath", options.Value, logger)
{
    /// <summary>How much of a person's height, from the top, is treated as the head.</summary>
    internal const float HeadFraction = 0.22f;

    private const int InputSize = 640;
    private const float NmsThreshold = 0.5f;
    private const int Classes = 80;
    private static readonly int[] Strides = [8, 16, 32];

    public override RedactionTarget Target => RedactionTarget.Head;

    protected override IReadOnlyList<DetectionBox> DetectCore(RgbFrame frame, float minConfidence)
    {
        var size = FixedInputSize() is { } fixedSize ? Math.Min(fixedSize.Width, fixedSize.Height) : InputSize;
        var (input, scale) = FrameOps.ToRgbLetterbox(frame, size);
        var (data, shape) = Run(input, size, size).Values.First();
        var stride = shape.Length == 3 ? (int)shape[2] : Classes + 5;

        var people = new List<DetectionBox>();
        var anchor = 0;
        foreach (var step in Strides)
        {
            var cells = size / step;
            for (var gy = 0; gy < cells; gy++)
            {
                for (var gx = 0; gx < cells; gx++, anchor++)
                {
                    var o = anchor * stride;
                    if (o + 5 >= data.Length)
                    {
                        break;
                    }

                    var score = data[o + 4] * data[o + 5];
                    if (score < minConfidence)
                    {
                        continue;
                    }

                    var cx = (data[o] + gx) * step / scale;
                    var cy = (data[o + 1] + gy) * step / scale;
                    var w = MathF.Exp(data[o + 2]) * step / scale;
                    var h = MathF.Exp(data[o + 3]) * step / scale;
                    people.Add(new DetectionBox(RedactionTarget.Head, cx - (w / 2), cy - (h / 2), w, h, score));
                }
            }
        }

        return BoxMath.NonMaxSuppression(people, NmsThreshold).Select(HeadOf).ToList();
    }

    /// <summary>The head area of a person box.</summary>
    internal static DetectionBox HeadOf(DetectionBox person) => person with { Height = person.Height * HeadFraction };
}
