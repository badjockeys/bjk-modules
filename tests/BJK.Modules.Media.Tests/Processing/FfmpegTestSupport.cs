using System.Diagnostics;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Xunit;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>Helpers for tests that run the real ffmpeg. They skip where it isn't installed; CI installs it.</summary>
internal static class FfmpegTestSupport
{
    /// <summary>16-pixel black and white squares: lots of detail, so a blur is easy to measure.</summary>
    public const string Checkerboard = "geq=lum='if(mod(floor(X/16)+floor(Y/16),2),235,16)':cb=128:cr=128";

    public static void RequireFfmpeg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.StandardOutput.ReadToEnd();
            process?.WaitForExit();
            if (process?.ExitCode == 0)
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        Assert.Skip("ffmpeg isn't installed.");
    }

    /// <summary>Runs ffmpeg with the arguments, writing to <paramref name="path"/>.</summary>
    public static string Make(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-y" }.Concat(arguments).Append(path))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return path;
    }

    public static async Task<string> Ffprobe(string path)
    {
        var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-show_format", "-show_streams", path })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    /// <summary>Decodes every frame (or the picture) of a file into memory.</summary>
    public static async Task<List<RgbFrame>> Frames(string path)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", path, "-map", "0:v:0", "-f", "image2pipe", "-c:v", "ppm", "-pix_fmt", "rgb24", "pipe:1" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var reader = new PpmReader(process.StandardOutput.BaseStream);
        var frames = new List<RgbFrame>();
        while (await reader.ReadAsync(CancellationToken.None) is { } frame)
        {
            frames.Add(frame);
        }

        await process.WaitForExitAsync();
        await errors;
        return frames;
    }

    /// <summary>Standard deviation of brightness in a rectangle: high for the checkerboard, low once blurred.</summary>
    public static double Detail(RgbFrame frame, int left, int top, int right, int bottom)
    {
        double sum = 0, squares = 0;
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = (y * frame.Stride) + (x * 3);
                var luma = (0.299 * frame.Pixels[i]) + (0.587 * frame.Pixels[i + 1]) + (0.114 * frame.Pixels[i + 2]);
                sum += luma;
                squares += luma * luma;
                count++;
            }
        }

        var mean = sum / count;
        return Math.Sqrt(Math.Max(0, (squares / count) - (mean * mean)));
    }
}

/// <summary>A detector that returns whatever the test says, or throws.</summary>
internal sealed class FakeDetector(RedactionTarget target, Func<RgbFrame, IReadOnlyList<DetectionBox>> detect, bool available = true)
    : IRedactionDetector
{
    public int Calls { get; private set; }

    public RedactionTarget Target => target;

    public bool IsAvailable => available;

    public string? UnavailableReason => available ? null : $"The {target} detection model is not configured.";

    public IReadOnlyList<DetectionBox> Detect(RgbFrame frame, float minConfidence)
    {
        Calls++;
        return detect(frame).Where(b => b.Confidence >= minConfidence).ToList();
    }

    public static FakeDetector Nothing(RedactionTarget target) => new(target, _ => []);
}
