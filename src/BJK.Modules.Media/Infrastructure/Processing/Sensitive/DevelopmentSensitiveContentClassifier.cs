using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Hosting;

namespace BJK.Modules.Media.Infrastructure.Processing.Sensitive;

/// <summary>
/// A stand-in classifier for local development and demos (<c>Media:SensitiveDetection:Provider</c> = Development): the
/// score is the share of strongly red pixels, times four, so a mostly red picture is flagged and ordinary dashcam
/// footage isn't. It knows nothing about real graphic content. Outside the Development environment it reports itself
/// unavailable, so a misconfigured server sends media to review rather than trusting it.
/// </summary>
internal sealed class DevelopmentSensitiveContentClassifier(IHostEnvironment environment) : ISensitiveContentClassifier
{
    public bool IsAvailable => environment.IsDevelopment();

    public string? UnavailableReason => IsAvailable
        ? null
        : "The development graphic content classifier only runs in the Development environment (Media:SensitiveDetection:Provider).";

    public float Score(RgbFrame frame)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        return RedShare(frame);
    }

    /// <summary>Four times the share of strongly red pixels (red over 150, green and blue under 80), at most 1.</summary>
    internal static float RedShare(RgbFrame frame)
    {
        var pixels = frame.Pixels;
        var count = frame.Width * frame.Height;
        var red = 0;
        for (var i = 0; i < count * 3; i += 3)
        {
            if (pixels[i] > 150 && pixels[i + 1] < 80 && pixels[i + 2] < 80)
            {
                red++;
            }
        }

        return Math.Min(1f, red * 4f / count);
    }
}
