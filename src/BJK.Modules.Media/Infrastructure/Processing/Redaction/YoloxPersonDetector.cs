using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// People and vehicles, with OpenCV Zoo's YOLOX-S (Apache 2.0 licence; <c>object_detection_yolox_2022nov.onnx</c>,
/// trained on COCO). Person detectors find pedestrians far more reliably than face detectors at night, at a distance,
/// side-on or from behind, so each person's head (the top <see cref="HeadFraction"/>, full width) is blurred to catch
/// faces YuNet misses. With <see cref="MediaRedactionOptions.BlurPlateAreas"/>, each vehicle (COCO car, motorbike, bus,
/// truck) also gives a <see cref="RedactionTarget.PlateArea"/> box over where its plate would be (see
/// <see cref="PlateAreaOf"/>); the redactor drops it when the plate model found a plate there. The usual padding is
/// added when blurring. The decoding (grid offsets, exp sizes, objectness x class score) matches OpenCV Zoo's yolox.py.
/// </summary>
internal sealed class YoloxPersonDetector(IOptions<MediaRedactionOptions> options, ILogger<YoloxPersonDetector> logger)
    : OnnxDetector(options.Value.PersonModelPath, "Media:Redaction:PersonModelPath", options.Value, logger)
{
    /// <summary>How much of a person's height, from the top, is treated as the head.</summary>
    internal const float HeadFraction = 0.22f;

    // The plate area, as fractions of the vehicle box: the middle 70% of its width, from 45% to 92% of its height.
    // Front and rear plates sit there whether the vehicle is coming or going; angled vehicles' plates still fall in it.
    internal const float PlateAreaLeft = 0.15f;
    internal const float PlateAreaWidth = 0.7f;
    internal const float PlateAreaTop = 0.45f;
    internal const float PlateAreaHeight = 0.47f;

    private const int InputSize = 640;
    private const float NmsThreshold = 0.5f;
    private const int Classes = 80;
    private static readonly int[] Strides = [8, 16, 32];

    // COCO classes: 2 car, 3 motorcycle, 5 bus, 7 truck (vans and lorries).
    private static readonly int[] VehicleClasses = [2, 3, 5, 7];

    private readonly bool _vehicles = options.Value.BlurPlateAreas;
    private readonly float _vehicleMin = (float)Math.Min(options.Value.VehicleMinConfidence, options.Value.VehicleTrackConfidence);

    public override RedactionTarget Target => RedactionTarget.Head;

    protected override IReadOnlyList<DetectionBox> DetectCore(RgbFrame frame, float minConfidence)
    {
        var size = FixedInputSize() is { } fixedSize ? Math.Min(fixedSize.Width, fixedSize.Height) : InputSize;
        var (input, scale) = FrameOps.ToRgbLetterbox(frame, size);
        var (data, shape) = Run(input, size, size).Values.First();
        var stride = shape.Length == 3 ? (int)shape[2] : Classes + 5;

        var people = new List<DetectionBox>();
        var vehicles = new List<DetectionBox>();
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

                    var person = data[o + 4] * data[o + 5];
                    var vehicle = 0f;
                    if (_vehicles)
                    {
                        foreach (var c in VehicleClasses)
                        {
                            if (o + 5 + c < data.Length)
                            {
                                vehicle = Math.Max(vehicle, data[o + 4] * data[o + 5 + c]);
                            }
                        }
                    }

                    var isPerson = person >= minConfidence;
                    var isVehicle = vehicle >= _vehicleMin;
                    if (!isPerson && !isVehicle)
                    {
                        continue;
                    }

                    var cx = (data[o] + gx) * step / scale;
                    var cy = (data[o + 1] + gy) * step / scale;
                    var w = MathF.Exp(data[o + 2]) * step / scale;
                    var h = MathF.Exp(data[o + 3]) * step / scale;
                    if (isPerson)
                    {
                        people.Add(new DetectionBox(RedactionTarget.Head, cx - (w / 2), cy - (h / 2), w, h, person));
                    }

                    if (isVehicle)
                    {
                        vehicles.Add(new DetectionBox(RedactionTarget.PlateArea, cx - (w / 2), cy - (h / 2), w, h, vehicle));
                    }
                }
            }
        }

        return BoxMath.NonMaxSuppression(people, NmsThreshold).Select(HeadOf)
            .Concat(BoxMath.NonMaxSuppression(vehicles, NmsThreshold).Select(PlateAreaOf))
            .ToList();
    }

    /// <summary>The head area of a person box.</summary>
    internal static DetectionBox HeadOf(DetectionBox person) => person with { Height = person.Height * HeadFraction };

    /// <summary>Where a vehicle's number plate would be, from the vehicle's box.</summary>
    internal static DetectionBox PlateAreaOf(DetectionBox vehicle) => vehicle with
    {
        X = vehicle.X + (vehicle.Width * PlateAreaLeft),
        Y = vehicle.Y + (vehicle.Height * PlateAreaTop),
        Width = vehicle.Width * PlateAreaWidth,
        Height = vehicle.Height * PlateAreaHeight,
    };
}
