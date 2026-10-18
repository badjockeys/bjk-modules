using System.Diagnostics;
using System.Globalization;
using System.Text;
using BJK.Modules.Media.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>What automatic blurring did to one photo or video.</summary>
/// <param name="FacesBlurred">Faces found (in a video: separate faces followed through it).</param>
/// <param name="PlatesBlurred">Number plates found (in a video: separate plates followed through it).</param>
/// <param name="ReviewReasons">Why a moderator must check it; empty if detection worked and was confident.</param>
/// <param name="Frames">Frames written (1 for a photo).</param>
/// <param name="FramesAnalysed">Frames the detectors ran on.</param>
/// <param name="Elapsed">Time taken.</param>
public sealed record RedactionResult(
    int FacesBlurred,
    int PlatesBlurred,
    IReadOnlyList<string> ReviewReasons,
    int Frames,
    int FramesAnalysed,
    TimeSpan Elapsed)
{
    public bool NeedsReview => ReviewReasons.Count > 0;
}

/// <summary>Finds and blurs faces and number plates, producing the files we publish.</summary>
public interface IMediaRedactor
{
    /// <summary>
    /// Writes a blurred H.264 MP4 (at most <c>Media:Processing:VideoHeight</c> high and 30 fps, no metadata, and no
    /// sound unless <c>Media:Redaction:RemoveAudio</c> is false).
    /// </summary>
    /// <param name="inputPath">The original.</param>
    /// <param name="probe">What ffprobe found in it.</param>
    /// <param name="manual">Moderators' extra blur regions.</param>
    /// <param name="outputPath">Where to write the MP4.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    Task<RedactionResult> RedactVideoAsync(
        string inputPath,
        ProbeResult probe,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the photo, upright, at most <c>Media:Processing:ImageMaxSize</c> on its longest side and blurred, as a
    /// lossless PPM file for the WebP conversion.
    /// </summary>
    /// <param name="inputPath">The original.</param>
    /// <param name="manual">Moderators' extra blur regions.</param>
    /// <param name="outputPath">Where to write the PPM.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    Task<RedactionResult> RedactImageAsync(
        string inputPath,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// ffmpeg decodes frames into memory over a pipe, the detectors and blur work on them, and a second ffmpeg encodes
/// the result, so nothing unblurred is ever written out. Videos are read once: detection runs on every Nth frame and
/// frames are held back just long enough for the tracker to know every box that covers them.
/// </summary>
internal sealed class MediaRedactor(
    IEnumerable<IRedactionDetector> detectors,
    IOptions<MediaRedactionOptions> redactionOptions,
    IOptions<MediaProcessingOptions> processingOptions,
    ILogger<MediaRedactor> logger)
    : IMediaRedactor
{
    private readonly MediaRedactionOptions _options = redactionOptions.Value;
    private readonly MediaProcessingOptions _processing = processingOptions.Value;
    private readonly IReadOnlyList<IRedactionDetector> _detectors = detectors.ToList();

    public async Task<RedactionResult> RedactImageAsync(
        string inputPath,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var size = _processing.ImageMaxSize;
        var fit = $"scale='min({size},iw)':'min({size},ih)':force_original_aspect_ratio=decrease";

        RgbFrame frame;
        using (var decoder = FfmpegPipe.Start(
            _processing.FfmpegPath,
            ["-hide_banner", "-nostdin", "-i", inputPath, "-map", "0:v:0", "-frames:v", "1", "-vf", fit,
             "-f", "image2pipe", "-c:v", "ppm", "-pix_fmt", "rgb24", "pipe:1"],
            redirectInput: false))
        {
            var read = await new PpmReader(decoder.Output).ReadAsync(cancellationToken);
            await decoder.WaitAsync(_processing.CommandTimeout, "decode the photo", cancellationToken);
            frame = read ?? throw new UnreadableMediaException("ffmpeg couldn't decode the photo.");
        }

        var pass = new DetectionPass(_detectors, _options, logger);
        var found = pass.Detect(frame);
        foreach (var box in found)
        {
            Blur(frame, box);
        }

        BlurManual(frame, manual, seconds: null);
        await PpmWriter.WriteAsync(frame, outputPath, cancellationToken);

        var uncertain = found.Count(b => b.Confidence < ConfidentAt(b.Target));
        return Result(
            pass,
            found.Count(b => b.Target == RedactionTarget.Face),
            found.Count(b => b.Target == RedactionTarget.NumberPlate),
            uncertain,
            frames: 1,
            analysed: 1,
            timer.Elapsed);
    }

    public async Task<RedactionResult> RedactVideoAsync(
        string inputPath,
        ProbeResult probe,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_processing.CommandTimeout);
        var token = timeout.Token;

        var fps = FrameRate(probe.FrameRate);
        var fpsText = fps.ToString("0.###", CultureInfo.InvariantCulture);
        var interval = _options.DetectEveryNthFrame;
        var lookahead = (_options.MaxMissedDetections + 1) * interval;
        var filters = $"scale=-2:'trunc(min({_processing.VideoHeight},ih)/2)*2':flags=lanczos,fps={fpsText}";

        var pass = new DetectionPass(_detectors, _options, logger);
        var tracker = new RedactionTracker(_options.MaxMissedDetections);
        var pending = new Queue<(int Index, RgbFrame Frame)>();
        var frames = 0;
        var analysed = 0;
        FfmpegPipe? encoder = null;

        using var decoder = FfmpegPipe.Start(
            _processing.FfmpegPath,
            ["-hide_banner", "-nostdin", "-i", inputPath, "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", filters,
             "-fps_mode", "passthrough", "-f", "image2pipe", "-c:v", "ppm", "-pix_fmt", "rgb24", "pipe:1"],
            redirectInput: false);
        try
        {
            var reader = new PpmReader(decoder.Output);
            var lastAnalysed = -1;
            while (await reader.ReadAsync(token) is { } frame)
            {
                var index = frames++;
                encoder ??= StartEncoder(inputPath, outputPath, frame.Width, frame.Height, fpsText);

                if (index % interval == 0)
                {
                    tracker.Add(index, pass.Detect(frame));
                    analysed++;
                    lastAnalysed = index;
                }

                pending.Enqueue((index, frame));

                // A frame's boxes are final once detection has run far enough past it.
                while (pending.Count > 0 && pending.Peek().Index <= lastAnalysed - lookahead)
                {
                    await WriteAsync(encoder, pending.Dequeue(), tracker, interval, manual, fps, token);
                }
            }

            await decoder.WaitAsync(_processing.CommandTimeout, "decode the video", token);
            if (encoder is null)
            {
                throw new UnreadableMediaException("ffmpeg found no frames in the video.");
            }

            while (pending.Count > 0)
            {
                await WriteAsync(encoder, pending.Dequeue(), tracker, interval, manual, fps, token);
            }

            await encoder.Input.FlushAsync(token);
            encoder.Input.Close();
            await encoder.WaitAsync(_processing.CommandTimeout, "encode the video", token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Blurring the video took longer than {_processing.CommandTimeout}.");
        }
        catch (IOException ex)
        {
            // A broken pipe: one of the ffmpeg processes stopped. Its error output says why.
            throw new UnreadableMediaException(
                $"ffmpeg stopped while blurring the video: {encoder?.ErrorTail() ?? decoder.ErrorTail()}", ex);
        }
        finally
        {
            encoder?.Dispose();
        }

        var tracks = tracker.Tracks;
        var uncertain = tracks.Count(t => t.MaxConfidence < ConfidentAt(t.Target));
        var result = Result(
            pass,
            tracks.Count(t => t.Target == RedactionTarget.Face),
            tracks.Count(t => t.Target == RedactionTarget.NumberPlate),
            uncertain,
            frames,
            analysed,
            timer.Elapsed);

        logger.LogInformation(
            "Blurred {Frames} frames ({Analysed} analysed) in {Elapsed}: {Faces} faces, {Plates} plates, {Uncertain} uncertain.",
            frames,
            analysed,
            timer.Elapsed,
            result.FacesBlurred,
            result.PlatesBlurred,
            uncertain);
        return result;
    }

    /// <summary>The output frame rate: the source's, at most 30; 30 if unknown.</summary>
    internal static double FrameRate(double? source) =>
        source is { } rate && double.IsFinite(rate) && rate > 0 ? Math.Min(30, rate) : 30;

    private FfmpegPipe StartEncoder(string inputPath, string outputPath, int width, int height, string fps)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-framerate", fps, "-i", "pipe:0",
        };
        if (_options.RemoveAudio)
        {
            arguments.AddRange(["-map", "0:v:0", "-an"]);
        }
        else
        {
            arguments.AddRange(["-i", inputPath, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        }

        arguments.AddRange(
        [
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", _processing.VideoCrf.ToString(CultureInfo.InvariantCulture),
            "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath,
        ]);
        return FfmpegPipe.Start(_processing.FfmpegPath, arguments, redirectInput: true);
    }

    private async Task WriteAsync(
        FfmpegPipe encoder,
        (int Index, RgbFrame Frame) item,
        RedactionTracker tracker,
        int interval,
        IReadOnlyList<ManualRedaction> manual,
        double fps,
        CancellationToken cancellationToken)
    {
        foreach (var box in tracker.BoxesAt(item.Index, interval))
        {
            Blur(item.Frame, box);
        }

        BlurManual(item.Frame, manual, item.Index / fps);
        await encoder.Input.WriteAsync(item.Frame.Pixels.AsMemory(0, item.Frame.Stride * item.Frame.Height), cancellationToken);
    }

    private void Blur(RgbFrame frame, DetectionBox box)
    {
        var (left, top, right, bottom) = BoxMath.Pad(box, _options.BoxPadding, frame.Width, frame.Height);
        FrameBlur.Apply(frame, left, top, right, bottom);
    }

    private static void BlurManual(RgbFrame frame, IReadOnlyList<ManualRedaction> manual, double? seconds)
    {
        foreach (var region in manual.Where(r => r.AppliesAt(seconds)))
        {
            FrameBlur.Apply(
                frame,
                (int)Math.Floor(region.X * frame.Width),
                (int)Math.Floor(region.Y * frame.Height),
                (int)Math.Ceiling((region.X + region.Width) * frame.Width),
                (int)Math.Ceiling((region.Y + region.Height) * frame.Height));
        }
    }

    private float ConfidentAt(RedactionTarget target) =>
        (float)(target == RedactionTarget.Face ? _options.FaceConfidentAt : _options.PlateConfidentAt);

    private RedactionResult Result(DetectionPass pass, int faces, int plates, int uncertain, int frames, int analysed, TimeSpan elapsed)
    {
        var reasons = pass.Problems.ToList();
        if (uncertain > 0)
        {
            reasons.Add($"{uncertain} detection(s) had low confidence.");
        }

        if (_options.ReviewEverything)
        {
            reasons.Add("Every upload is set to be reviewed (Media:Redaction:ReviewEverything).");
        }

        return new RedactionResult(faces, plates, reasons, frames, analysed, elapsed);
    }

    /// <summary>
    /// Runs the available detectors on frames for one job. A detector that's unavailable, or fails on any frame, is
    /// recorded as a problem (so the media goes to review) and not used again for the rest of the job.
    /// </summary>
    private sealed class DetectionPass
    {
        private readonly List<IRedactionDetector> _active = [];
        private readonly List<string> _problems = [];
        private readonly MediaRedactionOptions _options;
        private readonly ILogger _logger;

        public DetectionPass(IReadOnlyList<IRedactionDetector> detectors, MediaRedactionOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
            foreach (var target in Enum.GetValues<RedactionTarget>())
            {
                var detector = detectors.FirstOrDefault(d => d.Target == target);
                if (detector is null)
                {
                    _problems.Add($"No {target} detector is registered.");
                }
                else if (!detector.IsAvailable)
                {
                    _problems.Add(detector.UnavailableReason ?? $"The {target} detector is unavailable.");
                }
                else
                {
                    _active.Add(detector);
                }
            }
        }

        public IReadOnlyList<string> Problems => _problems;

        public List<DetectionBox> Detect(RgbFrame frame)
        {
            var boxes = new List<DetectionBox>();
            foreach (var detector in _active.ToList())
            {
                try
                {
                    var min = (float)(detector.Target == RedactionTarget.Face ? _options.FaceMinConfidence : _options.PlateMinConfidence);
                    boxes.AddRange(detector.Detect(frame, min));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The {Target} detector failed; the media will go to review.", detector.Target);
                    _problems.Add($"The {detector.Target} detector failed.");
                    _active.Remove(detector);
                }
            }

            return boxes;
        }
    }
}

/// <summary>An ffmpeg child process with piped output (and optionally input). Error output is kept for messages.</summary>
internal sealed class FfmpegPipe : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _errors = new();
    private readonly Task _errorReader;

    private FfmpegPipe(Process process)
    {
        _process = process;
        _errorReader = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                lock (_errors)
                {
                    _errors.AppendLine(line);
                    if (_errors.Length > 16_000)
                    {
                        _errors.Remove(0, _errors.Length - 8_000);
                    }
                }
            }
        });
    }

    public Stream Output => _process.StandardOutput.BaseStream;

    public Stream Input => _process.StandardInput.BaseStream;

    public static FfmpegPipe Start(string executable, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = !redirectInput,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"Couldn't start '{executable}'. Is ffmpeg installed? Set Media:Processing:FfmpegPath.", ex);
        }

        return new FfmpegPipe(process);
    }

    /// <summary>Waits for ffmpeg to finish; a non-zero exit is an <see cref="UnreadableMediaException"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        await _process.WaitForExitAsync(limit.Token);
        await _errorReader.WaitAsync(limit.Token);
        if (_process.ExitCode != 0)
        {
            throw new UnreadableMediaException($"ffmpeg couldn't {what}: {ErrorTail()}");
        }
    }

    public string ErrorTail()
    {
        string text;
        lock (_errors)
        {
            text = _errors.ToString();
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no details" : string.Join(" | ", lines.TakeLast(3));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
