using System.Diagnostics;
using System.Globalization;
using System.Text;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
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
/// <param name="DetectionTime">Part of <paramref name="Elapsed"/> spent running the detection models.</param>
/// <param name="SensitiveScore">
/// The highest graphic content score (0 to 1) of the frames scored, or null if detection is off or nothing was scored.
/// </param>
/// <param name="FramesClassified">Frames the graphic content classifier scored.</param>
/// <param name="ClassificationTime">Part of <paramref name="Elapsed"/> spent scoring graphic content.</param>
public sealed record RedactionResult(
    int FacesBlurred,
    int PlatesBlurred,
    IReadOnlyList<string> ReviewReasons,
    int Frames,
    int FramesAnalysed,
    TimeSpan Elapsed,
    TimeSpan DetectionTime = default,
    double? SensitiveScore = null,
    int FramesClassified = 0,
    TimeSpan ClassificationTime = default)
{
    public bool NeedsReview => ReviewReasons.Count > 0;
}

/// <summary>
/// Frames (counted from the start of the video) a pass decodes, from <see cref="First"/> to <see cref="Last"/>
/// (null: to the end), and its own part, <see cref="CoreStart"/> up to <see cref="CoreEnd"/> (null: to the end), which
/// it writes. Frames outside its own part are only analysed, for tracking.
/// </summary>
/// <param name="First">First frame decoded.</param>
/// <param name="CoreStart">First frame written.</param>
/// <param name="CoreEnd">Frame after the last written; null for the end of the video.</param>
/// <param name="Last">Last frame decoded; null for the end of the video.</param>
public sealed record FrameRange(int First, int CoreStart, int? CoreEnd, int? Last = null)
{
    public bool Contains(int frame) => frame >= CoreStart && (CoreEnd is null || frame < CoreEnd);

    /// <summary>True if frames <paramref name="from"/> to <paramref name="to"/> (inclusive) touch the own part.</summary>
    /// <param name="from">First frame.</param>
    /// <param name="to">Last frame.</param>
    public bool Overlaps(int from, int to) => to >= CoreStart && (CoreEnd is null || from < CoreEnd);
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
    /// Writes one segment of a video, blurred, as an H.264 MP4 with exactly the encoder settings of
    /// <see cref="RedactVideoAsync"/> and no sound, so the segments can be joined without re-encoding. The segment's
    /// overlap either side is decoded and analysed too, so tracking at the joins matches a whole-video pass. A last
    /// segment that turns out to have no frames writes nothing and reports 0 frames.
    /// </summary>
    /// <param name="inputPath">The original.</param>
    /// <param name="plan">How the video is split.</param>
    /// <param name="segment">Which segment, from 0.</param>
    /// <param name="manual">Moderators' extra blur regions.</param>
    /// <param name="outputPath">Where to write the MP4.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    Task<RedactionResult> RedactVideoSegmentAsync(
        string inputPath,
        VideoSegmentPlan plan,
        int segment,
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
    ILogger<MediaRedactor> logger,
    ISensitiveContentClassifier? classifier = null,
    IOptions<SensitiveDetectionOptions>? detectionOptions = null)
    : IMediaRedactor
{
    private readonly MediaRedactionOptions _options = redactionOptions.Value;
    private readonly MediaProcessingOptions _processing = processingOptions.Value;
    private readonly IReadOnlyList<IRedactionDetector> _detectors = detectors.ToList();

    // Graphic content scoring runs inside the same pass, on the same (scaled-down) frames: nothing is decoded twice.
    private readonly ISensitiveContentClassifier? _classifier = detectionOptions?.Value.Enabled == true ? classifier : null;
    private readonly bool _detectSensitive = detectionOptions?.Value.Enabled == true;
    private readonly int _sampleEvery = Math.Max(1, detectionOptions?.Value.SampleEvery ?? 1);

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

        // Photos are analysed at full size (they're few, and small plates matter in parking photos).
        var pass = NewPass(maxSize: 0);
        var found = pass.Detect(frame, classify: true);
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

    public Task<RedactionResult> RedactVideoAsync(
        string inputPath,
        ProbeResult probe,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fps = OutputFrameRate(probe.FrameRate);
        var fpsText = FpsText(fps);
        var threads = _processing.EffectiveFfmpegThreads.ToString(CultureInfo.InvariantCulture);
        var filters = $"scale=-2:'trunc(min({_processing.VideoHeight},ih)/2)*2':flags=lanczos,fps={fpsText}";
        var decoder = new List<string>
        {
            "-hide_banner", "-nostdin", "-threads", threads, "-i", inputPath, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", filters, "-fps_mode", "passthrough", "-f", "image2pipe", "-c:v", "ppm", "-pix_fmt", "rgb24", "pipe:1",
        };

        return RedactRangeAsync(
            inputPath,
            decoder,
            fps,
            new FrameRange(First: 0, CoreStart: 0, CoreEnd: null),
            manual,
            outputPath,
            keepAudio: !_options.RemoveAudio,
            cancellationToken);
    }

    public Task<RedactionResult> RedactVideoSegmentAsync(
        string inputPath,
        VideoSegmentPlan plan,
        int segment,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segment);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segment, plan.SegmentCount);

        var fps = plan.FrameRate;
        var range = SegmentRange(plan, segment);

        // Frames are picked by their place on the output's frame grid in the source's own timeline (-copyts), so
        // every segment agrees exactly where it starts and ends; the select filter keeps this segment's frames plus
        // the overlap either side (half a frame of tolerance each way). Seeking a second early and letting select
        // cut is exact whatever the keyframes are.
        var from = (range.First + plan.FirstFrame - 0.5) / fps;
        var select = range.Last is { } last
            ? $"select='gte(t,{Num(from)})*lt(t,{Num((last + plan.FirstFrame + 0.5) / fps)})'"
            : $"select='gte(t,{Num(from)})'";
        var seek = Math.Max(0, (range.First / fps) - 1.0);
        var filters = $"fps={FpsText(fps)},{select},scale=-2:'trunc(min({_processing.VideoHeight},ih)/2)*2':flags=lanczos";
        var decoder = new List<string>
        {
            "-hide_banner", "-nostdin", "-threads", _processing.EffectiveFfmpegThreads.ToString(CultureInfo.InvariantCulture),
            "-ss", Num(seek), "-i", inputPath, "-copyts", "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", filters, "-fps_mode", "passthrough",
        };
        if (range.Last is { } final)
        {
            // Stop decoding once the last frame needed has been read.
            decoder.AddRange(["-frames:v", (final - range.First + 1).ToString(CultureInfo.InvariantCulture)]);
        }

        decoder.AddRange(["-f", "image2pipe", "-c:v", "ppm", "-pix_fmt", "rgb24", "pipe:1"]);

        // Segments never carry sound: it's added (if kept at all) when they're joined.
        return RedactRangeAsync(inputPath, decoder, fps, range, manual, outputPath, keepAudio: false, cancellationToken);
    }

    /// <summary>
    /// The frames segment <paramref name="segment"/> decodes: its own part plus the overlap either side, clipped to the
    /// video. <see cref="FrameRange.Last"/> is null when it runs to the end.
    /// </summary>
    /// <param name="plan">The split.</param>
    /// <param name="segment">The segment.</param>
    internal static FrameRange SegmentRange(VideoSegmentPlan plan, int segment)
    {
        var coreStart = plan.CoreStart(segment);
        var coreEnd = plan.CoreEnd(segment);
        var first = Math.Max(0, coreStart - plan.OverlapFrames);
        int? last = coreEnd is { } end ? end - 1 + plan.OverlapFrames : null;
        return new FrameRange(first, coreStart, coreEnd, last);
    }

    /// <summary>
    /// Decodes frames with <paramref name="decoderArguments"/>, runs detection every Nth frame (counted from the start
    /// of the video, so a segment analyses the same frames the whole video would), tracks, blurs and encodes the
    /// frames in the range's own part. Frames in the overlap are analysed but not written.
    /// </summary>
    private async Task<RedactionResult> RedactRangeAsync(
        string inputPath,
        IReadOnlyList<string> decoderArguments,
        double fps,
        FrameRange range,
        IReadOnlyList<ManualRedaction> manual,
        string outputPath,
        bool keepAudio,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.VideoTimeout);
        var token = timeout.Token;

        var fpsText = FpsText(fps);
        var interval = _options.DetectEveryNthFrame;
        var lookahead = (_options.MaxMissedDetections + 1) * interval;

        var pass = NewPass(_options.DetectionMaxSize);
        var tracker = new RedactionTracker(_options.MaxMissedDetections);
        var pending = new Queue<(int Index, RgbFrame Frame)>();
        var decoded = 0;
        var written = 0;
        var analysed = 0;
        FfmpegPipe? encoder = null;

        using var decoder = FfmpegPipe.Start(_processing.FfmpegPath, decoderArguments, redirectInput: false);
        try
        {
            var reader = new PpmReader(decoder.Output);
            var lastAnalysed = -1;
            while (await reader.ReadAsync(token) is { } frame)
            {
                var index = range.First + decoded++;
                if (index % interval == 0)
                {
                    // Graphic content is scored on every SampleEvery-th analysed frame, counted from the start of the video.
                    tracker.Add(index, pass.Detect(frame, classify: (index / interval) % _sampleEvery == 0));
                    analysed++;
                    lastAnalysed = index;
                }

                if (range.Contains(index))
                {
                    encoder ??= StartEncoder(inputPath, outputPath, frame.Width, frame.Height, fpsText, keepAudio);
                    pending.Enqueue((index, frame));
                }

                // A frame's boxes are final once detection has run far enough past it.
                while (pending.Count > 0 && pending.Peek().Index <= lastAnalysed - lookahead)
                {
                    await WriteAsync(encoder!, pending.Dequeue(), tracker, interval, manual, fps, token);
                    written++;
                }
            }

            await decoder.WaitAsync(_options.VideoTimeout, "decode the video", token);
            if (encoder is null)
            {
                // Only the last segment of a video may turn out empty (its length was overstated); it's then skipped.
                if (range.CoreEnd is null && range.CoreStart > 0)
                {
                    return Result(pass, 0, 0, 0, frames: 0, analysed, timer.Elapsed);
                }

                throw new UnreadableMediaException("ffmpeg found no frames in the video.");
            }

            while (pending.Count > 0)
            {
                await WriteAsync(encoder, pending.Dequeue(), tracker, interval, manual, fps, token);
                written++;
            }

            await encoder.Input.FlushAsync(token);
            encoder.Input.Close();
            await encoder.WaitAsync(_options.VideoTimeout, "encode the video", token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Blurring the video took longer than {_options.VideoTimeout}.");
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

        // Only faces and plates that show in this range's own part count (the neighbours count the rest).
        var tracks = tracker.Tracks.Where(t => range.Overlaps(t.FirstFrame - interval, t.LastFrame + interval)).ToList();
        var uncertain = tracks.Count(t => t.MaxConfidence < ConfidentAt(t.Target));
        var result = Result(
            pass,
            tracks.Count(t => t.Target == RedactionTarget.Face),
            tracks.Count(t => t.Target == RedactionTarget.NumberPlate),
            uncertain,
            written,
            analysed,
            timer.Elapsed);

        logger.LogInformation(
            "Blurred {Frames} frames from {First} ({Analysed} analysed) in {Elapsed} ({Detection} detecting): {Faces} faces, {Plates} plates, {Uncertain} uncertain.",
            written,
            range.CoreStart,
            analysed,
            timer.Elapsed,
            result.DetectionTime,
            result.FacesBlurred,
            result.PlatesBlurred,
            uncertain);
        return result;
    }

    /// <summary>The output frame rate: the source's, at most 30; 30 if unknown.</summary>
    internal static double FrameRate(double? source) =>
        source is { } rate && double.IsFinite(rate) && rate > 0 ? Math.Min(30, rate) : 30;

    /// <summary>
    /// <see cref="FrameRate"/> rounded to the 3 decimal places given to ffmpeg's fps filter, so frame times worked
    /// out here match the filter's exactly.
    /// </summary>
    internal static double OutputFrameRate(double? source) =>
        Math.Round(FrameRate(source), 3, MidpointRounding.AwayFromZero);

    private static string FpsText(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private FfmpegPipe StartEncoder(string inputPath, string outputPath, int width, int height, string fps, bool keepAudio)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-framerate", fps, "-i", "pipe:0",
        };
        if (!keepAudio)
        {
            arguments.AddRange(["-map", "0:v:0", "-an"]);
        }
        else
        {
            arguments.AddRange(["-i", inputPath, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        }

        // Identical settings for whole videos and every segment, so segments join without re-encoding.
        arguments.AddRange(
        [
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", _processing.VideoCrf.ToString(CultureInfo.InvariantCulture),
            "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-threads", _processing.EffectiveFfmpegThreads.ToString(CultureInfo.InvariantCulture),
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

    /// <summary>How much to shrink a frame before detection: 1 (not at all) if it already fits or the limit is 0.</summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="maxSize">Longest side allowed; 0 for no limit.</param>
    internal static float DetectionScale(int width, int height, int maxSize)
    {
        var longest = Math.Max(width, height);
        return maxSize <= 0 || longest <= maxSize ? 1f : maxSize / (float)longest;
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

        return new RedactionResult(
            faces, plates, reasons, frames, analysed, elapsed, pass.Elapsed, pass.SensitiveScore, pass.FramesClassified, pass.ClassificationTime);
    }

    private DetectionPass NewPass(int maxSize) =>
        new(_detectors, _options, logger, maxSize, _detectSensitive, _classifier);

    /// <summary>
    /// Runs the available detectors on frames for one job. A detector that's unavailable, or fails on any frame, is
    /// recorded as a problem (so the media goes to review) and not used again for the rest of the job.
    /// </summary>
    private sealed class DetectionPass
    {
        private readonly List<IRedactionDetector> _active = [];
        private readonly List<string> _problems = [];
        private readonly Stopwatch _timer = new();
        private readonly Stopwatch _classifyTimer = new();
        private readonly MediaRedactionOptions _options;
        private readonly ILogger _logger;
        private readonly int _maxSize;
        private ISensitiveContentClassifier? _classifier;

        public DetectionPass(
            IReadOnlyList<IRedactionDetector> detectors,
            MediaRedactionOptions options,
            ILogger logger,
            int maxSize,
            bool detectSensitive = false,
            ISensitiveContentClassifier? classifier = null)
        {
            _options = options;
            _logger = logger;
            _maxSize = maxSize;
            if (detectSensitive)
            {
                // Fail safe: with detection on, a missing or unavailable classifier sends the media to review.
                if (classifier is null)
                {
                    _problems.Add("No graphic content classifier is registered.");
                }
                else if (!classifier.IsAvailable)
                {
                    _problems.Add(classifier.UnavailableReason ?? "The graphic content classifier is unavailable.");
                }
                else
                {
                    _classifier = classifier;
                }
            }

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

        public TimeSpan Elapsed => _timer.Elapsed;

        /// <summary>The highest graphic content score so far, or null if nothing was scored.</summary>
        public double? SensitiveScore { get; private set; }

        public int FramesClassified { get; private set; }

        public TimeSpan ClassificationTime => _classifyTimer.Elapsed;

        /// <param name="frame">The full frame (blurring is applied to it by the caller).</param>
        /// <param name="classify">Also score this frame for graphic content (when detection is on).</param>
        public List<DetectionBox> Detect(RgbFrame frame, bool classify = false)
        {
            _timer.Start();
            try
            {
                // Videos: detect on a smaller copy (Media:Redaction:DetectionMaxSize), then map the boxes back to the frame.
                var scale = DetectionScale(frame.Width, frame.Height, _maxSize);
                if (scale >= 1)
                {
                    Classify(frame, classify);
                    return DetectAll(frame);
                }

                var small = FrameOps.Resize(
                    frame,
                    Math.Max(1, (int)MathF.Round(frame.Width * scale)),
                    Math.Max(1, (int)MathF.Round(frame.Height * scale)));
                Classify(small, classify);
                var (sx, sy) = (frame.Width / (float)small.Width, frame.Height / (float)small.Height);
                return DetectAll(small)
                    .Select(b => b with { X = b.X * sx, Y = b.Y * sy, Width = b.Width * sx, Height = b.Height * sy })
                    .ToList();
            }
            finally
            {
                _timer.Stop();
            }
        }

        /// <summary>
        /// Scores the frame (already scaled down for detection) for graphic content. A failure is a problem (the media
        /// goes to review) and the classifier isn't used again for this job.
        /// </summary>
        private void Classify(RgbFrame frame, bool classify)
        {
            if (!classify || _classifier is null)
            {
                return;
            }

            _classifyTimer.Start();
            try
            {
                var score = _classifier.Score(frame);
                SensitiveScore = Math.Max(SensitiveScore ?? 0, score);
                FramesClassified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The graphic content classifier failed; the media will go to review.");
                _problems.Add("The graphic content check failed.");
                _classifier = null;
            }
            finally
            {
                _classifyTimer.Stop();
            }
        }

        private List<DetectionBox> DetectAll(RgbFrame frame)
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
