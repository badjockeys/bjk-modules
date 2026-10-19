using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Faces, with OpenCV Zoo's YuNet (MIT licence; <c>face_detection_yunet_2026may.onnx</c>, which accepts any input
/// size). YuNet finds faces of roughly 10 to 300 pixels, so each frame is searched at full size (up to 2048 pixels)
/// and again scaled down to 640 pixels for faces close to the camera. The decoding matches OpenCV's FaceDetectorYN.
/// </summary>
internal sealed class YuNetFaceDetector(IOptions<MediaRedactionOptions> options, ILogger<YuNetFaceDetector> logger)
    : OnnxDetector(options.Value.FaceModelPath, "Media:Redaction:FaceModelPath", options.Value, logger)
{
    private const int MaxInputSide = 2048;
    private const int SmallPassSide = 640;
    private const float NmsThreshold = 0.3f;
    private static readonly int[] Strides = [8, 16, 32];

    public override RedactionTarget Target => RedactionTarget.Face;

    protected override IReadOnlyList<DetectionBox> DetectCore(RgbFrame frame, float minConfidence)
    {
        var found = new List<DetectionBox>();
        var longest = Math.Max(frame.Width, frame.Height);

        found.AddRange(DetectAtScale(frame, Math.Min(1f, MaxInputSide / (float)longest), minConfidence));
        if (FixedInputSize() is null && longest > SmallPassSide * 1.5f)
        {
            found.AddRange(DetectAtScale(frame, SmallPassSide / (float)longest, minConfidence));
        }

        return BoxMath.NonMaxSuppression(found, NmsThreshold);
    }

    private List<DetectionBox> DetectAtScale(RgbFrame frame, float scale, float minConfidence)
    {
        var fixedSize = FixedInputSize();
        int width, height;
        float scaleX, scaleY;
        RgbFrame input;
        if (fixedSize is { } size)
        {
            // An older fixed-size export (e.g. 640x640): stretch the frame to fit.
            (width, height) = size;
            input = FrameOps.Resize(frame, width, height);
            (scaleX, scaleY) = (frame.Width / (float)width, frame.Height / (float)height);
        }
        else
        {
            var w = Math.Max(1, (int)MathF.Round(frame.Width * scale));
            var h = Math.Max(1, (int)MathF.Round(frame.Height * scale));
            input = w == frame.Width && h == frame.Height ? frame : FrameOps.Resize(frame, w, h);
            (scaleX, scaleY) = (frame.Width / (float)w, frame.Height / (float)h);

            // The network needs sizes divisible by 32; the frame sits in the top left of a zero-padded input.
            width = ((w - 1) / 32 + 1) * 32;
            height = ((h - 1) / 32 + 1) * 32;
        }

        var outputs = Run(FrameOps.ToBgrPlanes(input, width, height), width, height);
        var boxes = new List<DetectionBox>();
        foreach (var stride in Strides)
        {
            var cols = width / stride;
            var rows = height / stride;
            var cls = outputs[$"cls_{stride}"].Data;
            var obj = outputs[$"obj_{stride}"].Data;
            var bbox = outputs[$"bbox_{stride}"].Data;
            var count = Math.Min(rows * cols, Math.Min(cls.Length, obj.Length));
            for (var i = 0; i < count; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < minConfidence)
                {
                    continue;
                }

                var row = i / cols;
                var col = i % cols;
                var cx = (col + bbox[i * 4]) * stride;
                var cy = (row + bbox[(i * 4) + 1]) * stride;
                var bw = MathF.Exp(bbox[(i * 4) + 2]) * stride;
                var bh = MathF.Exp(bbox[(i * 4) + 3]) * stride;
                boxes.Add(new DetectionBox(
                    RedactionTarget.Face,
                    (cx - (bw / 2)) * scaleX,
                    (cy - (bh / 2)) * scaleY,
                    bw * scaleX,
                    bh * scaleY,
                    score));
            }
        }

        return boxes;
    }
}
